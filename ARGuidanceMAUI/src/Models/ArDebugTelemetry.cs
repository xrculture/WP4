namespace ARGuidanceMAUI.Models;

public sealed class ArDebugTelemetry
{
    public string ProjectName { get; set; } = string.Empty;
    public float DeltaPositionMeters { get; set; }
    public float DeltaYawRad { get; set; }
    public FeaturePoint[] AllFeaturePoints { get; set; } = Array.Empty<FeaturePoint>();
    public FeaturePoint[] FilteredFeaturePoints { get; set; } = Array.Empty<FeaturePoint>();
    public int Captures { get; set; }
}

public struct FeaturePoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Confidence { get; set; }
    public long Id { get; set; }
}