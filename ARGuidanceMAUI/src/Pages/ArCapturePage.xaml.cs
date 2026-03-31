using ARGuidanceMAUI.Models;
using ARGuidanceMAUI.Services;
using ARGuidanceMAUI.Views;
using System.IO;

#if ANDROID
using Android.Content;
using Android.Provider;
using Android.Graphics;
using Android.OS;
using Android.App;
#elif IOS
using Foundation;
using UIKit;
#endif

namespace ARGuidanceMAUI.Pages;

public class ArCapturePage : ContentPage
{
    private readonly IArPlatformService _ar;
    private readonly DebugHudDrawable _hud = new();
    private readonly ArrowOverlayDrawable _arrowDrawable = new();
    private readonly FeaturePointsDrawable _featurePointsDrawable = new();
    private GraphicsView _hudGraphicsView;
    private GraphicsView _featurePointsGraphicsView;

    public ArCapturePage(IArPlatformService arService)
    {
        _ar = arService;

        // HUD graphics view (transparent overlay)
        _hudGraphicsView = new GraphicsView
        {
            Drawable = _hud,
            BackgroundColor = Colors.Transparent,
            InputTransparent = true
        };

        // Feature points graphics view (transparent overlay)
        _featurePointsGraphicsView = new GraphicsView
        {
            Drawable = _featurePointsDrawable,
            BackgroundColor = Colors.Transparent,
            InputTransparent = true
        };

#if ANDROID
        if (_ar is ARGuidanceMAUI.Platforms.Android.ArCoreService droid)
        {
            droid.DebugUpdated += OnDebugUpdated;
        }
#elif IOS
        //#todo: Uncomment when iOS implementation supports debug telemetry
        //if (_ar is ARGuidanceMAUI.Platforms.iOS.ArKitService ios)
        //{
        //    ios.DebugUpdated += OnDebugUpdated;
        //}
#endif

        var arView = new NativeArView();

        var graphicsView = new GraphicsView
        {
            Drawable = _arrowDrawable,
            BackgroundColor = Colors.Transparent,
            InputTransparent = true
        };

        var capture = new Button { 
            Text = "Capture",
            FontAutoScalingEnabled = false
        };
        capture.Clicked += (_, __) => _ar.RequestCapture();

        var newProject = new Button { 
            Text = "New Project",
            FontAutoScalingEnabled = false
        };
        newProject.Clicked += OnNewProjectClicked;

        var projects = new Button { 
            Text = "Projects",
            FontAutoScalingEnabled = false
        };
        projects.Clicked += OnProjectsClicked;

        _ar.GuidanceUpdated += s =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _arrowDrawable.Hint = s.Hint;
                graphicsView.Invalidate();
            });
        };

#if ANDROID
        _ar.CaptureReady += async (pkg)  =>
        {
            await SaveImageToPicturesAsync(pkg.JpegBytes, $"{pkg.FileBaseName}.jpg", _ar.CurrentProjectFolder);
        };
#elif IOS
        _ar.CaptureReady += async (pkg) =>
        {
            await SaveImageToPhotosAsync(pkg.JpegBytes, $"{pkg.FileBaseName}.jpg", _ar.CurrentProjectFolder);
        };
#endif
        // Build overlay grid and place children via Grid attached properties
        var overlay = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            }
        };

        // Button row at the bottom
        var buttonRow = new StackLayout
        {
            Orientation = StackOrientation.Horizontal,
            Spacing = 12,
            Children = { newProject, projects, capture }
        };

        overlay.Children.Add(buttonRow);
        Grid.SetRow(buttonRow, 2);

        newProject.HorizontalOptions = LayoutOptions.Start;
        newProject.HorizontalOptions = LayoutOptions.Center;
        capture.HorizontalOptions = LayoutOptions.End;
        buttonRow.Margin = new Thickness(12);

        Content = new Grid
        {
            Children =
            {
                arView,                      // AR surface
                _featurePointsGraphicsView,  // feature points overlay
                graphicsView,                // arrow overlay
                _hudGraphicsView,            // HUD overlay
                overlay                      // text/buttons
            }
        };
    }

#if ANDROID
    // Save JPEG bytes to specific project folder using MediaStore
    public static async Task SaveImageToPicturesAsync(byte[] jpegBytes, string fileName, string projectFolder)
    {
        var context = Android.App.Application.Context;
        var values = new ContentValues();
        values.Put(MediaStore.Images.Media.InterfaceConsts.DisplayName, fileName);
        values.Put(MediaStore.Images.Media.InterfaceConsts.MimeType, "image/jpeg");
        
        // Convert Documents path to Pictures for images (Images.Media requires Pictures)
        var imagePath = projectFolder.Replace("Documents/", "Pictures/");
        values.Put(MediaStore.Images.Media.InterfaceConsts.RelativePath, imagePath);

        var uri = context.ContentResolver?.Insert(MediaStore.Images.Media.ExternalContentUri!, values);
        if (uri != null)
        {
            using var stream = context.ContentResolver?.OpenOutputStream(uri);
            if (stream != null)
            {
                await stream.WriteAsync(jpegBytes, 0, jpegBytes.Length);
            }
        }
    }
#elif IOS
    // Save JPEG bytes to Photos library for iOS
    public static async Task SaveImageToPhotosAsync(byte[] jpegBytes, string fileName, string projectFolder)
    {
        using var nsData = Foundation.NSData.FromArray(jpegBytes);
        var image = UIKit.UIImage.LoadFromData(nsData);
        
        if (image != null)
        {
            // For iOS, we save to Photos library as there's no direct folder access
            // The projectFolder is noted in the metadata but iOS Photos manages organization
            image.SaveToPhotosAlbum((img, error) =>
            {
                if (error != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Error saving image: {error.LocalizedDescription}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Image saved successfully: {fileName} (Project: {Path.GetFileName(projectFolder)})");
                }
            });
        }
        
        await Task.CompletedTask;
    }
#endif

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ar.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _ar.Stop();
    }

    private void OnDebugUpdated(ArDebugTelemetry t)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _hud.Update(t);
            _hudGraphicsView.Invalidate();
            
            _featurePointsDrawable.Update(t);
            _featurePointsGraphicsView.Invalidate();
        });
    }

    private async void OnNewProjectClicked(object? sender, EventArgs e)
    {
        try
        {
            _ar.NewProject();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to create a project: {ex.Message}", "OK");
        }
    }

    private async void OnProjectsClicked(object? sender, EventArgs e)
    {
        try
        {
            _ar.Projects();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed open Project UI: {ex.Message}", "OK");
        }
    }
}