using Microsoft.Data.Sqlite;
using System.Globalization;

namespace F1TelemetryLab;

// Kept under its historical name for call-site compatibility. Since schema 6 this
// service finalizes metadata inside session.sqlite and never writes a JSON manifest.
public static class SessionManifestService
{
    public static void Refresh(string sessionFolder, DateTimeOffset? analyzedAt = null, string? archivePath = null)
    {
        var databasePath = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(databasePath)) return;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        DatabaseSchemaMigrator.Apply(connection);
        if (analyzedAt is not null) SetMeta(connection, "analyzed_at", analyzedAt.Value.ToString("O"));
        SetMeta(connection, "database_size_bytes", new FileInfo(databasePath).Length.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
        {
            SetMeta(connection, "archive_name", Path.GetFileName(archivePath));
            SetMeta(connection, "archive_size_bytes", new FileInfo(archivePath).Length.ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, "packaged_at", DateTimeOffset.Now.ToString("O"));
        }
    }

    public static void FinalizeDatabase(string databasePath, Action<string>? log = null)
    {
        log?.Invoke("Finalizing player thermal and extended telemetry...");
        TelemetryCompletenessService.Enrich(databasePath, log, playerOnly: true);
        AdditionalTelemetry2026Service.Enrich(databasePath, log);
        SessionStorageOptimizer.Optimize(databasePath, log);
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        SetMeta(connection, "analyzed_at", DateTimeOffset.Now.ToString("O"));
        SetMeta(connection, "database_size_bytes", new FileInfo(databasePath).Length.ToString(CultureInfo.InvariantCulture));
        SetMeta(connection, "finalization_warning", "");
    }

    private static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session_metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
