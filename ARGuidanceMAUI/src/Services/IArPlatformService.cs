using ARGuidanceMAUI.Models;

namespace ARGuidanceMAUI.Services;

public interface IArPlatformService
{
    event Action<GuidanceState>? GuidanceUpdated;
    event Action<CapturePackage>? CaptureReady;
    event Action<string>? InfoMessage;

    void Start();
    void Stop();
    void RequestCapture();
    void NewProject();
    void Projects();
    string CurrentProjectFolder { get; }
}