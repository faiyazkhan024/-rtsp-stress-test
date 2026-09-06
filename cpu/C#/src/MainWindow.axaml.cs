using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace RtspStressTest;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly TelemetryManager _telemetry;
    private readonly List<StreamWorker> _workers = new();
    private readonly List<VideoTileControl> _tiles = new();
    private DispatcherTimer? _timer;
    private DispatcherTimer? _renderTimer;

    private TextBlock? _machineIdText;
    private TextBlock? _activeStreamsText;
    private TextBlock? _aggregateFpsText;
    private TextBlock? _windowTimeText;
    private TextBlock? _acceptableText;
    private TextBlock? _unacceptableText;
    private TextBlock? _logPathText;
    private UniformGrid? _videoGrid;

    public MainWindow() : this(new AppConfig(), new TelemetryManager("/var/log/benchmark/fps_metrics.log", "local", 30))
    {
    }

    public MainWindow(AppConfig config, TelemetryManager telemetry)
    {
        _config = config;
        _telemetry = telemetry;

        InitializeComponent();
        InitializeStreams();

        Closing += OnWindowClosing;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _machineIdText = this.FindControl<TextBlock>("MachineIdText");
        _activeStreamsText = this.FindControl<TextBlock>("ActiveStreamsText");
        _aggregateFpsText = this.FindControl<TextBlock>("AggregateFpsText");
        _windowTimeText = this.FindControl<TextBlock>("WindowTimeText");
        _acceptableText = this.FindControl<TextBlock>("AcceptableText");
        _unacceptableText = this.FindControl<TextBlock>("UnacceptableText");
        _logPathText = this.FindControl<TextBlock>("LogPathText");
        _videoGrid = this.FindControl<UniformGrid>("VideoGrid");

        if (_machineIdText != null) _machineIdText.Text = $"Node: {_config.MachineId}";
        if (_logPathText != null) _logPathText.Text = _config.LogPath;
    }

    private void InitializeStreams()
    {
        if (_videoGrid == null) return;

        _videoGrid.Children.Clear();

        // Calculate grid dimensions (e.g. 6 columns x 5 rows for 30 streams)
        var count = _config.StreamCount;
        var cols = 6;
        var rows = (int)Math.Ceiling((double)count / cols);
        _videoGrid.Columns = cols;
        _videoGrid.Rows = rows;

        for (var i = 1; i <= count; i++)
        {
            var worker = new StreamWorker(i, _config.UrlForStream(i - 1), _config.RenderWidth, _config.RenderHeight);
            _workers.Add(worker);

            var tile = new VideoTileControl();
            tile.BindWorker(worker);
            _tiles.Add(tile);

            _videoGrid.Children.Add(tile);
        }

        // Stagger thread startup by 25ms per stream to prevent TCP handshake stampedes
        Task.Run(async () =>
        {
            for (var i = 0; i < _workers.Count; i++)
            {
                _workers[i].Start();
                await Task.Delay(Platform.StreamStaggerMs);
            }
        });

        // 60 FPS Render Tick
        var renderInterval = 15;
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(renderInterval), DispatcherPriority.Normal, OnRenderTick);
        _renderTimer.Start();

        // 1-Second Telemetry and HUD Timer
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnTelemetryTick);
        _timer.Start();
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        for (var i = 0; i < _tiles.Count; i++)
        {
            _tiles[i].RequestRenderIfDirty();
        }
    }

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        _telemetry.Tick(_workers);

        if (_activeStreamsText != null)
        {
            _activeStreamsText.Text = $"{_telemetry.LiveStreams} / {_workers.Count}";
        }

        if (_aggregateFpsText != null)
        {
            _aggregateFpsText.Text = $"{_telemetry.AggregateFps:0.0} FPS";
        }

        if (_windowTimeText != null)
        {
            _windowTimeText.Text = $"{_telemetry.SecondsInWindow}s / 60s";
        }

        var buckets = _telemetry.GetCurrentBucketsSnapshot();

        if (_acceptableText != null)
        {
            _acceptableText.Text = $"25-30: {buckets.Fps25To30} | 20-24: {buckets.Fps20To24}";
        }

        if (_unacceptableText != null)
        {
            _unacceptableText.Text = $"Unacc: {buckets.TotalUnacceptable}";
        }

        for (var i = 0; i < _tiles.Count; i++)
        {
            _tiles[i].UpdateHud();
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _renderTimer?.Stop();
        _timer?.Stop();

        foreach (var worker in _workers)
        {
            worker.Dispose();
        }
    }
}
