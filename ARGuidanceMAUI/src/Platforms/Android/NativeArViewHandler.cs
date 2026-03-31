using Android.Opengl;
using Microsoft.Maui.Handlers;
using ARGuidanceMAUI.Views;
using ARGuidanceMAUI.Services;

namespace ARGuidanceMAUI.Platforms.Android;

public class NativeArViewHandler : ViewHandler<NativeArView, GLSurfaceView>
{
    // Required mappers for ViewHandler base constructor
    public static IPropertyMapper Mapper =
        new PropertyMapper<NativeArView, NativeArViewHandler>(ViewHandler.ViewMapper);

    public static CommandMapper<NativeArView, NativeArViewHandler> CommandMapper =
        new(ViewHandler.ViewCommandMapper);

    public NativeArViewHandler() : base(Mapper, CommandMapper) { }

    protected override GLSurfaceView CreatePlatformView()
    {
        var glView = new GLSurfaceView(Context);
        glView.PreserveEGLContextOnPause = true;
        glView.SetEGLContextClientVersion(2);

        if (MauiContext?.Services.GetService(typeof(IArPlatformService)) is ArCoreService ar)
            ar.AttachGlView(glView);

        return glView;
    }
}