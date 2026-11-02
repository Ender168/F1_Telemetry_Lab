using System.Buffers.Binary;
using F1TelemetryLab;
using Microsoft.Data.Sqlite;

namespace F1TelemetryLab.Tests;

public sealed class TerminalClassificationTests
{
    [Theory]
    [InlineData("matched")]
    [InlineData("duplicate")]
    [InlineData("conflict")]
    [InlineData("unrelated")]
    [InlineData("adjacent")]
    [InlineData("missing")]
    [InlineData("ordinary")]
    [InlineData("no_end")]
    [InlineData("grid_mismatch")]
    [InlineData("expired")]
    public void TerminalResultsAreGuardedAndNeverReplaceRaceFrames(string scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "f1-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.sqlite");
        var stored = new List<byte[]>();
        try
        {
            using (var db = new TelemetryDatabase(path))
            {
                void Put(byte[] p, double at)
                {
                    Assert.True(F12026Parser.TryParseHeader(p, out var h));
                    db.InsertRaw(DateTimeOffset.UnixEpoch.AddSeconds(at), h, p); stored.Add(p);
                }
                Put(Laps(0, 0, 1, 1, false), 0);
                var names = Packet(4, 1 + 60 * 24, 1, 0); names[29] = 2;
                names[40] = (byte)'A'; names[100] = (byte)'B'; Put(names, .1);
                Put(Status(0, 0, 38, 2_000_000), .2);
                Put(Laps(10, 80, 2, 1, false), 80);
                Put(Status(10, 80, 10, 1_000_000), 80.1);
                Put(Laps(11, 90, 2, 2, false), 90);
                Put(Laps(12, 160, 3, 2, true), 160);
                var end = Packet(3, 4, 13, 165); "SEND"u8.CopyTo(end.AsSpan(29)); if (scenario != "no_end") Put(end, 165);
                if (scenario == "adjacent") Put(Laps(1, 0, 1, 1, false, 92), 166);
                var zeroStatus = Status(0, 200, 1.32f, 4_000_000, 0); Put(zeroStatus, 200);
                Put(Laps(0, 200, 3, 2, true, 0), 200.1);
                if (scenario != "missing")
                {
                    var final = Final(scenario == "ordinary" ? 91ul : 0);
                    if (scenario == "unrelated") final[29 + 1 + 46 + 4] = 4; // Wrong pit count.
                    if (scenario == "grid_mismatch") final[30 + 46 + 2] = 1;
                    Put(final, scenario == "expired" ? 400 : 201);
                    if (scenario is "duplicate" or "conflict")
                    {
                        var again = (byte[])final.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(again.AsSpan(23), 170);
                        if (scenario == "conflict") again[29 + 1 + 46 + 19] = 9;
                        Put(again, 203);
                    }
                }
                db.SaveMetadata(new SessionMetadata { SessionUid = 91, TrackLengthMeters = 5000, SessionType = 15,
                    SessionFolder = folder, DatabasePath = path });
            }
            AnalysisEngine.AnalyzeSession(folder);
            if (scenario == "duplicate") AnalysisEngine.AnalyzeSession(folder);
            using var con = new SqliteConnection($"Data Source={path};Pooling=False"); con.Open();
            object? Value(string sql) { using var c = con.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
            long Count(string sql) => Convert.ToInt64(Value(sql));
            Assert.Equal(stored.Count, Count("SELECT COUNT(*) FROM raw_packets"));
            using (var c = con.CreateCommand())
            {
                c.CommandText = "SELECT payload FROM raw_packets ORDER BY id"; using var r = c.ExecuteReader();
                foreach (var expected in stored) { Assert.True(r.Read()); Assert.Equal(expected, (byte[])r[0]); }
            }
            Assert.Equal(0, Count("SELECT COUNT(*) FROM lap_quality WHERE session_uid='0'"));
            Assert.Equal(0, Count("SELECT COUNT(*) FROM rewind_events"));
            Assert.True(Count("SELECT COUNT(*) FROM terminal_packet_context") >= 2);
            var expectedSource = scenario == "ordinary" ? "official_udp" : scenario is "matched" or "duplicate" ? "contextual_uid_zero_udp" : "provisional_latest_lap_data";
            Assert.Equal(expectedSource, Value("SELECT classification_source FROM final_classification WHERE car_idx=1"));
            if (scenario is "matched" or "duplicate")
            {
                Assert.Equal(1, Count("SELECT lap_num FROM final_classification WHERE car_idx=1"));
                Assert.Equal(0, Count("SELECT classification_is_official FROM final_classification WHERE car_idx=1"));
                Assert.Equal("0", Value("SELECT header_session_uid FROM final_classification WHERE car_idx=1"));
                Assert.Equal("91", Value("SELECT associated_session_uid FROM final_classification WHERE car_idx=1"));
                Assert.Equal(0, Count("SELECT COUNT(*) FROM lap_summary WHERE car_idx=1 AND lap_num>1"));
                Assert.Equal("Contextual", Value("SELECT rating FROM data_quality WHERE dimension='session_completeness'"));
            }
            if (scenario == "conflict")
                Assert.Equal(2, Count("SELECT COUNT(*) FROM terminal_classification_association WHERE status='conflict'"));
            if (scenario != "adjacent")
            {
                Assert.Equal(38d, Convert.ToDouble(Value("SELECT fuel_start FROM lap_state_summary WHERE car_idx=1 AND lap_num=1")), 3);
                Assert.Equal(2_000_000d, Convert.ToDouble(Value("SELECT ers_start FROM lap_state_summary WHERE car_idx=1 AND lap_num=1")), 3);
                Assert.Equal(10d, Convert.ToDouble(Value("SELECT fuel_end FROM lap_state_summary WHERE car_idx=1 AND lap_num=1")), 3);
                Assert.Equal(1_000_000d, Convert.ToDouble(Value("SELECT ers_end FROM lap_state_summary WHERE car_idx=1 AND lap_num=1")), 3);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    private static byte[] Packet(byte id, int size, uint frame, float time, ulong uid = 91)
    {
        var p = new byte[29 + size]; BinaryPrimitives.WriteUInt16LittleEndian(p, 2026);
        p[2] = 26; p[5] = 1; p[6] = id; p[27] = 1; p[28] = 255;
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(7), uid);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(15), time);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(19), frame);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(23), frame); return p;
    }
    private static byte[] Laps(uint frame, float time, byte leaderLap, byte playerLap, bool finished, ulong uid = 91)
    {
        var p = Packet(2, 57 * 24, frame, time, uid);
        for (var i = 0; i < 2; i++)
        {
            var r = p.AsSpan(29 + i * 57, 57); var lap = i == 0 ? leaderLap : playerLap;
            r[32] = (byte)(i + 1); r[43] = (byte)(i + 1); r[33] = lap; r[44] = 1; r[45] = finished ? (byte)3 : (byte)2;
            BinaryPrimitives.WriteUInt32LittleEndian(r, lap > 1 ? (i == 0 ? 80000u : 90000u) : 0);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], (lap - 1) * 5000);
        }
        return p;
    }
    private static byte[] Status(uint frame, float time, float fuel, float ers, ulong uid = 91)
    {
        var p = Packet(7, 59 * 24, frame, time, uid);
        for (var i = 0; i < 2; i++)
        {
            var r = p.AsSpan(29 + i * 59, 59);
            BinaryPrimitives.WriteSingleLittleEndian(r[5..], fuel);
            BinaryPrimitives.WriteSingleLittleEndian(r[37..], ers);
        }
        return p;
    }
    private static byte[] Final(ulong uid)
    {
        var p = Packet(8, 1 + 46 * 24, 0, 201, uid); p[29] = 2;
        for (var i = 0; i < 2; i++)
        {
            var r = p.AsSpan(30 + i * 46, 46); r[0] = (byte)(i + 1); r[1] = (byte)(2 - i);
            r[2] = (byte)(i + 1); r[5] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(r[7..], i == 0 ? 80000u : 90000u);
            BinaryPrimitives.WriteDoubleLittleEndian(r[11..], i == 0 ? 160 : 90);
        }
        return p;
    }
}
