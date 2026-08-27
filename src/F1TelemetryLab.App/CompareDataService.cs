using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;

namespace F1TelemetryLab;

public sealed record LapOption(int CarIndex, int LapNum, bool IsPlayer, bool CleanLap, int RewindCount, int InvalidCount, double LapTimeMs, string DriverName, string ShortName, bool IsBestLap)
{
    public string DisplayName => IsPlayer ? "YOU" : CleanName(DriverName);
    public string Code => CleanShort(ShortName, CarIndex, IsPlayer);
    public string Label => $"#{CarIndex:00} {Code} {DisplayName}  {(IsBestLap ? "★ " : "  ")}Lap {LapNum}  {FormatLapTime(LapTimeMs)}  {(CleanLap ? "clean" : "dirty")}  rew:{RewindCount}";
    public string ShortLabel => $"#{CarIndex:00} {Code} {DisplayName} L{LapNum} {FormatLapTime(LapTimeMs)}";
    public override string ToString() => Label;

    public static string FormatLapTime(double ms)
    {
        if (double.IsNaN(ms) || ms <= 0) return "--:--.---";
        var total = TimeSpan.FromMilliseconds(ms);
        return $"{(int)total.TotalMinutes}:{total.Seconds:00}.{total.Milliseconds:000}";
    }

    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "CAR";
        if (name.Any(ch => char.IsControl(ch) || ch == '\uFFFD')) return "CAR";
        var trimmed = name.Trim();
        return trimmed.Length > 22 ? trimmed[..22] : trimmed;
    }

    private static string CleanShort(string value, int carIndex, bool isPlayer)
    {
        var clean = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length >= 2) return clean.Length > 4 ? clean[..4] : clean;
        return isPlayer ? "YOU" : $"C{carIndex:00}";
    }
}

public sealed record DriverOption(int CarIndex, bool IsPlayer, string DriverName, string ShortName, double BestCleanLapMs, int CleanLapCount, int TotalLapCount)
{
    public string DisplayName => IsPlayer ? "YOU" : CleanName(DriverName);
    public string Code => CleanShort(ShortName, CarIndex, IsPlayer);
    public string Label => $"#{CarIndex:00} {Code} {DisplayName}  best {LapOption.FormatLapTime(BestCleanLapMs)}  clean:{CleanLapCount}/{TotalLapCount}";
    public override string ToString() => Label;

    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "CAR";
        if (name.Any(ch => char.IsControl(ch) || ch == '\uFFFD')) return "CAR";
        var trimmed = name.Trim();
        return trimmed.Length > 22 ? trimmed[..22] : trimmed;
    }

    private static string CleanShort(string value, int carIndex, bool isPlayer)
    {
        var clean = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length >= 2) return clean.Length > 4 ? clean[..4] : clean;
        return isPlayer ? "YOU" : $"C{carIndex:00}";
    }
}

public sealed record ComparePoint(int DistanceBinM, double Value);
public sealed record CompareSeries(string Name, List<ComparePoint> Points);

public static class CompareDataService
{
    public static readonly string[] Metrics =
    {
        "speed",
        "throttle_%",
        "brake_%",
        "steer",
        "gear",
        "delta_ms"
    };

    public static List<LapOption> LoadLapOptions(string sessionFolder, bool cleanOnly)
    {
        var db = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(db)) throw new FileNotFoundException("session.sqlite not found", db);

        using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
        con.Open();
        RequireTable(con, "lap_summary");

        var hasFinal = TableExists(con, "final_classification");
        var hasDisplay = hasFinal && ColumnExists(con, "final_classification", "display_name");
        var hasShort = hasFinal && ColumnExists(con, "final_classification", "short_name");
        var namesCte = hasFinal
            ? $"""
        WITH names AS (
            SELECT car_idx,
                   COALESCE(NULLIF({(hasDisplay ? "display_name" : "name")},''), NULLIF(name,''), 'F1 Generic') AS name,
                   COALESCE(NULLIF({(hasShort ? "short_name" : "''")},''), '') AS short_name
            FROM final_classification
        )
        """
            : """
        WITH names AS (
            SELECT p.car_idx, p.name, '' AS short_name
            FROM participants p
            JOIN (
                SELECT car_idx, MAX(frame_identifier) AS max_frame
                FROM participants
                GROUP BY car_idx
            ) x ON x.car_idx = p.car_idx AND x.max_frame = p.frame_identifier
        )
        """;

