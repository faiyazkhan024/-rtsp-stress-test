using System;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using FFmpeg.AutoGen.Abstractions;
using static Avalonia.OpenGL.GlConsts;

namespace RtspStressTest;

public sealed unsafe class VideoGlControl : OpenGlControlBase
{
    private StreamWorker? _worker;
    private GlInterface? _gl;
    private GlExtras? _extras;
    private bool _coreProfile;

    private int _vao;
    private int _vbo;
    private int _progNv12;
    private int _progYuv;
    private int _locNv12Y;
    private int _locNv12Uv;
    private int _locYuvY;
    private int _locYuvU;
    private int _locYuvV;

    private int _texY;
    private int _texU;
    private int _texV;
    private int _texUv;
    private int _texWidth;
    private int _texHeight;

    private IntPtr _cudaResY;
    private IntPtr _cudaResUv;
    private bool _cudaRegistered;

    public void AttachWorker(StreamWorker worker)
    {
        _worker = worker;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = gl;
        _extras = new GlExtras(gl);
        _coreProfile = GlVersion.Type == GlProfileType.OpenGL && GlVersion.Major >= 3;

        float[] vertices =
        [
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
             1f,  1f, 1f, 0f,
            -1f, -1f, 0f, 1f,
             1f,  1f, 1f, 0f,
            -1f,  1f, 0f, 0f
        ];

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        fixed (float* p = vertices)
        {
            gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(vertices.Length * sizeof(float)), (IntPtr)p, GL_STATIC_DRAW);
        }

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 4 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GL_FLOAT, 0, 4 * sizeof(float), new IntPtr(2 * sizeof(float)));
        gl.BindVertexArray(0);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);

        _progNv12 = VideoShaders.CompileProgram(gl, VideoShaders.Vertex(_coreProfile), VideoShaders.Nv12(_coreProfile));
        _progYuv = VideoShaders.CompileProgram(gl, VideoShaders.Vertex(_coreProfile), VideoShaders.Yuv420p(_coreProfile));
        _locNv12Y = gl.GetUniformLocationString(_progNv12, "texY");
        _locNv12Uv = gl.GetUniformLocationString(_progNv12, "texUV");
        _locYuvY = gl.GetUniformLocationString(_progYuv, "texY");
        _locYuvU = gl.GetUniformLocationString(_progYuv, "texU");
        _locYuvV = gl.GetUniformLocationString(_progYuv, "texV");

        _texY = CreateTexture(gl);
        _texU = CreateTexture(gl);
        _texV = CreateTexture(gl);
        _texUv = CreateTexture(gl);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        UnregisterCuda();
        if (_texY != 0) gl.DeleteTexture(_texY);
        if (_texU != 0) gl.DeleteTexture(_texU);
        if (_texV != 0) gl.DeleteTexture(_texV);
        if (_texUv != 0) gl.DeleteTexture(_texUv);
        if (_progNv12 != 0) gl.DeleteProgram(_progNv12);
        if (_progYuv != 0) gl.DeleteProgram(_progYuv);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        _texY = _texU = _texV = _texUv = 0;
        _progNv12 = _progYuv = 0;
        _vbo = _vao = 0;
        _gl = null;
        _extras = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        gl.BindFramebuffer(GL_FRAMEBUFFER, fb);

        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var pw = Math.Max(1, (int)(Bounds.Width * scaling));
        var ph = Math.Max(1, (int)(Bounds.Height * scaling));
        gl.Viewport(0, 0, pw, ph);
        gl.ClearColor(0.05f, 0.07f, 0.09f, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT);

        if (_worker == null || _extras == null)
        {
            return;
        }

        var frame = _worker.AcquireFrame(out var isNew);
        if (frame == null || frame->width <= 0 || frame->height <= 0)
        {
            return;
        }

        RenderFrame(gl, _extras, frame, isNew);
    }

    public void RequestRenderIfDirty()
    {
        if (_worker is { HasNewFrame: true })
        {
            RequestNextFrameRendering();
        }
    }

    private void RenderFrame(GlInterface gl, GlExtras extras, AVFrame* frame, bool upload)
    {
        var w = frame->width;
        var h = frame->height;
        var needRealloc = _texWidth != w || _texHeight != h;
        if (needRealloc)
        {
            UnregisterCuda();
            _texWidth = w;
            _texHeight = h;
        }

        var format = (AVPixelFormat)frame->format;

        if (OperatingSystem.IsMacOS() &&
            format == AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX &&
            frame->data[3] != null)
        {
            EnsureNv12Storage(gl, w, h, needRealloc);
            UploadVideoToolbox(gl, extras, frame, upload || needRealloc);
            DrawNv12(gl, extras);
            return;
        }

        if (format == AVPixelFormat.AV_PIX_FMT_CUDA)
        {
            if (UploadCuda(gl, extras, frame, upload || needRealloc, needRealloc))
            {
                DrawNv12(gl, extras);
                return;
            }
            if (UploadMappedHwFrame(gl, extras, frame, upload || needRealloc, needRealloc))
            {
                return;
            }
        }

        if (format is AVPixelFormat.AV_PIX_FMT_VAAPI or AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            if (UploadMappedHwFrame(gl, extras, frame, upload || needRealloc, needRealloc))
            {
                return;
            }
        }

        if (format is AVPixelFormat.AV_PIX_FMT_CUDA or AVPixelFormat.AV_PIX_FMT_VAAPI or AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            return;
        }

        if (format == AVPixelFormat.AV_PIX_FMT_NV12 || (frame->data[0] != null && frame->data[1] != null && frame->data[2] == null))
        {
            EnsureNv12Storage(gl, w, h, needRealloc);
            UploadNv12(gl, extras, frame->data[0], frame->linesize[0], frame->data[1], frame->linesize[1], w, h, upload || needRealloc);
            DrawNv12(gl, extras);
            return;
        }

        if (format == AVPixelFormat.AV_PIX_FMT_YUV420P || (frame->data[0] != null && frame->data[1] != null && frame->data[2] != null))
        {
            EnsureYuv420pStorage(gl, w, h, needRealloc);
            UploadYuv420p(gl, extras, frame, upload || needRealloc);
            DrawYuv420p(gl, extras);
        }
    }

    private void UploadVideoToolbox(GlInterface gl, GlExtras extras, AVFrame* frame, bool upload)
    {
        var pixbuf = (IntPtr)frame->data[3];
        if (CoreVideoInterop.CVPixelBufferLockBaseAddress(pixbuf, CoreVideoInterop.LockReadOnly) != 0)
        {
            return;
        }

        var yPlane = CoreVideoInterop.CVPixelBufferGetBaseAddressOfPlane(pixbuf, 0);
        var uvPlane = CoreVideoInterop.CVPixelBufferGetBaseAddressOfPlane(pixbuf, 1);
        var yStride = (int)CoreVideoInterop.CVPixelBufferGetBytesPerRowOfPlane(pixbuf, 0);
        var uvStride = (int)CoreVideoInterop.CVPixelBufferGetBytesPerRowOfPlane(pixbuf, 1);
        UploadNv12(gl, extras, (byte*)yPlane, yStride, (byte*)uvPlane, uvStride, frame->width, frame->height, upload);
        CoreVideoInterop.CVPixelBufferUnlockBaseAddress(pixbuf, CoreVideoInterop.LockReadOnly);
    }

    private bool UploadCuda(GlInterface gl, GlExtras extras, AVFrame* frame, bool upload, bool realloc)
    {
        if (!CudaGlInterop.Available || App.HwAccel == null)
        {
            return false;
        }

        var ctx = App.HwAccel.CudaContextHandle();
        if (ctx == IntPtr.Zero)
        {
            return false;
        }

        EnsureNv12Storage(gl, frame->width, frame->height, realloc);

        try
        {
            if (CudaGlInterop.cuCtxPushCurrent(ctx) != CudaGlInterop.Success)
            {
                return false;
            }

            if (!_cudaRegistered)
            {
                if (CudaGlInterop.cuGraphicsGLRegisterImage(out _cudaResY, (uint)_texY, CudaGlInterop.Texture2D, CudaGlInterop.RegisterFlagsNone) != CudaGlInterop.Success ||
                    CudaGlInterop.cuGraphicsGLRegisterImage(out _cudaResUv, (uint)_texUv, CudaGlInterop.Texture2D, CudaGlInterop.RegisterFlagsNone) != CudaGlInterop.Success)
                {
                    UnregisterCuda();
                    CudaGlInterop.cuCtxPopCurrent(out _);
                    return false;
                }

                _cudaRegistered = true;
            }

            if (!upload)
            {
                CudaGlInterop.cuCtxPopCurrent(out _);
                return true;
            }

            var stream = App.HwAccel.CudaStreamHandle();
            var resY = _cudaResY;
            var resUv = _cudaResUv;
            if (CudaGlInterop.cuGraphicsMapResources(1, ref resY, stream) != CudaGlInterop.Success ||
                CudaGlInterop.cuGraphicsMapResources(1, ref resUv, stream) != CudaGlInterop.Success)
            {
                CudaGlInterop.cuCtxPopCurrent(out _);
                return false;
            }

            if (CudaGlInterop.cuGraphicsSubResourceGetMappedArray(out var arrayY, resY, 0, 0) == CudaGlInterop.Success)
            {
                CopyDeviceToArray(frame->data[0], frame->linesize[0], arrayY, (nuint)frame->width, (nuint)frame->height);
            }

            if (CudaGlInterop.cuGraphicsSubResourceGetMappedArray(out var arrayUv, resUv, 0, 0) == CudaGlInterop.Success)
            {
                CopyDeviceToArray(frame->data[1], frame->linesize[1], arrayUv, (nuint)frame->width, (nuint)(frame->height / 2));
            }

            CudaGlInterop.cuGraphicsUnmapResources(1, ref resY, stream);
            CudaGlInterop.cuGraphicsUnmapResources(1, ref resUv, stream);
            CudaGlInterop.cuCtxPopCurrent(out _);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static void CopyDeviceToArray(byte* src, int pitch, IntPtr dstArray, nuint widthBytes, nuint height)
    {
        var copy = new CudaGlInterop.CUDA_MEMCPY2D
        {
            srcMemoryType = CudaGlInterop.MemoryTypeDevice,
            srcDevice = (ulong)src,
            srcPitch = (nuint)pitch,
            dstMemoryType = CudaGlInterop.MemoryTypeArray,
            dstArray = dstArray,
            WidthInBytes = widthBytes,
            Height = height
        };
        CudaGlInterop.cuMemcpy2D(ref copy);
    }

    private bool UploadMappedHwFrame(GlInterface gl, GlExtras extras, AVFrame* frame, bool upload, bool needRealloc)
    {
        var mapped = ffmpeg.av_frame_alloc();
        if (mapped == null)
        {
            return false;
        }

        var flags = (int)AvHwframeMap.AV_HWFRAME_MAP_READ;
        var ok = ffmpeg.av_hwframe_map(mapped, frame, flags) == 0;
        if (!ok)
        {
            ok = ffmpeg.av_hwframe_transfer_data(mapped, frame, 0) == 0;
        }
        if (ok)
        {
            var mappedFmt = (AVPixelFormat)mapped->format;
            if (mappedFmt == AVPixelFormat.AV_PIX_FMT_NV12 || mapped->data[1] != null)
            {
                EnsureNv12Storage(gl, mapped->width, mapped->height, needRealloc);
                UploadNv12(gl, extras, mapped->data[0], mapped->linesize[0], mapped->data[1], mapped->linesize[1], mapped->width, mapped->height, upload);
                DrawNv12(gl, extras);
            }
            else if (mappedFmt == AVPixelFormat.AV_PIX_FMT_YUV420P || mapped->data[2] != null)
            {
                EnsureYuv420pStorage(gl, mapped->width, mapped->height, needRealloc);
                UploadYuv420p(gl, extras, mapped, upload);
                DrawYuv420p(gl, extras);
            }

            ffmpeg.av_frame_unref(mapped);
        }

        ffmpeg.av_frame_free(&mapped);
        return ok;
    }

    private void EnsureNv12Storage(GlInterface gl, int w, int h, bool realloc)
    {
        if (!realloc && _texWidth == w && _texHeight == h)
        {
            return;
        }

        gl.BindTexture(GL_TEXTURE_2D, _texY);
        gl.TexImage2D(GL_TEXTURE_2D, 0, GlExtras.GL_R8, w, h, 0, GlExtras.GL_RED, GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.BindTexture(GL_TEXTURE_2D, _texUv);
        gl.TexImage2D(GL_TEXTURE_2D, 0, GlExtras.GL_RG8, w / 2, h / 2, 0, GlExtras.GL_RG, GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.BindTexture(GL_TEXTURE_2D, 0);
    }

    private void EnsureYuv420pStorage(GlInterface gl, int w, int h, bool realloc)
    {
        if (!realloc && _texWidth == w && _texHeight == h)
        {
            return;
        }

        gl.BindTexture(GL_TEXTURE_2D, _texY);
        gl.TexImage2D(GL_TEXTURE_2D, 0, GlExtras.GL_R8, w, h, 0, GlExtras.GL_RED, GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.BindTexture(GL_TEXTURE_2D, _texU);
        gl.TexImage2D(GL_TEXTURE_2D, 0, GlExtras.GL_R8, w / 2, h / 2, 0, GlExtras.GL_RED, GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.BindTexture(GL_TEXTURE_2D, _texV);
        gl.TexImage2D(GL_TEXTURE_2D, 0, GlExtras.GL_R8, w / 2, h / 2, 0, GlExtras.GL_RED, GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.BindTexture(GL_TEXTURE_2D, 0);
    }

    private void UploadNv12(GlInterface gl, GlExtras extras, byte* y, int yStride, byte* uv, int uvStride, int w, int h, bool upload)
    {
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_2D, _texY);
        if (upload)
        {
            extras.PixelStorei(GlExtras.GL_UNPACK_ALIGNMENT, 1);
            extras.PixelStorei(GlExtras.GL_UNPACK_ROW_LENGTH, yStride);
            extras.TexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GlExtras.GL_RED, GL_UNSIGNED_BYTE, y);
        }

        gl.ActiveTexture(GL_TEXTURE0 + 1);
        gl.BindTexture(GL_TEXTURE_2D, _texUv);
        if (upload)
        {
            extras.PixelStorei(GlExtras.GL_UNPACK_ROW_LENGTH, uvStride / 2);
            extras.TexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w / 2, h / 2, GlExtras.GL_RG, GL_UNSIGNED_BYTE, uv);
            extras.PixelStorei(GlExtras.GL_UNPACK_ALIGNMENT, 4);
            extras.PixelStorei(GlExtras.GL_UNPACK_ROW_LENGTH, 0);
        }
    }

    private void UploadYuv420p(GlInterface gl, GlExtras extras, AVFrame* frame, bool upload)
    {
        var w = frame->width;
        var h = frame->height;
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_2D, _texY);
        if (upload)
        {
            extras.PixelStorei(GlExtras.GL_UNPACK_ALIGNMENT, 1);
            extras.PixelStorei(GlExtras.GL_UNPACK_ROW_LENGTH, frame->linesize[0]);
            extras.TexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GlExtras.GL_RED, GL_UNSIGNED_BYTE, frame->data[0]);
        }

        gl.ActiveTexture(GL_TEXTURE0 + 1);
        gl.BindTexture(GL_TEXTURE_2D, _texU);
        if (upload)
        {
            extras.PixelStorei(GlExtras.GL_UNPACK_ROW_LENGTH, frame->linesize[1]);
            extras.TexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w / 2, h / 2, GlExtras.GL_RED, GL_UNSIGNED_BYTE, frame->data[1]);
        }

        gl.ActiveTexture(GL_TEXTURE0 + 2);
        gl.BindTexture(GL_TEXTURE_2D, _texV);
        if (upload)
        {
            extras.PixelStorei(GlExtras.GL_UNPACK_ROW_LENGTH, frame->linesize[2]);
            extras.TexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w / 2, h / 2, GlExtras.GL_RED, GL_UNSIGNED_BYTE, frame->data[2]);
            extras.PixelStorei(GlExtras.GL_UNPACK_ALIGNMENT, 4);
            extras.PixelStorei(GlExtras.GL_UNPACK_ROW_LENGTH, 0);
        }
    }

    private void DrawNv12(GlInterface gl, GlExtras extras)
    {
        gl.UseProgram(_progNv12);
        extras.Uniform1i(_locNv12Y, 0);
        extras.Uniform1i(_locNv12Uv, 1);
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_2D, _texY);
        gl.ActiveTexture(GL_TEXTURE0 + 1);
        gl.BindTexture(GL_TEXTURE_2D, _texUv);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(GL_TRIANGLES, 0, new IntPtr(6));
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    private void DrawYuv420p(GlInterface gl, GlExtras extras)
    {
        gl.UseProgram(_progYuv);
        extras.Uniform1i(_locYuvY, 0);
        extras.Uniform1i(_locYuvU, 1);
        extras.Uniform1i(_locYuvV, 2);
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_2D, _texY);
        gl.ActiveTexture(GL_TEXTURE0 + 1);
        gl.BindTexture(GL_TEXTURE_2D, _texU);
        gl.ActiveTexture(GL_TEXTURE0 + 2);
        gl.BindTexture(GL_TEXTURE_2D, _texV);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(GL_TRIANGLES, 0, new IntPtr(6));
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    private void UnregisterCuda()
    {
        if (!_cudaRegistered)
        {
            return;
        }

        try
        {
            var ctx = App.HwAccel?.CudaContextHandle() ?? IntPtr.Zero;
            if (ctx != IntPtr.Zero)
            {
                CudaGlInterop.cuCtxPushCurrent(ctx);
            }

            if (_cudaResY != IntPtr.Zero)
            {
                CudaGlInterop.cuGraphicsUnregisterResource(_cudaResY);
            }

            if (_cudaResUv != IntPtr.Zero)
            {
                CudaGlInterop.cuGraphicsUnregisterResource(_cudaResUv);
            }

            if (ctx != IntPtr.Zero)
            {
                CudaGlInterop.cuCtxPopCurrent(out _);
            }
        }
        catch
        {
            // CUDA library may be absent on this host.
        }

        _cudaResY = IntPtr.Zero;
        _cudaResUv = IntPtr.Zero;
        _cudaRegistered = false;
    }

    private static int CreateTexture(GlInterface gl)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, tex);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GlExtras.GL_TEXTURE_WRAP_S, GlExtras.GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GL_TEXTURE_2D, GlExtras.GL_TEXTURE_WRAP_T, GlExtras.GL_CLAMP_TO_EDGE);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return tex;
    }
}
