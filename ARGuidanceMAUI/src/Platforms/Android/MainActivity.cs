using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Google.AR.Core;

namespace ARGuidanceMAUI.Platforms.Android
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        private static bool _isMediaTekDevice = false;
        private static bool _isCameraInUse = false;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Detect MediaTek devices
            _isMediaTekDevice = IsMediaTekDevice();
            if (_isMediaTekDevice)
            {
                System.Diagnostics.Debug.WriteLine("⚠️ MediaTek device detected - applying camera workarounds");
            }

            // Check ARCore availability BEFORE any camera operations
            CheckArCoreAvailability();

            // Reduce screen brightness to minimize heat generation
            var layoutParams = Window?.Attributes;
            if (layoutParams != null)
            {
                layoutParams.ScreenBrightness = 0.4f; // 40% brightness (range: 0.0 to 1.0)
                Window?.Attributes = layoutParams;
            }

            // Keep screen on only during active use (not when app is in background)
            Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);

            // Request battery optimization exemption
            RequestBatteryOptimizationExemption();

            StartForegroundService();
        }

        private bool IsMediaTekDevice()
        {
            try
            {
                var hardware = Build.Hardware?.ToLower() ?? "";
                var board = Build.Board?.ToLower() ?? "";
                var soc = Build.SocManufacturer?.ToLower() ?? "";

                return hardware.Contains("mt") ||
                       hardware.Contains("mediatek") ||
                       board.Contains("mt") ||
                       soc.Contains("mt") ||
                       soc.Contains("mediatek");
            }
            catch
            {
                return false;
            }
        }

        private void CheckArCoreAvailability()
        {
            try
            {
                var availability = ArCoreApk.Instance.CheckAvailability(this);

                if (availability.IsTransient)
                {
                    System.Diagnostics.Debug.WriteLine("ARCore availability is transient, waiting...");
                    // Wait and check again
                    new Handler(Looper.MainLooper!).PostDelayed(() =>
                    {
                        CheckArCoreAvailability();
                    }, 200);
                    return;
                }

                if (availability != ArCoreApk.Availability.SupportedInstalled)
                {
                    System.Diagnostics.Debug.WriteLine($"ARCore not available: {availability}");

                    // Show error to user
                    RunOnUiThread(() =>
                    {
                        var builder = new AlertDialog.Builder(this);
                        builder.SetTitle("ARCore Required");
                        builder.SetMessage($"This app requires ARCore, but it's not available on this device.\nStatus: {availability}");
                        builder.SetPositiveButton("OK", (s, e) => Finish());
                        builder.Show();
                    });
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("✓ ARCore is available and installed");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking ARCore: {ex.Message}");
            }
        }

        protected override void OnPause()
        {
            System.Diagnostics.Debug.WriteLine("MainActivity.OnPause");

            // Mark camera as not in use
            _isCameraInUse = false;

            // For MediaTek devices, give extra time for camera to release
            if (_isMediaTekDevice)
            {
                System.Threading.Thread.Sleep(150); // Small delay to help camera release
            }

            base.OnPause();

            // Allow screen to turn off when app is paused to save battery/reduce heat
            Window?.ClearFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
        }

        protected override void OnResume()
        {
            System.Diagnostics.Debug.WriteLine("MainActivity.OnResume");

            base.OnResume();

            // Re-enable when app resumes
            Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);

            // For MediaTek devices, delay camera initialization
            if (_isMediaTekDevice)
            {
                new Handler(Looper.MainLooper!).PostDelayed(() =>
                {
                    _isCameraInUse = true;
                    System.Diagnostics.Debug.WriteLine("Camera ready for use");
                }, 300); // Delay camera access
            }
            else
            {
                _isCameraInUse = true;
            }
        }

        protected override void OnStop()
        {
            System.Diagnostics.Debug.WriteLine("MainActivity.OnStop");

            // Ensure camera is released before stopping
            _isCameraInUse = false;

            if (_isMediaTekDevice)
            {
                System.Threading.Thread.Sleep(200); // Extra time for cleanup
            }

            base.OnStop();
        }

        private void RequestMIUIPermissions()
        {
            try
            {
                var manufacturer = global::Android.OS.Build.Manufacturer?.ToLower() ?? "";

                if (manufacturer.Contains("xiaomi") || manufacturer.Contains("redmi"))
                {
                    System.Diagnostics.Debug.WriteLine("Detected Xiaomi/Redmi device, requesting MIUI permissions");

                    // Autostart permission
                    try
                    {
                        var autoStartIntent = new Intent();
                        autoStartIntent.SetComponent(new ComponentName(
                            "com.miui.securitycenter",
                            "com.miui.permcenter.autostart.AutoStartManagementActivity"));
                        StartActivity(autoStartIntent);
                    }
                    catch
                    {
                        System.Diagnostics.Debug.WriteLine("MIUI Autostart settings not available");
                    }

                    // Battery saver whitelist (more aggressive for MIUI)
                    try
                    {
                        var batteryIntent = new Intent("miui.intent.action.APP_PERM_EDITOR");
                        batteryIntent.SetClassName("com.miui.securitycenter",
                            "com.miui.permcenter.permissions.PermissionsEditorActivity");
                        batteryIntent.PutExtra("extra_pkgname", PackageName);
                        StartActivity(batteryIntent);
                    }
                    catch
                    {
                        System.Diagnostics.Debug.WriteLine("MIUI Battery settings not available");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error requesting MIUI permissions: {ex.Message}");
            }
        }

        private void RequestBatteryOptimizationExemption()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                {
                    var powerManager = GetSystemService(PowerService) as PowerManager;
                    var packageName = PackageName;

                    if (powerManager != null && !powerManager.IsIgnoringBatteryOptimizations(packageName))
                    {
                        var intent = new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
                        intent.SetData(global::Android.Net.Uri.Parse($"package:{packageName}"));
                        StartActivity(intent);

                        System.Diagnostics.Debug.WriteLine("Requesting battery optimization exemption");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error requesting battery exemption: {ex.Message}");
            }
        }

        private void StartForegroundService()
        {
            try
            {
                var serviceIntent = new Intent(this, typeof(ArCaptureService));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    StartForegroundService(serviceIntent);
                }
                else
                {
                    StartService(serviceIntent);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting foreground service: {ex.Message}");
            }
        }

        protected override void OnDestroy()
        {
            System.Diagnostics.Debug.WriteLine("MainActivity.OnDestroy");

            _isCameraInUse = false;

            try
            {
                var serviceIntent = new Intent(this, typeof(ArCaptureService));
                StopService(serviceIntent);
            }
            catch { }

            // Extra cleanup time for MediaTek
            if (_isMediaTekDevice)
            {
                System.Threading.Thread.Sleep(250);
            }

            base.OnDestroy();
        }

        // Static helper methods for ARCore service
        public static bool IsMediaTekHardware() => _isMediaTekDevice;
        public static bool IsCameraReady() => _isCameraInUse;
    }

    [Service(ForegroundServiceType = ForegroundService.TypeCamera)]
    public class ArCaptureService : Service
    {
        private const int NotificationId = 1001;
        private const string ChannelId = "ar_capture_channel";

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            CreateNotificationChannel();

            var notification = new Notification.Builder(this, ChannelId)
                .SetContentTitle("AR Capture Active")
                .SetContentText("Capturing AR images...")
                .SetSmallIcon(global::Android.Resource.Drawable.IcMenuCamera)
                .SetOngoing(true)
                .Build();

            StartForeground(NotificationId, notification);
            return StartCommandResult.Sticky;
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(
                    ChannelId,
                    "AR Capture Service",
                    NotificationImportance.Low)
                {
                    Description = "Keeps AR capture service running"
                };

                var notificationManager = GetSystemService(NotificationService) as NotificationManager;
                notificationManager?.CreateNotificationChannel(channel);
            }
        }

        public override void OnDestroy()
        {
            StopForeground(StopForegroundFlags.Remove);
            base.OnDestroy();
        }
    }
}