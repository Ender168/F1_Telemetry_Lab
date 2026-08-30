using Microsoft.Data.Sqlite;
using System.Globalization;

namespace F1TelemetryLab;

public sealed record SessionStorageOptimizationResult(
    bool Optimized,
    long SizeBeforeBytes,
    long SizeAfterBytes,
    long RowsPruned,
    int TablesDropped);

/// <summary>
/// Shrinks an analyzed session without touching authoritative raw UDP data.
/// Detailed frame-level rows are retained for the player car only; all-car summaries
/// and the 10 m comparison trace remain available for normal analysis. Everything
/// removed here can be rebuilt by running the analysis pipeline against raw_packets.
/// </summary>
public static class SessionStorageOptimizer
{
    private const int StorageProfileVersion = 1;

    private static readonly string[] PlayerDetailTables =
    {
        "car_telemetry",
        "lap_data",
        "motion_data",
        "car_status",
        "car_damage"
    };

    private static readonly string[] TransientTables =
    {
        "analysis_samples",
        "analysis_context",
        "final_classification_packet"
    };

    public static SessionStorageOptimizationResult Optimize(string databasePath, Action<string>? log = null)
    {
        if (!File.Exists(databasePath))
            return new SessionStorageOptimizationResult(false, 0, 0, 0, 0);

        SQLitePCL.Batteries_V2.Init();
        var before = new FileInfo(databasePath).Length;
        long rowsPruned = 0;
        var dropped = 0;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 60,
            Pooling = false
        };

        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 60000;");

            // Do not compact a live/unanalysed recording. The summary tables are the
            // durable replacement for the all-car frame-level projections.
            if (!TableExists(connection, "lap_summary") || !TableExists(connection, "final_classification"))
                return new SessionStorageOptimizationResult(false, before, before, 0, 0);

            var needsOptimization = TransientTables.Any(table => TableExists(connection, table)) ||
                                    PlayerDetailTables.Any(table => HasNonPlayerRows(connection, table));
            if (!needsOptimization)
                return new SessionStorageOptimizationResult(false, before, before, 0, 0);

            using var transaction = connection.BeginTransaction();
            foreach (var table in PlayerDetailTables)
            {
                if (!TableExists(connection, table, transaction) || !ColumnExists(connection, table, "is_player", transaction)) continue;
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE COALESCE(is_player, 0) = 0";
                rowsPruned += command.ExecuteNonQuery();
            }

            foreach (var table in TransientTables)
            {
                if (!TableExists(connection, table, transaction)) continue;
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DROP TABLE {table}";
                command.ExecuteNonQuery();
                dropped++;
            }

            SetMeta(connection, transaction, "storage_profile_version", StorageProfileVersion.ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, transaction, "storage_profile", "raw_plus_summaries_plus_player_detail");
            SetMeta(connection, transaction, "storage_rebuildable_detail", "car_telemetry,lap_data,motion_data,car_status,car_damage,analysis_samples,analysis_context,final_classification_packet");
            SetMeta(connection, transaction, "storage_rows_pruned", rowsPruned.ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, transaction, "storage_tables_dropped", dropped.ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, transaction, "storage_size_before_bytes", before.ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, transaction, "storage_optimized_at", DateTimeOffset.Now.ToString("O"));
            transaction.Commit();

            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }

        using (var vacuum = new SqliteConnection(builder.ToString()))
        {
            vacuum.Open();
            Execute(vacuum, "VACUUM;");
            Execute(vacuum, "PRAGMA optimize;");
        }

        var after = new FileInfo(databasePath).Length;
        using (var metadata = new SqliteConnection(builder.ToString()))
        {
            metadata.Open();
            using var transaction = metadata.BeginTransaction();
            SetMeta(metadata, transaction, "storage_size_after_bytes", after.ToString(CultureInfo.InvariantCulture));
            SetMeta(metadata, transaction, "storage_saved_bytes", Math.Max(0, before - after).ToString(CultureInfo.InvariantCulture));
            transaction.Commit();
        }

        log?.Invoke($"Lean storage: pruned {rowsPruned:N0} non-player derived rows, dropped {dropped} transient tables, database {before:N0} -> {after:N0} bytes.");
        return new SessionStorageOptimizationResult(true, before, after, rowsPruned, dropped);
    }

    private static bool HasNonPlayerRows(SqliteConnection connection, string table)
    {
        if (!TableExists(connection, table) || !ColumnExists(connection, table, "is_player")) return false;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {table} WHERE COALESCE(is_player,0)=0 LIMIT 1";
        return command.ExecuteScalar() is not null;
    }

    private static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        if (!TableExists(connection, "session_metadata", transaction)) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO session_metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string table, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
