using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace F1TelemetryLab;

internal sealed class OverlayGrid : Control
{
    public override void Render(DrawingContext context)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(35, 130, 180, 210)), 1);
        for (double x = 0; x < Bounds.Width; x += 20)
            context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
        for (double y = 0; y < Bounds.Height; y += 20)
            context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
    }
}
