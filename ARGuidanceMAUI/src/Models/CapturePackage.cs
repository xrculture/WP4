namespace ARGuidanceMAUI.Models;

public class CapturePackage
{
    public byte[] JpegBytes { get; set; } = Array.Empty<byte>();
    public string MetadataJson { get; set; } = "";
    public string FileBaseName { get; set; } = "";
}