using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace RtspStressTest;

public class VideoImageControl : Control
{
    private StreamWorker? _worker;
    private long _lastPresentedPts = -1;

    public void AttachWorker(StreamWorker worker)
    {
        _worker = worker;
        _lastPresentedPts = -1;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_worker?.Bitmap is { } bmp)
        {
            var bounds = Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                var srcSize = bmp.Size;
                if (srcSize.Width > 0 && srcSize.Height > 0)
                {
                    var scale = Math.Min(bounds.Width / srcSize.Width, bounds.Height / srcSize.Height);
                    var w = srcSize.Width * scale;
                    var h = srcSize.Height * scale;
                    var x = (bounds.Width - w) / 2;
                    var y = (bounds.Height - h) / 2;

                    context.DrawImage(bmp, new Rect(x, y, w, h));
                    _worker.ResetNewFrame();

                    // Effective FPS Standard: Only increment if new Presentation Timestamp (PTS)
                    var curPts = _worker.CurrentPts;
                    if (curPts != _lastPresentedPts && curPts >= 0)
                    {
                        _lastPresentedPts = curPts;
                        _worker.RecordPresentedFrame(curPts);
                    }
                }
            }
        }
    }
}

public partial class VideoTileControl : UserControl
{
    private StreamWorker? _worker;
    private VideoImageControl? _videoImage;
    private TextBlock? _connectingText;
    private TextBlock? _cameraIdText;
    private Ellipse? _statusDot;
    private TextBlock? _resolutionText;
    private TextBlock? _fpsText;
    private Border? _fpsBadge;

    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#2ecc71"));
    private static readonly IBrush AmberBrush = new SolidColorBrush(Color.Parse("#f39c12"));
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#e74c3c"));

    private static readonly IBrush GreenBadgeBg = new SolidColorBrush(Color.Parse("#223822"));
    private static readonly IBrush AmberBadgeBg = new SolidColorBrush(Color.Parse("#383218"));
    private static readonly IBrush RedBadgeBg = new SolidColorBrush(Color.Parse("#381e1e"));

    public VideoTileControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _videoImage = this.FindControl<VideoImageControl>("VideoImage");
        _connectingText = this.FindControl<TextBlock>("ConnectingText");
        _cameraIdText = this.FindControl<TextBlock>("CameraIdText");
        _statusDot = this.FindControl<Ellipse>("StatusDot");
        _resolutionText = this.FindControl<TextBlock>("ResolutionText");
        _fpsText = this.FindControl<TextBlock>("FpsText");
        _fpsBadge = this.FindControl<Border>("FpsBadge");
    }

    public void BindWorker(StreamWorker worker)
    {
        _worker = worker;
        if (_cameraIdText != null)
        {
            _cameraIdText.Text = $"CAM {worker.StreamId:D2}";
        }

        _videoImage?.AttachWorker(worker);
    }

    public void RequestRenderIfDirty()
    {
        if (_worker != null && _worker.HasNewFrame)
        {
            _videoImage?.InvalidateVisual();

            if (_connectingText != null && _connectingText.IsVisible && (_worker.PaintedFrames > 0 || _worker.DecodedFrames > 0))
            {
                _connectingText.IsVisible = false;
            }
        }
    }

    public void UpdateHud()
    {
        if (_worker == null) return;

        var isConnected = _worker.IsConnected;
        var pFps = _worker.CurrentPaintedFps;
        var dFps = _worker.CurrentDecodedFps;
        var fps = _worker.CurrentFps;

        if (_statusDot != null)
        {
            _statusDot.Fill = isConnected ? GreenBrush : RedBrush;
        }

        if (_resolutionText != null)
        {
            if (isConnected && _worker.Width > 0 && _worker.Height > 0)
            {
                _resolutionText.Text = $"{_worker.Width}x{_worker.Height}";
            }
            else
            {
                _resolutionText.Text = "Offline";
            }
        }

        if (_fpsText != null)
        {
            if (pFps < dFps && dFps > 0 && pFps > 0)
            {
                _fpsText.Text = $"{pFps:0} FPS ({dFps:0} dec)";
            }
            else
            {
                _fpsText.Text = $"{fps:0.0} FPS";
            }

            if (fps >= 25)
            {
                _fpsText.Foreground = GreenBrush;
                if (_fpsBadge != null) _fpsBadge.Background = GreenBadgeBg;
            }
            else if (fps >= 20)
            {
                _fpsText.Foreground = AmberBrush;
                if (_fpsBadge != null) _fpsBadge.Background = AmberBadgeBg;
            }
            else
            {
                _fpsText.Foreground = RedBrush;
                if (_fpsBadge != null) _fpsBadge.Background = RedBadgeBg;
            }
        }

        if (_connectingText != null)
        {
            if (!isConnected && _worker.PaintedFrames == 0 && _worker.DecodedFrames == 0)
            {
                _connectingText.Text = "CONNECTING...";
                _connectingText.IsVisible = true;
            }
            else if (!isConnected)
            {
                _connectingText.Text = "RECONNECTING...";
                _connectingText.IsVisible = true;
            }
            else
            {
                _connectingText.IsVisible = false;
            }
        }
    }
}
