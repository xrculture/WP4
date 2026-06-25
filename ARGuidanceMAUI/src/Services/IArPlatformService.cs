using ARGuidanceMAUI.Models;

namespace ARGuidanceMAUI.Services;

public interface IArPlatformService
{
    event Action<GuidanceState>? GuidanceUpdated;
    public event Func<CapturePackage, Task>? CaptureReady;

    void Start();
    void Stop();
    void RequestCapture();
    Task Options();
    string CurrentProjectFolder { get; }
}