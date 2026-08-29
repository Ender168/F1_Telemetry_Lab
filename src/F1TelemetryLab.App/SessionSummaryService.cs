using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace F1TelemetryLab;

public sealed record SessionListItem(
    string FolderPath,
    string FolderName,
    string SessionName,
    string TrackName,
    DateTimeOffset? StartedAt,
    TimeSpan? Duration,
    long SizeBytes,
    int TotalLaps,
    string ClassificationSource,
    string CaptureQuality,
    string Completeness,
    string AnalysisConfidence,
    string AnalysisState,
    string PlayerName,
    long SetupSnapshots,
    string TechnicalDetails)
{
    public string DateLabel => StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "date unavailable";
    public string DurationLabel => Duration is null ? "duration unavailable" : $"{(int)Duration.Value.TotalMinutes}:{Duration.Value.Seconds:00}";
    public string SizeLabel => FormatBytes(SizeBytes);
    public string SummaryLine => $"{TrackName} | {DateLabel} | {DurationLabel} | {SizeLabel} | {TotalLaps} laps";
    public string QualityLine => $"Capture {CaptureQuality} | Completeness {Completeness} | Analysis {AnalysisConfidence}";
    public override string ToString() => FolderName;

    private static string FormatBytes(long value)
    {
        if (value >= 1_073_741_824) return $"{value / 1_073_741_824d:0.0} GB";
        if (value >= 1_048_576) return $"{value / 1_048_576d:0.0} MB";
        if (value >= 1_024) return $"{value / 1_024d:0.0} KB";
        return $"{value:N0} B";
    }
}

public static class SessionSummaryService
{
    public static SessionListItem Load(string folder)
    {
        var folderName = Path.GetFileName(folder);
        var manifestPath = Path.Combine(folder, "manifest.json");
        var rawManifest = File.Exists(manifestPath) ? SafeReadAllText(manifestPath) : "manifest.json not found";
        using var manifest = ParseManifest(rawManifest);
        var root = manifest?.RootElement;
        var sessionName = Text(root, "session_name") ?? folderName;
        var trackName = Text(root, "track_name") ?? "Unknown track";
        var started = ParseDate(Text(root, "started_at"));
        var stopped = ParseDate(Text(root, "stopped_at"));
        var duration = started is not null && stopped is not null && stopped >= started ? stopped - started : null;
        var totalLaps = Int32(root, "total_laps");
        var classificationSource = FormatClassificationSource(Text(root, "classification_source"));
        var setupSnapshots = Int64(root, "car_setup_snapshots");
        var database = Path.Combine(folder, "session.sqlite");
        var analysisState = File.Exists(database) && HasTable(database, "lap_summary") ? "Analyzed" : "Recording only";
        var playerName = LoadPlayerName(database) ?? "YOU";
        var quality = RecordingQualityService.Load(folder);
        return new SessionListItem(
            folder,
            folderName,
            sessionName,
            trackName,
            started,
            duration,
            DirectorySize(folder),
            totalLaps,
            classificationSource,
            quality?.Rating ?? "not assessed",
            quality?.SessionCompleteness ?? "not assessed",
            quality?.AnalysisConfidence ?? "not assessed",
            analysisState,
            playerName,
            setupSnapshots,
            rawManifest);
    }

    private static JsonDocument? ParseManifest(string raw)
    {
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException ex) { return "manifest read failed: " + ex.Message; }
        catch (UnauthorizedAccessException ex) { return "manifest read failed: " + ex.Message; }
    }

    private static string? Text(JsonElement? root, string property)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(property, out var item)) return null;
        return item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
    }

    private static int Int32(JsonElement? root, string property) =>
        int.TryParse(Text(root, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static long Int64(JsonElement? root, string property) =>
        long.TryParse(Text(root, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string FormatClassificationSource(string? source) => source switch
    {
        "official_udp" => "Official (UDP packet 8)",
        "provisional_latest_lap_data" => "Provisional (packet 8 absent; latest Lap Data)",
        "not_analyzed" or null or "" => "Not analyzed",
        _ => source.Replace('_', ' ')
    };

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    private static bool HasTable(string database, string table)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Shared");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
            command.Parameters.AddWithValue("$name", table);
            return command.ExecuteScalar() is not null;
        }
        catch (SqliteException) { return false; }
    }

    private static string? LoadPlayerName(string database)
    {
        if (!File.Exists(database)) return null;
        try
        {
            using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Shared");
            connection.Open();
            if (!TableExists(connection, "final_classification")) return null;
            var nameColumn = ColumnExists(connection, "final_classification", "display_name") ? "display_name" : "name";
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {nameColumn} FROM final_classification WHERE is_player=1 LIMIT 1";
            var result = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (SqliteException) { return null; }
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
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static long DirectorySize(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(path =>
            {
                try { return new FileInfo(path).Length; }
                catch (IOException) { return 0L; }
                catch (UnauthorizedAccessException) { return 0L; }
            });
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
