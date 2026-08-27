using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace F1TelemetryLab;

public sealed record LapChartPoint(int Lap, double Value);
public sealed record LapChartSeries(string Label, IReadOnlyList<LapChartPoint> Points);

public sealed class LapSeriesChart : Control
{
    private IReadOnlyList<LapChartSeries> _series = Array.Empty<LapChartSeries>();
    private string _title = "Chart";
    private string _unit = "";

    private static readonly Color[] Palette =
    {
        Color.FromRgb(86, 156, 214),
        Color.FromRgb(244, 191, 117),
        Color.FromRgb(184, 215, 163),
        Color.FromRgb(197, 134, 192),
        Color.FromRgb(220, 90, 90),
        Color.FromRgb(78, 201, 176)
    };

    public void SetData(IReadOnlyList<LapChartSeries> series, string title, string unit)
    {
        _series = series;
        _title = title;
        _unit = unit;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(22, 27, 34)), bounds);

        var plot = new Rect(56, 32, Math.Max(20, bounds.Width - 80), Math.Max(20, bounds.Height - 72));
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(82, 92, 106)), 1);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 48, 56)), 1);
        var textBrush = new SolidColorBrush(Color.FromRgb(210, 215, 222));

        DrawText(context, _title, 14, textBrush, new Point(plot.Left, 8));
        context.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);
        context.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);

        var all = _series.SelectMany(s => s.Points).Where(p => !double.IsNaN(p.Value) && !double.IsInfinity(p.Value)).ToList();
        if (_series.Count == 0 || all.Count == 0)
        {
            DrawText(context, "No chart data yet.", 13, textBrush, new Point(plot.Left + 16, plot.Top + 20));
            return;
        }

        var minLap = all.Min(p => p.Lap);
        var maxLap = all.Max(p => p.Lap);
        if (maxLap <= minLap) maxLap = minLap + 1;
        var minY = all.Min(p => p.Value);
        var maxY = all.Max(p => p.Value);
        if (Math.Abs(maxY - minY) < 0.0001) { minY -= 1; maxY += 1; }
        var pad = (maxY - minY) * 0.08;
        minY -= pad;
        maxY += pad;

        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4.0;
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = (minY + (maxY - minY) * i / 4.0).ToString("0.##", CultureInfo.InvariantCulture) + _unit;
            DrawText(context, label, 11, textBrush, new Point(6, y - 8));
        }

        var lapSteps = Math.Min(6, Math.Max(2, maxLap - minLap + 1));
        for (var i = 0; i <= lapSteps; i++)
        {
            var lap = minLap + (maxLap - minLap) * i / Math.Max(1, lapSteps);
            var x = plot.Left + (lap - minLap) / Math.Max(1.0, maxLap - minLap) * plot.Width;
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            DrawText(context, "L" + lap.ToString(CultureInfo.InvariantCulture), 11, textBrush, new Point(x - 12, plot.Bottom + 8));
        }

        for (var s = 0; s < _series.Count; s++)
        {
            var color = Palette[s % Palette.Length];
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, s == 0 ? 2.6 : 2.0);
            var points = _series[s].Points.OrderBy(p => p.Lap).ToList();
            Point? last = null;
            foreach (var p in points)
            {
                if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;
                var x = plot.Left + (p.Lap - minLap) / Math.Max(1.0, maxLap - minLap) * plot.Width;
                var y = plot.Bottom - (p.Value - minY) / Math.Max(0.0001, maxY - minY) * plot.Height;
                var point = new Point(x, y);
                if (last is not null) context.DrawLine(pen, last.Value, point);
                context.DrawEllipse(brush, null, point, 2.6, 2.6);
                last = point;
            }
            DrawText(context, _series[s].Label, 12, brush, new Point(plot.Left + 12 + s * 120, plot.Bottom + 30));
        }
    }

    private static void DrawText(DrawingContext context, string text, double size, IBrush brush, Point point)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush);
        context.DrawText(formatted, point);
    }
}
