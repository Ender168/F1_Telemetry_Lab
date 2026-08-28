using F1TelemetryLab;
using Microsoft.Data.Sqlite;
using System.Buffers.Binary;

namespace F1TelemetryLab.Tests;

public sealed class QualityAndSchemaTests
{
    [Fact]
    public void FinalClassificationPreservesPenaltyDnfAndTyreStints()
    {
        var packet = new byte[F12026Parser.HeaderSize + 1 + 46 * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 2026);
        packet[2] = 26;
        packet[5] = 1;
        packet[6] = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 77);
        packet[27] = 0;
        packet[28] = 255;
        packet[F12026Parser.HeaderSize] = 2;

        var winner = packet.AsSpan(F12026Parser.HeaderSize + 1, 46);
        winner[0] = 1;
        winner[1] = 3;
        winner[4] = 1;
        winner[5] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(winner[7..], 81_081);
        BinaryPrimitives.WriteDoubleLittleEndian(winner[11..], 250.5);
        winner[21] = 2;

        var dnf = packet.AsSpan(F12026Parser.HeaderSize + 1 + 46, 46);
        dnf[0] = 2;
        dnf[1] = 2;
        dnf[4] = 1;
        dnf[5] = 4;
        dnf[6] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(dnf[7..], 83_559);
        BinaryPrimitives.WriteDoubleLittleEndian(dnf[11..], 180.0);
        dnf[19] = 7;
        dnf[20] = 2;
        dnf[21] = 3;

