using System;
using Avalonia;

namespace RtspStressTest;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                TimeBeginPeriod(1);
            }

            Console.WriteLine("===============================================================");
            Console.WriteLine(" 6-Hour RTSP 30-Video Grid Benchmark (C# Avalonia CPU Decode) ");
            Console.WriteLine("===============================================================");

            FFmpegHelper.RaiseFileDescriptorLimit(Platform.NofileTarget);
            Platform.LogCpuPath();
            Console.WriteLine($"[Platform] OS: {Platform.Name}");

            // 2. Load configuration from CLI flags and environment variables
            var config = AppConfig.Load(args);
            Console.WriteLine($"[Config] Active Streams: {config.StreamCount}");
            Console.WriteLine($"[Config] RTSP URL:       {config.RtspUrl}");
            Console.WriteLine($"[Config] Machine ID:     {config.MachineId}");
            Console.WriteLine($"[Config] Telemetry Log:  {config.LogPath}");

            // 3. Initialize FFmpeg native libraries
            FFmpegHelper.Initialize(config.FFmpegPath);

            // 4. Initialize Telemetry Manager
            var telemetry = new TelemetryManager(config.LogPath, config.MachineId, config.StreamCount);
            App.Config = config;
            App.Telemetry = telemetry;

            // 5. Build and launch Avalonia application
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Fatal Error] {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
