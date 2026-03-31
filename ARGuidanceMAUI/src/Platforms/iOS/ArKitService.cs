using ARKit;
using CoreGraphics;
using CoreImage;
using CoreVideo;
using Foundation;
using ARGuidanceMAUI.Models;
using ARGuidanceMAUI.Services;
using ARGuidanceMAUI.Views;
using SceneKit;
using UIKit;
using ImageIO;
using UniformTypeIdentifiers;
using System.Numerics;

namespace ARGuidanceMAUI.Platforms.iOS;

public class ArKitService : NSObject, IArPlatformService, IARSessionDelegate
{
    public event Action<GuidanceState>? GuidanceUpdated;
    public event Action<CapturePackage>? CaptureReady;
    public event Action<string>? InfoMessage;
    //public event Action<ArDebugTelemetry>? DebugUpdated;#todo

    private ARSCNView? _arView;
    private readonly ARSession _session = new();

    private float _lastDeltaYaw = 0f;
    private bool _highRes = false;

    // Centroid accumulation for yaw calculation
    private Vector3 _centroidAccum = new(0, 0, 0);
    private int _centroidCount = 0;
    private const int MinIdsForKeyframe = 30;

    // Project management
    public string CurrentProjectFolder { get; private set; } = string.Empty;

    // Camera capabilities - iOS provides dynamic resolution
    private int _cameraWidth = 1920; // Default fallback
    private int _cameraHeight = 1440; // Default fallback

    public ArKitService()
    {
        // Handler will call AttachArView to provide the ARSCNView
        _session.Delegate = this;
        CreateNewProject(); // Create initial project on startup
    }

    // Called by the platform handler to supply the native AR view
    public void AttachArView(ARSCNView view)
    {
        _arView = view;
        _arView.Session = _session;
        _arView.AutomaticallyUpdatesLighting = false;
    }

    public void SetHighResEnabled(bool enabled)
    {
        _highRes = enabled;
        if (_highRes)
            InfoMessage?.Invoke("High-res: TODO integrate AVCapturePhotoOutput with ARKit. Using AR stream for now.");
        else
            InfoMessage?.Invoke("High-res disabled. Using AR stream.");
    }

    public void Start()
    {
        var cfg = new ARWorldTrackingConfiguration
        {
            PlaneDetection = ARPlaneDetection.None,
            EnvironmentTexturing = AREnvironmentTexturing.None
        };
        _session.Run(cfg, ARSessionRunOptions.ResetTracking | ARSessionRunOptions.RemoveExistingAnchors);
    }

    public void Stop() => _session.Pause();

    public void NewProject()
    {
        CreateNewProject();
        ResetDataStructures();
    }

    public void Projects()
    {
        // Implementation for managing projects
    }

    private void CreateNewProject()
    {
        var now = DateTime.Now;
        var folderName = $"{now:yyyy}-{now:MM}-{now:dd}-{now:mm}-{now:ss}";
        CurrentProjectFolder = $"Pictures/ARGuidanceMAUI/{folderName}";
    }

    private void ResetDataStructures()
    {
        // Reset all AR tracking data
        _centroidAccum = new Vector3(0, 0, 0);
        _centroidCount = 0;
        _lastDeltaYaw = 0f;
        //#todo?
        //_lastOverlap = 0f;
        //Array.Clear(_bins, 0, _bins.Length);
        //_lastKeyframeIds = null;
        //_lastKfTransform = null;
    }

