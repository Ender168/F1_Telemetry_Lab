using F1TelemetryLab;
using Microsoft.Data.Sqlite;
using System.Buffers.Binary;

namespace F1TelemetryLab.Tests;

public sealed class TelemetryCompletenessTests
{
    [Fact]
    public void Enrich_recovers_thermal_motion_and_lap_position_data_from_raw_packets()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = Path.Combine(Path.GetTempPath(), "F1TelemetryLab_completeness_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "session.sqlite");
        try
        {
            using (var con = new SqliteConnection($"Data Source={dbPath}"))
            {
                con.Open();
                using var schema = con.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE raw_packets(
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        received_at TEXT,
                        packet_format INTEGER,
                        packet_id INTEGER,
                        payload BLOB
                    );
                    CREATE TABLE car_telemetry(
                        session_uid TEXT NOT NULL,
                        overall_frame_identifier INTEGER NOT NULL,
                        car_idx INTEGER NOT NULL,
                        speed INTEGER,
                        PRIMARY KEY(session_uid, overall_frame_identifier, car_idx)
                    ) WITHOUT ROWID;
                    CREATE TABLE session_metadata(key TEXT PRIMARY KEY, value TEXT);
                    CREATE TABLE final_classification(
                        position INTEGER,
                        car_idx INTEGER,
                        classification_source TEXT,
                        classification_is_official INTEGER,
                        classification_note TEXT
                    );
                    INSERT INTO car_telemetry(session_uid,overall_frame_identifier,car_idx,speed) VALUES('123',10,2,300);
                    INSERT INTO session_metadata(key,value) VALUES('session_type','11');
                    INSERT INTO final_classification(position,car_idx,classification_source,classification_is_official,classification_note)
                    VALUES(0,2,'provisional_latest_lap_data',0,'provisional');
                    """;
                schema.ExecuteNonQuery();

                InsertRaw(con, 6, BuildTelemetryPacket());
                InsertRaw(con, 13, BuildMotionExPacket());
                InsertRaw(con, 15, BuildLapPositionsPacket());
                InsertRaw(con, 3, BuildEventPacket("RCWN", 13));
            }

            TelemetryCompletenessService.Enrich(dbPath);

            using var verify = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            verify.Open();

            using (var telemetry = verify.CreateCommand())
            {
                telemetry.CommandText = "SELECT brake_temp_fl, tyre_surface_temp_fl, tyre_inner_temp_fl, tyre_pressure_fl FROM car_telemetry WHERE session_uid='123' AND overall_frame_identifier=10 AND car_idx=2";
                using var reader = telemetry.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(500, reader.GetInt32(0));
                Assert.Equal(105, reader.GetInt32(1));
                Assert.Equal(99, reader.GetInt32(2));
                Assert.InRange(reader.GetDouble(3), 23.29, 23.31);
            }

            using (var motion = verify.CreateCommand())
            {
                motion.CommandText = "SELECT wheel_slip_ratio_fl, wheel_slip_angle_fl, suspension_position_fl FROM motion_ex_player WHERE session_uid='123' AND overall_frame_identifier=11";
                using var reader = motion.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(19.0, reader.GetDouble(0), 3);
                Assert.Equal(23.0, reader.GetDouble(1), 3);
                Assert.Equal(3.0, reader.GetDouble(2), 3);
            }

            using (var positions = verify.CreateCommand())
            {
                positions.CommandText = "SELECT lap_index, lap_num, position, is_player FROM lap_positions WHERE session_uid='123' AND car_idx=2";
                using var reader = positions.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(4, reader.GetInt32(0));
                Assert.Equal(5, reader.GetInt32(1));
                Assert.Equal(3, reader.GetInt32(2));
                Assert.Equal(1, reader.GetInt32(3));
            }

            Assert.Equal("Race", ReadMeta(verify, "inferred_session_kind"));
            Assert.Equal("Sprint Shootout 2", ReadMeta(verify, "raw_session_name"));
            Assert.Equal("1", ReadMeta(verify, "session_type_conflict"));
            Assert.Equal("0", ReadMeta(verify, "packet_8_present"));

            using (var classification = verify.CreateCommand())
            {
                classification.CommandText = "SELECT position, provisional, classification_source FROM final_classification WHERE car_idx=2";
                using var reader = classification.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(3, reader.GetInt32(0));
                Assert.Equal(1, reader.GetInt32(1));
                Assert.Equal("provisional_lap_data_with_lap_positions_backfill", reader.GetString(2));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Official_session_type_names_match_2026_season_pack_spec()
    {
        Assert.Equal("Sprint Shootout 1", TelemetryCompletenessService.GetOfficialSessionTypeName(10));
        Assert.Equal("Sprint Shootout 2", TelemetryCompletenessService.GetOfficialSessionTypeName(11));
        Assert.Equal("One Shot Sprint Shootout", TelemetryCompletenessService.GetOfficialSessionTypeName(14));
        Assert.Equal("Race", TelemetryCompletenessService.GetOfficialSessionTypeName(15));
        Assert.Equal("Race 2", TelemetryCompletenessService.GetOfficialSessionTypeName(16));
        Assert.Equal("Race 3", TelemetryCompletenessService.GetOfficialSessionTypeName(17));
        Assert.Equal("Time Trial", TelemetryCompletenessService.GetOfficialSessionTypeName(18));
    }

    private static void InsertRaw(SqliteConnection con, int packetId, byte[] payload)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO raw_packets(received_at,packet_format,packet_id,payload) VALUES($received,2026,$id,$payload)";
        cmd.Parameters.AddWithValue("$received", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", packetId);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.ExecuteNonQuery();
    }

    private static byte[] BuildTelemetryPacket()
    {
        var packet = BuildPacket(6, 10, 1448);
        var o = F12026Parser.HeaderSize + 2 * 59;
        WriteU16(packet, o + 22, 400);
        WriteU16(packet, o + 24, 410);
        WriteU16(packet, o + 26, 500);
        WriteU16(packet, o + 28, 450);
        packet[o + 30] = 90; packet[o + 31] = 91; packet[o + 32] = 105; packet[o + 33] = 95;
        packet[o + 34] = 80; packet[o + 35] = 81; packet[o + 36] = 99; packet[o + 37] = 85;
        packet[o + 38] = 100;
        WriteF32(packet, o + 39, 22.1f); WriteF32(packet, o + 43, 22.2f); WriteF32(packet, o + 47, 23.3f); WriteF32(packet, o + 51, 23.4f);
        return packet;
    }

    private static byte[] BuildMotionExPacket()
    {
        var packet = BuildPacket(13, 11, 273);
        var o = F12026Parser.HeaderSize;
        for (var i = 0; i < 61; i++) WriteF32(packet, o + i * 4, i + 1);
        return packet;
    }

    private static byte[] BuildLapPositionsPacket()
    {
        var packet = BuildPacket(15, 12, 1231);
        packet[F12026Parser.HeaderSize] = 1;
        packet[F12026Parser.HeaderSize + 1] = 4;
        packet[F12026Parser.HeaderSize + 2 + 2] = 3;
        return packet;
    }

    private static byte[] BuildEventPacket(string code, uint overallFrame)
    {
        var packet = BuildPacket(3, overallFrame, F12026Parser.HeaderSize + 4);
        System.Text.Encoding.ASCII.GetBytes(code).CopyTo(packet, F12026Parser.HeaderSize);
        return packet;
    }

    private static byte[] BuildPacket(byte packetId, uint overallFrame, int size)
    {
        var packet = new byte[size];
        WriteU16(packet, 0, 2026);
        packet[2] = 26;
        packet[3] = 1;
        packet[5] = 1;
        packet[6] = packetId;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7, 8), 123);
        WriteF32(packet, 15, overallFrame / 60f);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(19, 4), overallFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23, 4), overallFrame);
        packet[27] = 2;
        packet[28] = 255;
        return packet;
    }

    private static string? ReadMeta(SqliteConnection con, string key)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT value FROM session_metadata WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return Convert.ToString(cmd.ExecuteScalar());
    }

    private static void WriteU16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    private static void WriteF32(byte[] data, int offset, float value) => BitConverter.GetBytes(value).CopyTo(data, offset);
}
