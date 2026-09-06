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
    private readonly HwAccelManager _hwAccel;
    private readonly List<StreamWorker> _workers = new();
    private readonly List<VideoTileControl> _tiles = new();
    private DispatcherTimer? _telemetryTimer;
    private DispatcherTimer? _renderTimer;

    private TextBlock? _machineIdText;
    private TextBlock? _modeBadgeText;
    private TextBlock? _activeStreamsText;
    private TextBlock? _aggregateFpsText;
    private TextBlock? _windowTimeText;
    private TextBlock? _acceptableText;
    private TextBlock? _unacceptableText;
    private TextBlock? _logPathText;
    private UniformGrid? _videoGrid;

    public MainWindow() : this(new AppConfig(), new TelemetryManager("./logs/fps_metrics.log", "local", 30), HwAccelManager.Create("auto"))
    {
    }

    public MainWindow(AppConfig config, TelemetryManager telemetry, HwAccelManager hwAccel)
    {
        _config = config;
        _telemetry = telemetry;
        _hwAccel = hwAccel;

        InitializeComponent();
        InitializeStreams();
        Closing += OnWindowClosing;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _machineIdText = this.FindControl<TextBlock>("MachineIdText");
        _modeBadgeText = this.FindControl<TextBlock>("ModeBadgeText");
        _activeStreamsText = this.FindControl<TextBlock>("ActiveStreamsText");
        _aggregateFpsText = this.FindControl<TextBlock>("AggregateFpsText");
        _windowTimeText = this.FindControl<TextBlock>("WindowTimeText");
        _acceptableText = this.FindControl<TextBlock>("AcceptableText");
        _unacceptableText = this.FindControl<TextBlock>("UnacceptableText");
        _logPathText = this.FindControl<TextBlock>("LogPathText");
        _videoGrid = this.FindControl<UniformGrid>("VideoGrid");

        if (_machineIdText != null) _machineIdText.Text = $"Node: {_config.MachineId}";
        if (_logPathText != null) _logPathText.Text = _config.LogPath;
        if (_modeBadgeText != null)
        {
            _modeBadgeText.Text = $"C# Avalonia GPU ({_hwAccel.DeviceName.ToUpperInvariant()})";
        }
    }

    private void InitializeStreams()
    {
        if (_videoGrid == null) return;

        _videoGrid.Children.Clear();

        var count = _config.StreamCount;
        var cols = count <= 4 ? 2 : count <= 9 ? 3 : count <= 16 ? 4 : count <= 25 ? 5 : 6;
        var rows = (int)Math.Ceiling((double)count / cols);
        _videoGrid.Columns = cols;
        _videoGrid.Rows = rows;

        for (var i = 1; i <= count; i++)
        {
            var worker = new StreamWorker(i, _config.UrlForStream(i - 1), _hwAccel);
            _workers.Add(worker);

            var tile = new VideoTileControl();
            tile.BindWorker(worker);
            _tiles.Add(tile);
            _videoGrid.Children.Add(tile);
        }

        Task.Run(async () =>
        {
            for (var i = 0; i < _workers.Count; i++)
            {
                _workers[i].Start();
                await Task.Delay(Platform.StreamStaggerMs);
            }
        });

        var renderInterval = 15;
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(renderInterval), DispatcherPriority.Normal, OnRenderTick);
        _renderTimer.Start();

        _telemetryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnTelemetryTick);
        _telemetryTimer.Start();
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
        _telemetryTimer?.Stop();
        foreach (var worker in _workers)
        {
            worker.Dispose();
        }

        _hwAccel.Dispose();
    }
}
