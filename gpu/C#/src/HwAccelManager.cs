using System;
using FFmpeg.AutoGen.Abstractions;

namespace RtspStressTest;

public sealed unsafe class HwAccelManager : IDisposable
{
    private AVBufferRef* _hwDeviceCtx;
    private bool _disposed;

    public AVHWDeviceType HwDeviceType { get; private set; } = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    public AVPixelFormat HwPixFormat { get; private set; } = AVPixelFormat.AV_PIX_FMT_NONE;
    public string DeviceName { get; private set; } = "None";
    public bool IsInitialized => _hwDeviceCtx != null;
    public AVCodecContext_get_format GetFormatCallback { get; }

    public static HwAccelManager? Current { get; private set; }

    public HwAccelManager()
    {
        GetFormatCallback = GetHwFormat;
    }

    public static HwAccelManager Create(string typePreference = "auto")
    {
        var mgr = new HwAccelManager();
        Current = mgr;

        var pref = (typePreference ?? "auto").Trim().ToLowerInvariant();
        if (pref is "none" or "cpu")
        {
            return mgr;
        }

        var candidates = BuildCandidates(pref);
        foreach (var type in candidates)
        {
            if (mgr.InitDevice(type))
            {
                Console.WriteLine($"[HwAccel] Successfully initialized GPU hardware acceleration: {mgr.DeviceName}");
                return mgr;
            }
        }

        Console.Error.WriteLine("[HwAccel] Warning: No requested hardware acceleration device initialized. " +
                                "Falling back to GPU-shaded software frames.");
        return mgr;
    }

    private static AVHWDeviceType[] BuildCandidates(string pref)
    {
        return pref switch
        {
            "cuda" => [AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA],
            "vaapi" => [AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI],
            "videotoolbox" => [AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX],
            "d3d11va" => [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA],
            _ => OperatingSystem.IsMacOS()
                ? [AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX]
                : OperatingSystem.IsWindows()
                    ? [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA]
                    : [AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI]
        };
    }

    private bool InitDevice(AVHWDeviceType type)
    {
        var typeName = ffmpeg.av_hwdevice_get_type_name(type);
        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }

        AVBufferRef* ctx = null;
        var ret = ffmpeg.av_hwdevice_ctx_create(&ctx, type, null, null, 0);
        if (ret < 0)
        {
            Console.WriteLine($"[HwAccel] Could not initialize device type {typeName} ({FFmpegHelper.ErrorToString(ret)}). Trying next...");
            return false;
        }

        _hwDeviceCtx = ctx;
        HwDeviceType = type;
        DeviceName = typeName;
        HwPixFormat = type switch
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => AVPixelFormat.AV_PIX_FMT_CUDA,
            AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => AVPixelFormat.AV_PIX_FMT_VAAPI,
            AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX => AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX,
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => AVPixelFormat.AV_PIX_FMT_D3D11,
            _ => AVPixelFormat.AV_PIX_FMT_NONE
        };
        return true;
    }

    public AVBufferRef* CreateDeviceRef()
    {
        return _hwDeviceCtx == null ? null : ffmpeg.av_buffer_ref(_hwDeviceCtx);
    }

    public IntPtr CudaContextHandle()
    {
        if (!IsInitialized || HwDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)
        {
            return IntPtr.Zero;
        }

        var dev = (AVHWDeviceContext*)_hwDeviceCtx->data;
        if (dev == null || dev->hwctx == null)
        {
            return IntPtr.Zero;
        }

        var cuda = (AVCUDADeviceContext*)dev->hwctx;
        return cuda->cuda_ctx;
    }

    public IntPtr CudaStreamHandle()
    {
        if (!IsInitialized || HwDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)
        {
            return IntPtr.Zero;
        }

        var dev = (AVHWDeviceContext*)_hwDeviceCtx->data;
        if (dev == null || dev->hwctx == null)
        {
            return IntPtr.Zero;
        }

        var cuda = (AVCUDADeviceContext*)dev->hwctx;
        return cuda->stream;
    }

    private AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* pixFmts)
    {
        if (!IsInitialized)
        {
            return pixFmts[0];
        }

        var target = HwPixFormat;
        for (var p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == target)
            {
                return *p;
            }
        }

        Console.Error.WriteLine($"[HwAccel] Target hardware format {target} not found in codec formats list. Falling back.");
        return pixFmts[0];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_hwDeviceCtx != null)
        {
            var ctx = _hwDeviceCtx;
            ffmpeg.av_buffer_unref(&ctx);
            _hwDeviceCtx = null;
        }

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        _disposed = true;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct AVCUDADeviceContext
    {
        public IntPtr cuda_ctx;
        public IntPtr stream;
        public IntPtr @internal;
    }
}