    public void RequestCapture()
    {
        var frame = _session.CurrentFrame;
        if (frame == null) return;

        if (_highRes)
            InfoMessage?.Invoke("High-res capture requested. TODO implement AVCapturePhotoOutput. Capturing AR stream this time.");

        var tsNs = (ulong)(frame.Timestamp * 1_000_000_000.0);
        var cam = frame.Camera;
        var intr = cam.Intrinsics; // NMatrix3
        var res = cam.ImageResolution;
        var m = cam.Transform;     // NMatrix4

        var t = GetTranslation(m);
        var q = QuaternionFrom(m);

        // NMatrix3 is row-major K = [ fx 0 cx; 0 fy cy; 0 0 1 ]
        var fx = intr.M11; var fy = intr.M22; var cx = intr.M13; var cy = intr.M23;

        var meta = $@"{{
  ""timestamp_nanos"": {tsNs},
  ""pose"": {{
    ""translation_m"": [{t.X:F6},{t.Y:F6},{t.Z:F6}],
    ""rotation_quaternion_xyzw"": [{q.X:F6},{q.Y:F6},{q.Z:F6},{q.W:F6}]
  }},
  ""intrinsics"": {{
    ""fx"": {fx:F6}, ""fy"": {fy:F6}, ""cx"": {cx:F6}, ""cy"": {cy:F6},
    ""width"": {(int)res.Width}, ""height"": {(int)res.Height}
  }},
  ""guidance"": {{
    ""overlap_estimate_percent"": #todo(int)Math.Round(_lastOverlap * 100),
    ""delta_yaw_deg"": {_lastDeltaYaw * 180.0f / (float)Math.PI:F2}
  }}
}}";

        // Update camera resolution from current frame
        _cameraWidth = (int)res.Width;
        _cameraHeight = (int)res.Height;

        var jpeg = PixelBufferToJpeg(frame.CapturedImage, 0.92f);
        if (jpeg != null)
        {
            //#todo
            // Mark current slice as captured
            //if (CurrentCaptureSettings != null)
            //{
            //    var currentSlice = GetCurrentSlice(GetYaw(m), m);
            //    if (currentSlice >= 0 && currentSlice < CurrentCaptureSettings.CapturedSlices.Length)
            //    {
            //        CurrentCaptureSettings.CapturedSlices[currentSlice] = true;
            //    }
            //}

            CaptureReady?.Invoke(new CapturePackage
            {
                JpegBytes = jpeg,
                MetadataJson = meta,
                FileBaseName = $"cap_{tsNs}"
            });
        }
    }

    // ARSession delegate
    [Export("session:didUpdateFrame:")]
    public void DidUpdateFrame(ARSession session, ARFrame frame)
    {
        if (frame.Camera.TrackingState != ARTrackingState.Normal)
        {
            GuidanceUpdated?.Invoke(new GuidanceState { Hint = "Move phone to initialize..." });
            return;
        }

        var m = frame.Camera.Transform; // NMatrix4

        // Identifiers: ARKit provides ulong[] here; use as-is
        var ids = frame.RawFeaturePoints?.Identifiers ?? Array.Empty<ulong>();

        // Points: convert from NVector3[] to System.Numerics.Vector3[]
        Vector3[] pts = Array.Empty<Vector3>();
        if (frame.RawFeaturePoints?.Points is NVector3[] npts)
            pts = Array.ConvertAll(npts, p => new Vector3((float)p.X, (float)p.Y, (float)p.Z));

        AccumulateCentroid(pts);
        var centroid = _centroidCount > 0 ? _centroidAccum : (Vector3?)null;

        var poseYaw = YawRadians(m, centroid);
        
        // Use slice-based guidance if capture settings are available
        int currentSlice = -1;
        float targetYaw = 0f;
        string hint = "Move phone to initialize...";

        //#todo
        //if (CurrentCaptureSettings != null)
        {
            currentSlice = GetCurrentSlice(poseYaw, m);
            targetYaw = GetTargetYawForSlice(currentSlice);
            hint = GetSliceGuidanceHint(currentSlice, poseYaw, targetYaw);
        }
        //else
        //{
        //    // Simple guidance without bins - just point forward
        //    targetYaw = poseYaw;
        //    hint = "No project settings";
        //}
        
        var deltaYaw = _lastDeltaYaw = DeltaYaw(poseYaw, targetYaw);

        var guidanceState = new GuidanceState 
        { 
            Hint = hint,
        };
        
        GuidanceUpdated?.Invoke(guidanceState);

        // Render OpenGL cube for iOS demonstration
        RenderCubeVisualization(frame);

        // Check for enough features for basic tracking
        if (ids.Length < MinIdsForKeyframe)
        {
            GuidanceUpdated?.Invoke(new GuidanceState { Hint = "Find textured surfaces…" });
            return;
        }
    }

    // Helpers

    // replace the existing OverlapRatio with this version

    private void AccumulateCentroid(Vector3[] pts)
    {
        if (pts.Length == 0) return;
        int N = Math.Min(pts.Length, 2000);
        double sx = 0, sy = 0, sz = 0;
        for (int i = 0; i < N; i++) { sx += pts[i].X; sy += pts[i].Y; sz += pts[i].Z; }
        var c = new Vector3((float)(sx / N), (float)(sy / N), (float)(sz / N));
        _centroidAccum = (_centroidAccum * _centroidCount + c) / (_centroidCount + 1);
        _centroidCount++;
    }

    private static float TranslationDelta(NMatrix4 a, NMatrix4 b)
    {
        var at = GetTranslation(a); var bt = GetTranslation(b);
        var d = at - bt;
        return d.Length();
    }

    private static float YawRadians(NMatrix4 m, Vector3? target)
    {
        var camPos = GetTranslation(m);
        if (target is Vector3 t)
        {
            var vx = camPos.X - t.X; var vz = camPos.Z - t.Z;
            return (float)Math.Atan2(vx, vz);
        }
        var fwd = GetForward(m);
        return (float)Math.Atan2(fwd.X, fwd.Z);
    }

    private static Vector3 GetTranslation(NMatrix4 m) => new((float)m.M41, (float)m.M42, (float)m.M43);

    // Assuming right-handed with -Z forward in view space
    private static Vector3 GetForward(NMatrix4 m) => Vector3.Normalize(new Vector3((float)-m.M31, (float)-m.M32, (float)-m.M33));

    private static float DeltaYaw(float from, float to)
    {
        var d = to - from;
        while (d > Math.PI) d -= (float)(2 * Math.PI);
        while (d < -Math.PI) d += (float)(2 * Math.PI);
        return d;
    }


    private int GetCurrentSlice(float currentYaw, NMatrix4 transform)
    {
        //#todo
        return -1;
        //if (CurrentCaptureSettings == null)
        //    return -1;

        //if (CurrentCaptureSettings.MovementType == MovementType.Circle)
        //{
        //    // Normalize yaw to 0-2π
        //    var normalizedYaw = currentYaw;
        //    while (normalizedYaw < 0) normalizedYaw += (float)(2 * Math.PI);
        //    while (normalizedYaw >= 2 * Math.PI) normalizedYaw -= (float)(2 * Math.PI);

        //    var sliceAngle = (float)(2 * Math.PI / CurrentCaptureSettings.CapturesCount);
        //    return (int)(normalizedYaw / sliceAngle);
        //}
        //else // Plane mode
        //{
        //    // For plane mode, use X position
        //    var position = GetTranslation(transform).X;
        //    var sliceIndex = (int)Math.Round(position / 0.1f); // 0.1m per slice
        //    return Math.Max(0, Math.Min(CurrentCaptureSettings.CapturesCount - 1, sliceIndex));
        //}
    }

    private float GetTargetYawForSlice(int sliceIndex)
    {
        //#todo
        return 0f;
        //if (CurrentCaptureSettings == null || sliceIndex < 0)
        //    return 0f;

        //if (CurrentCaptureSettings.MovementType == MovementType.Circle)
        //{
        //    return CurrentCaptureSettings.GetSliceAngleRadians(sliceIndex);
        //}
        //else // Plane mode
        //{
        //    // For plane mode, keep same yaw but indicate X position
        //    return 0f; // Face forward
        //}
    }

    private string GetSliceGuidanceHint(int currentSlice, float currentYaw, float targetYaw)
    {
        //#todo
        return string.Empty;
        //if (CurrentCaptureSettings == null)
        //    return "No project settings";

        //var completedCount = CurrentCaptureSettings.GetCompletedCapturesCount();
        //var totalSlices = CurrentCaptureSettings.CapturesCount;

        //if (completedCount >= totalSlices)
        //{
        //    return "All captures completed!";
        //}

        //if (CurrentCaptureSettings.MovementType == MovementType.Circle)
        //{
        //    var deltaYaw = DeltaYaw(currentYaw, targetYaw);
        //    var deg = deltaYaw * 180f / (float)Math.PI;
            
        //    if (Math.Abs(deg) < 10)
        //    {
        //        return "Ready for capture.";
        //    }
        //    else
        //    {
        //        return deg > 0 ? 
        //            $"Turn right ~{Math.Round(Math.Abs(deg))}° to reach slice {currentSlice + 1}" : 
        //            $"Turn left ~{Math.Round(Math.Abs(deg))}° to reach slice {currentSlice + 1}";
        //    }
        //}
        //else // Plane mode
        //{
        //    var targetPosition = CurrentCaptureSettings.GetSlicePositionMeters(currentSlice);
        //    return $"Move to position {targetPosition:F1}m for slice {currentSlice + 1}";
        //}
    }

    private static float GetYaw(NMatrix4 transform)
    {
        var forward = GetForward(transform);
        return (float)Math.Atan2(forward.X, forward.Z);
    }

    private static (float X, float Y, float Z, float W) QuaternionFrom(NMatrix4 m)
    {
        // Convert to System.Numerics.Matrix4x4 for quaternion creation
        var mm = new Matrix4x4(
            (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
            (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
            (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
            (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);
        var q = Quaternion.CreateFromRotationMatrix(mm);
        return (q.X, q.Y, q.Z, q.W);
    }

    private static byte[]? PixelBufferToJpeg(CVPixelBuffer pb, float quality)
    {
        using var ci = new CIImage(pb);
        using var ctx = new CIContext();
        using var cg = ctx.CreateCGImage(ci, ci.Extent);
        if (cg == null) return null;

        using var data = new NSMutableData();
        using var dest = CGImageDestination.Create(data, "public.jpeg", 1); // UTI for JPEG
        var opts = new CGImageDestinationOptions { LossyCompressionQuality = quality };
        dest?.AddImage(cg, opts);
        if (!dest!.Close()) return null;
        return data.ToArray();
    }

    // Render cube visualization using OpenGL
    private void RenderCubeVisualization(ARFrame frame)
    {
        try
        {
            // Create view-projection matrix from ARKit frame
            //var viewMatrix = Matrix4x4.Transpose(ConvertToMatrix4x4(frame.Camera.ViewMatrix));
            //var projectionMatrix = Matrix4x4.Transpose(ConvertToMatrix4x4(frame.Camera.ProjectionMatrix));
            //var viewProjectionMatrix = Matrix4x4.Multiply(viewMatrix, projectionMatrix);

            //// Define cube properties
            //var cubePosition = new Vector3(0, 0, -1.0f); // 1 meter in front of camera
            //var cubeScale = new Vector3(0.1f, 0.1f, 0.1f); // 10cm cube
            //var cubeRotation = new Vector3(0, (float)(DateTime.Now.TimeOfDay.TotalSeconds * 0.5), 0); // Rotate over time

            //// Render the cube
            //FeaturePointsDrawable.RenderCubeOpenGL(viewProjectionMatrix, cubePosition, cubeScale, cubeRotation);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cube rendering failed: {ex.Message}");
        }
    }

    // Helper to convert NMatrix4 to System.Numerics.Matrix4x4
    private static Matrix4x4 ConvertToMatrix4x4(NMatrix4 nMatrix)
    {
        return new Matrix4x4(
            (float)nMatrix.M11, (float)nMatrix.M12, (float)nMatrix.M13, (float)nMatrix.M14,
            (float)nMatrix.M21, (float)nMatrix.M22, (float)nMatrix.M23, (float)nMatrix.M24,
            (float)nMatrix.M31, (float)nMatrix.M32, (float)nMatrix.M33, (float)nMatrix.M34,
            (float)nMatrix.M41, (float)nMatrix.M42, (float)nMatrix.M43, (float)nMatrix.M44
        );
    }
}