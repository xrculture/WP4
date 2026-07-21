using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace ARGuidanceMAUI.Platforms.Android
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Redmi Note: Reduce screen brightness to minimize heat generation
            //var layoutParams = Window?.Attributes;
            //if (layoutParams != null)
            //{
            //    layoutParams.ScreenBrightness = 0.4f; // 40% brightness (range: 0.0 to 1.0)
            //    Window?.Attributes = layoutParams;
            //}

            // Keep screen on only during active use (not when app is in background)
            Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);

            // Causes overheating on some devices, so we will not request this for now
            // Request battery optimization exemption
            //RequestBatteryOptimizationExemption();

            // Request MIUI-specific permissions
            //RequestMIUIPermissions();

            StartForegroundService();
        }

        protected override void OnPause()
        {
            base.OnPause();

            // Allow screen to turn off when app is paused to save battery/reduce heat
            Window?.ClearFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
        }

        protected override void OnResume()
        {
            base.OnResume();

            // Re-enable when app resumes
            Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
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
            try
            {
                var serviceIntent = new Intent(this, typeof(ArCaptureService));
                StopService(serviceIntent);
            }
            catch { }

            base.OnDestroy();
        }
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
                var channel = new NotificationChannel(ChannelId, "AR Capture Service",
                    NotificationImportance.Low)
                {
                    Description = "Keeps AR capture running in the foreground"
                };

                var notificationManager = GetSystemService(NotificationService) as NotificationManager;
                notificationManager?.CreateNotificationChannel(channel);
            }
        }
    }
}