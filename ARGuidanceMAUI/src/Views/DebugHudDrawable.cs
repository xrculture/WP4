using ARGuidanceMAUI.Models;

namespace ARGuidanceMAUI.Views;

// Drawable that renders AR debug telemetry as a HUD (Heads-Up Display) overlay
public class DebugHudDrawable : IDrawable
{
    private ArDebugTelemetry? _t;
    public void Update(ArDebugTelemetry t) => _t = t;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (_t == null) return;

        canvas.SaveState();

        canvas.Alpha = 0.9f;        
        canvas.FontSize = 14;        

        float y0 = 12, lh = 16;
        canvas.FontColor = Colors.White;
        canvas.DrawString($"project: {_t.ProjectName}", 8, y0, dirtyRect.Width - 16, lh, HorizontalAlignment.Left, VerticalAlignment.Center, TextFlow.OverflowBounds);
        canvas.FontColor = _t.FilteredFeaturePoints.Length > 0 ? Colors.White : Colors.Red;
        canvas.DrawString($"features: {_t.FilteredFeaturePoints.Length}/{_t.AllFeaturePoints.Length}", 8, y0 += 2 * lh, HorizontalAlignment.Left);
        canvas.FontColor = Colors.White;
        canvas.DrawString($"Δ position: {_t.DeltaPositionMeters:F2}m", 8, y0 += lh, HorizontalAlignment.Left);
        canvas.DrawString($"Δ yaw: {_t.DeltaYawRad * 180/Math.PI:F1}°", 8, y0 += lh, HorizontalAlignment.Left);
        canvas.DrawString($"captures: {_t.Captures}", 8, y0 += lh, HorizontalAlignment.Left);

        canvas.RestoreState();
    }
}