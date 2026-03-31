using ARGuidanceMAUI.Pages;
using ARGuidanceMAUI.Services;
using ARGuidanceMAUI.Views;

namespace ARGuidanceMAUI;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder()
            .UseMauiApp<App>();

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