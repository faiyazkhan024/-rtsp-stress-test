using System;
using System.IO;

namespace RtspStressTest;

public sealed class AppConfig
{
    public string RtspUrl { get; set; } = "rtsp://127.0.0.1:8554/live";
    public string? RtspUrlPattern { get; set; }
    public int StreamCount { get; set; } = 30;
    public string LogPath { get; set; } = "/var/log/benchmark/fps_metrics.log";
    public string MachineId { get; set; } = Environment.MachineName;
    public string? FFmpegPath { get; set; }
    public int RenderWidth { get; set; } = 320; // 320x180 matches native on-screen tile dimensions in 6x5 grid
    public int RenderHeight { get; set; } = 180;

    public static AppConfig Load(string[] args)
    {
        var config = new AppConfig();

        // 1. Environment variables
        var envW = Environment.GetEnvironmentVariable("RENDER_WIDTH");
        if (!string.IsNullOrWhiteSpace(envW) && int.TryParse(envW, out var envRw) && envRw > 0)
        {
            config.RenderWidth = envRw;
        }

        var envH = Environment.GetEnvironmentVariable("RENDER_HEIGHT");
        if (!string.IsNullOrWhiteSpace(envH) && int.TryParse(envH, out var envRh) && envRh > 0)
        {
            config.RenderHeight = envRh;
        }

        var envUrl = Environment.GetEnvironmentVariable("RTSP_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            config.RtspUrl = envUrl;
        }

        var envPattern = Environment.GetEnvironmentVariable("RTSP_URL_PATTERN");
        if (!string.IsNullOrWhiteSpace(envPattern))
        {
            config.RtspUrlPattern = envPattern;
        }

        var envCount = Environment.GetEnvironmentVariable("STREAM_COUNT");
        if (!string.IsNullOrWhiteSpace(envCount) && int.TryParse(envCount, out var parsedCount) && parsedCount > 0)
        {
            config.StreamCount = parsedCount;
        }

        var envLogPath = Environment.GetEnvironmentVariable("FPS_METRICS_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envLogPath))
        {
            config.LogPath = envLogPath;
        }
        else
        {
            var envLogDir = Environment.GetEnvironmentVariable("BENCHMARK_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(envLogDir))
            {
                config.LogPath = Path.Combine(envLogDir, "fps_metrics.log");
            }
        }

        var envMachineId = Environment.GetEnvironmentVariable("MACHINE_ID");
        if (!string.IsNullOrWhiteSpace(envMachineId))
        {
            config.MachineId = envMachineId;
        }

        var envFfmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ??
                            Environment.GetEnvironmentVariable("FFMPEG_LIBRARIES_PATH");
        if (!string.IsNullOrWhiteSpace(envFfmpegPath))
        {
            config.FFmpegPath = envFfmpegPath;
        }

        // 2. Command-line arguments
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if ((arg == "--url" || arg == "-u") && i + 1 < args.Length)
            {
                config.RtspUrl = args[++i];
            }
            else if ((arg == "--streams" || arg == "-s") && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var count) && count > 0)
                {
                    config.StreamCount = count;
                }
            }
            else if ((arg == "--log-path" || arg == "-l") && i + 1 < args.Length)
            {
                config.LogPath = args[++i];
            }
            else if (arg == "--log-dir" && i + 1 < args.Length)
            {
                config.LogPath = Path.Combine(args[++i], "fps_metrics.log");
            }
            else if ((arg == "--machine-id" || arg == "-m") && i + 1 < args.Length)
            {
                config.MachineId = args[++i];
            }
            else if (arg == "--ffmpeg-path" && i + 1 < args.Length)
            {
                config.FFmpegPath = args[++i];
            }
            else if (arg == "--tile-mode")
            {
                config.RenderWidth = 640;
                config.RenderHeight = 360;
            }
            else if (arg == "--native-res")
            {
                config.RenderWidth = 0;
                config.RenderHeight = 0;
            }
            else if (arg == "--render-width" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var rw) && rw > 0) config.RenderWidth = rw;
            }
            else if (arg == "--render-height" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var rh) && rh > 0) config.RenderHeight = rh;
            }
            else if (arg == "--help" || arg == "-h")
            {
                PrintHelp();
                Environment.Exit(0);
            }
        }

        // 3. Resolve log path permissions with fallback
        config.LogPath = ResolveLogPath(config.LogPath);
        if (string.IsNullOrEmpty(config.RtspUrlPattern) && config.RtspUrl.Contains("%d"))
        {
            config.RtspUrlPattern = config.RtspUrl;
        }

        return config;
    }

    public string UrlForStream(int zeroBasedIndex)
    {
        if (string.IsNullOrEmpty(RtspUrlPattern))
        {
            return RtspUrl;
        }
        return RtspUrlPattern.Replace("%d", zeroBasedIndex.ToString());
    }

    private static string ResolveLogPath(string preferredPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(preferredPath);
            if (string.IsNullOrEmpty(dir))
            {
                dir = ".";
            }

            Directory.CreateDirectory(dir);
            var testFile = Path.Combine(dir, ".test_write_perm_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return preferredPath;
        }
        catch (Exception ex)
        {
            var fallbackDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(fallbackDir);
            var fallbackPath = Path.Combine(fallbackDir, Path.GetFileName(preferredPath));
            Console.WriteLine($"[Config] Warning: Cannot write to '{preferredPath}' ({ex.Message}). Falling back to '{fallbackPath}'");
            return fallbackPath;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("30-Camera RTSP Video Grid Benchmark (C# Avalonia CPU Software Decode)");
        Console.WriteLine("Usage: rtsp-stress-test-csharp-cpu [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --url, -u <url>         RTSP stream URL (default: rtsp://127.0.0.1:8554/live)");
        Console.WriteLine("  --streams, -s <count>   Number of streams in grid (default: 30)");
        Console.WriteLine("  --log-path, -l <path>   Telemetry log file path (default: /var/log/benchmark/fps_metrics.log)");
        Console.WriteLine("  --log-dir <dir>         Telemetry log directory");
        Console.WriteLine("  --machine-id, -m <id>   Machine/node identifier (default: hostname)");
        Console.WriteLine("  --ffmpeg-path <path>    Explicit path to FFmpeg native libraries");
        Console.WriteLine("  --help, -h              Show this help message");
    }
}
