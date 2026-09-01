using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;

namespace F1TelemetryLab;

public sealed class SimpleLineChart : Control
{
    private IReadOnlyList<CompareSeries> _series = Array.Empty<CompareSeries>();
    private string _metric = "speed";
    private int? _minDistance;
    private int? _maxDistance;
    private Point? _cursor;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(86, 156, 214),
        Color.FromRgb(244, 191, 117),
        Color.FromRgb(184, 215, 163),
        Color.FromRgb(197, 134, 192),
        Color.FromRgb(220, 90, 90),
        Color.FromRgb(78, 201, 176)
    };

    public SimpleLineChart()
    {
        PointerMoved += (_, args) =>
        {
            _cursor = args.GetPosition(this);
            InvalidateVisual();
        };
        PointerExited += (_, _) =>
        {
            _cursor = null;
            InvalidateVisual();
        };
    }

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
            DrawText(context, "Select a session and at least a reference lap, then choose Plot.", 16, textBrush, new Point(plot.X + 18, plot.Y + 18));
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

        DrawCursor(context, plot, minX, maxX, minY, maxY, textBrush);
    }

    private void DrawCursor(DrawingContext context, Rect plot, double minX, double maxX, double minY, double maxY, IBrush textBrush)
    {
        if (_cursor is not Point cursor || !plot.Contains(cursor)) return;
        var distance = minX + (cursor.X - plot.Left) / Math.Max(1, plot.Width) * (maxX - minX);
        var cursorPen = new Pen(new SolidColorBrush(Color.FromArgb(190, 210, 215, 222)), 1);
        context.DrawLine(cursorPen, new Point(cursor.X, plot.Top), new Point(cursor.X, plot.Bottom));

        var values = new List<(string Name, double Value, Color Color, Point Point)>();
        for (var index = 0; index < _series.Count; index++)
        {
            var nearest = _series[index].Points
                .Where(point => point.DistanceBinM >= minX && point.DistanceBinM <= maxX && !double.IsNaN(point.Value) && !double.IsInfinity(point.Value))
                .MinBy(point => Math.Abs(point.DistanceBinM - distance));
            if (nearest is null) continue;
            var x = plot.Left + (nearest.DistanceBinM - minX) / Math.Max(1.0, maxX - minX) * plot.Width;
            var y = plot.Bottom - (nearest.Value - minY) / Math.Max(0.0001, maxY - minY) * plot.Height;
            values.Add((_series[index].Name, nearest.Value, Palette[index % Palette.Length], new Point(x, y)));
        }

        foreach (var item in values)
        {
            var brush = new SolidColorBrush(item.Color);
            context.DrawEllipse(brush, new Pen(Brushes.White, 1), item.Point, 3.5, 3.5);
        }

        var tooltipWidth = Math.Min(300, Math.Max(180, plot.Width * 0.34));
        var tooltipHeight = 30 + values.Count * 17;
        var tooltipX = cursor.X + 10 + tooltipWidth <= plot.Right ? cursor.X + 10 : cursor.X - tooltipWidth - 10;
        var tooltipY = Math.Clamp(cursor.Y - tooltipHeight / 2, plot.Top + 4, plot.Bottom - tooltipHeight - 4);
        var tooltip = new Rect(tooltipX, tooltipY, tooltipWidth, tooltipHeight);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(235, 21, 26, 33)), tooltip);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(80, 88, 98)), 1), tooltip);
        DrawText(context, $"{distance:0} m", 12, textBrush, new Point(tooltip.Left + 8, tooltip.Top + 6));
        var unit = MetricUnit(_metric);
        for (var index = 0; index < values.Count; index++)
        {
            var item = values[index];
            var name = item.Name.Length > 24 ? item.Name[..24] : item.Name;
            DrawText(context, $"{name}: {item.Value:0.##}{unit}", 11, new SolidColorBrush(item.Color), new Point(tooltip.Left + 8, tooltip.Top + 24 + index * 17));
        }
    }

    private static string MetricUnit(string metric) => metric switch
    {
        "speed" => " km/h",
        "throttle_%" or "brake_%" => " %",
        "delta_ms" => " ms",
        _ => ""
    };

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