        var sql = namesCte + """
        SELECT s.car_idx, s.lap_num, s.is_player, s.clean_lap, s.rewind_count, s.invalid_count, s.lap_time_ms,
               COALESCE(n.name, '') AS name,
               COALESCE(n.short_name, '') AS short_name
        FROM lap_summary s
        LEFT JOIN names n ON n.car_idx = s.car_idx
        WHERE s.lap_num > 0 AND s.lap_time_ms > 0
        """ + (cleanOnly ? " AND s.clean_lap = 1 " : " ") + """
        ORDER BY s.is_player DESC, s.car_idx ASC, s.clean_lap DESC, s.lap_time_ms ASC, s.lap_num ASC
        """;

        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var baseRows = new List<LapOption>();
        while (reader.Read())
        {
            baseRows.Add(new LapOption(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2) == 1,
                reader.GetInt32(3) == 1,
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                D(reader, 6),
                reader.IsDBNull(7) ? "" : reader.GetString(7),
                reader.IsDBNull(8) ? "" : reader.GetString(8),
                false));
        }

        var bestKeys = baseRows
            .GroupBy(x => x.CarIndex)
            .Select(g =>
            {
                var clean = g.Where(x => x.CleanLap).OrderBy(x => x.LapTimeMs).FirstOrDefault();
                var best = clean ?? g.OrderBy(x => x.LapTimeMs).First();
                return (best.CarIndex, best.LapNum);
            })
            .ToHashSet();

        return baseRows
            .Select(x => x with { IsBestLap = bestKeys.Contains((x.CarIndex, x.LapNum)) })
            .OrderByDescending(x => x.IsPlayer)
            .ThenBy(x => x.CarIndex)
            .ThenByDescending(x => x.IsBestLap)
            .ThenByDescending(x => x.CleanLap)
            .ThenBy(x => x.LapTimeMs)
            .ThenBy(x => x.LapNum)
            .ToList();
    }

    public static List<DriverOption> LoadDrivers(string sessionFolder, bool cleanOnly)
    {
        var laps = LoadLapOptions(sessionFolder, cleanOnly: false);
        return laps
            .GroupBy(x => x.CarIndex)
            .Select(g =>
            {
                var clean = g.Where(x => x.CleanLap).OrderBy(x => x.LapTimeMs).FirstOrDefault();
                var best = clean ?? g.OrderBy(x => x.LapTimeMs).First();
                return new DriverOption(
                    best.CarIndex,
                    g.Any(x => x.IsPlayer),
                    best.DriverName,
                    best.ShortName,
                    best.LapTimeMs,
                    g.Count(x => x.CleanLap),
                    g.Count());
            })
            .Where(d => !cleanOnly || d.CleanLapCount > 0)
            .OrderByDescending(x => x.IsPlayer)
            .ThenBy(x => x.BestCleanLapMs)
            .ThenBy(x => x.CarIndex)
            .ToList();
    }

    public static List<LapOption> LoadLapOptionsForDriver(string sessionFolder, int carIndex, bool cleanOnly)
    {
        return LoadLapOptions(sessionFolder, cleanOnly)
            .Where(x => x.CarIndex == carIndex)
            .OrderByDescending(x => x.IsBestLap)
            .ThenByDescending(x => x.CleanLap)
            .ThenBy(x => x.LapTimeMs)
            .ThenBy(x => x.LapNum)
            .ToList();
    }

    public static List<LapOption> LoadBestCleanLaps(string sessionFolder, int take)
    {
        return LoadLapOptions(sessionFolder, cleanOnly: true)
            .GroupBy(x => x.CarIndex)
            .Select(g => g.OrderBy(x => x.LapTimeMs).First())
            .OrderBy(x => x.LapTimeMs)
            .Take(take)
            .ToList();
    }

    public static List<LapOption> LoadBestAvailableLaps(string sessionFolder, int take)
    {
        return LoadLapOptions(sessionFolder, cleanOnly: false)
            .GroupBy(x => x.CarIndex)
            .Select(g =>
                g.Where(x => x.CleanLap).OrderBy(x => x.LapTimeMs).FirstOrDefault()
                ?? g.OrderBy(x => x.LapTimeMs).First())
            .OrderByDescending(x => x.CleanLap)
            .ThenBy(x => x.LapTimeMs)
            .Take(take)
            .ToList();
    }

    public static List<LapOption> LoadYouVsTop(string sessionFolder, int totalSlots)
    {
        var best = LoadBestCleanLaps(sessionFolder, 50);
        var you = best.FirstOrDefault(x => x.IsPlayer);
        var result = new List<LapOption>();
        if (you is not null) result.Add(you);
        result.AddRange(best.Where(x => you is null || x.CarIndex != you.CarIndex).Take(Math.Max(0, totalSlots - result.Count)));
        return result.Take(totalSlots).ToList();
    }

    public static List<LapOption> LoadYouVsTopAvailable(string sessionFolder, int totalSlots)
    {
        var best = LoadBestAvailableLaps(sessionFolder, 50);
        var you = best.FirstOrDefault(x => x.IsPlayer);
        var result = new List<LapOption>();
        if (you is not null) result.Add(you);
        result.AddRange(best.Where(x => you is null || x.CarIndex != you.CarIndex).Take(Math.Max(0, totalSlots - result.Count)));
        return result.Take(totalSlots).ToList();
    }

    public static List<CompareSeries> LoadSeries(string sessionFolder, IReadOnlyList<LapOption> laps, string metric, int? minDistance = null, int? maxDistance = null)
    {
        var db = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(db)) throw new FileNotFoundException("session.sqlite not found", db);
        if (laps.Count == 0) return new List<CompareSeries>();

        using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
        con.Open();
        RequireTable(con, "analysis_trace_10m");

        var raw = laps.Select(lap => (lap, points: LoadRawTrace(con, lap, minDistance, maxDistance))).ToList();
        if (metric == "delta_ms")
        {
            var cleanRaw = raw.Select(x => (x.lap, points: CleanTimeTrace(x.points))).ToList();
            var reference = cleanRaw[0].points
                .GroupBy(x => x.DistanceBinM)
                .ToDictionary(g => g.Key, g => g.Last().TimeMs);
            return cleanRaw.Select((item, idx) => new CompareSeries(SeriesName(item.lap, idx), item.points
                .Where(p => reference.ContainsKey(p.DistanceBinM))
                .Select(p => new ComparePoint(p.DistanceBinM, p.TimeMs - reference[p.DistanceBinM]))
                .Where(p => !double.IsNaN(p.Value) && !double.IsInfinity(p.Value) && Math.Abs(p.Value) <= 10000)
                .ToList())).ToList();
        }

        return raw.Select((item, idx) => new CompareSeries(SeriesName(item.lap, idx), item.points
            .Select(p => new ComparePoint(p.DistanceBinM, ValueForMetric(p, metric)))
            .Where(p => !double.IsNaN(p.Value) && !double.IsInfinity(p.Value))
            .ToList())).ToList();
    }

    public static string ExportCustomComparison(string sessionFolder, IReadOnlyList<LapOption> laps)
    {
        var exports = Path.Combine(sessionFolder, "exports", "comparison");
        Directory.CreateDirectory(exports);
        var path = Path.Combine(exports, "custom_compare_selected_laps.csv");
        var db = Path.Combine(sessionFolder, "session.sqlite");

        using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
        con.Open();
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("series,car_idx,lap_num,is_player,clean_lap,distance_bin_m,time_ms,speed,throttle,brake,steer,gear,world_position_x,world_position_z");
        foreach (var lap in laps)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
            SELECT distance_bin_m, time_ms, speed, throttle, brake, steer, gear, world_position_x, world_position_z
            FROM analysis_trace_10m
            WHERE car_idx = $car AND lap_num = $lap
            ORDER BY distance_bin_m
            """;
            cmd.Parameters.AddWithValue("$car", lap.CarIndex);
            cmd.Parameters.AddWithValue("$lap", lap.LapNum);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                writer.WriteLine(string.Join(',', new[]
                {
                    Csv(lap.Label),
                    lap.CarIndex.ToString(CultureInfo.InvariantCulture),
                    lap.LapNum.ToString(CultureInfo.InvariantCulture),
                    lap.IsPlayer ? "1" : "0",
                    lap.CleanLap ? "1" : "0",
                    I(reader,0), Num(reader,1), Num(reader,2), Num(reader,3), Num(reader,4), Num(reader,5), Num(reader,6), Num(reader,7), Num(reader,8)
                }));
            }
        }
        return path;
    }

    private sealed record RawTracePoint(int DistanceBinM, double TimeMs, double Speed, double Throttle, double Brake, double Steer, double Gear);

    private static List<RawTracePoint> LoadRawTrace(SqliteConnection con, LapOption lap, int? minDistance, int? maxDistance)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT distance_bin_m, time_ms, speed, throttle, brake, steer, gear
        FROM analysis_trace_10m
        WHERE car_idx = $car AND lap_num = $lap
          AND ($min IS NULL OR distance_bin_m >= $min)
          AND ($max IS NULL OR distance_bin_m <= $max)
        ORDER BY distance_bin_m
        """;
        cmd.Parameters.AddWithValue("$car", lap.CarIndex);
        cmd.Parameters.AddWithValue("$lap", lap.LapNum);
        cmd.Parameters.AddWithValue("$min", minDistance is null ? DBNull.Value : minDistance.Value);
        cmd.Parameters.AddWithValue("$max", maxDistance is null ? DBNull.Value : maxDistance.Value);
        using var reader = cmd.ExecuteReader();
        var result = new List<RawTracePoint>();
        while (reader.Read())
        {
            result.Add(new RawTracePoint(reader.GetInt32(0), D(reader, 1), D(reader, 2), D(reader, 3), D(reader, 4), D(reader, 5), D(reader, 6)));
        }
        return result;
    }

    private static List<RawTracePoint> CleanTimeTrace(List<RawTracePoint> points)
    {
        var result = new List<RawTracePoint>(points.Count);
        double lastTime = -1;
        foreach (var p in points.OrderBy(x => x.DistanceBinM))
        {
            if (p.TimeMs <= 100 && p.DistanceBinM > 200) continue;
            if (lastTime > 0 && p.TimeMs + 1000 < lastTime) continue;
            if (lastTime > 0 && p.TimeMs - lastTime > 15000) continue;
            result.Add(p);
            if (p.TimeMs > lastTime) lastTime = p.TimeMs;
        }
        return result;
    }

    private static double ValueForMetric(RawTracePoint p, string metric) => metric switch
    {
        "speed" => p.Speed,
        "throttle_%" => p.Throttle * 100.0,
        "brake_%" => p.Brake * 100.0,
        "steer" => p.Steer,
        "gear" => p.Gear,
        _ => p.Speed
    };

    private static string SeriesName(LapOption lap, int index)
    {
        return $"{lap.Code} | #{lap.CarIndex:00} {lap.DisplayName} | Lap {lap.LapNum} | {LapOption.FormatLapTime(lap.LapTimeMs)} | {(lap.CleanLap ? "clean" : "dirty")}";
    }

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", table);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static bool ColumnExists(SqliteConnection con, string table, string column)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void RequireTable(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", table);
        var found = cmd.ExecuteScalar() as string;
        if (found != table) throw new InvalidOperationException($"Table '{table}' not found. Run Analyze selected session first.");
    }

    private static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? double.NaN : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
    private static string I(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    private static string Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
