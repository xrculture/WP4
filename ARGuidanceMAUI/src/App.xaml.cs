using Microsoft.Maui.Controls;

namespace ARGuidanceMAUI;

public class App : Application
{
    public App(Pages.ArCapturePage page)
    {
        MainPage = new NavigationPage(page);
    }
}