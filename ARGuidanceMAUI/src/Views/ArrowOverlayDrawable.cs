using Microsoft.Maui.Graphics;

namespace ARGuidanceMAUI.Views;

public class ArrowOverlayDrawable : IDrawable
{
    public string Hint { get; set; } = "";

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.SaveState();

        var w = dirtyRect.Width;
        var h = dirtyRect.Height;
        var size = Math.Min(w, h);
        var radius = (float)(size * 0.22);
        var cx = dirtyRect.Center.X;
        var cy = dirtyRect.Bottom - radius - 24;

        // Hint text (centered, no measuring needed)
        if (!string.IsNullOrWhiteSpace(Hint))
        {
            canvas.FontSize = 14;
            canvas.FontColor = Colors.White;

            var textTop = cy - radius - 24 - 24; // 24px above the ring
            var textRect = new RectF(0, textTop, dirtyRect.Width, 48);
            canvas.DrawString(Hint, textRect, HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        canvas.RestoreState();
    }
}