using Microsoft.Data.Sqlite;
using System.Globalization;

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
        var database = Path.Combine(folder, "session.sqlite");
        using var connection = OpenSummaryDatabase(database);
        var metadata = LoadMetadata(connection);
        var sessionName = metadata.GetValueOrDefault("session_name") ?? folderName;
        var trackName = metadata.GetValueOrDefault("track_name") ?? "Unknown track";
        var started = ParseDate(metadata.GetValueOrDefault("started_at"));
        var stopped = ParseDate(metadata.GetValueOrDefault("stopped_at"));
        var duration = started is not null && stopped is not null && stopped >= started ? stopped - started : null;
        var totalLaps = int.TryParse(metadata.GetValueOrDefault("total_laps"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var laps) ? laps : 0;
        var classificationSource = FormatClassificationSource(ReadClassificationSource(connection));
        var setupSnapshots = CountRows(connection, "car_setups");
        var analysisState = connection is not null && HasTable(connection, "lap_summary") ? "Analyzed" : "Recording only";
        var playerName = LoadPlayerName(connection) ?? "YOU";
        var quality = connection is null ? null : RecordingQualityService.Load(connection);
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
            BuildTechnicalDetails(connection, metadata));
    }

    private static SqliteConnection? OpenSummaryDatabase(string database)
    {
        if (!File.Exists(database)) return null;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch (SqliteException) { connection.Dispose(); return null; }
    }

    private static Dictionary<string, string> LoadMetadata(SqliteConnection? connection)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (connection is null) return result;
        try
        {
            if (!TableExists(connection, "session_metadata")) return result;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key,value FROM session_metadata ORDER BY key";
            using var reader = command.ExecuteReader();
            while (reader.Read()) result[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }
        catch (SqliteException) { }
        return result;
    }

    private static string BuildTechnicalDetails(SqliteConnection? connection, IReadOnlyDictionary<string, string> metadata)
    {
        if (connection is null) return "session.sqlite not found";
        var lines = new List<string>
        {
            $"Database: session.sqlite",
            $"App: {metadata.GetValueOrDefault("app_version", "unknown")}",
            $"Schema: {metadata.GetValueOrDefault("schema_version", "unknown")}",
            $"Session UID: {metadata.GetValueOrDefault("session_uid", "unknown")}",
            $"Analyzed at: {metadata.GetValueOrDefault("analyzed_at", "not analyzed")}",
            $"Archive: {metadata.GetValueOrDefault("archive_name", "not created")}",
            $"Finalization warning: {metadata.GetValueOrDefault("finalization_warning", "")}"
        };
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var reader = command.ExecuteReader();
            var tables = new List<string>();
            while (reader.Read()) tables.Add(reader.GetString(0));
            lines.Add("Tables: " + string.Join(", ", tables));
        }
        catch (SqliteException ex) { lines.Add("Database read warning: " + ex.Message); }
        return string.Join(Environment.NewLine, lines);
    }

    private static string? ReadClassificationSource(SqliteConnection? connection)
    {
        if (connection is null) return null;
        try
        {
            if (!TableExists(connection, "final_classification") || !ColumnExists(connection, "final_classification", "classification_source")) return "not_analyzed";
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT classification_source FROM final_classification LIMIT 1";
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { return null; }
    }

    private static long CountRows(SqliteConnection? connection, string table)
    {
        if (connection is null) return 0;
        try
        {
            if (!TableExists(connection, table)) return 0;
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { return 0; }
    }

    private static string FormatClassificationSource(string? source) => source switch
    {
        "official_udp" => "Official (UDP packet 8)",
        "provisional_latest_lap_data" => "Provisional (packet 8 absent; latest Lap Data)",
        "not_analyzed" or null or "" => "Not analyzed",
        _ => source.Replace('_', ' ')
    };

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    private static bool HasTable(SqliteConnection? connection, string table)
    {
        if (connection is null) return false;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
            command.Parameters.AddWithValue("$name", table);
            return command.ExecuteScalar() is not null;
        }
        catch (SqliteException) { return false; }
    }

    private static string? LoadPlayerName(SqliteConnection? connection)
    {
        if (connection is null) return null;
        try
        {
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
