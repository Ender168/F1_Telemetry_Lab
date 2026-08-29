using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F1TelemetryLab;

public static class SessionPackager
{
    private const int AnalysisPackVersion = 1;

    public static string CreateZip(string sessionFolder, string databasePath, string? preferredSessionName = null)
    {
        if (!Directory.Exists(sessionFolder))
            throw new DirectoryNotFoundException($"Session folder not found: {sessionFolder}");

        if (!File.Exists(databasePath))
            throw new FileNotFoundException("session.sqlite was not created. The archive would be useless, so packaging was stopped.", databasePath);

        SessionManifestService.Refresh(sessionFolder);

        var physicalBaseName = Path.GetFileName(sessionFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var baseName = SafeFileName(string.IsNullOrWhiteSpace(preferredSessionName) ? physicalBaseName : preferredSessionName!);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = physicalBaseName;

        var zipPath = Path.Combine(sessionFolder, baseName + ".zip");
        TryDelete(zipPath);

        var stagingRoot = Path.Combine(Path.GetTempPath(), "F1TelemetryLab_pack_" + Guid.NewGuid().ToString("N"));
        var stagingSession = Path.Combine(stagingRoot, baseName);
        var stagedFullDb = Path.Combine(stagingSession, "session.sqlite");
        var compactLocalDb = Path.Combine(sessionFolder, "chatgpt_pack.sqlite");
        var stagedCompactDb = Path.Combine(stagingSession, "chatgpt_pack.sqlite");

        try
        {
            Directory.CreateDirectory(stagingSession);

            // Never copy the live SQLite file byte-for-byte. A session may still have WAL pages,
            // so use SQLite backup to produce one internally consistent, self-contained snapshot.
            CreateDatabaseSnapshot(databasePath, stagedFullDb);

            // Keep the compact projection because it is convenient for quick inspection, but it is no longer
            // a substitute for the raw database. The analysis pack always contains both databases.
            CreateChatGptDatabase(stagedFullDb, compactLocalDb);
            File.Copy(compactLocalDb, stagedCompactDb, overwrite: true);

            CopySessionSidecars(sessionFolder, stagingSession, zipPath);
            WriteAnalysisJsonFiles(stagingSession, stagedFullDb, sessionFolder);
            WritePackContents(stagingSession);

            ZipFile.CreateFromDirectory(stagingRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            SessionManifestService.Refresh(sessionFolder, zipPath: zipPath);
            return zipPath;
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); } catch { }
        }
    }

