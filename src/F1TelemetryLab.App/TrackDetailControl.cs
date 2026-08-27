using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace F1TelemetryLab;

public sealed class TrackDetailControl : Control
{
    private const double ApproxTrackWidthM = 12.0;

    private TrackMapRenderData? _data;
    private TrackMapInsight? _selectedInsight;
    private double _amplification = 1.0;

    private sealed record ScreenDeviationPoint(
        int Bin,
        Point Reference,
        Point Compare,
        double OffsetMeters,
        double OffsetPixels);

    private readonly record struct ViewTransform(double MinX, double MinZ, double Scale, double OffsetX, double OffsetY, double UsedHeight)
    {
        public Point Map(double x, double z) => new(
            OffsetX + (x - MinX) * Scale,
            OffsetY + UsedHeight - (z - MinZ) * Scale);
    }

    public void SetData(TrackMapRenderData? data, double amplification = 1.0, TrackMapInsight? selectedInsight = null)
    {
        _data = data;
        _amplification = Math.Clamp(amplification, 0.25, 32.0);
        _selectedInsight = selectedInsight ?? data?.Insights.FirstOrDefault();
        InvalidateVisual();
    }

    public void SetSelectedInsight(TrackMapInsight? insight)
    {
        _selectedInsight = insight;
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
            DrawText(context, "Detail data not loaded. Построй Track Map или Best vs YOU, потом выбирай top-zone.", 16, textBrush, new Point(24, 24));
            return;
        }

        var insight = _selectedInsight ?? _data.Insights.FirstOrDefault();
        if (insight is null)
        {
            DrawText(context, "No top-zones found. Тут нечего приближать, трагедия без кульминации.", 16, textBrush, new Point(24, 24));
            return;
        }

        var profile = _data.Profile;
        var allPoints = profile.Points;
        var trackLength = profile.TrackLengthM > 0 ? profile.TrackLengthM : allPoints.Max(p => p.DistanceM);
        var (fromM, toM) = BuildDistanceWindow(insight, trackLength);

        var points = allPoints.Where(p => p.DistanceM >= fromM && p.DistanceM <= toM).ToList();
        if (points.Count < 2)
        {
            var nearest = NearestPoint(allPoints, insight.PeakDistanceM);
            var idx = allPoints.IndexOf(nearest);
            var fromIdx = Math.Max(0, idx - 70);
            var toIdx = Math.Min(allPoints.Count - 1, idx + 70);
            points = allPoints.Skip(fromIdx).Take(toIdx - fromIdx + 1).ToList();
            if (points.Count > 1)
            {
                fromM = points.First().DistanceM;
                toM = points.Last().DistanceM;
            }
        }

        var refTrace = TraceWindow(_data.ReferenceTrace, fromM, toM);
        var cmpTrace = TraceWindow(_data.CompareTrace, fromM, toM);
        var boundaryPoints = BoundaryWindow(profile.Boundary, fromM, toM);
        var valueByBin = _data.Values.GroupBy(v => RoundTo10(v.DistanceM)).ToDictionary(g => g.Key, g => g.Last().Value);
        var absValues = _data.Values.Select(v => Math.Abs(v.Value)).Where(v => v > 0.001).OrderBy(v => v).ToList();
        var p90 = absValues.Count == 0 ? 1.0 : absValues[(int)Math.Clamp(Math.Floor(absValues.Count * 0.90), 0, absValues.Count - 1)];
        if (p90 < 1) p90 = 1;

        var viewport = BuildViewport(points, boundaryPoints, refTrace, cmpTrace, ApproxTrackWidthM);
        var plot = new Rect(40, 72, Math.Max(20, bounds.Width - 80), Math.Max(20, bounds.Height - 152));
        var focus = NearestPoint(allPoints, insight.PeakDistanceM);
        var transform = BuildTransformCentered(viewport, plot, focus.X, focus.Z);

        Point Map(TrackPoint p) => transform.Map(p.X, p.Z);
        Point MapRaw(double x, double z) => transform.Map(x, z);

