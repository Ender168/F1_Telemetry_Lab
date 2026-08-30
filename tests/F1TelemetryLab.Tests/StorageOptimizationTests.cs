using F1TelemetryLab;
using Microsoft.Data.Sqlite;

namespace F1TelemetryLab.Tests;

public sealed class StorageOptimizationTests
{
    [Fact]
    public void Storage_optimizer_keeps_raw_and_player_detail_but_removes_rebuildable_bulk()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = Path.Combine(Path.GetTempPath(), "F1TelemetryLab_storage_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "session.sqlite");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE session_metadata(key TEXT PRIMARY KEY, value TEXT);
                    CREATE TABLE raw_packets(id INTEGER PRIMARY KEY, payload BLOB);
                    INSERT INTO raw_packets VALUES(1, X'010203');
                    CREATE TABLE lap_summary(car_idx INTEGER, lap_num INTEGER);
                    INSERT INTO lap_summary VALUES(0,1);
                    CREATE TABLE final_classification(car_idx INTEGER, position INTEGER);
                    INSERT INTO final_classification VALUES(0,1);

                    CREATE TABLE car_telemetry(is_player INTEGER, value INTEGER);
                    CREATE TABLE lap_data(is_player INTEGER, value INTEGER);
                    CREATE TABLE motion_data(is_player INTEGER, value INTEGER);
                    CREATE TABLE car_status(is_player INTEGER, value INTEGER);
                    CREATE TABLE car_damage(is_player INTEGER, value INTEGER);
                    INSERT INTO car_telemetry VALUES(1,1),(0,2),(0,3);
                    INSERT INTO lap_data VALUES(1,1),(0,2),(0,3);
                    INSERT INTO motion_data VALUES(1,1),(0,2),(0,3);
                    INSERT INTO car_status VALUES(1,1),(0,2),(0,3);
                    INSERT INTO car_damage VALUES(1,1),(0,2),(0,3);

                    CREATE TABLE analysis_samples(value INTEGER);
                    CREATE TABLE analysis_context(value INTEGER);
                    CREATE TABLE final_classification_packet(value INTEGER);
                    INSERT INTO analysis_samples VALUES(1);
                    INSERT INTO analysis_context VALUES(1);
                    INSERT INTO final_classification_packet VALUES(1);
                    """;
                command.ExecuteNonQuery();
            }

            var result = SessionStorageOptimizer.Optimize(dbPath);
            Assert.True(result.Optimized);
            Assert.Equal(10, result.RowsPruned);
            Assert.Equal(3, result.TablesDropped);

            using var verify = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            verify.Open();
            Assert.Equal(1L, ScalarLong(verify, "SELECT COUNT(*) FROM raw_packets"));
            foreach (var table in new[] { "car_telemetry", "lap_data", "motion_data", "car_status", "car_damage" })
            {
                Assert.Equal(1L, ScalarLong(verify, $"SELECT COUNT(*) FROM {table}"));
                Assert.Equal(1L, ScalarLong(verify, $"SELECT is_player FROM {table} LIMIT 1"));
            }

            Assert.False(TableExists(verify, "analysis_samples"));
            Assert.False(TableExists(verify, "analysis_context"));
            Assert.False(TableExists(verify, "final_classification_packet"));
            Assert.Equal("raw_plus_summaries_plus_player_detail", ReadMeta(verify, "storage_profile"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Raw_storage_policy_skips_only_known_redundant_packets()
    {
        var policy = new RawPacketStoragePolicy();
        var lobbyHeader = Header(9, 100);
        Assert.Equal(RawPacketStorageDecision.SkipLobbyInfo, policy.Evaluate(lobbyHeader, new byte[40], 15));

        var timeTrialHeader = Header(14, 101);
        Assert.Equal(RawPacketStorageDecision.SkipNonTimeTrialPacket, policy.Evaluate(timeTrialHeader, new byte[40], 15));
        Assert.Equal(RawPacketStorageDecision.Store, policy.Evaluate(timeTrialHeader, new byte[40], -1));
        Assert.Equal(RawPacketStorageDecision.Store, policy.Evaluate(timeTrialHeader, new byte[40], 18));

        var setupHeader = Header(5, 102);
        var first = new byte[F12026Parser.HeaderSize + 4];
        first[F12026Parser.HeaderSize] = 7;
        var second = new byte[first.Length];
        second[0] = 99;
        second[F12026Parser.HeaderSize] = 7;
        var changed = new byte[first.Length];
        changed[F12026Parser.HeaderSize] = 8;

        Assert.Equal(RawPacketStorageDecision.Store, policy.Evaluate(setupHeader, first, 15));
        Assert.Equal(RawPacketStorageDecision.SkipDuplicateSetup, policy.Evaluate(setupHeader, second, 15));
        Assert.Equal(RawPacketStorageDecision.Store, policy.Evaluate(setupHeader, changed, 15));
    }

    private static PacketHeader Header(byte packetId, uint frame) => new(
        2026, 26, 1, 0, 1, packetId, 123, frame / 60f, frame, frame, 0, 255);

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM session_metadata WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToString(command.ExecuteScalar());
    }
}
