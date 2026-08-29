using Microsoft.Data.Sqlite;
using System.IO.Compression;

namespace F1TelemetryLab;

public static class SessionPackager
{
    public static string CreateZip(string sessionFolder, string databasePath, string? preferredSessionName = null)
    {
        if (!Directory.Exists(sessionFolder))
            throw new DirectoryNotFoundException($"Session folder not found: {sessionFolder}");

        if (!File.Exists(databasePath))
            throw new FileNotFoundException("session.sqlite was not created. The archive would be useless, so packaging was stopped.", databasePath);

        SessionManifestService.Refresh(sessionFolder);

        // Build the compact upload database in the session folder first. That way, even if zip creation
        // fails for some reason, the user still has a small chatgpt_pack.sqlite to send manually.
        var compactLocalDb = Path.Combine(sessionFolder, "chatgpt_pack.sqlite");
        CreateChatGptDatabase(databasePath, compactLocalDb);

        var physicalBaseName = Path.GetFileName(sessionFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var baseName = SafeFileName(string.IsNullOrWhiteSpace(preferredSessionName) ? physicalBaseName : preferredSessionName!);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = physicalBaseName;

        // Keep the upload zip inside the physical session folder. The folder may keep its original Unknown_* name
        // because Windows often holds SQLite handles too long for a safe rename, but the zip itself uses the readable session name.
        var zipPath = Path.Combine(sessionFolder, baseName + ".zip");
        TryDelete(zipPath);

        var stagingRoot = Path.Combine(Path.GetTempPath(), "F1TelemetryLab_pack_" + Guid.NewGuid().ToString("N"));
        var stagingSession = Path.Combine(stagingRoot, baseName);

        try
        {
            Directory.CreateDirectory(stagingSession);

            foreach (var file in Directory.EnumerateFiles(sessionFolder, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                if (string.Equals(full, Path.GetFullPath(zipPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = Path.GetRelativePath(sessionFolder, file);

                // Keep the large raw database local for lossless re-analysis; the archive uses the compact projection.
                if (relative.Equals("session.sqlite", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (relative.Equals("session.sqlite-wal", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("session.sqlite-shm", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = Path.Combine(stagingSession, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            var notePath = Path.Combine(stagingSession, "PACK_CONTENTS.txt");
            File.WriteAllText(notePath,
                "This archive intentionally contains chatgpt_pack.sqlite instead of the full raw session.sqlite.\n" +
                "The full raw UDP database stays in the local session folder.\n" +
                "chatgpt_pack.sqlite contains analysis tables, setup changes and 10m telemetry bins for upload-friendly review.\n");

            ZipFile.CreateFromDirectory(stagingRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            SessionManifestService.Refresh(sessionFolder, zipPath: zipPath);
            return zipPath;
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); } catch { }
        }
    }

    private static void CreateChatGptDatabase(string sourceDbPath, string targetDbPath)
    {
        SQLitePCL.Batteries_V2.Init();
        Directory.CreateDirectory(Path.GetDirectoryName(targetDbPath)!);
        TryDelete(targetDbPath);

        // ATTACH needs a writable primary connection even though the source data is not modified.
        using var con = new SqliteConnection($"Data Source={sourceDbPath};Default Timeout=30");
        con.Open();

        Execute(con, "PRAGMA busy_timeout = 10000;");
        Execute(con, "PRAGMA journal_mode = WAL;");
        Execute(con, "PRAGMA wal_checkpoint(TRUNCATE);");

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

            // If analysis_trace_10m does not exist because an older DB is being packed,
            // create a tiny fallback from lap_summary only. It is not detailed, but at least not useless.
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
            InsertPackInfo(con, "source_database", Path.GetFileName(sourceDbPath));
            InsertPackInfo(con, "note", "Compact ChatGPT upload DB. Raw packets are intentionally excluded.");
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
