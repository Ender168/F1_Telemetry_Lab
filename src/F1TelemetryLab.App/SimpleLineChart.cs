using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace F1TelemetryLab;

public sealed class SimpleLineChart : Control
{
    private IReadOnlyList<CompareSeries> _series = Array.Empty<CompareSeries>();
    private string _metric = "speed";
    private int? _minDistance;
    private int? _maxDistance;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(86, 156, 214),
        Color.FromRgb(244, 191, 117),
        Color.FromRgb(184, 215, 163),
        Color.FromRgb(197, 134, 192),
        Color.FromRgb(220, 90, 90),
        Color.FromRgb(78, 201, 176)
    };

    public void SetData(IReadOnlyList<CompareSeries> series, string metric)
    {
        _series = series;
        _metric = metric;
        InvalidateVisual();
    }

    public void SetZoom(int? minDistance, int? maxDistance)
    {
        _minDistance = minDistance;
        _maxDistance = maxDistance;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        var bg = new SolidColorBrush(Color.FromRgb(22, 27, 34));
        context.FillRectangle(bg, bounds);

        var marginLeft = 54.0;
        var marginRight = 18.0;
        var marginTop = 30.0;
        var marginBottom = 36.0;
        var plot = new Rect(marginLeft, marginTop, Math.Max(20, bounds.Width - marginLeft - marginRight), Math.Max(20, bounds.Height - marginTop - marginBottom));
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(80, 88, 98)), 1);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 48, 56)), 1);
        var textBrush = new SolidColorBrush(Color.FromRgb(210, 215, 222));
        var small = new Typeface("Segoe UI");

        context.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);
        context.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);

        var all = _series.SelectMany(s => s.Points)
            .Where(p => !double.IsNaN(p.Value) && !double.IsInfinity(p.Value))
            .Where(p => _minDistance is null || p.DistanceBinM >= _minDistance.Value)
            .Where(p => _maxDistance is null || p.DistanceBinM <= _maxDistance.Value)
            .ToList();
        if (_series.Count == 0 || all.Count == 0)
        {
            DrawText(context, "Выбери сессию, до 6 кругов и нажми Plot slots. Да, кнопки, куда без них.", 18, textBrush, new Point(plot.X + 18, plot.Y + 18));
            return;
        }

        var minX = _minDistance ?? all.Min(p => p.DistanceBinM);
        var maxX = _maxDistance ?? all.Max(p => p.DistanceBinM);
        if (maxX <= minX) maxX = minX + 1;
        var minY = all.Min(p => p.Value);
        var maxY = all.Max(p => p.Value);
        if (Math.Abs(maxY - minY) < 0.0001) { maxY += 1; minY -= 1; }
        var yPad = (maxY - minY) * 0.08;
        minY -= yPad;
        maxY += yPad;

        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4.0;
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = (minY + (maxY - minY) * i / 4.0).ToString("0.##", CultureInfo.InvariantCulture);
            DrawText(context, label, 11, textBrush, new Point(6, y - 8));
        }
        for (var i = 0; i <= 5; i++)
        {
            var x = plot.Left + plot.Width * i / 5.0;
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = (minX + (maxX - minX) * i / 5.0).ToString("0", CultureInfo.InvariantCulture) + "m";
            DrawText(context, label, 11, textBrush, new Point(x - 18, plot.Bottom + 8));
        }

        DrawText(context, $"Metric: {_metric}", 14, textBrush, new Point(plot.Left, 6));

        for (var s = 0; s < _series.Count; s++)
        {
            var color = Palette[s % Palette.Length];
            var pen = new Pen(new SolidColorBrush(color), s == 0 ? 2.5 : 1.8);
            var pts = _series[s].Points
                .Where(p => p.DistanceBinM >= minX && p.DistanceBinM <= maxX)
                .OrderBy(p => p.DistanceBinM)
                .ToList();
            Point? last = null;
            foreach (var p in pts)
            {
                var x = plot.Left + (p.DistanceBinM - minX) / Math.Max(1.0, maxX - minX) * plot.Width;
                var y = plot.Bottom - (p.Value - minY) / Math.Max(0.0001, maxY - minY) * plot.Height;
                var pt = new Point(x, y);
                if (last is not null) context.DrawLine(pen, last.Value, pt);
                last = pt;
            }
        }
    }

    private static void DrawText(DrawingContext context, string text, double size, IBrush brush, Point point)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush);
        context.DrawText(formatted, point);
    }
}
