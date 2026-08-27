using Microsoft.Data.Sqlite;
using System.Globalization;

namespace F1TelemetryLab;

public sealed class DriverAliasRow
{
    public int Position { get; init; }
    public int CarIndex { get; init; }
    public bool IsPlayer { get; init; }
    public string OriginalName { get; init; } = "";
    public string DisplayName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public int LapNum { get; init; }
    public double BestLapMs { get; init; }
    public double LastLapMs { get; init; }
    public int Penalties { get; init; }
    public int Warnings { get; init; }

    public string Header => $"P{Position,2}  #{CarIndex:00}  {SafeShort(ShortName, CarIndex, IsPlayer),-3}  {(IsPlayer ? "YOU" : Safe(OriginalName))}  Best {LapOption.FormatLapTime(BestLapMs)}";
    public string FinalLabel => $"P{Position,2}  #{CarIndex:00}  {SafeShort(ShortName, CarIndex, IsPlayer),-3}  {Safe(DisplayName),-18}  Lap {LapNum}  Best {LapOption.FormatLapTime(BestLapMs)}  Last {LapOption.FormatLapTime(LastLapMs)}  Pen {Penalties}s  W {Warnings}";

    public override string ToString() => FinalLabel;

    public static string Safe(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "F1 Generic";
        if (value.Any(ch => char.IsControl(ch) || ch == '\uFFFD')) return "F1 Generic";
        var text = value.Trim();
        return text.Length > 40 ? text[..40] : text;
    }

    public static string SafeShort(string value, int carIndex, bool isPlayer = false)
    {
        var clean = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length >= 2) return clean.Length > 4 ? clean[..4] : clean;
        if (isPlayer) return "YOU";
        return $"C{carIndex:00}";
    }

    public static string MakeShortName(string displayName, int carIndex, bool isPlayer)
    {
        if (isPlayer) return "YOU";
        var clean = Safe(displayName);
        if (string.IsNullOrWhiteSpace(clean) || clean.Equals("F1 Generic", StringComparison.OrdinalIgnoreCase) || clean.Equals("CAR", StringComparison.OrdinalIgnoreCase))
            return $"C{carIndex:00}";
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var source = parts.Length > 1 ? parts[^1] : parts[0];
        var code = new string(source.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (code.Length >= 3) return code[..3];
        if (code.Length > 0) return code;
        return $"C{carIndex:00}";
    }
}

public static class DriverAliasService
{
    public static void EnsureAliasTable(SqliteConnection con)
    {
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS driver_aliases(
                car_idx INTEGER PRIMARY KEY,
                original_name TEXT,
                display_name TEXT,
                short_name TEXT,
                updated_at TEXT
            );
            """;
            cmd.ExecuteNonQuery();
        }
        EnsureColumn(con, "driver_aliases", "short_name", "TEXT");
    }

    public static List<DriverAliasRow> LoadRows(string sessionFolder)
    {
        var db = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(db)) return new List<DriverAliasRow>();
        SQLitePCL.Batteries_V2.Init();
        using var con = new SqliteConnection($"Data Source={db}");
        con.Open();
        EnsureAliasTable(con);
        if (!TableExists(con, "final_classification")) return new List<DriverAliasRow>();
        EnsureFinalColumns(con);
        RefreshFinalClassificationNames(con);

        var originalExpr = ColumnExists(con, "final_classification", "original_name") ? "COALESCE(original_name, name, '')" : "COALESCE(name, '')";
        var displayExpr = ColumnExists(con, "final_classification", "display_name")
            ? "COALESCE(NULLIF(display_name,''), NULLIF(name,''), CASE WHEN is_player = 1 THEN 'YOU' ELSE 'F1 Generic' END)"
            : "COALESCE(NULLIF(name,''), CASE WHEN is_player = 1 THEN 'YOU' ELSE 'F1 Generic' END)";
        var shortExpr = ColumnExists(con, "final_classification", "short_name")
            ? "COALESCE(NULLIF(short_name,''), '')"
            : "''";

        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
        SELECT position, car_idx, is_player,
               {originalExpr} AS original_name,
               {displayExpr} AS display_name,
               {shortExpr} AS short_name,
               lap_num, best_lap_ms, last_lap_time_ms, penalties, warnings
        FROM final_classification
        ORDER BY position, car_idx;
        """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<DriverAliasRow>();
        while (reader.Read())
        {
            var car = reader.GetInt32(1);
            var isPlayer = reader.GetInt32(2) == 1;
            var original = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var display = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var shortName = reader.IsDBNull(5) ? "" : reader.GetString(5);
            rows.Add(new DriverAliasRow
            {
                Position = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                CarIndex = car,
                IsPlayer = isPlayer,
                OriginalName = DriverAliasRow.Safe(original),
                DisplayName = DriverAliasRow.Safe(display),
                ShortName = DriverAliasRow.SafeShort(string.IsNullOrWhiteSpace(shortName) ? DriverAliasRow.MakeShortName(display, car, isPlayer) : shortName, car, isPlayer),
                LapNum = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                BestLapMs = reader.IsDBNull(7) ? 0 : Convert.ToDouble(reader.GetValue(7), CultureInfo.InvariantCulture),
                LastLapMs = reader.IsDBNull(8) ? 0 : Convert.ToDouble(reader.GetValue(8), CultureInfo.InvariantCulture),
                Penalties = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                Warnings = reader.IsDBNull(10) ? 0 : reader.GetInt32(10)
            });
        }
        return rows;
    }

