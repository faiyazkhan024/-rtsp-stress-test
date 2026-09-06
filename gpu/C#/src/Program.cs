using System;
using Avalonia;

namespace RtspStressTest;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

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
            Console.WriteLine(" 6-Hour RTSP 30-Video Grid Benchmark (C# Avalonia GPU Decode) ");
            Console.WriteLine("===============================================================");

            FFmpegHelper.RaiseFileDescriptorLimit(Platform.NofileTarget);
            Platform.LogGpuPath();
            Console.WriteLine($"[Platform] OS: {Platform.Name}");

            var config = AppConfig.Load(args);
            Console.WriteLine($"[Config] Active Streams: {config.StreamCount}");
            Console.WriteLine($"[Config] RTSP URL:       {config.RtspUrl}");
            Console.WriteLine($"[Config] Machine ID:     {config.MachineId}");
            Console.WriteLine($"[Config] Telemetry Log:  {config.LogPath}");
            Console.WriteLine($"[Config] HwAccel:        {config.HwAccel}");

            FFmpegHelper.Initialize(config.FFmpegPath);

            var hwAccel = HwAccelManager.Create(config.HwAccel);
            Console.WriteLine($"[HwAccel] Active GPU Device: {hwAccel.DeviceName}");

            var telemetry = new TelemetryManager(config.LogPath, config.MachineId, config.StreamCount);
            App.Config = config;
            App.Telemetry = telemetry;
            App.HwAccel = hwAccel;

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Fatal Error] {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
