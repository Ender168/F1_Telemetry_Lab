using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace F1TelemetryLab;

public sealed record TrackProfile(
    int TrackId,
    string TrackName,
    double TrackLengthM,
    List<TrackPoint> Points,
    List<TrackCorner> Corners,
    TrackBoundary? Boundary,
    string DistanceSource = "geometric_xz");

public sealed record TrackPoint(double DistanceM, double X, double Z);
public sealed record TrackBoundary(
    int TrackId,
    string TrackName,
    string Source,
    double TrackLengthM,
    List<TrackBoundaryPoint> Points);
public sealed record TrackBoundaryPoint(
    double DistanceM,
    double RacingX,
    double RacingZ,
    double LeftTrackX,
    double LeftTrackZ,
    double RightTrackX,
    double RightTrackZ,
    double LeftWhiteX,
    double LeftWhiteZ,
    double RightWhiteX,
    double RightWhiteZ,
    double LeftRunOffX,
    double LeftRunOffZ,
    double RightRunOffX,
    double RightRunOffZ);
public sealed record TrackCorner(double DistanceM, string Label, string Side, double XOffset, double YOffset, bool IsEstimated = false);
public sealed record TrackMapValue(double DistanceM, double Value);
public sealed record TrackMapTracePoint(double DistanceM, double X, double Z);
public sealed record TrackMapInsight(
    int Rank,
    string Kind,
    double StartM,
    double EndM,
    double PeakDistanceM,
    double Value,
    string NearestCornerLabel,
    string Label)
{
    public override string ToString() => Label;
}

public sealed record TrackMapRenderData(
    TrackProfile? Profile,
    string Metric,
    string ReferenceLabel,
    string CompareLabel,
    string Status,
    List<TrackMapValue> Values,
    List<TrackMapTracePoint> ReferenceTrace,
    List<TrackMapTracePoint> CompareTrace,
    List<TrackMapInsight> Insights);

public static class RacenetSplineDataService
{
    private const string AustriaSplineFile = "Track_17_Austria_RacenetSpline.json";