    private static void CreateDatabaseSnapshot(string sourceDbPath, string targetDbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetDbPath)!);
        TryDelete(targetDbPath);

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        };
        var targetBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = targetDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        };

        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var target = new SqliteConnection(targetBuilder.ToString());
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }

    private static void CopySessionSidecars(string sessionFolder, string stagingSession, string zipPath)
    {
        var regenerated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "session.sqlite",
            "session.sqlite-wal",
            "session.sqlite-shm",
            "chatgpt_pack.sqlite",
            "chatgpt_pack.sqlite-wal",
            "chatgpt_pack.sqlite-shm",
            "session_summary.json",
            "setup.json",
            "events.json",
            "data_quality.json",
            "track.json",
            "PACK_CONTENTS.txt"
        };

        foreach (var file in Directory.EnumerateFiles(sessionFolder, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (string.Equals(full, Path.GetFullPath(zipPath), StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(sessionFolder, file);
            if (regenerated.Contains(relative)) continue;
            if (relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = Path.Combine(stagingSession, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void WriteAnalysisJsonFiles(string stagingSession, string databasePath, string sessionFolder)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();

        var generatedAt = DateTimeOffset.Now.ToString("O");
        var manifest = ReadJsonNode(Path.Combine(sessionFolder, "manifest.json"));
        var playerClassification = ReadRowsSafe(connection,
            "SELECT * FROM final_classification WHERE is_player = 1 LIMIT 1");
        var playerLapStats = ReadRowsSafe(connection,
            "SELECT COUNT(DISTINCT lap_num) AS laps_observed, " +
            "MIN(CASE WHEN lap_time_ms > 0 THEN lap_time_ms END) AS best_lap_ms " +
            "FROM lap_summary WHERE is_player = 1");

        var summary = new JsonObject
        {
            ["analysis_pack_version"] = AnalysisPackVersion,
            ["generated_at"] = generatedAt,
            ["app_version"] = AppInfo.Version,
            ["schema_version"] = AppInfo.DatabaseSchemaVersion,
            ["full_database"] = "session.sqlite",
            ["compact_database"] = "chatgpt_pack.sqlite",
            ["full_database_included"] = true,
            ["manifest"] = manifest?.DeepClone(),
            ["player_classification"] = JsonSerializer.SerializeToNode(playerClassification),
            ["player_lap_stats"] = JsonSerializer.SerializeToNode(playerLapStats),
            ["tables_present"] = JsonSerializer.SerializeToNode(ReadTableNames(connection))
        };
        WriteJson(Path.Combine(stagingSession, "session_summary.json"), summary);

        var setup = new JsonObject
        {
            ["generated_at"] = generatedAt,
            ["player_setup_snapshots"] = JsonSerializer.SerializeToNode(ReadRowsSafe(connection,
                "SELECT * FROM car_setups WHERE is_player = 1 ORDER BY received_at, overall_frame_identifier"))
        };
        WriteJson(Path.Combine(stagingSession, "setup.json"), setup);

        var events = new JsonObject
        {
            ["generated_at"] = generatedAt,
            ["events"] = JsonSerializer.SerializeToNode(ReadRowsSafe(connection,
                "SELECT * FROM events ORDER BY received_at, overall_frame_identifier")),
            ["confirmed_rewinds"] = JsonSerializer.SerializeToNode(ReadRowsSafe(connection,
                "SELECT * FROM rewind_events ORDER BY received_at, overall_frame_identifier")),
            ["suspected_state_resets"] = JsonSerializer.SerializeToNode(ReadRowsSafe(connection,
                "SELECT * FROM suspected_state_reset_events ORDER BY received_at, overall_frame_identifier"))
        };
        WriteJson(Path.Combine(stagingSession, "events.json"), events);

        var quality = new JsonObject
        {
            ["generated_at"] = generatedAt,
            ["recording_quality"] = JsonSerializer.SerializeToNode(ReadRowsSafe(connection,
                "SELECT * FROM recording_quality")),
            ["data_quality"] = JsonSerializer.SerializeToNode(ReadRowsSafe(connection,
                "SELECT * FROM data_quality"))
        };
        WriteJson(Path.Combine(stagingSession, "data_quality.json"), quality);

        var metadata = ReadKeyValueTable(connection, "session_metadata");
        var track = new JsonObject
        {
            ["track_id"] = metadata.TryGetValue("track_id", out var trackId) ? trackId : null,
            ["track_name"] = metadata.TryGetValue("track_name", out var trackName) ? trackName : null,
            ["track_length_m"] = metadata.TryGetValue("track_length_m", out var trackLength) ? trackLength : null,
            ["session_type"] = metadata.TryGetValue("session_type", out var sessionType) ? sessionType : null,
            ["total_laps"] = metadata.TryGetValue("total_laps", out var totalLaps) ? totalLaps : null
        };
        WriteJson(Path.Combine(stagingSession, "track.json"), track);
    }

    private static void WritePackContents(string stagingSession)
    {
        File.WriteAllText(Path.Combine(stagingSession, "PACK_CONTENTS.txt"),
            "F1 Telemetry Lab analysis pack\n\n" +
            "Send this ZIP as-is for race analysis.\n\n" +
            "session.sqlite       Full, consistent SQLite snapshot including raw UDP packets and derived tables.\n" +
            "chatgpt_pack.sqlite Compact projection for quick inspection.\n" +
            "session_summary.json Session metadata, player result, lap summary and table inventory.\n" +
            "setup.json           Player setup snapshots and changes.\n" +
            "events.json          Race events, confirmed flashbacks and suspected state resets.\n" +
            "data_quality.json    Capture and analysis quality diagnostics.\n" +
            "track.json           Track/session identifiers used by the game.\n\n" +
            "The full database is intentionally included. Compact data alone can hide parser defects that are recoverable from raw packets.\n");
    }

    private static void CreateChatGptDatabase(string sourceDbPath, string targetDbPath)
    {
        SQLitePCL.Batteries_V2.Init();
        Directory.CreateDirectory(Path.GetDirectoryName(targetDbPath)!);
        TryDelete(targetDbPath);

        using var con = new SqliteConnection($"Data Source={sourceDbPath};Default Timeout=30");
        con.Open();

        Execute(con, "PRAGMA busy_timeout = 10000;");

        using var cmd = con.CreateCommand();
        cmd.CommandText = "ATTACH DATABASE $target AS out";
        cmd.Parameters.AddWithValue("$target", targetDbPath);
        cmd.ExecuteNonQuery();

        try
        {
            CopyTableIfExists(con, "session_metadata");
            CopyTableIfExists(con, "session_segments");
            CopyTableIfExists(con, "recording_quality");
            CopyTableIfExists(con, "data_quality");
            CopyTableIfExists(con, "lap_summary");
            CopyTableIfExists(con, "lap_state_summary");
            CopyTableIfExists(con, "lap_quality");
            CopyTableIfExists(con, "car_setups");
            CopyTableIfExists(con, "rewind_events");
            CopyTableIfExists(con, "suspected_state_reset_events");
            CopyTableIfExists(con, "events");
            CopyTableIfExists(con, "participants");
            CopyTableIfExists(con, "analysis_trace_10m");
            CopyTableIfExists(con, "final_classification");
            CopyTableIfExists(con, "driver_aliases");

            if (!TableExists(con, "analysis_trace_10m") && TableExists(con, "lap_summary"))
            {
                Execute(con,
                    "CREATE TABLE IF NOT EXISTS out.analysis_trace_10m AS " +
                    "SELECT car_idx, lap_num, is_player, clean_lap, 0 AS distance_bin_m, lap_time_ms AS time_ms, " +
                    "max_speed AS speed, avg_throttle AS throttle, avg_brake AS brake, NULL AS steer, NULL AS gear, " +
                    "NULL AS world_position_x, NULL AS world_position_z, NULL AS yaw, NULL AS g_force_lateral, NULL AS g_force_longitudinal " +
                    "FROM main.lap_summary");
            }

            Execute(con, "CREATE TABLE IF NOT EXISTS out.pack_info(key TEXT PRIMARY KEY, value TEXT);");
            InsertPackInfo(con, "created_at", DateTimeOffset.Now.ToString("O"));
            InsertPackInfo(con, "source_database", "session.sqlite analysis-pack snapshot");
            InsertPackInfo(con, "note", "Compact convenience DB. The same ZIP also contains the complete session.sqlite snapshot.");
            Execute(con, $"PRAGMA out.user_version = {AppInfo.DatabaseSchemaVersion};");
        }
        finally
        {
            try { Execute(con, "DETACH DATABASE out"); } catch { }
        }

        using var compact = new SqliteConnection($"Data Source={targetDbPath};Default Timeout=30");
        compact.Open();
        Execute(compact, "PRAGMA journal_mode = DELETE;");
        Execute(compact, "VACUUM;");
    }

    private static List<Dictionary<string, object?>> ReadRowsSafe(SqliteConnection connection, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (SqliteException)
        {
            return new List<Dictionary<string, object?>>();
        }
    }

    private static Dictionary<string, string?> ReadKeyValueTable(SqliteConnection connection, string table)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists(connection, table)) return result;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT key, value FROM {table}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1));
        }
        catch (SqliteException) { }
        return result;
    }

    private static List<string> ReadTableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static JsonNode? ReadJsonNode(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static void WriteJson(string path, JsonNode node) =>
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static void CopyTableIfExists(SqliteConnection con, string table)
    {
        if (!TableExists(con, table)) return;
        Execute(con, $"DROP TABLE IF EXISTS out.{table}");
        Execute(con, $"CREATE TABLE out.{table} AS SELECT * FROM main.{table}");
    }

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", table);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static void Execute(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void InsertPackInfo(SqliteConnection con, string key, string value)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO out.pack_info(key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).Trim();
        while (safe.Contains("__", StringComparison.Ordinal)) safe = safe.Replace("__", "_");
        return safe.Trim('_', ' ');
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
