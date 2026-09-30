using System.Buffers.Binary;
using System.Security.Cryptography;
using F1TelemetryLab;
using Microsoft.Data.Sqlite;

namespace F1TelemetryLab.Tests;

public sealed class SessionReliabilityTests
{
    [Fact]
    public void PerCarAnalysisMatchesReferenceIncludingCounterResetsAndHighPlayerSlot()
    {
        WithDatabase((folder, path) =>
        {
            var samples = new List<LapDataSample>();
            using (var db = new TelemetryDatabase(path))
            {
                for (uint frame = 1; frame <= 160; frame++)
                {
                    var packet = Packet(2, 57 * 24, frame);
                    for (var car = 0; car < 24; car++)
                    {
                        var row = packet.AsSpan(F12026Parser.HeaderSize + car * 57, 57);
                        var lap = 1 + (frame - 1) / 50;
                        var step = (frame - 1) % 50;
                        // A reset without FLBK must keep the old analyzer's suspected-reset semantics.
                        if (car == 21 && frame == 80) step = 10;
                        BinaryPrimitives.WriteUInt32LittleEndian(row, lap > 1 ? 90_000u : 0u);
                        BinaryPrimitives.WriteUInt32LittleEndian(row[4..], step * 1_800);
                        BinaryPrimitives.WriteSingleLittleEndian(row[20..], step * 100);
                        BinaryPrimitives.WriteSingleLittleEndian(row[24..], (lap - 1) * 5_000 + step * 100);
                        row[32] = (byte)(car + 1);
                        row[33] = (byte)lap;
                        row[37] = (byte)(car == 23 && frame == 30 ? 1 : 0);
                        row[44] = 1;
                        row[45] = 2;
                    }
                    var at = DateTimeOffset.UnixEpoch.AddMilliseconds(frame * 1_800);
                    samples.AddRange(F12026Parser.ParseLapDataPacket(packet, at));
                    Store(db, packet, at);
                }
                db.SaveMetadata(new SessionMetadata { SessionUid = 91, TrackLengthMeters = 5_000,
                    SessionFolder = folder, DatabasePath = path, SessionType = 15 });
            }
            var expected = LapQualityAnalyzer.Analyze(samples, 5_000, out var rewinds);
            AnalysisEngine.AnalyzeSession(folder);
            using var con = Open(path);
            Assert.Equal(expected.Count, Scalar(con, "SELECT COUNT(*) FROM lap_quality"));
            Assert.Equal(expected.Count(x => x.CleanLap), Scalar(con, "SELECT COUNT(*) FROM lap_quality WHERE clean_lap=1"));
            Assert.Equal(rewinds.Count, Scalar(con, "SELECT COUNT(*) FROM rewind_events"));
            Assert.True(Scalar(con, "SELECT COUNT(*) FROM suspected_state_reset_events WHERE car_idx=21") > 0);
            foreach (var quality in expected)
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT lap_time_ms, clean_lap, sample_count FROM lap_quality WHERE session_uid=$uid AND car_idx=$car AND lap_num=$lap";
                cmd.Parameters.AddWithValue("$uid", quality.SessionUid.ToString());
                cmd.Parameters.AddWithValue("$car", quality.CarIndex);
                cmd.Parameters.AddWithValue("$lap", quality.LapNum);
                using var r = cmd.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal(quality.LapTimeMs, (uint)r.GetInt64(0));
                Assert.Equal(quality.CleanLap, r.GetBoolean(1));
                Assert.Equal(quality.SampleCount, r.GetInt32(2));
            }
        });
    }

    [Fact]
    public void RepeatedFinalPacketsCountSelectedDriversOnly()
    {
        WithDatabase((folder, path) =>
        {
            using (var db = new TelemetryDatabase(path))
            {
                for (uint frame = 1; frame <= 7; frame++)
                {
                    var packet = Packet(8, 1 + 46 * 24, frame);
                    packet[F12026Parser.HeaderSize] = 22;
                    for (var car = 0; car < 22; car++)
                    {
                        var row = packet.AsSpan(F12026Parser.HeaderSize + 1 + car * 46, 46);
                        row[0] = (byte)(car + 1);
                        row[1] = 29;
                        row[5] = 3;
                    }
                    Store(db, packet, DateTimeOffset.UnixEpoch.AddSeconds(frame));
                }
                db.SaveMetadata(new SessionMetadata { SessionUid = 91, SessionType = 15,
                    SessionFolder = folder, DatabasePath = path });
            }
            AnalysisEngine.AnalyzeSession(folder);
            using var con = Open(path);
            Assert.Equal(22, Scalar(con, "SELECT COUNT(*) FROM final_classification WHERE classification_source='official_udp'"));
            using var quality = con.CreateCommand();
            quality.CommandText = "SELECT summary FROM data_quality WHERE dimension='session_completeness'";
            Assert.Equal("Official UDP classification contains 22 rows.", quality.ExecuteScalar());
        });
    }

    [Fact]
    public void ClassificationFromAnotherSessionIsNeverAppliedToLatestLapSession()
    {
        WithDatabase((folder, path) =>
        {
            using (var db = new TelemetryDatabase(path))
            {
                var laps = Packet(2, 57 * 24, 1);
                BinaryPrimitives.WriteUInt64LittleEndian(laps.AsSpan(7), 92);
                var row = laps.AsSpan(F12026Parser.HeaderSize + 21 * 57, 57);
                row[32] = 1; row[33] = 1;
                Store(db, laps, DateTimeOffset.UnixEpoch);
                var finals = Packet(8, 1 + 46 * 24, 2);
                finals[F12026Parser.HeaderSize] = 1;
                finals[F12026Parser.HeaderSize + 1] = 1;
                finals[F12026Parser.HeaderSize + 2] = 29;
                finals[F12026Parser.HeaderSize + 6] = 3;
                Store(db, finals, DateTimeOffset.UnixEpoch.AddSeconds(1));
            }
            AnalysisEngine.AnalyzeSession(folder);
            using var con = Open(path);
            Assert.Equal(0, Scalar(con, "SELECT COUNT(*) FROM final_classification WHERE classification_source='official_udp'"));
            Assert.Equal(1, Scalar(con, "SELECT COUNT(*) FROM final_classification WHERE car_idx=21 AND classification_is_official=0"));
        });
    }

    [Fact]
    public void FailedFinalizationDoesNotReplaceOriginalDatabase()
    {
        WithDatabase((folder, path) =>
        {
            using (var db = new TelemetryDatabase(path))
                Store(db, Packet(6, 59 * 24, 1), DateTimeOffset.UnixEpoch);
            SqliteConnection.ClearAllPools();
            var before = SHA256.HashData(File.ReadAllBytes(path));
            Assert.Throws<InvalidOperationException>(() => AnalysisEngine.AnalyzeSession(folder, message =>
            {
                if (message.StartsWith("Finalizing player", StringComparison.Ordinal))
                    throw new InvalidOperationException("Injected finalization failure");
            }));
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Empty(Directory.GetFiles(folder, "session.analysis.*"));
        });
    }

    [Fact]
    public void RawPacketsAndSessionCounterCommitTogetherAtBatchBoundary()
    {
        WithDatabase((folder, path) =>
        {
            using var db = new TelemetryDatabase(path);
            for (uint frame = 1; frame <= 750; frame++)
                Store(db, Packet(6, 59, frame), DateTimeOffset.UnixEpoch.AddMilliseconds(frame));
            db.Flush();
            using var con = Open(path);
            Assert.Equal(750, Scalar(con, "SELECT COUNT(*) FROM raw_packets"));
            Assert.Equal(750, Scalar(con, "SELECT SUM(packet_count) FROM session_segments"));
        });
    }

    [Fact]
    public void SelectedCarParsingMatchesFullArrayAtSlotTwentyOne()
    {
        var at = DateTimeOffset.UnixEpoch;
        var telemetry = Packet(6, 59 * 24, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(telemetry.AsSpan(F12026Parser.HeaderSize + 21 * 59), 321);
        Assert.Equal(F12026Parser.ParseCarTelemetryPacket(telemetry, at)[21],
            Assert.Single(F12026Parser.ParseCarTelemetryPacket(telemetry, at, onlyCarIndex: 21)));
        var status = Packet(7, 59 * 24, 1);
        status[F12026Parser.HeaderSize + 21 * 59 + 41] = 3;
        Assert.Equal(F12026Parser.ParseCarStatusPacket(status, at)[21],
            Assert.Single(F12026Parser.ParseCarStatusPacket(status, at, onlyCarIndex: 21)));
        var damage = Packet(10, 46 * 24, 1);
        BinaryPrimitives.WriteSingleLittleEndian(damage.AsSpan(F12026Parser.HeaderSize + 21 * 46), 42);
        Assert.Equal(F12026Parser.ParseCarDamagePacket(damage, at)[21],
            Assert.Single(F12026Parser.ParseCarDamagePacket(damage, at, onlyCarIndex: 21)));
        foreach (var invalid in new[] { -1, 24, 255, int.MaxValue })
            Assert.Empty(F12026Parser.ParseCarTelemetryPacket(telemetry, at, onlyCarIndex: invalid));
    }

    private static byte[] Packet(byte id, int size, uint frame)
    {
        var packet = new byte[F12026Parser.HeaderSize + size];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 2026);
        packet[2] = 26; packet[5] = 1; packet[6] = id;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 91);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(19), frame);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), frame);
        packet[27] = 21; packet[28] = 255;
        return packet;
    }

    private static void Store(TelemetryDatabase db, byte[] packet, DateTimeOffset at)
    {
        Assert.True(F12026Parser.TryParseHeader(packet, out var header));
        db.InsertRaw(at, header, packet);
    }

    private static SqliteConnection Open(string path)
    {
        var con = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        con.Open(); return con;
    }

    private static long Scalar(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void WithDatabase(Action<string, string> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), "f1-reliability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { test(folder, Path.Combine(folder, "session.sqlite")); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
}
