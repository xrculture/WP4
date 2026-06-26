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

            // Keep screen on to prevent MIUI power management
            Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);

            // Request battery optimization exemption for MIUI
            RequestBatteryOptimizationExemption();

            // Start foreground service to prevent MIUI from killing the app
            StartForegroundService();
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