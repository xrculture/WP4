using ARKit;
using Microsoft.Maui.Handlers;
using ARGuidanceMAUI.Views;
using ARGuidanceMAUI.Services;

namespace ARGuidanceMAUI.Platforms.iOS;

public class NativeArViewHandler : ViewHandler<NativeArView, ARSCNView>
{
    public static IPropertyMapper Mapper =
        new PropertyMapper<NativeArView, NativeArViewHandler>(ViewHandler.ViewMapper);

    public static CommandMapper<NativeArView, NativeArViewHandler> CommandMapper =
        new(ViewHandler.ViewCommandMapper);

    public NativeArViewHandler() : base(Mapper, CommandMapper) { }

    protected override ARSCNView CreatePlatformView()
    {
        var view = new ARSCNView();
        if (MauiContext?.Services.GetService(typeof(IArPlatformService)) is ArKitService ar)
            ar.AttachArView(view);
        return view;
    }
}