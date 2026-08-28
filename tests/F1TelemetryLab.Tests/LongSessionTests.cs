using F1TelemetryLab;
using System.Buffers.Binary;
using System.Diagnostics;

namespace F1TelemetryLab.Tests;

public sealed class LongSessionTests
{
    [Fact]
    public void LongSessionBenchmarkRemainsBoundedWhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("F1TLAB_RUN_LONG_TESTS"), "1", StringComparison.Ordinal)) return;

        const int packetCount = 72_000;
        const ulong sessionUid = 9_001;
        var folder = Path.Combine(Path.GetTempPath(), $"f1tlab-hour-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var databasePath = Path.Combine(folder, "session.sqlite");
        try
        {
            var startedAt = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
            using (var database = new TelemetryDatabase(databasePath))
            {
                var payload = TelemetryPacket(sessionUid);
                for (var frame = 1; frame <= packetCount; frame++)
                {
                    var sessionTime = frame / 20f;
                    BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(15), sessionTime);
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(19), checked((uint)frame));
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(23), checked((uint)frame));
                    var header = new PacketHeader(2026, 26, 1, 0, 1, 6, sessionUid, sessionTime, checked((uint)frame), checked((uint)frame), 0, 255);
                    database.InsertRaw(startedAt.AddMilliseconds(frame * 50L), header, payload);
                }
                database.SaveMetadata(new SessionMetadata
                {
                    SessionName = "One_Hour_Benchmark",
                    TrackName = "Benchmark",
                    TrackId = 0,
                    SessionType = 10,
                    TotalLaps = 50,
                    TrackLengthMeters = 5_000,
                    SessionUid = sessionUid,
                    StartedAt = startedAt,
                    StoppedAt = startedAt.AddHours(1),
                    DatabasePath = databasePath,
                    SessionFolder = folder
                });
                database.SaveQuality(new RecordingQualitySnapshot(packetCount, packetCount, 0, 0, 0, 0, 0, 0, 0, 16, 0));
            }

            var timer = Stopwatch.StartNew();
            var result = AnalysisEngine.AnalyzeSession(folder);
            var zip = SessionPackager.CreateZip(folder, databasePath, "One_Hour_Benchmark");
            timer.Stop();

            Assert.Equal(packetCount, result.RawPacketsProcessed);
            Assert.Equal(packetCount, result.TelemetryRows);
            Assert.True(timer.Elapsed < TimeSpan.FromMinutes(3), $"One-hour fixture analysis and packaging took {timer.Elapsed}.");
            Assert.True(Process.GetCurrentProcess().PeakWorkingSet64 < 2_500_000_000L, "Peak working set exceeded 2.5 GB.");
            Assert.True(new FileInfo(databasePath).Length > 1_000_000);
            Assert.True(File.Exists(zip));
            var compact = Path.Combine(folder, "chatgpt_pack.sqlite");
            Assert.True(File.Exists(compact));
            Assert.True(new FileInfo(compact).Length < new FileInfo(databasePath).Length);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static byte[] TelemetryPacket(ulong sessionUid)
    {
        var packet = new byte[F12026Parser.HeaderSize + 59];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 2026);
        packet[2] = 26;
        packet[3] = 1;
        packet[5] = 1;
        packet[6] = 6;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), sessionUid);
        packet[27] = 0;
        packet[28] = 255;
        var row = packet.AsSpan(F12026Parser.HeaderSize, 59);
        BinaryPrimitives.WriteUInt16LittleEndian(row, 280);
        BinaryPrimitives.WriteSingleLittleEndian(row[2..], 0.85f);
        BinaryPrimitives.WriteSingleLittleEndian(row[6..], 0.05f);
        BinaryPrimitives.WriteSingleLittleEndian(row[10..], 0.0f);
        row[15] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(row[16..], 11_000);
        row[18] = 1;
        return packet;
    }
}
