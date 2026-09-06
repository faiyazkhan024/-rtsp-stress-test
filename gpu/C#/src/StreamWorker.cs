using System;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;

namespace RtspStressTest;

public sealed unsafe class StreamWorker : IDisposable
{
    private readonly int _streamId;
    private readonly string _rtspUrl;
    private readonly HwAccelManager _hwAccel;
    private readonly CancellationTokenSource _cts = new();
    private readonly AVIOInterruptCB_callback _interruptCallback;

    private volatile bool _isConnected;
    private volatile bool _isHwAccelerated;
    private long _decodedFrames;
    private long _paintedFrames;
    private int _width;
    private int _height;
    private int _hasNewFrame;
    private nint _sharedFrame;
    private AVFrame* _consumedFrame;
    private Task? _workerTask;
    private long _currentPts = -1;
    private long _lastPresentedTimestamp;
    private double _lastDeltaMs;
    private string _hwDeviceName = "CPU";
    private SwsContext* _swsCtx;

    public int StreamId => _streamId;
    public bool IsConnected => _isConnected;
    public bool IsHwAccelerated => _isHwAccelerated;
    public string HwDeviceName => _hwDeviceName;
    public ulong DecodedFrames => (ulong)Interlocked.Read(ref _decodedFrames);
    public ulong PaintedFrames => (ulong)Interlocked.Read(ref _paintedFrames);
    public long CurrentPts => Interlocked.Read(ref _currentPts);
    public double LastDeltaMs => _lastDeltaMs;
    public uint CurrentFps { get; set; }
    public uint CurrentPaintedFps { get; set; }
    public uint CurrentDecodedFps { get; set; }
    public int Width => _width;
    public int Height => _height;
    public bool HasNewFrame => Volatile.Read(ref _hasNewFrame) != 0;

    public StreamWorker(int streamId, string rtspUrl, HwAccelManager hwAccel)
    {
        _streamId = streamId;
        _rtspUrl = rtspUrl;
        _hwAccel = hwAccel;
        _interruptCallback = InterruptCallback;
        if (_hwAccel.IsInitialized)
        {
            _hwDeviceName = _hwAccel.DeviceName;
        }
    }

    public void Start()
    {
        _workerTask = Task.Factory.StartNew(WorkerLoop, TaskCreationOptions.LongRunning);
    }

    public void RecordPresentedFrame(long pts)
    {
        if (pts != -1)
        {
            Interlocked.Exchange(ref _currentPts, pts);
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var prev = Interlocked.Exchange(ref _lastPresentedTimestamp, now);
        if (prev > 0)
        {
            var deltaSec = (double)(now - prev) / System.Diagnostics.Stopwatch.Frequency;
            _lastDeltaMs = deltaSec * 1000.0;
        }

        Interlocked.Increment(ref _paintedFrames);
    }

    public AVFrame* AcquireFrame(out bool isNew)
    {
        isNew = Interlocked.Exchange(ref _hasNewFrame, 0) != 0;
        if (isNew)
        {
            var ptr = Interlocked.Exchange(ref _sharedFrame, 0);
            if (ptr != 0)
            {
                if (_consumedFrame != null)
                {
                    var old = _consumedFrame;
                    ffmpeg.av_frame_free(&old);
                }

                _consumedFrame = (AVFrame*)ptr;
                var pts = _consumedFrame->pts != ffmpeg.AV_NOPTS_VALUE
                    ? _consumedFrame->pts
                    : _consumedFrame->best_effort_timestamp;
                RecordPresentedFrame(pts);
            }
        }

        return _consumedFrame;
    }

    private int InterruptCallback(void* opaque)
    {
        return _cts.IsCancellationRequested ? 1 : 0;
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

            var interruptCb = new AVIOInterruptCB
            {
                callback = _interruptCallback,
                opaque = null
            };
            fmtCtx->interrupt_callback = interruptCb;

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&opts, "stimeout", "5000000", 0);
            ffmpeg.av_dict_set(&opts, "max_delay", "500000", 0);
            ffmpeg.av_dict_set(&opts, "buffer_size", "4194304", 0);

            var ret = ffmpeg.avformat_open_input(&fmtCtx, _rtspUrl, null, &opts);
            ffmpeg.av_dict_free(&opts);

            if (ret < 0)
            {
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                SleepReconnect();
                continue;
            }

            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
            {
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                SleepReconnect();
                continue;
            }

            var videoStreamIdx = ffmpeg.av_find_best_stream(fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoStreamIdx < 0)
            {
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                SleepReconnect();
                continue;
            }

            var codecPar = fmtCtx->streams[videoStreamIdx]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecPar->codec_id);
            if (codec == null)
            {
                Console.Error.WriteLine($"[Stream {_streamId}] Codec not found for ID: {codecPar->codec_id}");
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                SleepReconnect();
                continue;
            }