        var deviation = BuildScreenDeviation(refTrace, cmpTrace, MapRaw);
        var zoneDeviation = deviation
            .Where(p => p.Bin >= RoundTo10(insight.StartM) && p.Bin <= RoundTo10(insight.EndM))
            .ToList();
        if (zoneDeviation.Count == 0) zoneDeviation = deviation;

        var hasBoundary = boundaryPoints.Count >= 3;
        DrawHeader(context, bounds, insight, zoneDeviation, hasBoundary, textBrush, dimBrush);
        DrawLegend(context, bounds, insight, hasBoundary);

        if (hasBoundary) DrawTrackBoundary(context, boundaryPoints, MapRaw);
        else DrawTrackCorridor(context, points, transform, ApproxTrackWidthM);

        for (var i = 1; i < points.Count; i++)
        {
            var mid = (points[i - 1].DistanceM + points[i].DistanceM) / 2.0;
            var bin = RoundTo10(mid);
            valueByBin.TryGetValue(bin, out var value);
            var color = ColorForValue(value * _amplification, p90, _data.Metric);
            context.DrawLine(new Pen(new SolidColorBrush(color), 8.8), Map(points[i - 1]), Map(points[i]));
        }

        DrawZoneSegment(context, points, Map, insight, new Pen(new SolidColorBrush(InsightColor(insight.Kind, 80)), 30));
        DrawZoneSegment(context, points, Map, insight, new Pen(new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), 16));
        DrawZoneSegment(context, points, Map, insight, new Pen(new SolidColorBrush(InsightColor(insight.Kind, 245)), 10));

        DrawPathOffsetConnectors(context, deviation, valueByBin);
        DrawTelemetryTrace(context, refTrace, MapRaw, Color.FromArgb(245, 80, 170, 255), 5.4);
        DrawTelemetryTrace(context, cmpTrace, MapRaw, Color.FromArgb(245, 255, 216, 84), 5.4);

        DrawPeakMarker(context, points, Map, insight, textBrush);
        DrawCornerLabels(context, profile, allPoints, fromM, toM, Map);
        DrawDirectionArrow(context, points, Map, textBrush);
        DrawRangeChip(context, bounds, fromM, toM, insight, zoneDeviation, textBrush);
    }

    private static (double FromM, double ToM) BuildDistanceWindow(TrackMapInsight insight, double trackLength)
    {
        var zoneLength = Math.Max(20, insight.EndM - insight.StartM);
        var center = Math.Clamp(insight.PeakDistanceM, insight.StartM, insight.EndM);
        var halfWindow = Math.Clamp(zoneLength * 4.2, 155, 250);
        var fromM = center - halfWindow;
        var toM = center + halfWindow;

        if (fromM < 0)
        {
            toM = Math.Min(trackLength, toM - fromM);
            fromM = 0;
        }
        if (toM > trackLength)
        {
            fromM = Math.Max(0, fromM - (toM - trackLength));
            toM = trackLength;
        }
        return (fromM, toM);
    }

    private static (double MinX, double MaxX, double MinZ, double MaxZ) BuildViewport(
        List<TrackPoint> points,
        List<TrackBoundaryPoint> boundaryPoints,
        List<TrackMapTracePoint> refTrace,
        List<TrackMapTracePoint> cmpTrace,
        double trackWidthM)
    {
        var xValues = points.Select(p => p.X)
            .Concat(refTrace.Select(p => p.X))
            .Concat(cmpTrace.Select(p => p.X))
            .ToList();
        var zValues = points.Select(p => p.Z)
            .Concat(refTrace.Select(p => p.Z))
            .Concat(cmpTrace.Select(p => p.Z))
            .ToList();
        foreach (var p in boundaryPoints)
        {
            xValues.AddRange(BoundaryXs(p));
            zValues.AddRange(BoundaryZs(p));
        }

        var minX = xValues.Min();
        var maxX = xValues.Max();
        var minZ = zValues.Min();
        var maxZ = zValues.Max();
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxZ - minZ);
        var basePad = trackWidthM * 1.8;
        var padX = Math.Max(basePad, width * 0.18);
        var padZ = Math.Max(basePad, height * 0.18);
        return (minX - padX, maxX + padX, minZ - padZ, maxZ + padZ);
    }

    private static ViewTransform BuildTransformCentered(
        (double MinX, double MaxX, double MinZ, double MaxZ) viewport,
        Rect plot,
        double focusX,
        double focusZ)
    {
        var width = Math.Max(1, viewport.MaxX - viewport.MinX);
        var height = Math.Max(1, viewport.MaxZ - viewport.MinZ);
        var scale = Math.Min(plot.Width / width, plot.Height / height);
        var usedH = height * scale;
        var centerX = plot.Left + plot.Width / 2.0;
        var centerY = plot.Top + plot.Height / 2.0;
        var ox = centerX - (focusX - viewport.MinX) * scale;
        var oy = centerY - usedH + (focusZ - viewport.MinZ) * scale;
        return new ViewTransform(viewport.MinX, viewport.MinZ, scale, ox, oy, usedH);
    }

    private static void DrawHeader(DrawingContext context, Rect bounds, TrackMapInsight insight, List<ScreenDeviationPoint> zoneDeviation, bool hasBoundary, IBrush textBrush, IBrush dimBrush)
    {
        DrawText(context, "Track Detail", 18, textBrush, new Point(18, 12));
        DrawText(context, insight.Label, 13, dimBrush, new Point(18, 36));

        var avgOffset = zoneDeviation.Count == 0 ? 0 : zoneDeviation.Average(p => p.OffsetMeters);
        var maxOffset = zoneDeviation.Count == 0 ? 0 : zoneDeviation.Max(p => p.OffsetMeters);
        var limitsText = hasBoundary
            ? "Track surface, white lines and limits are from the embedded Austria Racenet spline."
            : $"No spline boundary loaded; using approximate {ApproxTrackWidthM:0.#}m corridor fallback.";
        var info = $"Actual path offset: avg {avgOffset:0.00}m, max {maxOffset:0.00}m. {limitsText}";
        DrawText(context, info, 12, dimBrush, new Point(18, bounds.Height - 34));
    }

    private static void DrawLegend(DrawingContext context, Rect bounds, TrackMapInsight insight, bool hasBoundary)
    {
        var x = Math.Max(18, bounds.Width - 335);
        var y = 14.0;
        var bg = new SolidColorBrush(Color.FromArgb(215, 16, 19, 24));
        context.FillRectangle(bg, new Rect(x - 10, y - 8, 320, hasBoundary ? 150 : 118));
        DrawText(context, "Legend", 13, Brushes.White, new Point(x, y));

        if (hasBoundary)
        {
            DrawLegendLine(context, x, y + 25, Color.FromRgb(47, 53, 63), "track surface");
            DrawLegendLine(context, x, y + 43, Color.FromArgb(225, 240, 244, 248), "white lines");
            DrawLegendLine(context, x, y + 61, Color.FromArgb(180, 255, 120, 80), "track limits");
            DrawLegendLine(context, x, y + 79, Color.FromArgb(130, 120, 210, 255), "Racenet racing line");
            DrawLegendLine(context, x, y + 97, Color.FromArgb(245, 80, 170, 255), "Reference path");
            DrawLegendLine(context, x, y + 115, Color.FromArgb(245, 255, 216, 84), "Compare path");
            DrawLegendLine(context, x, y + 133, InsightColor(insight.Kind, 245), "selected top-zone");
        }
        else
        {
            DrawLegendLine(context, x, y + 25, Color.FromRgb(210, 215, 222), "approx track limits / asphalt");
            DrawLegendLine(context, x, y + 43, Color.FromArgb(245, 80, 170, 255), "Reference path");
            DrawLegendLine(context, x, y + 61, Color.FromArgb(245, 255, 216, 84), "Compare path");
            DrawLegendLine(context, x, y + 79, InsightColor(insight.Kind, 245), "selected top-zone");
            DrawLegendLine(context, x, y + 97, Color.FromArgb(155, 255, 90, 90), "actual offset connectors");
        }
    }

    private static void DrawLegendLine(DrawingContext context, double x, double y, Color color, string text)
    {
        context.DrawLine(new Pen(new SolidColorBrush(color), 4.0), new Point(x, y + 7), new Point(x + 34, y + 7));
        DrawText(context, text, 11, new SolidColorBrush(Color.FromRgb(190, 200, 210)), new Point(x + 44, y));
    }

    private static List<TrackMapTracePoint> TraceWindow(List<TrackMapTracePoint> trace, double fromM, double toM)
    {
        return trace
            .Where(p => p.DistanceM >= fromM && p.DistanceM <= toM)
            .Where(p => !double.IsNaN(p.X) && !double.IsNaN(p.Z) && !double.IsInfinity(p.X) && !double.IsInfinity(p.Z))
            .OrderBy(p => p.DistanceM)
            .ToList();
    }


    private static List<TrackBoundaryPoint> BoundaryWindow(TrackBoundary? boundary, double fromM, double toM)
    {
        if (boundary is null || boundary.Points.Count == 0) return new List<TrackBoundaryPoint>();
        var ordered = boundary.Points.OrderBy(p => p.DistanceM).ToList();
        var window = ordered.Where(p => p.DistanceM >= fromM && p.DistanceM <= toM).ToList();
        var before = ordered.LastOrDefault(p => p.DistanceM < fromM);
        var after = ordered.FirstOrDefault(p => p.DistanceM > toM);
        if (before is not null) window.Insert(0, before);
        if (after is not null) window.Add(after);
        return window;
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

    private static void DrawTrackBoundary(DrawingContext context, List<TrackBoundaryPoint> boundary, Func<double, double, Point> map)
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
        DrawPolyline(context, leftTrack, new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 120, 80)), 2.2));
        DrawPolyline(context, rightTrack, new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 120, 80)), 2.2));
        DrawPolyline(context, leftWhite, new Pen(new SolidColorBrush(Color.FromArgb(245, 240, 244, 248)), 3.0));
        DrawPolyline(context, rightWhite, new Pen(new SolidColorBrush(Color.FromArgb(245, 240, 244, 248)), 3.0));
        DrawPolyline(context, racing, new Pen(new SolidColorBrush(Color.FromArgb(135, 120, 210, 255)), 1.8));
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

    private static void DrawTrackCorridor(DrawingContext context, List<TrackPoint> points, ViewTransform transform, double trackWidthM)
    {
        if (points.Count < 2) return;
        Point Map(TrackPoint p) => transform.Map(p.X, p.Z);
        var asphaltWidth = Math.Clamp(trackWidthM * transform.Scale, 18.0, 92.0);
        var shadow = new Pen(new SolidColorBrush(Color.FromArgb(190, 7, 10, 14)), asphaltWidth + 10.0);
        var asphalt = new Pen(new SolidColorBrush(Color.FromRgb(42, 48, 57)), asphaltWidth);
        var edgeShadow = new Pen(new SolidColorBrush(Color.FromArgb(220, 6, 8, 12)), 4.8);
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(220, 235, 238, 242)), 2.0);

        for (var i = 1; i < points.Count; i++) context.DrawLine(shadow, Map(points[i - 1]), Map(points[i]));
        for (var i = 1; i < points.Count; i++) context.DrawLine(asphalt, Map(points[i - 1]), Map(points[i]));

        var left = BuildOffsetPolyline(points, transform, trackWidthM / 2.0);
        var right = BuildOffsetPolyline(points, transform, -trackWidthM / 2.0);
        DrawPolyline(context, left, edgeShadow);
        DrawPolyline(context, right, edgeShadow);
        DrawPolyline(context, left, edge);
        DrawPolyline(context, right, edge);
    }

    private static List<Point> BuildOffsetPolyline(List<TrackPoint> points, ViewTransform transform, double offsetM)
    {
        var result = new List<Point>();
        for (var i = 0; i < points.Count; i++)
        {
            var prev = points[Math.Max(0, i - 1)];
            var next = points[Math.Min(points.Count - 1, i + 1)];
            var dx = next.X - prev.X;
            var dz = next.Z - prev.Z;
            var len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 0.001)
            {
                result.Add(transform.Map(points[i].X, points[i].Z));
                continue;
            }
            var nx = -dz / len;
            var nz = dx / len;
            result.Add(transform.Map(points[i].X + nx * offsetM, points[i].Z + nz * offsetM));
        }
        return result;
    }

    private static void DrawPolyline(DrawingContext context, List<Point> points, Pen pen)
    {
        for (var i = 1; i < points.Count; i++) context.DrawLine(pen, points[i - 1], points[i]);
    }

    private static void DrawTelemetryTrace(DrawingContext context, List<TrackMapTracePoint> trace, Func<double, double, Point> mapRaw, Color color, double width)
    {
        if (trace.Count < 2) return;
        var outer = new Pen(new SolidColorBrush(Color.FromArgb(160, 8, 12, 18)), width + 3.2);
        var inner = new Pen(new SolidColorBrush(color), width);
        for (var i = 1; i < trace.Count; i++)
        {
            var a = trace[i - 1];
            var b = trace[i];
            if (b.DistanceM - a.DistanceM > 60) continue;
            var p1 = mapRaw(a.X, a.Z);
            var p2 = mapRaw(b.X, b.Z);
            context.DrawLine(outer, p1, p2);
            context.DrawLine(inner, p1, p2);
        }
    }

    private static List<ScreenDeviationPoint> BuildScreenDeviation(
        List<TrackMapTracePoint> refTrace,
        List<TrackMapTracePoint> cmpTrace,
        Func<double, double, Point> mapRaw)
    {
        var refByBin = refTrace.GroupBy(p => RoundTo10(p.DistanceM)).ToDictionary(g => g.Key, g => g.Last());
        var cmpByBin = cmpTrace.GroupBy(p => RoundTo10(p.DistanceM)).ToDictionary(g => g.Key, g => g.Last());
        var result = new List<ScreenDeviationPoint>();
        foreach (var bin in refByBin.Keys.Intersect(cmpByBin.Keys).OrderBy(x => x))
        {
            var r = refByBin[bin];
            var c = cmpByBin[bin];
            var reference = mapRaw(r.X, r.Z);
            var compare = mapRaw(c.X, c.Z);
            var dx = compare.X - reference.X;
            var dy = compare.Y - reference.Y;
            var offsetMeters = Math.Sqrt(Math.Pow(c.X - r.X, 2) + Math.Pow(c.Z - r.Z, 2));
            var offsetPixels = Math.Sqrt(dx * dx + dy * dy);
            result.Add(new ScreenDeviationPoint(bin, reference, compare, offsetMeters, offsetPixels));
        }
        return result;
    }

    private static void DrawPathOffsetConnectors(DrawingContext context, List<ScreenDeviationPoint> deviation, Dictionary<int, double> valueByBin)
    {
        foreach (var p in deviation)
        {
            if (p.Bin % 30 != 0) continue;
            valueByBin.TryGetValue(p.Bin, out var value);
            var color = value >= 0 ? Color.FromArgb(145, 255, 90, 90) : Color.FromArgb(145, 80, 170, 255);
            context.DrawLine(new Pen(new SolidColorBrush(color), 1.8), p.Reference, p.Compare);
            context.DrawEllipse(new SolidColorBrush(color), null, p.Compare, 2.8, 2.8);
        }
    }

    private static void DrawZoneSegment(DrawingContext context, List<TrackPoint> points, Func<TrackPoint, Point> map, TrackMapInsight insight, Pen pen)
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
        var from = Math.Max(1, idx - 3);
        var to = Math.Min(points.Count - 1, idx + 3);
        for (var i = from; i <= to; i++) context.DrawLine(pen, map(points[i - 1]), map(points[i]));
    }

    private static void DrawPeakMarker(DrawingContext context, List<TrackPoint> points, Func<TrackPoint, Point> map, TrackMapInsight insight, IBrush textBrush)
    {
        var p = map(NearestPoint(points, insight.PeakDistanceM));
        var fill = new SolidColorBrush(InsightColor(insight.Kind, 245));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 2.2);
        context.DrawEllipse(fill, border, p, 13, 13);
        DrawText(context, $"#{insight.Rank}", 12, textBrush, new Point(p.X + 13, p.Y - 8));
    }

    private static void DrawCornerLabels(DrawingContext context, TrackProfile profile, List<TrackPoint> allPoints, double fromM, double toM, Func<TrackPoint, Point> map)
    {
        foreach (var c in profile.Corners.Where(c => c.DistanceM >= fromM && c.DistanceM <= toM))
        {
            var nearest = NearestPoint(allPoints, c.DistanceM);
            var p = map(nearest);
            var pt = new Point(p.X + c.XOffset * 0.45, p.Y + c.YOffset * 0.45);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(220, 16, 19, 24)), new Rect(pt.X - 4, pt.Y - 3, Math.Max(44, c.Label.Length * 7.8), 19));
            DrawText(context, c.Label, 12, Brushes.White, pt);
        }
    }

    private static void DrawDirectionArrow(DrawingContext context, List<TrackPoint> points, Func<TrackPoint, Point> map, IBrush textBrush)
    {
        if (points.Count < 12) return;
        var startIdx = Math.Clamp(points.Count / 3, 0, points.Count - 2);
        var endIdx = Math.Clamp(startIdx + Math.Max(6, points.Count / 14), startIdx + 1, points.Count - 1);
        var start = map(points[startIdx]);
        var end = map(points[endIdx]);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 5) return;
        dx /= len;
        dy /= len;
        var arrowEnd = new Point(start.X + dx * 54, start.Y + dy * 54);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(235, 120, 210, 255)), 3.2);
        context.DrawLine(pen, start, arrowEnd);
        var left = new Point(arrowEnd.X - dx * 12 - dy * 7, arrowEnd.Y - dy * 12 + dx * 7);
        var right = new Point(arrowEnd.X - dx * 12 + dy * 7, arrowEnd.Y - dy * 12 - dx * 7);
        context.DrawLine(pen, arrowEnd, left);
        context.DrawLine(pen, arrowEnd, right);
        DrawText(context, "direction", 10, textBrush, new Point(arrowEnd.X + 7, arrowEnd.Y - 8));
    }

    private static void DrawRangeChip(DrawingContext context, Rect bounds, double fromM, double toM, TrackMapInsight insight, List<ScreenDeviationPoint> zoneDeviation, IBrush textBrush)
    {
        var x = 18.0;
        var y = bounds.Height - 64;
        var avgOffset = zoneDeviation.Count == 0 ? 0 : zoneDeviation.Average(p => p.OffsetMeters);
        var maxOffset = zoneDeviation.Count == 0 ? 0 : zoneDeviation.Max(p => p.OffsetMeters);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(190, 16, 19, 24)), new Rect(x - 6, y - 7, 730, 25));
        DrawText(context, $"Zoom: {fromM:0}m to {toM:0}m    Peak: {insight.PeakDistanceM:0}m    {insight.NearestCornerLabel}    offset avg/max: {avgOffset:0.00}/{maxOffset:0.00}m", 12, textBrush, new Point(x, y - 4));
    }

    private static Color InsightColor(string kind, byte alpha)
    {
        return kind.Equals("LOSS", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(alpha, 255, 74, 74)
            : Color.FromArgb(alpha, 55, 165, 255);
    }

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
        var baseScale = metric == "cumulative_delta_ms" ? Math.Max(50.0, scale) : Math.Max(1.0, scale);
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
        if (normalized > 0) return Lerp(Color.FromRgb(250, 250, 250), Color.FromRgb(220, 25, 25), normalized);
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
