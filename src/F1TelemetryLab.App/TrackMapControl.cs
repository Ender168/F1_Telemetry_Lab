using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace F1TelemetryLab;

public sealed class TrackMapControl : Control
{
    private TrackMapRenderData? _data;
    private double _amplification = 1.0;
    private int? _selectedInsightRank;

    public void SetData(TrackMapRenderData? data, double amplification = 1.0)
    {
        _data = data;
        _amplification = Math.Clamp(amplification, 0.25, 32.0);
        if (_data?.Insights.Count > 0 && (_selectedInsightRank is null || !_data.Insights.Any(x => x.Rank == _selectedInsightRank)))
            _selectedInsightRank = _data.Insights[0].Rank;
        if (_data is null) _selectedInsightRank = null;
        InvalidateVisual();
    }

    public void SetSelectedInsight(TrackMapInsight? insight)
    {
        _selectedInsightRank = insight?.Rank;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(22, 27, 34)), bounds);

        var textBrush = new SolidColorBrush(Color.FromRgb(225, 230, 235));
        var dimBrush = new SolidColorBrush(Color.FromRgb(150, 160, 170));
        if (_data?.Profile is null || _data.Profile.Points.Count < 2)
        {
            DrawText(context, "Track map data not loaded. Analyze and select a recorded session.", 16, textBrush, new Point(24, 24));
            return;
        }

        var profile = _data.Profile;
        var points = profile.Points;
        var left = 34.0;
        var top = 42.0;
        var right = 24.0;
        var bottom = 60.0;
        var plot = new Rect(left, top, Math.Max(20, bounds.Width - left - right), Math.Max(20, bounds.Height - top - bottom));

        var xs = points.Select(p => p.X).ToList();
        var zs = points.Select(p => p.Z).ToList();
        if (profile.Boundary is not null)
        {
            foreach (var p in profile.Boundary.Points)
            {
                xs.AddRange(BoundaryXs(p));
                zs.AddRange(BoundaryZs(p));
            }
        }
        var minX = xs.Min();
        var maxX = xs.Max();
        var minZ = zs.Min();
        var maxZ = zs.Max();
        var scale = Math.Min(plot.Width / Math.Max(1, maxX - minX), plot.Height / Math.Max(1, maxZ - minZ));
        var usedW = (maxX - minX) * scale;
        var usedH = (maxZ - minZ) * scale;
        var ox = plot.Left + (plot.Width - usedW) / 2.0;
        var oy = plot.Top + (plot.Height - usedH) / 2.0;

        Point Map(TrackPoint p) => new(
            ox + (p.X - minX) * scale,
            oy + usedH - (p.Z - minZ) * scale);
        Point MapRaw(double x, double z) => new(
            ox + (x - minX) * scale,
            oy + usedH - (z - minZ) * scale);

        if (profile.Boundary is not null)
        {
            DrawTrackBoundary(context, profile.Boundary.Points, MapRaw, compact: true);
        }
        else
        {
            var shadowPen = new Pen(new SolidColorBrush(Color.FromRgb(54, 62, 72)), 9);
            var basePen = new Pen(new SolidColorBrush(Color.FromRgb(210, 215, 222)), 3.5);
            for (var i = 1; i < points.Count; i++) context.DrawLine(shadowPen, Map(points[i - 1]), Map(points[i]));
            for (var i = 1; i < points.Count; i++) context.DrawLine(basePen, Map(points[i - 1]), Map(points[i]));
        }

        var valueByBin = _data.Values
            .GroupBy(v => RoundTo10(v.DistanceM))
            .ToDictionary(g => g.Key, g => g.Last().Value);
        var absValues = _data.Values
            .Select(v => Math.Abs(v.Value))
            .Where(v => v > 0.001)
            .OrderBy(v => v)
            .ToList();
        var p90 = absValues.Count == 0 ? 1.0 : absValues[(int)Math.Clamp(Math.Floor(absValues.Count * 0.90), 0, absValues.Count - 1)];
        if (p90 < 1) p90 = 1;

        for (var i = 1; i < points.Count; i++)
        {
            var mid = (points[i - 1].DistanceM + points[i].DistanceM) / 2.0;
            var bin = RoundTo10(mid);
            valueByBin.TryGetValue(bin, out var value);
            var color = ColorForValue(value * _amplification, p90, _data.Metric);
            var pen = new Pen(new SolidColorBrush(color), 5.2);
            context.DrawLine(pen, Map(points[i - 1]), Map(points[i]));
        }

        DrawTopZoneHighlights(context, points, Map, _data.Insights, _selectedInsightRank);

        DrawTelemetryTrace(context, _data.ReferenceTrace, MapRaw, Color.FromArgb(125, 80, 170, 255), 1.6);
        DrawTelemetryTrace(context, _data.CompareTrace, MapRaw, Color.FromArgb(125, 255, 204, 102), 1.6);

        DrawTitle(context, profile, _data, textBrush, dimBrush);
        DrawColorLegend(context, bounds, _data.Metric, _amplification, textBrush, dimBrush);
        DrawDirectionArrow(context, points, Map, textBrush);

        var cornerBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var cornerBg = new SolidColorBrush(Color.FromArgb(190, 16, 19, 24));
        foreach (var c in profile.Corners)
        {
            var nearest = NearestPoint(points, c.DistanceM);
            var p = Map(nearest);
            var labelPt = new Point(p.X + c.XOffset, p.Y + c.YOffset);
            context.FillRectangle(cornerBg, new Rect(labelPt.X - 3, labelPt.Y - 2, Math.Max(42, c.Label.Length * 7.5), 18));
            DrawText(context, c.Label, 11, cornerBrush, labelPt);
        }

        var start = Map(points[0]);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(86, 156, 214)), new Rect(start.X - 4, start.Y - 4, 8, 8));
        DrawText(context, "S/F", 11, textBrush, new Point(start.X + 7, start.Y - 7));
    }


    private static IEnumerable<double> BoundaryXs(TrackBoundaryPoint p)
    {
        yield return p.RacingX;
        yield return p.LeftTrackX;
        yield return p.RightTrackX;
        yield return p.LeftWhiteX;
        yield return p.RightWhiteX;
        yield return p.LeftRunOffX;
        yield return p.RightRunOffX;
    }

    private static IEnumerable<double> BoundaryZs(TrackBoundaryPoint p)
    {
        yield return p.RacingZ;
        yield return p.LeftTrackZ;
        yield return p.RightTrackZ;
        yield return p.LeftWhiteZ;
        yield return p.RightWhiteZ;
        yield return p.LeftRunOffZ;
        yield return p.RightRunOffZ;
    }

    private static void DrawTrackBoundary(DrawingContext context, List<TrackBoundaryPoint> boundary, Func<double, double, Point> map, bool compact)
    {
        if (boundary.Count < 3) return;
        var leftRunOff = boundary.Select(p => map(p.LeftRunOffX, p.LeftRunOffZ)).ToList();
        var rightRunOff = boundary.Select(p => map(p.RightRunOffX, p.RightRunOffZ)).ToList();
        var leftTrack = boundary.Select(p => map(p.LeftTrackX, p.LeftTrackZ)).ToList();
        var rightTrack = boundary.Select(p => map(p.RightTrackX, p.RightTrackZ)).ToList();
        var leftWhite = boundary.Select(p => map(p.LeftWhiteX, p.LeftWhiteZ)).ToList();
        var rightWhite = boundary.Select(p => map(p.RightWhiteX, p.RightWhiteZ)).ToList();
        var racing = boundary.Select(p => map(p.RacingX, p.RacingZ)).ToList();

        FillRibbon(context, leftRunOff, rightRunOff, new SolidColorBrush(Color.FromRgb(30, 35, 43)));
        FillRibbon(context, leftTrack, rightTrack, new SolidColorBrush(Color.FromRgb(47, 53, 63)));

        DrawClosedPolyline(context, leftTrack, new Pen(new SolidColorBrush(Color.FromArgb(165, 255, 120, 80)), compact ? 1.3 : 2.0));
        DrawClosedPolyline(context, rightTrack, new Pen(new SolidColorBrush(Color.FromArgb(165, 255, 120, 80)), compact ? 1.3 : 2.0));
        DrawClosedPolyline(context, leftWhite, new Pen(new SolidColorBrush(Color.FromArgb(225, 240, 244, 248)), compact ? 1.6 : 2.3));
        DrawClosedPolyline(context, rightWhite, new Pen(new SolidColorBrush(Color.FromArgb(225, 240, 244, 248)), compact ? 1.6 : 2.3));
        DrawClosedPolyline(context, racing, new Pen(new SolidColorBrush(Color.FromArgb(105, 120, 210, 255)), compact ? 1.0 : 1.8));
    }

    private static void FillRibbon(DrawingContext context, List<Point> left, List<Point> right, IBrush brush)
    {
        if (left.Count < 2 || right.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(left[0], true);
            for (var i = 1; i < left.Count; i++) g.LineTo(left[i]);
            for (var i = right.Count - 1; i >= 0; i--) g.LineTo(right[i]);
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawClosedPolyline(DrawingContext context, List<Point> points, Pen pen)
    {
        if (points.Count < 2) return;
        for (var i = 1; i < points.Count; i++) context.DrawLine(pen, points[i - 1], points[i]);
        context.DrawLine(pen, points[^1], points[0]);
    }

    private static void DrawTopZoneHighlights(
        DrawingContext context,
        List<TrackPoint> points,
        Func<TrackPoint, Point> map,
        List<TrackMapInsight> insights,
        int? selectedRank)
    {
        if (insights.Count == 0) return;

        var visible = insights.Take(5).ToList();
        var selectedInsight = selectedRank is null ? null : insights.FirstOrDefault(x => x.Rank == selectedRank);
        if (selectedInsight is not null && visible.All(x => x.Rank != selectedInsight.Rank)) visible.Add(selectedInsight);

        foreach (var insight in visible.AsEnumerable().Reverse())
        {
            var selected = selectedRank == insight.Rank;
            var color = InsightColor(insight.Kind, selected ? (byte)255 : (byte)205);
            var glow = new Pen(new SolidColorBrush(InsightColor(insight.Kind, selected ? (byte)95 : (byte)45)), selected ? 18 : 12);
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(selected ? (byte)210 : (byte)110, 255, 255, 255)), selected ? 11 : 7);
            var main = new Pen(new SolidColorBrush(color), selected ? 7 : 4.8);

            DrawInsightSegment(context, points, map, insight, glow);
            DrawInsightSegment(context, points, map, insight, outline);
            DrawInsightSegment(context, points, map, insight, main);
        }

        foreach (var insight in visible)
            DrawInsightBadge(context, points, map, insight, selectedRank == insight.Rank);
    }

    private static void DrawInsightSegment(DrawingContext context, List<TrackPoint> points, Func<TrackPoint, Point> map, TrackMapInsight insight, Pen pen)
    {
        var any = false;
        for (var i = 1; i < points.Count; i++)
        {
            var mid = (points[i - 1].DistanceM + points[i].DistanceM) / 2.0;
            if (mid < insight.StartM || mid > insight.EndM) continue;
            context.DrawLine(pen, map(points[i - 1]), map(points[i]));
            any = true;
        }

        if (any) return;
        var nearest = NearestPoint(points, insight.PeakDistanceM);
        var idx = points.IndexOf(nearest);
        var from = Math.Max(1, idx - 2);
        var to = Math.Min(points.Count - 1, idx + 2);
        for (var i = from; i <= to; i++) context.DrawLine(pen, map(points[i - 1]), map(points[i]));
    }

    private static void DrawInsightBadge(DrawingContext context, List<TrackPoint> points, Func<TrackPoint, Point> map, TrackMapInsight insight, bool selected)
    {
        var p = map(NearestPoint(points, insight.PeakDistanceM));
        var fill = new SolidColorBrush(InsightColor(insight.Kind, 245));
        var border = new Pen(new SolidColorBrush(selected ? Color.FromRgb(255, 255, 255) : Color.FromRgb(12, 16, 22)), selected ? 2.4 : 1.4);
        context.DrawEllipse(fill, border, p, selected ? 13 : 11, selected ? 13 : 11);
        DrawText(context, insight.Rank.ToString(CultureInfo.InvariantCulture), selected ? 12 : 10.5, Brushes.White, new Point(p.X - (insight.Rank >= 10 ? 6.5 : 3.3), p.Y - 7.2));
    }

    private static Color InsightColor(string kind, byte alpha)
    {
        return kind.Equals("LOSS", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(alpha, 255, 74, 74)
            : Color.FromArgb(alpha, 55, 165, 255);
    }

    private static void DrawTelemetryTrace(DrawingContext context, List<TrackMapTracePoint> trace, Func<double, double, Point> mapRaw, Color color, double width)
    {
        if (trace.Count < 2) return;
        var pen = new Pen(new SolidColorBrush(color), width);
        var ordered = trace
            .Where(p => !double.IsNaN(p.X) && !double.IsNaN(p.Z) && !double.IsInfinity(p.X) && !double.IsInfinity(p.Z))
            .OrderBy(p => p.DistanceM)
            .ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var a = ordered[i - 1];
            var b = ordered[i];
            if (b.DistanceM - a.DistanceM > 60) continue;
            context.DrawLine(pen, mapRaw(a.X, a.Z), mapRaw(b.X, b.Z));
        }
    }

    private static void DrawTitle(DrawingContext context, TrackProfile profile, TrackMapRenderData data, IBrush textBrush, IBrush dimBrush)
    {
        DrawText(context, $"{profile.TrackName} Track Map", 17, textBrush, new Point(18, 12));
        DrawText(context, $"REF: {data.ReferenceLabel}    CMP: {data.CompareLabel}", 12, dimBrush, new Point(18, 32));
    }

    private static void DrawColorLegend(DrawingContext context, Rect bounds, string metric, double amplification, IBrush textBrush, IBrush dimBrush)
    {
        var x = bounds.Width - 270;
        var y = bounds.Height - 42;
        DrawText(context, $"{MetricLabel(metric)}  contrast x{amplification:0.##}", 12, textBrush, new Point(x, y - 18));
        for (var i = 0; i < 120; i++)
        {
            var t = i / 119.0;
            var value = (t * 2.0) - 1.0;
            var color = DivergingColor(value);
            context.DrawLine(new Pen(new SolidColorBrush(color), 4), new Point(x + i * 2, y), new Point(x + i * 2, y + 12));
        }
        DrawText(context, "gain", 10, dimBrush, new Point(x, y + 16));
        DrawText(context, "neutral", 10, dimBrush, new Point(x + 94, y + 16));
        DrawText(context, "loss", 10, dimBrush, new Point(x + 198, y + 16));
    }

    private static void DrawDirectionArrow(DrawingContext context, List<TrackPoint> points, Func<TrackPoint, Point> map, IBrush textBrush)
    {
        if (points.Count < 20) return;
        var start = map(points[0]);
        var endPoint = points.FirstOrDefault(p => p.DistanceM >= 110);
        if (endPoint is null) endPoint = points[Math.Min(points.Count - 1, 20)];
        var end = map(endPoint);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 4) return;
        dx /= len;
        dy /= len;
        var arrowEnd = new Point(start.X + dx * 42, start.Y + dy * 42);
        var arrowStart = new Point(start.X + dx * 12, start.Y + dy * 12);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(80, 180, 255)), 3);
        context.DrawLine(pen, arrowStart, arrowEnd);
        var left = new Point(arrowEnd.X - dx * 10 - dy * 6, arrowEnd.Y - dy * 10 + dx * 6);
        var right = new Point(arrowEnd.X - dx * 10 + dy * 6, arrowEnd.Y - dy * 10 - dx * 6);
        context.DrawLine(pen, arrowEnd, left);
        context.DrawLine(pen, arrowEnd, right);
        DrawText(context, "direction", 10, textBrush, new Point(arrowEnd.X + 6, arrowEnd.Y + 2));
    }

    private static string MetricLabel(string metric) => metric switch
    {
        "cumulative_delta_ms" => "cumulative delta",
        "speed_loss_kmh" => "speed loss",
        _ => "segment loss"
    };

    private static TrackPoint NearestPoint(List<TrackPoint> points, double distanceM)
    {
        var best = points[0];
        var bestAbs = Math.Abs(best.DistanceM - distanceM);
        foreach (var p in points)
        {
            var d = Math.Abs(p.DistanceM - distanceM);
            if (d < bestAbs) { best = p; bestAbs = d; }
        }
        return best;
    }

    private static int RoundTo10(double meters) => (int)(Math.Round(meters / 10.0) * 10);

    private static Color ColorForValue(double value, double scale, string metric)
    {
        var baseScale = metric == "cumulative_delta_ms"
            ? Math.Max(50.0, scale)
            : Math.Max(1.0, scale);
        var normalized = Math.Clamp(value / baseScale, -1.0, 1.0);
        return DivergingColor(normalized);
    }

    private static Color DivergingColor(double normalized)
    {
        normalized = Math.Clamp(normalized, -1.0, 1.0);
        if (normalized < 0)
        {
            var t = Math.Abs(normalized);
            return Lerp(Color.FromRgb(250, 250, 250), Color.FromRgb(35, 135, 225), t);
        }
        if (normalized > 0)
        {
            return Lerp(Color.FromRgb(250, 250, 250), Color.FromRgb(220, 25, 25), normalized);
        }
        return Color.FromRgb(250, 250, 250);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
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
