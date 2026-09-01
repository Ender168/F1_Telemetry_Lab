using Microsoft.Data.Sqlite;
using System.Globalization;

namespace F1TelemetryLab;

public sealed record RecordingQualityReport(
    long PacketsReceived,
    long CarSamplesWritten,
    long InvalidHeaders,
    long UnsupportedPackets,
    long DuplicateFrames,
    long OutOfOrderFrames,
    long EstimatedMissingFrames,
    long QueueDrops,
    int QueueHighWatermark,
    int SessionChanges,
    string Rating,
    DateTimeOffset? UpdatedAt)
{
    public bool MissingFrameEstimateAvailable { get; init; }
    public double? MeasuredTelemetryRateHz { get; init; }
    public string SessionCompleteness { get; init; } = "Not assessed";
    public string SessionCompletenessSummary { get; init; } = "Run analysis to assess session completeness.";
    public string AnalysisConfidence { get; init; } = "Not assessed";
    public string AnalysisConfidenceSummary { get; init; } = "Run analysis to assess report confidence.";

    public string Summary =>
        $"Capture {Rating}; completeness {SessionCompleteness}; analysis {AnalysisConfidence}. " +
        $"Packets {PacketsReceived:N0}, samples {CarSamplesWritten:N0}, " +
        $"queue drops {QueueDrops:N0}, invalid/unsupported {InvalidHeaders:N0}/{UnsupportedPackets:N0}, " +
        $"duplicate/out-of-order {DuplicateFrames:N0}/{OutOfOrderFrames:N0}, " +
        $"estimated missing {(MissingFrameEstimateAvailable ? EstimatedMissingFrames.ToString("N0", CultureInfo.CurrentCulture) : "not calculated")}, " +
        $"queue high-water {QueueHighWatermark:N0}, " +
        $"session changes {SessionChanges:N0}.";
}

public static class RecordingQualityService
{
    public static RecordingQualityReport? Load(string sessionFolder)
    {
        var database = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(database)) return null;

        try
        {
            return LoadDatabase(database);
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static RecordingQualityReport? LoadDatabase(string database)
    {
        using var con = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Shared");
        con.Open();
        using (var exists = con.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='recording_quality' LIMIT 1";
            if (exists.ExecuteScalar() is null) return null;
        }

        var hasMissingEstimateFlag = ColumnExists(con, "recording_quality", "missing_frames_estimated");
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT packets_received, car_samples_written, invalid_headers, unsupported_packets,
               duplicate_frames, out_of_order_frames, estimated_missing_frames, queue_drops,
               queue_high_watermark, session_changes, rating, updated_at
        FROM recording_quality
        WHERE id = 1
        """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var updated = DateTimeOffset.TryParse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : (DateTimeOffset?)null;
        var report = new RecordingQualityReport(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10),
            updated);
        reader.Close();

        report = report with
        {
            MissingFrameEstimateAvailable = hasMissingEstimateFlag && LoadMissingEstimateFlag(con),
            MeasuredTelemetryRateHz = MeasureTelemetryRate(con)
        };

        var dimensions = LoadDimensions(con);
        return report with
        {
            SessionCompleteness = dimensions.GetValueOrDefault("session_completeness").Rating ?? report.SessionCompleteness,
            SessionCompletenessSummary = dimensions.GetValueOrDefault("session_completeness").Summary ?? report.SessionCompletenessSummary,
            AnalysisConfidence = dimensions.GetValueOrDefault("analysis_confidence").Rating ?? report.AnalysisConfidence,
            AnalysisConfidenceSummary = dimensions.GetValueOrDefault("analysis_confidence").Summary ?? report.AnalysisConfidenceSummary
        };
    }

    private static bool LoadMissingEstimateFlag(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT missing_frames_estimated FROM recording_quality WHERE id=1";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static double? MeasureTelemetryRate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(received_at), MAX(received_at), COUNT(*)
            FROM raw_packets
            WHERE packet_format = 2026 AND packet_id = 6
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetInt64(2) < 2) return null;
        if (!DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var first)) return null;
        if (!DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)) return null;
        var seconds = (last - first).TotalSeconds;
        return seconds > 0 ? (reader.GetInt64(2) - 1) / seconds : null;
    }

    private static Dictionary<string, (string? Rating, string? Summary)> LoadDimensions(SqliteConnection connection)
    {
        var result = new Dictionary<string, (string? Rating, string? Summary)>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists(connection, "data_quality")) return result;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dimension, rating, summary FROM data_quality";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
        return result;
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
