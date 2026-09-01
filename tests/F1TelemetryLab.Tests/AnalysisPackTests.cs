using F1TelemetryLab;
using Microsoft.Data.Sqlite;

namespace F1TelemetryLab.Tests;

public sealed class AnalysisPackTests
{
    [Fact]
    public void AnalysisRarContainsOnlyVerifiedFullDatabaseSnapshotAtMaximumCompression()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = Path.Combine(Path.GetTempPath(), "F1TelemetryLab_pack_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var database = Path.Combine(root, "session.sqlite");
            using (var telemetry = new TelemetryDatabase(database))
            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO session_metadata(key, value) VALUES
                        ('session_name', 'Pack Test'),
                        ('track_name', 'Mexico'),
                        ('track_id', '19'),
                        ('track_length_m', '4304'),
                        ('session_type', '10'),
                        ('total_laps', '36')
                    ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                    INSERT INTO raw_packets(received_at, packet_format, game_year, game_major_version, game_minor_version,
                        packet_version, packet_id, session_uid, session_time, frame_identifier,
                        overall_frame_identifier, player_car_index, secondary_player_car_index, packet_size, payload)
                    VALUES ('2026-01-01T00:00:00Z', 2026, 26, 1, 0, 1, 6, '1', 1, 1, 1, 0, 255, 3, X'010203');
                    """;
                command.ExecuteNonQuery();
            }

            var runner = new CapturingRarRunner();
            var rar = SessionPackager.CreateRar(root, database, "Pack Test", processRunner: runner);

            Assert.True(File.Exists(rar));
            Assert.Equal(2, runner.Calls.Count);
            var add = runner.Calls[0];
            Assert.Equal("a", add[0]);
            Assert.Contains("-ma5", add);
            Assert.Contains("-m5", add);
            Assert.Contains("-md128m", add);
            Assert.Equal("session.sqlite", add[^1]);
            Assert.Single(add, x => !x.StartsWith('-') && x != "a" && x != rar);
            Assert.Equal("t", runner.Calls[1][0]);

            var extracted = Path.Combine(root, "snapshot.sqlite");
            File.WriteAllBytes(extracted, runner.SnapshotBytes!);
            using var snapshot = new SqliteConnection($"Data Source={extracted};Mode=ReadOnly");
            snapshot.Open();
            using var integrity = snapshot.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", Convert.ToString(integrity.ExecuteScalar()));
            using var raw = snapshot.CreateCommand();
            raw.CommandText = "SELECT COUNT(*) FROM raw_packets";
            Assert.Equal(1L, Convert.ToInt64(raw.ExecuteScalar()));

            Assert.False(File.Exists(Path.Combine(root, "analysis_manifest.json")));
            Assert.False(File.Exists(Path.Combine(root, "chatgpt_pack.sqlite")));
            Assert.False(File.Exists(Path.Combine(root, "ers_control_log.csv")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class CapturingRarRunner : IRarProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public byte[]? SnapshotBytes { get; private set; }

        public RarProcessResult Run(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
        {
            Calls.Add(arguments.ToArray());
            if (arguments[0] == "a")
            {
                SnapshotBytes = File.ReadAllBytes(Path.Combine(workingDirectory, "session.sqlite"));
                File.WriteAllText(arguments[^2], "fake-rar-for-contract-test");
            }
            return new RarProcessResult(0, "ok", "");
        }
    }
}
