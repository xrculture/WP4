using ARGuidanceMAUI.Pages;
using ARGuidanceMAUI.Services;
using ARGuidanceMAUI.Views;
using Microsoft.Extensions.Logging;
using Serilog;

namespace ARGuidanceMAUI;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder()
            .UseMauiApp<App>();

        var logsDir = Path.Combine(FileSystem.AppDataDirectory, "logs");

        try
        {
            Directory.CreateDirectory(logsDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create logs directory: {ex.Message}");
        }

        // Serilog
        var logPath = Path.Combine(logsDir, "log.txt");
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Log the path for debugging
        Log.Information("Log file path: {LogPath}", logPath);

        builder.Logging.AddSerilog(dispose: true);

#if DEBUG
        builder.Logging.AddDebug();
#endif

#if ANDROID
        builder.Services.AddSingleton<IArPlatformService, Platforms.Android.ArCoreService>();
        builder.ConfigureMauiHandlers(h =>
        {
            h.AddHandler<NativeArView, Platforms.Android.NativeArViewHandler>();
        });
#elif IOS
        builder.Services.AddSingleton<IArPlatformService, Platforms.iOS.ArKitService>();
        builder.ConfigureMauiHandlers(h =>
        {
            h.AddHandler<NativeArView, Platforms.iOS.NativeArViewHandler>();
        });
#endif

        builder.Services.AddSingleton<ArCapturePage>();
        return builder.Build();
    }
}