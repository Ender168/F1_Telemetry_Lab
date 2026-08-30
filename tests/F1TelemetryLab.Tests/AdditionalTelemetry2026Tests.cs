using F1TelemetryLab;
using Microsoft.Data.Sqlite;
using System.Buffers.Binary;

namespace F1TelemetryLab.Tests;

public sealed class AdditionalTelemetry2026Tests
{
    [Fact]
    public void Extra_2026_packets_build_compact_queryable_projections()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = Path.Combine(Path.GetTempPath(), "f1tlab-extra-2026-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "session.sqlite");
        const ulong uid = 98765;
        try
        {
            using (var database = new TelemetryDatabase(dbPath))
            {
                var history = SessionHistoryPacket(uid, frame: 10, playerCarIndex: 0);
                var tyreSets = TyreSetsPacket(uid, frame: 11, playerCarIndex: 0);
                var telemetry2 = Telemetry2Packet(uid, frame: 12, playerCarIndex: 0);
                foreach (var payload in new[] { history, tyreSets, telemetry2 })
                {
                    Assert.True(F12026Parser.TryParseHeader(payload, out var header));
                    database.InsertRaw(DateTimeOffset.UnixEpoch.AddMilliseconds(header.FrameIdentifier), header, payload);
                }
            }

            AdditionalTelemetry2026Service.Enrich(dbPath);
            AdditionalTelemetry2026Service.Enrich(dbPath); // idempotency

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            Assert.Equal(2L, ScalarLong(connection, "SELECT COUNT(*) FROM session_history_laps"));
            Assert.Equal(90_000L, ScalarLong(connection, "SELECT lap_time_ms FROM session_history_laps WHERE lap_num=1"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT is_best_lap FROM session_history_laps WHERE lap_num=2"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT lap_valid FROM session_history_laps WHERE lap_num=1"));

            Assert.Equal(2L, ScalarLong(connection, "SELECT COUNT(*) FROM session_history_stints"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT is_current FROM session_history_stints WHERE stint_index=1"));
            Assert.Equal(17L, ScalarLong(connection, "SELECT actual_tyre_compound FROM session_history_stints WHERE stint_index=1"));

            Assert.Equal(20L, ScalarLong(connection, "SELECT COUNT(*) FROM tyre_sets"));
            Assert.Equal(12L, ScalarLong(connection, "SELECT wear FROM tyre_sets WHERE set_idx=0"));
            Assert.Equal(-250L, ScalarLong(connection, "SELECT lap_delta_time_ms FROM tyre_sets WHERE set_idx=0"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT fitted FROM tyre_sets WHERE set_idx=0"));

            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM car_telemetry_2_player"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT active_aero_mode FROM car_telemetry_2_player"));
            Assert.Equal(120L, ScalarLong(connection, "SELECT active_aero_activation_distance FROM car_telemetry_2_player"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT overtake_active FROM car_telemetry_2_player"));
            Assert.Equal(80L, ScalarLong(connection, "SELECT overtake_activation_distance FROM car_telemetry_2_player"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT regulations_2026 FROM car_telemetry_2_player"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static byte[] SessionHistoryPacket(ulong uid, uint frame, byte playerCarIndex)
    {
        var packet = Packet(11, 1460, uid, frame, playerCarIndex);
        var p = F12026Parser.HeaderSize;
        packet[p] = playerCarIndex;
        packet[p + 1] = 2; // laps
        packet[p + 2] = 2; // stints
        packet[p + 3] = 2; // best lap
        packet[p + 4] = 1;
        packet[p + 5] = 2;
        packet[p + 6] = 2;

        var lap1 = packet.AsSpan(p + 7, 14);
        BinaryPrimitives.WriteUInt32LittleEndian(lap1, 90_000);
        BinaryPrimitives.WriteUInt16LittleEndian(lap1[4..], 30_000);
        lap1[6] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(lap1[7..], 29_000);
        lap1[9] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(lap1[10..], 31_000);
        lap1[12] = 0;
        lap1[13] = 0x0F;

        var lap2 = packet.AsSpan(p + 7 + 14, 14);
        BinaryPrimitives.WriteUInt32LittleEndian(lap2, 89_000);
        BinaryPrimitives.WriteUInt16LittleEndian(lap2[4..], 29_500);
        BinaryPrimitives.WriteUInt16LittleEndian(lap2[7..], 28_500);
        BinaryPrimitives.WriteUInt16LittleEndian(lap2[10..], 31_000);
        lap2[13] = 0x0F;

        var stintBase = p + 7 + 100 * 14;
        packet[stintBase] = 1;
        packet[stintBase + 1] = 16;
        packet[stintBase + 2] = 16;
        packet[stintBase + 3] = 255;
        packet[stintBase + 4] = 17;
        packet[stintBase + 5] = 17;
        return packet;
    }

    private static byte[] TyreSetsPacket(ulong uid, uint frame, byte playerCarIndex)
    {
        var packet = Packet(12, 231, uid, frame, playerCarIndex);
        var p = F12026Parser.HeaderSize;
        packet[p] = playerCarIndex;
        var set0 = packet.AsSpan(p + 1, 10);
        set0[0] = 16;
        set0[1] = 16;
        set0[2] = 12;
        set0[3] = 1;
        set0[4] = 15;
        set0[5] = 15;
        set0[6] = 25;
        BinaryPrimitives.WriteInt16LittleEndian(set0[7..], -250);
        set0[9] = 1;
        packet[p + 1 + 20 * 10] = 0;
        return packet;
    }

    private static byte[] Telemetry2Packet(ulong uid, uint frame, byte playerCarIndex)
    {
        var packet = Packet(16, 269, uid, frame, playerCarIndex);
        var row = packet.AsSpan(F12026Parser.HeaderSize + playerCarIndex * 10, 10);
        row[0] = 1;
        row[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(row[2..], 120);
        row[4] = 1;
        row[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(row[6..], 80);
        row[8] = 1;
        row[9] = 0;
        return packet;
    }

    private static byte[] Packet(byte packetId, int totalSize, ulong uid, uint frame, byte playerCarIndex)
    {
        var packet = new byte[totalSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 2026);
        packet[2] = 26;
        packet[3] = 1;
        packet[5] = 1;
        packet[6] = packetId;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), uid);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(15), frame / 60f);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(19), frame);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), frame);
        packet[27] = playerCarIndex;
        packet[28] = 255;
        return packet;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