    public static void SaveAlias(string sessionFolder, int carIndex, string originalName, string displayName, string shortName)
    {
        var db = Path.Combine(sessionFolder, "session.sqlite");
        SQLitePCL.Batteries_V2.Init();
        using var con = new SqliteConnection($"Data Source={db}");
        con.Open();
        EnsureAliasTable(con);
        var display = DriverAliasRow.Safe(displayName);
        var shortCode = DriverAliasRow.SafeShort(shortName, carIndex);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
            INSERT INTO driver_aliases(car_idx, original_name, display_name, short_name, updated_at)
            VALUES($car, $original, $display, $short, $updated)
            ON CONFLICT(car_idx) DO UPDATE SET
                original_name = excluded.original_name,
                display_name = excluded.display_name,
                short_name = excluded.short_name,
                updated_at = excluded.updated_at;
            """;
            cmd.Parameters.AddWithValue("$car", carIndex);
            cmd.Parameters.AddWithValue("$original", DriverAliasRow.Safe(originalName));
            cmd.Parameters.AddWithValue("$display", display);
            cmd.Parameters.AddWithValue("$short", shortCode);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        RefreshFinalClassificationNames(con);
    }

    public static void RefreshFinalClassificationNames(SqliteConnection con)
    {
        EnsureAliasTable(con);
        if (!TableExists(con, "final_classification")) return;
        EnsureFinalColumns(con);
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        UPDATE final_classification
        SET display_name = COALESCE(
                (SELECT NULLIF(display_name, '') FROM driver_aliases a WHERE a.car_idx = final_classification.car_idx),
                display_name,
                name
            ),
            short_name = COALESCE(
                (SELECT NULLIF(short_name, '') FROM driver_aliases a WHERE a.car_idx = final_classification.car_idx),
                short_name
            )
        WHERE EXISTS (SELECT 1 FROM driver_aliases a WHERE a.car_idx = final_classification.car_idx);
        """;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureFinalColumns(SqliteConnection con)
    {
        if (!TableExists(con, "final_classification")) return;
        EnsureColumn(con, "final_classification", "display_name", "TEXT");
        EnsureColumn(con, "final_classification", "original_name", "TEXT");
        EnsureColumn(con, "final_classification", "short_name", "TEXT");
    }

    private static void EnsureColumn(SqliteConnection con, string table, string column, string type)
    {
        if (ColumnExists(con, table, column)) return;
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        cmd.ExecuteNonQuery();
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

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", table);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }
}
