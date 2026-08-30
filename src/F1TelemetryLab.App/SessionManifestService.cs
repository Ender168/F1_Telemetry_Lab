using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F1TelemetryLab;

public static class SessionManifestService
{
    public static void Refresh(string sessionFolder, DateTimeOffset? analyzedAt = null, string? zipPath = null)
    {
        var manifestPath = Path.Combine(sessionFolder, "manifest.json");
        var databasePath = Path.Combine(sessionFolder, "session.sqlite");
        JsonObject manifest;
        try
        {
            manifest = File.Exists(manifestPath)
                ? JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            manifest = new JsonObject();
        }

        manifest["app"] = AppInfo.Name;
        manifest["version"] = AppInfo.Version;
        manifest["schema_version"] = AppInfo.DatabaseSchemaVersion;
        manifest["database"] = "session.sqlite";
        manifest["database_exists"] = File.Exists(databasePath);
        manifest["database_size_bytes"] = File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0;
        if (analyzedAt is not null) manifest["analyzed_at"] = analyzedAt.Value.ToString("O");

        if (File.Exists(databasePath))
        {
            try
            {
                TelemetryCompletenessService.Enrich(databasePath);
                manifest.Remove("telemetry_completeness_warning");
            }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
            {
                manifest["telemetry_completeness_warning"] = ex.Message;
            }

            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString());
                connection.Open();
                manifest["database_user_version"] = DatabaseSchemaMigrator.ReadUserVersion(connection);
                ApplyMetadata(connection, manifest);
                var classificationSource = ReadClassificationSource(connection);
                manifest["classification_source"] = classificationSource;
                manifest["classification_status"] = classificationSource switch
                {
                    "official_udp" => "official",
                    var source when source.StartsWith("provisional_", StringComparison.OrdinalIgnoreCase) => "provisional",
                    _ => "unavailable"
                };
                manifest["classification_note"] = ReadClassificationNote(connection, classificationSource);
                manifest["car_setup_snapshots"] = CountRows(connection, "car_setups");
                manifest["confirmed_rewinds"] = CountRows(connection, "rewind_events");
                manifest["suspected_state_resets"] = CountRows(connection, "suspected_state_reset_events");
                manifest["motion_ex_player_rows"] = CountRows(connection, "motion_ex_player");
                manifest["lap_position_rows"] = CountRows(connection, "lap_positions");
            }
            catch (SqliteException)
            {
                manifest["database_read_warning"] = "Database metadata could not be refreshed.";
            }
        }

        var quality = RecordingQualityService.Load(sessionFolder);
        if (quality is not null)
        {
            manifest["data_quality"] = JsonSerializer.SerializeToNode(new
            {
                capture = new { rating = quality.Rating, summary = quality.Summary },
                session_completeness = new { rating = quality.SessionCompleteness, summary = quality.SessionCompletenessSummary },
                analysis_confidence = new { rating = quality.AnalysisConfidence, summary = quality.AnalysisConfidenceSummary },
                measured_telemetry_rate_hz = quality.MeasuredTelemetryRateHz
            });
        }

        if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
        {
            manifest["zip"] = Path.GetFileName(zipPath);
            manifest["zip_size_bytes"] = new FileInfo(zipPath).Length;
            manifest["packaged_at"] = DateTimeOffset.Now.ToString("O");
        }

        var temporaryPath = manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    private static void ApplyMetadata(SqliteConnection connection, JsonObject manifest)
    {
        if (!TableExists(connection, "session_metadata")) return;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM session_metadata";
        using var reader = command.ExecuteReader();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) metadata[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);

        CopyString(metadata, manifest, "session_name");
        CopyString(metadata, manifest, "session_uid");
        CopyString(metadata, manifest, "track_name");
        CopyNumber(metadata, manifest, "track_id");
        CopyNumber(metadata, manifest, "session_type");
        CopyNumber(metadata, manifest, "raw_session_type");
        CopyString(metadata, manifest, "raw_session_name");
        CopyString(metadata, manifest, "inferred_session_kind");
        CopyNumber(metadata, manifest, "session_type_conflict");
        CopyString(metadata, manifest, "session_type_conflict_note");
        CopyNumber(metadata, manifest, "packet_8_present");
        CopyNumber(metadata, manifest, "extended_telemetry_rows_updated");
        CopyNumber(metadata, manifest, "motion_ex_rows");
        CopyNumber(metadata, manifest, "lap_position_rows");
        CopyNumber(metadata, manifest, "total_laps");
        CopyNumber(metadata, manifest, "track_length_m");
        CopyString(metadata, manifest, "started_at");
        CopyString(metadata, manifest, "stopped_at");
    }

    private static string ReadClassificationSource(SqliteConnection connection)
    {
        if (!TableExists(connection, "final_classification")) return "not_analyzed";
        if (!ColumnExists(connection, "final_classification", "classification_source")) return "legacy_analysis";
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT classification_source FROM final_classification LIMIT 1";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unavailable";
    }

    private static string ReadClassificationNote(SqliteConnection connection, string source)
    {
        if (TableExists(connection, "final_classification") && ColumnExists(connection, "final_classification", "classification_note"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT classification_note FROM final_classification WHERE classification_note IS NOT NULL AND classification_note <> '' LIMIT 1";
            var note = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(note)) return note;
        }

        if (source.Equals("official_udp", StringComparison.OrdinalIgnoreCase))
            return "Official final classification from UDP packet 8.";
        if (source.StartsWith("provisional_", StringComparison.OrdinalIgnoreCase))
            return "UDP packet 8 is absent. Classification is reconstructed from recorded race-state data and remains provisional.";
        return "Classification has not been built or no usable classification rows were found.";
    }

    private static long CountRows(SqliteConnection connection, string table)
    {
        if (!TableExists(connection, table)) return 0;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void CopyString(IReadOnlyDictionary<string, string> metadata, JsonObject manifest, string key)
    {
        if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) manifest[key] = value;
    }

    private static void CopyNumber(IReadOnlyDictionary<string, string> metadata, JsonObject manifest, string key)
    {
        if (metadata.TryGetValue(key, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            manifest[key] = number;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
