using ARGuidanceMAUI.Models;
using ARGuidanceMAUI.Services;
using ARGuidanceMAUI.Views;
using System.IO;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<ArCapturePage> _logger;
    private readonly DebugHudDrawable _hud = new();
    private readonly ArrowOverlayDrawable _arrowDrawable = new();
    private readonly FeaturePointsDrawable _featurePointsDrawable = new();
    private GraphicsView _hudGraphicsView;
    private GraphicsView _featurePointsGraphicsView;

    public ArCapturePage(IArPlatformService arService, ILogger<ArCapturePage> logger)
    {
        _ar = arService;
        _logger = logger;

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
            Text = "📷",
            FontAutoScalingEnabled = false
        };
        capture.Clicked += (_, __) => _ar.RequestCapture();

        var options = new Button { 
            Text = "☰",
            FontAutoScalingEnabled = false
        };
        options.Clicked += OnOptionsClicked;

        _ar.GuidanceUpdated += s =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _arrowDrawable.Hint = s.Hint;
                graphicsView.Invalidate();
            });
        };

#if ANDROID
        _ar.CaptureReady += async (pkg) =>
        {
            try
            {
                _logger.LogInformation("CaptureReady event fired. FileName: {FileName}, BytesLength: {Length}, ProjectFolder: {Folder}",
                    pkg.FileBaseName, pkg.JpegBytes?.Length ?? 0, _ar.CurrentProjectFolder);

                if (string.IsNullOrEmpty(_ar.CurrentProjectFolder))
                {
                    _logger.LogError("CurrentProjectFolder is empty!");
                    return;
                }

                await SaveImageToPicturesAsync(_logger, pkg.JpegBytes, $"{pkg.FileBaseName}.jpg", _ar.CurrentProjectFolder);

                _logger.LogInformation("SaveImageToPicturesAsync returned successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CaptureReady event handler");
            }
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
            Children = { options, capture }
        };

        overlay.Children.Add(buttonRow);
        Grid.SetRow(buttonRow, 2);

        options.HorizontalOptions = LayoutOptions.Start;
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
    public static async Task SaveImageToPicturesAsync(ILogger logger, byte[]? jpegBytes, string fileName, string projectFolder)
    {
        Stream? stream = null;
        try
        {
            logger?.LogInformation("SaveImageToPicturesAsync START: {FileName}", fileName);

            if (jpegBytes == null || jpegBytes.Length == 0)
            {
                logger?.LogWarning("SaveImageToPicturesAsync: jpegBytes is null or empty for {FileName}", fileName);
                return;
            }

            logger?.LogInformation("Image data OK: {FileName}, Size: {Size} bytes, ProjectFolder: {Folder}",
                fileName, jpegBytes.Length, projectFolder);

            var context = Android.App.Application.Context;
            if (context == null)
            {
                logger?.LogError("Android Context is null!");
                return;
            }

            if (context.ContentResolver == null)
            {
                logger?.LogError("ContentResolver is null!");
                return;
            }

            var values = new ContentValues();
            values.Put(MediaStore.Images.Media.InterfaceConsts.DisplayName, fileName);
            values.Put(MediaStore.Images.Media.InterfaceConsts.MimeType, "image/jpeg");

            var imagePath = projectFolder.Replace("Documents/", "Pictures/");
            values.Put(MediaStore.Images.Media.InterfaceConsts.RelativePath, imagePath);

            logger?.LogInformation("Inserting into MediaStore with path: {Path}", imagePath);

            var uri = context.ContentResolver.Insert(MediaStore.Images.Media.ExternalContentUri!, values);
            if (uri != null)
            {
                logger?.LogInformation("MediaStore URI created: {Uri}", uri);

                stream = context.ContentResolver.OpenOutputStream(uri);
                if (stream != null)
                {
                    logger?.LogInformation("Output stream opened, writing {Size} bytes...", jpegBytes.Length);

                    await stream.WriteAsync(jpegBytes, 0, jpegBytes.Length);
                    await stream.FlushAsync();

                    logger?.LogInformation("Image saved successfully: {FileName} at {Uri}", fileName, uri);
                }
                else
                {
                    logger?.LogWarning("Failed to open output stream for: {FileName}", fileName);
                }
            }
            else
            {
                logger?.LogWarning("Failed to insert into MediaStore for: {FileName}. Check permissions!", fileName);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error saving image: {FileName}", fileName);
        }
        finally
        {
            if (stream != null)
            {
                try
                {
                    await stream.DisposeAsync();
                    logger?.LogInformation("Stream disposed");
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error disposing stream");
                }
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

    private async void OnOptionsClicked(object? sender, EventArgs e)
    {
        try
        {
            _ar.Options();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open options");
        }
    }
}