            var codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null)
            {
                _isConnected = false;
                ffmpeg.avformat_close_input(&fmtCtx);
                SleepReconnect();
                continue;
            }

            if (ffmpeg.avcodec_parameters_to_context(codecCtx, codecPar) < 0)
            {
                _isConnected = false;
                ffmpeg.avcodec_free_context(&codecCtx);
                ffmpeg.avformat_close_input(&fmtCtx);
                SleepReconnect();
                continue;
            }

            if (_hwAccel.IsInitialized)
            {
                var hwRef = _hwAccel.CreateDeviceRef();
                if (hwRef != null)
                {
                    codecCtx->hw_device_ctx = hwRef;
                    codecCtx->get_format = _hwAccel.GetFormatCallback;
                    _hwDeviceName = _hwAccel.DeviceName;
                }
            }

            codecCtx->thread_count = 1;
            codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            if (ffmpeg.avcodec_open2(codecCtx, codec, null) < 0)
            {
                Console.Error.WriteLine($"[Stream {_streamId}] Failed to open codec context with hwaccel.");
                _isConnected = false;
                ffmpeg.avcodec_free_context(&codecCtx);
                ffmpeg.avformat_close_input(&fmtCtx);
                SleepReconnect();
                continue;
            }

            AVBSFContext* bsfCtx = null;
            var bsf = ffmpeg.av_bsf_get_by_name("h264_mp4toannexb");
            if (bsf != null && ffmpeg.av_bsf_alloc(bsf, &bsfCtx) == 0)
            {
                ffmpeg.avcodec_parameters_copy(bsfCtx->par_in, codecPar);
                if (ffmpeg.av_bsf_init(bsfCtx) < 0)
                {
                    ffmpeg.av_bsf_free(&bsfCtx);
                    bsfCtx = null;
                }
            }

            var filteredPkt = ffmpeg.av_packet_alloc();
            _isConnected = true;

            while (!_cts.IsCancellationRequested)
            {
                ret = ffmpeg.av_read_frame(fmtCtx, pkt);
                if (ret < 0)
                {
                    break;
                }

                if (pkt->stream_index == videoStreamIdx)
                {
                    if (bsfCtx != null)
                    {
                        if (ffmpeg.av_bsf_send_packet(bsfCtx, pkt) == 0)
                        {
                            while (ffmpeg.av_bsf_receive_packet(bsfCtx, filteredPkt) == 0)
                            {
                                DecodePacket(codecCtx, filteredPkt, frame);
                                ffmpeg.av_packet_unref(filteredPkt);
                            }
                        }
                    }
                    else
                    {
                        DecodePacket(codecCtx, pkt, frame);
                    }
                }

                ffmpeg.av_packet_unref(pkt);
            }

            if (bsfCtx != null)
            {
                ffmpeg.av_bsf_free(&bsfCtx);
            }

            ffmpeg.av_packet_free(&filteredPkt);
            _isConnected = false;
            _isHwAccelerated = false;
            ffmpeg.avcodec_free_context(&codecCtx);
            ffmpeg.avformat_close_input(&fmtCtx);
            DropSharedFrame();
            SleepReconnect();
        }

        ffmpeg.av_frame_free(&frame);
        ffmpeg.av_packet_free(&pkt);
        DropSharedFrame();
    }

    private void DecodePacket(AVCodecContext* codecCtx, AVPacket* pkt, AVFrame* frame)
    {
        if (ffmpeg.avcodec_send_packet(codecCtx, pkt) < 0)
        {
            return;
        }

        while (ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
        {
            var w = frame->width;
            var h = frame->height;
            if (w > 0 && h > 0)
            {
                _width = w;
                _height = h;
                var pts = frame->pts != ffmpeg.AV_NOPTS_VALUE ? frame->pts : frame->best_effort_timestamp;
                Interlocked.Exchange(ref _currentPts, pts);

                AVFrame* publishFrame = null;
                if (_hwAccel.IsInitialized && (AVPixelFormat)frame->format == _hwAccel.HwPixFormat)
                {
                    _isHwAccelerated = true;
                    var swFrame = ffmpeg.av_frame_alloc();
                    if (swFrame != null)
                    {
                        if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) == 0)
                        {
                            swFrame->pts = pts;
                            const int targetW = 320;
                            const int targetH = 180;
                            if (swFrame->width > targetW && swFrame->height > targetH)
                            {
                                _swsCtx = ffmpeg.sws_getCachedContext(
                                    _swsCtx,
                                    swFrame->width, swFrame->height, (AVPixelFormat)swFrame->format,
                                    targetW, targetH, (AVPixelFormat)swFrame->format,
                                    (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                                if (_swsCtx != null)
                                {
                                    var scaledFrame = ffmpeg.av_frame_alloc();
                                    if (scaledFrame != null)
                                    {
                                        scaledFrame->format = swFrame->format;
                                        scaledFrame->width = targetW;
                                        scaledFrame->height = targetH;
                                        scaledFrame->pts = pts;
                                        if (ffmpeg.av_frame_get_buffer(scaledFrame, 32) == 0)
                                        {
                                            ffmpeg.sws_scale(
                                                _swsCtx,
                                                swFrame->data, swFrame->linesize, 0, swFrame->height,
                                                scaledFrame->data, scaledFrame->linesize);
                                            publishFrame = scaledFrame;
                                            ffmpeg.av_frame_free(&swFrame);
                                        }
                                        else
                                        {
                                            ffmpeg.av_frame_free(&scaledFrame);
                                            publishFrame = swFrame;
                                        }
                                    }
                                    else
                                    {
                                        publishFrame = swFrame;
                                    }
                                }
                                else
                                {
                                    publishFrame = swFrame;
                                }
                            }
                            else
                            {
                                publishFrame = swFrame;
                            }
                        }
                        else
                        {
                            ffmpeg.av_frame_free(&swFrame);
                        }
                    }
                }
                else
                {
                    const int targetW = 320;
                    const int targetH = 180;
                    if (frame->width > targetW && frame->height > targetH)
                    {
                        _swsCtx = ffmpeg.sws_getCachedContext(
                            _swsCtx,
                            frame->width, frame->height, (AVPixelFormat)frame->format,
                            targetW, targetH, (AVPixelFormat)frame->format,
                            (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                        if (_swsCtx != null)
                        {
                            var scaledFrame = ffmpeg.av_frame_alloc();
                            if (scaledFrame != null)
                            {
                                scaledFrame->format = frame->format;
                                scaledFrame->width = targetW;
                                scaledFrame->height = targetH;
                                scaledFrame->pts = pts;
                                if (ffmpeg.av_frame_get_buffer(scaledFrame, 32) == 0)
                                {
                                    ffmpeg.sws_scale(
                                        _swsCtx,
                                        frame->data, frame->linesize, 0, frame->height,
                                        scaledFrame->data, scaledFrame->linesize);
                                    publishFrame = scaledFrame;
                                }
                                else
                                {
                                    ffmpeg.av_frame_free(&scaledFrame);
                                    publishFrame = ffmpeg.av_frame_clone(frame);
                                }
                            }
                            else
                            {
                                publishFrame = ffmpeg.av_frame_clone(frame);
                            }
                        }
                        else
                        {
                            publishFrame = ffmpeg.av_frame_clone(frame);
                        }
                    }
                    else
                    {
                        publishFrame = ffmpeg.av_frame_clone(frame);
                    }
                }

                if (publishFrame != null)
                {
                    var old = Interlocked.Exchange(ref _sharedFrame, (nint)publishFrame);
                    if (old != 0)
                    {
                        var oldFrame = (AVFrame*)old;
                        ffmpeg.av_frame_free(&oldFrame);
                    }

                    Interlocked.Exchange(ref _hasNewFrame, 1);
                    Interlocked.Increment(ref _decodedFrames);
                }
            }

            ffmpeg.av_frame_unref(frame);
        }
    }

    private void DropSharedFrame()
    {
        var stale = Interlocked.Exchange(ref _sharedFrame, 0);
        if (stale != 0)
        {
            var frame = (AVFrame*)stale;
            ffmpeg.av_frame_free(&frame);
        }

        Interlocked.Exchange(ref _hasNewFrame, 0);
    }

    private void SleepReconnect()
    {
        if (!_cts.IsCancellationRequested)
        {
            Thread.Sleep(3000);
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
            // Ignore cancellation / timeout on shutdown.
        }

        if (_consumedFrame != null)
        {
            var consumed = _consumedFrame;
            ffmpeg.av_frame_free(&consumed);
            _consumedFrame = null;
        }

        DropSharedFrame();
        if (_swsCtx != null)
        {
            ffmpeg.sws_freeContext(_swsCtx);
            _swsCtx = null;
        }
        _cts.Dispose();
    }
}