        var rows = F12026Parser.ParseFinalClassificationPacket(packet, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].NumTyreStints);
        Assert.Equal(4, rows[1].ResultStatus);
        Assert.Equal(7, rows[1].PenaltiesTimeSeconds);
        Assert.Equal(2, rows[1].NumPenalties);
        Assert.Equal(3, rows[1].NumTyreStints);
        Assert.Equal(7, rows[1].ResultReason);
    }

    [Fact]
    public void SetupPacketUsesTheOfficial2026FiftyByteLayout()
    {
        var packet = new byte[1_233];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 2026);
        packet[2] = 26;
        packet[5] = 1;
        packet[6] = 5;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(19), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), 120);
        packet[27] = 21;
        packet[28] = 255;

        var row = packet.AsSpan(F12026Parser.HeaderSize + 21 * 50, 50);
        row[0] = 28; row[1] = 22; row[2] = 70; row[3] = 50;
        BinaryPrimitives.WriteSingleLittleEndian(row[4..], -3.2f);
        BinaryPrimitives.WriteSingleLittleEndian(row[8..], -1.8f);
        BinaryPrimitives.WriteSingleLittleEndian(row[12..], 0.05f);
        BinaryPrimitives.WriteSingleLittleEndian(row[16..], 0.18f);
        row[20] = 32; row[21] = 12; row[22] = 18; row[23] = 8;
        row[24] = 21; row[25] = 48; row[26] = 100; row[27] = 55; row[28] = 40;
        BinaryPrimitives.WriteSingleLittleEndian(row[29..], 22.4f);
        BinaryPrimitives.WriteSingleLittleEndian(row[33..], 22.4f);
        BinaryPrimitives.WriteSingleLittleEndian(row[37..], 24.2f);
        BinaryPrimitives.WriteSingleLittleEndian(row[41..], 24.2f);
        row[45] = 7;
        BinaryPrimitives.WriteSingleLittleEndian(row[46..], 31.5f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(F12026Parser.HeaderSize + 24 * 50), 30f);

        var setups = F12026Parser.ParseCarSetupPacket(packet, DateTimeOffset.UnixEpoch, activeCars: 22);
        Assert.Equal(22, setups.Count);
        var setup = setups[21];
        Assert.True(setup.IsPlayer);
        Assert.Equal((28, 22, 70, 50), (setup.FrontWing, setup.RearWing, setup.OnThrottle, setup.OffThrottle));
        Assert.Equal((-3.2f, -1.8f, 0.05f, 0.18f), (setup.FrontCamber, setup.RearCamber, setup.FrontToe, setup.RearToe));
        Assert.Equal((32, 12, 18, 8), (setup.FrontSuspension, setup.RearSuspension, setup.FrontAntiRollBar, setup.RearAntiRollBar));
        Assert.Equal((21, 48, 100, 55, 40), (setup.FrontRideHeight, setup.RearRideHeight, setup.BrakePressure, setup.BrakeBias, setup.EngineBraking));
        Assert.Equal((22.4f, 22.4f, 24.2f, 24.2f), (setup.RearLeftTyrePressure, setup.RearRightTyrePressure, setup.FrontLeftTyrePressure, setup.FrontRightTyrePressure));
        Assert.Equal(7, setup.Ballast);
        Assert.Equal(31.5f, setup.FuelLoad);
        Assert.Equal(30f, setup.NextFrontWingValue.GetValueOrDefault());
    }

    [Fact]
    public void SequenceRulesIgnoreLegitimateRepeatedFrames()
    {
        var tracker = new PacketSequenceTracker();
        var firstEvent = tracker.Observe(Header(packetId: 3, overall: 100));
        var secondEvent = tracker.Observe(Header(packetId: 3, overall: 100));
        var perCarFirst = tracker.Observe(Header(packetId: 11, overall: 101));
        var perCarSecond = tracker.Observe(Header(packetId: 11, overall: 101));
        var terminal = tracker.Observe(Header(packetId: 6, overall: 0));

        Assert.True(firstEvent.SequenceIgnored);
        Assert.True(secondEvent.SequenceIgnored);
        Assert.True(perCarFirst.SequenceIgnored);
        Assert.True(perCarSecond.SequenceIgnored);
        Assert.True(terminal.SequenceIgnored);
        Assert.False(firstEvent.Duplicate || secondEvent.Duplicate || perCarFirst.Duplicate || perCarSecond.Duplicate || terminal.OutOfOrder);
    }

    [Fact]
    public void SequenceRulesStillDetectTelemetryDuplicatesAndDisorder()
    {
        var tracker = new PacketSequenceTracker();
        Assert.False(tracker.Observe(Header(packetId: 6, overall: 100)).Duplicate);
        Assert.True(tracker.Observe(Header(packetId: 6, overall: 100)).Duplicate);
        Assert.True(tracker.Observe(Header(packetId: 6, overall: 99)).OutOfOrder);
    }

    [Fact]
    public void UnknownMissingFrameEstimateDoesNotCreateAWarning()
    {
        var quality = new RecordingQualitySnapshot(50_000, 100_000, 0, 0, 0, 0, 12_000, 0, 0, 18, 0);
        Assert.False(quality.MissingFrameEstimateAvailable);
        Assert.Equal("Good", quality.Rating);
    }

    [Fact]
    public void SchemaMigrationIsVersionedAndIdempotent()
    {
        SQLitePCL.Batteries_V2.Init();
        var path = Path.Combine(Path.GetTempPath(), $"f1tlab-schema-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            Execute(connection, "CREATE TABLE raw_packets(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE car_telemetry(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE recording_quality(id INTEGER PRIMARY KEY);");

            DatabaseSchemaMigrator.Apply(connection);
            DatabaseSchemaMigrator.Apply(connection);

            Assert.Equal(AppInfo.DatabaseSchemaVersion, DatabaseSchemaMigrator.ReadUserVersion(connection));
            Assert.Contains("overall_frame_identifier", Columns(connection, "raw_packets"));
            Assert.Contains("missing_frames_estimated", Columns(connection, "recording_quality"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='data_quality'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CompactChatGptDatabaseContainsSetupSnapshots()
    {
        SQLitePCL.Batteries_V2.Init();
        var folder = Path.Combine(Path.GetTempPath(), $"f1tlab-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "session.sqlite");
        try
        {
            using (var database = new TelemetryDatabase(source)) { }
            using (var connection = new SqliteConnection($"Data Source={source}"))
            {
                connection.Open();
                Execute(connection, """
                    INSERT INTO car_setups(
                        received_at,session_uid,session_time,frame_identifier,overall_frame_identifier,player_car_index,car_idx,is_player,
                        front_wing,rear_wing,on_throttle,off_throttle,front_camber,rear_camber,front_toe,rear_toe,
                        front_suspension,rear_suspension,front_anti_roll_bar,rear_anti_roll_bar,front_ride_height,rear_ride_height,
                        brake_pressure,brake_bias,engine_braking,rear_left_tyre_pressure,rear_right_tyre_pressure,
                        front_left_tyre_pressure,front_right_tyre_pressure,ballast,fuel_load,next_front_wing_value)
                    VALUES ('2026-01-01T00:00:00Z','42',1,1,1,0,0,1,30,25,70,50,-3.2,-1.8,0.05,0.18,30,10,18,8,21,48,100,55,40,22.4,22.4,24.2,24.2,7,31.5,30);
                    """);
            }

            _ = SessionPackager.CreateZip(folder, source, "SetupFixture");
            var compact = Path.Combine(folder, "chatgpt_pack.sqlite");
            using var packed = new SqliteConnection($"Data Source={compact};Mode=ReadOnly");
            packed.Open();
            Assert.Equal(1L, ScalarLong(packed, "SELECT COUNT(*) FROM car_setups"));
            Assert.Equal(30L, ScalarLong(packed, "SELECT front_wing FROM car_setups WHERE is_player=1"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static PacketHeader Header(byte packetId, uint overall, ulong sessionUid = 1) =>
        new(2026, 26, 1, 0, 1, packetId, sessionUid, 1, overall, overall, 0, 255);

    private static IReadOnlyList<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