    public static TrackBoundary? LoadBoundary(string rootFolder, int trackId, string fallbackName)
    {
        if (trackId != 17) return null;
        foreach (var path in CandidateBoundaryPaths(rootFolder, trackId))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var boundary = Parse(File.ReadAllText(path), trackId, string.IsNullOrWhiteSpace(fallbackName) ? "Austria" : fallbackName, Path.GetFileName(path));
                if (boundary.Points.Count >= 20) return boundary;
            }
            catch
            {
                // Ignore malformed user-supplied files. Track Map can fall back to old centerline drawing.
            }
        }
        return null;
    }

    public static TrackBoundary ScaleDistances(TrackBoundary boundary, double ratio, double newLengthM)
    {
        var scaled = boundary.Points
            .Select(p => p with { DistanceM = p.DistanceM * ratio })
            .ToList();
        return boundary with { TrackLengthM = newLengthM, Points = scaled };
    }

    private static IEnumerable<string> CandidateBoundaryPaths(string rootFolder, int trackId)
    {
        var file = trackId == 17 ? AustriaSplineFile : $"Track_{trackId}_RacenetSpline.json";
        var roots = new[]
        {
            Path.Combine(rootFolder, "data", "tracks", "racenet"),
            Path.Combine(rootFolder, "data", "tracks"),
            Path.Combine(AppContext.BaseDirectory, "data", "tracks", "racenet"),
            Path.Combine(AppContext.BaseDirectory, "data", "tracks"),
            Path.Combine(Environment.CurrentDirectory, "data", "tracks", "racenet"),
            Path.Combine(Environment.CurrentDirectory, "data", "tracks")
        };
        foreach (var root in roots)
        {
            yield return Path.Combine(root, file);
            if (trackId == 17)
            {
                yield return Path.Combine(root, "Austria_RacenetSpline.json");
                yield return Path.Combine(root, "RedBullRing_RacenetSpline.json");
            }
        }
    }

    private static TrackBoundary Parse(string json, int trackId, string fallbackName, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var track = doc.RootElement.GetProperty("track");
        var gates = track.GetProperty("gates").GetProperty("gate")
            .EnumerateArray()
            .Select(ReadGate)
            .Where(g => g.Name.StartsWith("ai_gate_track_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Id)
            .ToList();

        var result = new List<TrackBoundaryPoint>();
        double distance = 0;
        RacenetGate? previous = null;
        foreach (var gate in gates)
        {
            var point = BuildPoint(gate, distance);
            result.Add(point);
            if (previous is not null)
            {
                // distance for next gate is accumulated along the Racenet racing line
            }
            previous = gate;
            var idx = gates.IndexOf(gate);
            if (idx + 1 < gates.Count)
            {
                var next = gates[idx + 1];
                distance += Distance2D(BuildPoint(gate, 0), BuildPoint(next, 0));
            }
        }

        var trackLength = result.Count > 1
            ? result.Last().DistanceM + Distance2D(result.Last(), result.First())
            : 0;
        var trackName = trackId == 17 ? "Austria" : fallbackName;
        return new TrackBoundary(trackId, trackName, source, trackLength, result);
    }

    private static RacenetGate ReadGate(JsonElement gate)
    {
        var position = gate.GetProperty("position");
        var normal = gate.GetProperty("normal");
        var waypoints = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (gate.TryGetProperty("waypoints", out var waypointsNode) && waypointsNode.TryGetProperty("waypoint", out var waypointArray))
        {
            foreach (var waypoint in waypointArray.EnumerateArray())
            {
                if (!waypoint.TryGetProperty("type", out var typeNode)) continue;
                if (!waypoint.TryGetProperty("length", out var lengthNode)) continue;
                var type = typeNode.GetString();
                if (string.IsNullOrWhiteSpace(type)) continue;
                waypoints[type] = lengthNode.GetDouble();
            }
        }

        return new RacenetGate(
            gate.GetProperty("id").GetInt32(),
            gate.GetProperty("name").GetString() ?? "",
            position.GetProperty("x").GetDouble(),
            position.GetProperty("z").GetDouble(),
            normal.GetProperty("x").GetDouble(),
            normal.GetProperty("z").GetDouble(),
            waypoints);
    }

    private static TrackBoundaryPoint BuildPoint(RacenetGate gate, double distanceM)
    {
        var leftTrack = Offset(gate, ReadLength(gate, "left_track_limit", -6));
        var rightTrack = Offset(gate, ReadLength(gate, "right_track_limit", 6));
        var leftWhite = Offset(gate, ReadLength(gate, "left_white_line", ReadLength(gate, "left_track_limit", -6)));
        var rightWhite = Offset(gate, ReadLength(gate, "right_white_line", ReadLength(gate, "right_track_limit", 6)));
        var leftRunOff = Offset(gate, ReadLength(gate, "left_run_off", ReadLength(gate, "left_track_limit", -6)));
        var rightRunOff = Offset(gate, ReadLength(gate, "right_run_off", ReadLength(gate, "right_track_limit", 6)));
        var racing = Offset(gate, ReadLength(gate, "racing_line", 0));

        return new TrackBoundaryPoint(
            distanceM,
            racing.X, racing.Z,
            leftTrack.X, leftTrack.Z,
            rightTrack.X, rightTrack.Z,
            leftWhite.X, leftWhite.Z,
            rightWhite.X, rightWhite.Z,
            leftRunOff.X, leftRunOff.Z,
            rightRunOff.X, rightRunOff.Z);
    }

    private static double ReadLength(RacenetGate gate, string type, double fallback)
    {
        return gate.Waypoints.TryGetValue(type, out var v) ? v : fallback;
    }

    private static (double X, double Z) Offset(RacenetGate gate, double length)
    {
        return (gate.X + gate.NormalX * length, gate.Z + gate.NormalZ * length);
    }

    private static double Distance2D(TrackBoundaryPoint a, TrackBoundaryPoint b)
    {
        var dx = b.RacingX - a.RacingX;
        var dz = b.RacingZ - a.RacingZ;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private sealed record RacenetGate(
        int Id,
        string Name,
        double X,
        double Z,
        double NormalX,
        double NormalZ,
        Dictionary<string, double> Waypoints);
}

public static class TrackMapDataService
{
    private sealed record SourceTrackPoint(double SourceDistanceM, double X, double Z);
    private sealed record ParsedTrackGeometry(
        List<TrackPoint> Points,
        List<(double SourceDistanceM, double GeometryDistanceM)> DistanceMap,
        double TrackLengthM);

    public static readonly string[] Metrics =
    {
        "segment_loss_ms",
        "cumulative_delta_ms",
        "speed_loss_kmh"
    };

    public static TrackMapRenderData Build(string rootFolder, string sessionFolder, LapOption reference, LapOption compare, string metric)
    {
        var db = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(db)) throw new FileNotFoundException("session.sqlite not found", db);

        using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
        con.Open();
        RequireTable(con, "analysis_trace_10m");
        var trackId = ReadIntMeta(con, "track_id") ?? -1;
        var trackName = ReadStringMeta(con, "track_name") ?? TrackNames.GetTrackName(trackId);
        var trackLength = ReadIntMeta(con, "track_length_m") ?? 0;
        var profile = LoadTrackProfile(rootFolder, trackId, trackName, trackLength);

        var refTrace = CleanTrace(LoadTrace(con, reference));
        var cmpTrace = CleanTrace(LoadTrace(con, compare));
        var values = BuildValues(refTrace, cmpTrace, metric);
        var insights = BuildInsights(values, metric, profile);
        var status = profile is null
            ? $"Track profile unavailable for track_id={trackId}; telemetry trace fallback is active."
            : $"{profile.TrackName}: {profile.Points.Count:N0} map points, {profile.Corners.Count:N0} labels, geometric X/Z distance normalized to {profile.TrackLengthM:0}m, {BoundaryStatus(profile)}. {reference.Code} vs {compare.Code}.";

        return new TrackMapRenderData(
            profile,
            metric,
            reference.ShortLabel,
            compare.ShortLabel,
            status,
            values,
            refTrace.Select(p => new TrackMapTracePoint(p.DistanceM, p.X, p.Z)).ToList(),
            cmpTrace.Select(p => new TrackMapTracePoint(p.DistanceM, p.X, p.Z)).ToList(),
            insights);
    }


    private static string BoundaryStatus(TrackProfile profile)
    {
        return profile.Boundary is null
            ? "no track-boundary spline"
            : $"Racenet boundary {profile.Boundary.Points.Count:N0} gates";
    }

    public static TrackProfile? LoadTrackProfile(string rootFolder, int trackId, string fallbackName, double gameTrackLengthM = 0)
    {
        if (trackId < 0) return null;
        var roots = CandidateTrackRoots(rootFolder).ToList();
        foreach (var root in roots)
        {
            var trackPath = Path.Combine(root, $"Track_{trackId}.csv");
            var settingsPath = Path.Combine(root, "Description", $"Track_Settings_{trackId}.csv");
            if (!File.Exists(trackPath)) continue;
            var (name, settingsLength, sourceCorners) = File.Exists(settingsPath)
                ? ParseSettings(settingsPath, fallbackName)
                : (fallbackName, 0.0, new List<TrackCorner>());
            var targetLength = gameTrackLengthM > 1_000 ? gameTrackLengthM : settingsLength;
            var geometry = ParseTrackPoints(trackPath, targetLength);
            var points = geometry.Points;
            if (points.Count == 0) continue;
            var length = geometry.TrackLengthM;
            var corners = RemapCorners(sourceCorners, geometry.DistanceMap);
            corners = ApplyManualCornerOverrides(trackId, corners);
            var boundary = RacenetSplineDataService.LoadBoundary(rootFolder, trackId, string.IsNullOrWhiteSpace(name) ? fallbackName : name);
            if (boundary is not null && length > 1000 && boundary.TrackLengthM > 1000)
            {
                var ratio = length / boundary.TrackLengthM;
                if (ratio > 0.95 && ratio < 1.05) boundary = RacenetSplineDataService.ScaleDistances(boundary, ratio, length);
            }
            return new TrackProfile(trackId, string.IsNullOrWhiteSpace(name) ? fallbackName : name, length, points, corners, boundary);
        }

        var fallbackBoundary = RacenetSplineDataService.LoadBoundary(rootFolder, trackId, fallbackName);
        if (fallbackBoundary is not null)
        {
            var points = fallbackBoundary.Points
                .Select(p => new TrackPoint(p.DistanceM, p.RacingX, p.RacingZ))
                .ToList();
            var corners = ApplyManualCornerOverrides(trackId, new List<TrackCorner>());
            return new TrackProfile(trackId, fallbackBoundary.TrackName, fallbackBoundary.TrackLengthM, points, corners, fallbackBoundary);
        }

        return null;
    }

    private static IEnumerable<string> CandidateTrackRoots(string rootFolder)
    {
        var roots = new[]
        {
            Path.Combine(rootFolder, "data", "tracks"),
            Path.Combine(rootFolder, "data", "tracks", "Tracks"),
            Path.Combine(rootFolder, "Tracks"),
            Path.Combine(rootFolder, "tracks"),
            Path.Combine(AppContext.BaseDirectory, "data", "tracks"),
            Path.Combine(AppContext.BaseDirectory, "data", "tracks", "Tracks")
        };
        foreach (var r in roots)
        {
            if (Directory.Exists(r)) yield return r;
            var nested = Path.Combine(r, "Tracks");
            if (Directory.Exists(nested)) yield return nested;
        }
    }

    private static ParsedTrackGeometry ParseTrackPoints(string path, double targetLengthM)
    {
        var source = new List<SourceTrackPoint>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var cm)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
            source.Add(new SourceTrackPoint(cm / 100.0, x / 100.0, z / 100.0));
        }
        if (source.Count < 2) return new ParsedTrackGeometry(new List<TrackPoint>(), new List<(double, double)>(), 0);

        var cumulative = new double[source.Count];
        for (var i = 1; i < source.Count; i++)
        {
            var dx = source[i].X - source[i - 1].X;
            var dz = source[i].Z - source[i - 1].Z;
            cumulative[i] = cumulative[i - 1] + Math.Sqrt(dx * dx + dz * dz);
        }

        var closeDx = source[0].X - source[^1].X;
        var closeDz = source[0].Z - source[^1].Z;
        var closedLength = cumulative[^1] + Math.Sqrt(closeDx * closeDx + closeDz * closeDz);
        if (closedLength <= 0) return new ParsedTrackGeometry(new List<TrackPoint>(), new List<(double, double)>(), 0);
        var normalizedLength = targetLengthM > 1_000 ? targetLengthM : closedLength;
        var scale = normalizedLength / closedLength;

        var points = source
            .Select((point, index) => new TrackPoint(cumulative[index] * scale, point.X, point.Z))
            .ToList();
        if (points[^1].DistanceM < normalizedLength - 0.5)
            points.Add(new TrackPoint(normalizedLength, points[0].X, points[0].Z));

        var distanceMap = source
            .Select((point, index) => (point.SourceDistanceM, cumulative[index] * scale))
            .OrderBy(point => point.SourceDistanceM)
            .ToList();
        return new ParsedTrackGeometry(points, distanceMap, normalizedLength);
    }

    private static List<TrackCorner> RemapCorners(
        IReadOnlyList<TrackCorner> corners,
        IReadOnlyList<(double SourceDistanceM, double GeometryDistanceM)> distanceMap)
    {
        if (corners.Count == 0 || distanceMap.Count < 2) return corners.ToList();
        return corners
            .Select(corner => corner with { DistanceM = MapSourceDistance(distanceMap, corner.DistanceM) })
            .OrderBy(corner => corner.DistanceM)
            .ToList();
    }

    private static double MapSourceDistance(
        IReadOnlyList<(double SourceDistanceM, double GeometryDistanceM)> map,
        double sourceDistanceM)
    {
        if (sourceDistanceM <= map[0].SourceDistanceM) return map[0].GeometryDistanceM;
        if (sourceDistanceM >= map[^1].SourceDistanceM) return map[^1].GeometryDistanceM;
        var high = 1;
        while (high < map.Count && map[high].SourceDistanceM < sourceDistanceM) high++;
        var low = high - 1;
        var sourceSpan = map[high].SourceDistanceM - map[low].SourceDistanceM;
        if (sourceSpan <= 0) return map[low].GeometryDistanceM;
        var t = (sourceDistanceM - map[low].SourceDistanceM) / sourceSpan;
        return map[low].GeometryDistanceM + (map[high].GeometryDistanceM - map[low].GeometryDistanceM) * t;
    }

    private static (string Name, double Length, List<TrackCorner> Corners) ParseSettings(string path, string fallbackName)
    {
        var name = fallbackName;
        var length = 0.0;
        var corners = new List<TrackCorner>();
        var inDesc = false;
        var first = true;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(';');
            if (first)
            {
                first = false;
                var raw = parts[0].Replace("Settings for Track-ID", "", StringComparison.OrdinalIgnoreCase).Trim();
                var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2) name = string.Join(' ', tokens.Skip(1));
            }
            if (parts.Length >= 2 && parts[0].Trim().Equals("TrackLength", StringComparison.OrdinalIgnoreCase))
            {
                double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out length);
            }
            if (parts[0].Trim().Equals("TrackDescription", StringComparison.OrdinalIgnoreCase))
            {
                inDesc = true;
                continue;
            }
            if (!inDesc) continue;
            if (parts.Length < 2) continue;
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cm)) continue;
            var label = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(label)) continue;
            var side = parts.Length > 2 ? parts[2].Trim() : "R";
            var xOff = ParseDouble(parts, 3);
            var yOff = ParseDouble(parts, 4);
            corners.Add(new TrackCorner(cm / 100.0, label, side, xOff, yOff));
        }
        return (name, length, corners.OrderBy(c => c.DistanceM).ToList());
    }



    private static List<TrackCorner> ApplyManualCornerOverrides(int trackId, List<TrackCorner> sourceCorners)
    {
        // Track_17 (Austria / Red Bull Ring) source settings from Team Telemetry-style files
        // contain only major labels: 1,3,4,6,7,9,10. The game/F1 layout numbers the small
        // kinks as 2,5,8, so we add practical estimated positions for a complete 1-10 display.
        // Source labels remain untouched; missing labels are marked as estimated for the side list.
        if (trackId != 17) return sourceCorners.OrderBy(c => c.DistanceM).ToList();

        var result = sourceCorners.ToList();
        bool HasTurn(int n) => result.Any(c => c.Label.Equals($"Turn {n}", StringComparison.OrdinalIgnoreCase));
        void AddMissing(int n, double distanceM, string side, double xOff, double yOff)
        {
            if (!HasTurn(n)) result.Add(new TrackCorner(distanceM, $"Turn {n}", side, xOff, yOff, true));
        }

        AddMissing(1, 384, "R", 18, -24);
        AddMissing(2, 1171, "R", 18, -24);
        AddMissing(3, 1364, "R", 10, -30);
        AddMissing(4, 2230, "R", 18, -18);
        AddMissing(5, 2440, "L", -34, 12);
        AddMissing(6, 2714, "L", -34, -6);
        AddMissing(7, 3094, "L", 12, 20);
        AddMissing(8, 3290, "R", 10, -32);
        AddMissing(9, 3790, "R", 12, -20);
        AddMissing(10, 3990, "R", 12, -20);
        return result.OrderBy(c => c.DistanceM).ToList();
    }

    private static double ParseDouble(string[] parts, int index)
    {
        if (index >= parts.Length) return 0;
        return double.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private sealed record TracePoint(int DistanceM, double TimeMs, double Speed, double X, double Z);

    private static List<TracePoint> LoadTrace(SqliteConnection con, LapOption lap)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT distance_bin_m, time_ms, speed, world_position_x, world_position_z
        FROM analysis_trace_10m
        WHERE car_idx = $car AND lap_num = $lap
        ORDER BY distance_bin_m
        """;
        cmd.Parameters.AddWithValue("$car", lap.CarIndex);
        cmd.Parameters.AddWithValue("$lap", lap.LapNum);
        using var reader = cmd.ExecuteReader();
        var result = new List<TracePoint>();
        while (reader.Read())
        {
            result.Add(new TracePoint(reader.GetInt32(0), D(reader, 1), D(reader, 2), D(reader, 3), D(reader, 4)));
        }
        return result;
    }

    private static List<TracePoint> CleanTrace(List<TracePoint> points)
    {
        var result = new List<TracePoint>();
        double lastTime = -1;
        foreach (var p in points.OrderBy(x => x.DistanceM))
        {
            if (double.IsNaN(p.TimeMs) || double.IsInfinity(p.TimeMs)) continue;
            if (p.TimeMs <= 100 && p.DistanceM > 200) continue;
            if (lastTime > 0 && p.TimeMs + 1000 < lastTime) continue;
            if (lastTime > 0 && p.TimeMs - lastTime > 15000) continue;
            result.Add(p);
            if (p.TimeMs > lastTime) lastTime = p.TimeMs;
        }
        return result;
    }

    private static List<TrackMapValue> BuildValues(List<TracePoint> reference, List<TracePoint> compare, string metric)
    {
        var referencePoints = reference
            .GroupBy(x => x.DistanceM)
            .Select(g => g.Last())
            .OrderBy(x => x.DistanceM)
            .ToList();
        var comparePoints = compare
            .GroupBy(x => x.DistanceM)
            .Select(g => g.Last())
            .OrderBy(x => x.DistanceM)
            .ToList();
        var values = new List<TrackMapValue>();
        var deltas = new List<(int DistanceM, double DeltaMs)>();
        foreach (var r in referencePoints)
        {
            var compareTime = DistanceSeriesInterpolator.Linear(comparePoints, r.DistanceM, p => p.DistanceM, p => p.TimeMs);
            var compareSpeed = DistanceSeriesInterpolator.Linear(comparePoints, r.DistanceM, p => p.DistanceM, p => p.Speed);
            if (compareTime is null) continue;
            var delta = compareTime.Value - r.TimeMs;
            if (Math.Abs(delta) > 10000) continue;
            var segmentBaseline = deltas.LastOrDefault(x => x.DistanceM <= r.DistanceM - 30);
            var hasSegmentBaseline = deltas.Any(x => x.DistanceM <= r.DistanceM - 30);
            var value = metric switch
            {
                "cumulative_delta_ms" => delta,
                "speed_loss_kmh" => compareSpeed is null ? double.NaN : r.Speed - compareSpeed.Value,
                _ => hasSegmentBaseline ? delta - segmentBaseline.DeltaMs : 0
            };
            deltas.Add((r.DistanceM, delta));
            if (!double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 5000)
                values.Add(new TrackMapValue(r.DistanceM, value));
        }
        return values;
    }

    private static List<TrackMapInsight> BuildInsights(List<TrackMapValue> values, string metric, TrackProfile? profile)
    {
        if (values.Count == 0) return new List<TrackMapInsight>();
        var ordered = values.OrderBy(v => v.DistanceM).ToList();
        var abs = ordered.Select(v => Math.Abs(v.Value)).Where(v => v > 0.0001).OrderBy(v => v).ToList();
        if (abs.Count == 0) return new List<TrackMapInsight>();

        var p75 = abs[(int)Math.Clamp(Math.Floor(abs.Count * 0.75), 0, abs.Count - 1)];
        var minimum = metric switch
        {
            "cumulative_delta_ms" => 35.0,
            "speed_loss_kmh" => 2.0,
            _ => 8.0
        };
        var threshold = Math.Max(minimum, p75 * 0.35);

        var groups = new List<List<TrackMapValue>>();
        List<TrackMapValue>? current = null;
        int currentSign = 0;
        double lastDistance = -9999;
        foreach (var v in ordered)
        {
            if (Math.Abs(v.Value) < threshold) { current = null; currentSign = 0; continue; }
            var sign = v.Value >= 0 ? 1 : -1;
            if (current is null || sign != currentSign || v.DistanceM - lastDistance > 35)
            {
                current = new List<TrackMapValue>();
                groups.Add(current);
                currentSign = sign;
            }
            current.Add(v);
            lastDistance = v.DistanceM;
        }

        var ranked = groups
            .Where(g => g.Count > 0)
            .Select(g =>
            {
                var avg = g.Average(x => x.Value);
                var maxAbsPoint = g.OrderByDescending(x => Math.Abs(x.Value)).First();
                var kind = avg >= 0 ? "LOSS" : "GAIN";
                return new
                {
                    Kind = kind,
                    StartM = g.First().DistanceM,
                    EndM = g.Last().DistanceM,
                    PeakDistanceM = maxAbsPoint.DistanceM,
                    Value = maxAbsPoint.Value,
                    AbsValue = Math.Abs(maxAbsPoint.Value)
                };
            })
            .OrderByDescending(x => x.AbsValue)
            .ThenBy(x => x.StartM)
            .Take(10)
            .ToList();

        var result = new List<TrackMapInsight>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var x = ranked[i];
            var corner = DescribeInsightCorner(profile?.Corners, x.StartM, x.EndM, x.PeakDistanceM);
            var label = FormatInsight(i + 1, x.Kind, x.StartM, x.EndM, x.Value, metric, corner);
            result.Add(new TrackMapInsight(i + 1, x.Kind, x.StartM, x.EndM, x.PeakDistanceM, x.Value, corner, label));
        }

        return result;
    }

    private static string FormatInsight(int rank, string kind, double startM, double endM, double value, string metric, string corner)
    {
        var range = endM <= startM + 1 ? $"{startM:0}m" : $"{startM:0}-{endM:0}m";
        var formatted = metric switch
        {
            "speed_loss_kmh" => $"{value:+0.0;-0.0;0.0} km/h",
            _ => $"{value:+0;-0;0} ms"
        };
        var at = string.IsNullOrWhiteSpace(corner) ? "track" : corner;
        return $"#{rank,-2} {kind,-4}  {range,-11}  {formatted,9}  {at}";
    }

    private static string DescribeInsightCorner(List<TrackCorner>? corners, double startM, double endM, double peakM)
    {
        if (corners is null || corners.Count == 0) return "";
        var mid = (startM + endM) / 2.0;
        var nearest = corners.OrderBy(c => Math.Abs(c.DistanceM - mid)).First();
        var delta = mid - nearest.DistanceM;
        if (Math.Abs(delta) <= 80) return nearest.Label;
        if (delta < 0 && Math.Abs(delta) <= 220) return $"approach {nearest.Label}";
        if (delta > 0 && Math.Abs(delta) <= 220) return $"{nearest.Label} exit";

        var previous = corners.Where(c => c.DistanceM < mid).OrderByDescending(c => c.DistanceM).FirstOrDefault();
        var next = corners.Where(c => c.DistanceM > mid).OrderBy(c => c.DistanceM).FirstOrDefault();
        if (previous is not null && next is not null) return $"{previous.Label} to {next.Label}";
        if (previous is not null) return $"after {previous.Label}";
        if (next is not null) return $"before {next.Label}";
        return nearest.Label;
    }

    private static int? ReadIntMeta(SqliteConnection con, string key)
    {
        var value = ReadStringMeta(con, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static string? ReadStringMeta(SqliteConnection con, string key)
    {
        if (!TableExists(con, "session_metadata")) return null;
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT value FROM session_metadata WHERE key=$key LIMIT 1";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", table);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static void RequireTable(SqliteConnection con, string table)
    {
        if (!TableExists(con, table)) throw new InvalidOperationException($"Table '{table}' not found. Run Analyze selected session first.");
    }

    private static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? double.NaN : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
}
