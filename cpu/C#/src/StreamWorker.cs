using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace RtspStressTest;

public sealed unsafe class StreamWorker : IDisposable
{
    private readonly int _streamId;
    private readonly string _rtspUrl;
    private readonly CancellationTokenSource _cts = new();

    private volatile bool _isConnected;
    private long _decodedFrames;
    private long _paintedFrames;
    private int _width = 2560;
    private int _height = 1440;
    private int _renderPending;
    private volatile bool _hasNewFrame;

    private WriteableBitmap? _writeableBitmap;

    private readonly int _targetWidth;
    private readonly int _targetHeight;

    private int _bufferWidth;
    private int _bufferHeight;

    private SwsContext* _swsContext;
    private int _swsSrcWidth;
    private int _swsSrcHeight;
    private int _swsDstWidth;
    private int _swsDstHeight;
    private AVPixelFormat _swsFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    // Reusable arrays for sws_scale to eliminate GC allocations
    private readonly byte*[] _srcData = new byte*[8];
    private readonly int[] _srcStride = new int[8];
    private readonly byte*[] _dstData = new byte*[8];
    private readonly int[] _dstStride = new int[8];

    private Task? _workerTask;

    private long _currentPts = -1;
    private long _lastPresentedTimestamp;
    private double _lastDeltaMs;

    public int StreamId => _streamId;
    public string RtspUrl => _rtspUrl;
    public bool IsConnected => _isConnected;
    public ulong DecodedFrames => (ulong)Interlocked.Read(ref _decodedFrames);
    public ulong PaintedFrames => (ulong)Interlocked.Read(ref _paintedFrames);
    public long CurrentPts => Interlocked.Read(ref _currentPts);
    public double LastDeltaMs => _lastDeltaMs;
    public uint CurrentFps { get; set; }
    public uint CurrentPaintedFps { get; set; }
    public uint CurrentDecodedFps { get; set; }
    public int Width => _width;
    public int Height => _height;
    public WriteableBitmap? Bitmap => _writeableBitmap;

    public void IncrementPaintedFrames() => RecordPresentedFrame(CurrentPts);

