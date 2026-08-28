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
    public string Summary =>
        $"{Rating}: packets {PacketsReceived:N0}, samples {CarSamplesWritten:N0}, " +
        $"queue drops {QueueDrops:N0}, invalid headers {InvalidHeaders:N0}, " +
        $"duplicate/out-of-order {DuplicateFrames:N0}/{OutOfOrderFrames:N0}, " +
        $"queue high-water {QueueHighWatermark:N0}, session changes {SessionChanges:N0}.";
}

public static class RecordingQualityService
{
    public static RecordingQualityReport? Load(string sessionFolder)
    {
        var database = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(database)) return null;

        using var con = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Shared");
        con.Open();
        using (var exists = con.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='recording_quality' LIMIT 1";
            if (exists.ExecuteScalar() is null) return null;
        }

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
        return new RecordingQualityReport(
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
    }
}