    public void RecordPresentedFrame(long pts)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var prev = Interlocked.Exchange(ref _lastPresentedTimestamp, now);
        if (prev > 0)
        {
            var deltaSec = (double)(now - prev) / System.Diagnostics.Stopwatch.Frequency;
            _lastDeltaMs = deltaSec * 1000.0;
        }
        Interlocked.Increment(ref _paintedFrames);
    }

    public bool HasNewFrame => _hasNewFrame;
    public void ResetNewFrame() => _hasNewFrame = false;

    public event Action? FrameRendered;

    public StreamWorker(int streamId, string rtspUrl, int targetWidth = 0, int targetHeight = 0)
    {
        _streamId = streamId;
        _rtspUrl = rtspUrl;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;

        var initialW = targetWidth > 0 ? targetWidth : _width;
        var initialH = targetHeight > 0 ? targetHeight : _height;

        // Initialize WriteableBitmap and pre-allocated buffer
        EnsureBuffers(initialW, initialH);
    }

    public void Start()
    {
        _workerTask = Task.Factory.StartNew(WorkerLoop, TaskCreationOptions.LongRunning);
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_writeableBitmap == null || _bufferWidth != width || _bufferHeight != height)
        {
            _bufferWidth = width;
            _bufferHeight = height;

            var size = new PixelSize(width, height);
            _writeableBitmap = new WriteableBitmap(
                size,
                new Vector(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Opaque
            );
        }
    }

    private void EnsureSwsContext(int srcW, int srcH, AVPixelFormat format, int dstW, int dstH)
    {
        if (_swsContext == null || _swsSrcWidth != srcW || _swsSrcHeight != srcH ||
            _swsDstWidth != dstW || _swsDstHeight != dstH || _swsFormat != format)
        {
            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            _swsContext = ffmpeg.sws_getContext(
                srcW,
                srcH,
                format,
                dstW,
                dstH,
                AVPixelFormat.AV_PIX_FMT_RGBA,
                (int)SwsFlags.SWS_FAST_BILINEAR,
                null,
                null,
                null
            );

            _swsSrcWidth = srcW;
            _swsSrcHeight = srcH;
            _swsDstWidth = dstW;
            _swsDstHeight = dstH;
            _swsFormat = format;
        }
    }

    private void WorkerLoop()
    {
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();

        while (!_cts.IsCancellationRequested)
        {
            AVFormatContext* fmtCtx = ffmpeg.avformat_alloc_context();
            if (fmtCtx == null)
            {
                Thread.Sleep(500);
                continue;
            }

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&opts, "stimeout", "5000000", 0);       // 5 sec timeout (in us)
            ffmpeg.av_dict_set(&opts, "max_delay", "500000", 0);        // 500 ms max latency (in us)
            ffmpeg.av_dict_set(&opts, "buffer_size", "4194304", 0);     // 4MB socket buffer

            var ret = ffmpeg.avformat_open_input(&fmtCtx, _rtspUrl, null, &opts);
            ffmpeg.av_dict_free(&opts);

            if (ret < 0)
            {
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(3000);
                }
                continue;
            }

            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
            {
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(3000);
                }
                continue;
            }

            var videoStreamIdx = ffmpeg.av_find_best_stream(fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoStreamIdx < 0)
            {
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(3000);
                }
                continue;
            }

            var codecPar = fmtCtx->streams[videoStreamIdx]->codecpar;
            // Pure CPU software decoding: avcodec_find_decoder
            var codec = ffmpeg.avcodec_find_decoder(codecPar->codec_id);
            if (codec == null)
            {
                Console.Error.WriteLine($"[Stream {_streamId}] Codec not found for ID: {codecPar->codec_id}");
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(3000);
                }
                continue;
            }

            var codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null)
            {
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(3000);
                }
                continue;
            }

            if (ffmpeg.avcodec_parameters_to_context(codecCtx, codecPar) < 0)
            {
                _isConnected = false;
                ffmpeg.avcodec_free_context(&codecCtx);
                ffmpeg.avformat_close_input(&fmtCtx);
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(3000);
                }
                continue;
            }

            // Software decode constraints & thread tuning
            codecCtx->thread_count = 1; // 1 thread per decoder worker for balanced CPU core distribution
            codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            if (ffmpeg.avcodec_open2(codecCtx, codec, null) < 0)
            {
                _isConnected = false;
                ffmpeg.avcodec_free_context(&codecCtx);
                ffmpeg.avformat_close_input(&fmtCtx);
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(3000);
                }
                continue;
            }

            _isConnected = true;

            // Demux and decode loop
            while (!_cts.IsCancellationRequested)
            {
                ret = ffmpeg.av_read_frame(fmtCtx, pkt);
                if (ret < 0)
                {
                    // Connection dropped or EOF
                    break;
                }

                if (pkt->stream_index == videoStreamIdx)
                {
                    var sendRet = ffmpeg.avcodec_send_packet(codecCtx, pkt);
                    ffmpeg.av_packet_unref(pkt);

                    if (sendRet < 0)
                    {
                        continue;
                    }

                    while (ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
                    {
                        var w = frame->width;
                        var h = frame->height;
                        if (w > 0 && h > 0)
                        {
                            _width = w;
                            _height = h;

                            var dstW = _targetWidth > 0 ? _targetWidth : w;
                            var dstH = _targetHeight > 0 ? _targetHeight : h;

                            EnsureBuffers(dstW, dstH);
                            EnsureSwsContext(w, h, (AVPixelFormat)frame->format, dstW, dstH);

                            if (_writeableBitmap != null && _swsContext != null)
                            {
                                for (uint i = 0; i < 8; i++)
                                {
                                    _srcData[i] = frame->data[i];
                                    _srcStride[i] = frame->linesize[i];
                                }

                                // Direct zero-copy scale into locked WriteableBitmap surface
                                using (var fb = _writeableBitmap.Lock())
                                {
                                    _dstData[0] = (byte*)fb.Address;
                                    _dstStride[0] = fb.RowBytes;
                                    for (uint i = 1; i < 8; i++)
                                    {
                                        _dstData[i] = null;
                                        _dstStride[i] = 0;
                                    }

                                    ffmpeg.sws_scale(
                                        _swsContext,
                                        _srcData,
                                        _srcStride,
                                        0,
                                        h,
                                        _dstData,
                                        _dstStride
                                    );
                                }

                                var pts = frame->pts != ffmpeg.AV_NOPTS_VALUE ? frame->pts : frame->best_effort_timestamp;
                                Interlocked.Exchange(ref _currentPts, pts);
                                Interlocked.Increment(ref _decodedFrames);
                                _hasNewFrame = true;
                            }
                        }

                        ffmpeg.av_frame_unref(frame);
                    }
                }
                else
                {
                    ffmpeg.av_packet_unref(pkt);
                }
            }

            _isConnected = false;
            ffmpeg.avcodec_free_context(&codecCtx);
            ffmpeg.avformat_close_input(&fmtCtx);
            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            if (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(3000); // 3-second backoff before reconnecting per Master Spec
            }
        }

        ffmpeg.av_frame_free(&frame);
        ffmpeg.av_packet_free(&pkt);

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }
    }

    private void RequestRender()
    {
        if (Interlocked.CompareExchange(ref _renderPending, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _renderPending = 0;
                if (_hasNewFrame)
                {
                    _hasNewFrame = false;
                    FrameRendered?.Invoke();
                }
            }, DispatcherPriority.Render);
        }
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    public void Dispose()
    {
        Stop();
        try
        {
            _workerTask?.Wait(2000);
        }
        catch
        {
            // Ignore
        }
        _cts.Dispose();
    }
}
