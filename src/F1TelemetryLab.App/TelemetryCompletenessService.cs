using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace F1TelemetryLab;

/// <summary>
/// Rebuilds analysis fields that are intentionally recoverable from raw UDP packets.
/// The operation is idempotent and can therefore be run for newly recorded sessions
/// as well as old session.sqlite files after the main analysis pipeline has finished.
/// </summary>
public static class TelemetryCompletenessService
{
    private const int CompletenessVersion = 1;
    private const int HeaderSize = F12026Parser.HeaderSize;
    private const int MaxCars = F12026Parser.MaxCars2026;
    private const int CarTelemetrySize = 59;
    private const int MotionExPayloadSize = 244;
    private const int LapPositionsHistoryLaps = 50;

    private sealed record LapPositionValue(
        string SessionUid,
        string ReceivedAt,
        long OverallFrame,
        int LapIndex,
        int CarIndex,
        int Position,
        int PlayerCarIndex);

    public static void Enrich(string databasePath, Action<string>? log = null)
    {
        if (!File.Exists(databasePath)) return;
        SQLitePCL.Batteries_V2.Init();

        var writeBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 60,
            Pooling = false
        };
        using var write = new SqliteConnection(writeBuilder.ToString());
        write.Open();
        Execute(write, "PRAGMA busy_timeout = 60000;");

        if (!TableExists(write, "raw_packets")) return;

        var relevantRawCount = CountRelevantRawPackets(write);
        var previousVersion = ReadMetaInt(write, "telemetry_completeness_version");
        var previousCount = ReadMetaLong(write, "telemetry_completeness_raw_packets");
        var alreadyComplete = previousVersion == CompletenessVersion &&
                              previousCount == relevantRawCount &&
                              (!TableExists(write, "car_telemetry") || ColumnExists(write, "car_telemetry", "tyre_inner_temp_fl")) &&
                              TableExists(write, "motion_ex_player") &&
                              TableExists(write, "lap_positions");
        if (alreadyComplete) return;

        EnsureSchema(write);

        var readBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 60,
            Pooling = false
        };
        using var read = new SqliteConnection(readBuilder.ToString());
        read.Open();

        var lapPositions = new Dictionary<(string SessionUid, int LapIndex, int CarIndex), LapPositionValue>();
        var eventCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packet8Seen = false;
        var telemetryRows = 0L;
        var motionExRows = 0L;

        using var tx = write.BeginTransaction();
        using var telemetryUpdate = PrepareTelemetryUpdate(write, tx);
        using var motionExInsert = PrepareMotionExInsert(write, tx);

        using (var raw = read.CreateCommand())
        {
            raw.CommandText = """
                SELECT received_at, packet_id, payload
                FROM raw_packets
                WHERE packet_format = 2026 AND packet_id IN (3,6,8,13,15)
                ORDER BY id
                """;
            using var reader = raw.ExecuteReader();
            while (reader.Read())
            {
                var receivedAt = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var packetId = reader.GetInt32(1);
                var payload = (byte[])reader[2];
                if (!F12026Parser.TryParseHeader(payload, out var header)) continue;

                switch (packetId)
                {
                    case 3:
                        if (payload.Length >= HeaderSize + 4)
                            eventCodes.Add(Encoding.ASCII.GetString(payload, HeaderSize, 4));
                        break;
                    case 6:
                        telemetryRows += EnrichCarTelemetryPacket(telemetryUpdate, payload, header);
                        break;
                    case 8:
                        packet8Seen = true;
                        break;
                    case 13:
                        if (InsertMotionExPacket(motionExInsert, receivedAt, payload, header)) motionExRows++;
                        break;
                    case 15:
                        CollectLapPositions(lapPositions, receivedAt, payload, header);
                        break;
                }
            }
        }

        ReplaceLapPositions(write, tx, lapPositions.Values);
        tx.Commit();

        ImproveClassification(write, packet8Seen);
        UpdateSessionKindMetadata(write, eventCodes, packet8Seen);
        SetMeta(write, "telemetry_completeness_version", CompletenessVersion.ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "telemetry_completeness_raw_packets", relevantRawCount.ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "extended_telemetry_rows_updated", telemetryRows.ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "motion_ex_rows", motionExRows.ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "lap_position_rows", lapPositions.Count.ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "packet_8_present", packet8Seen ? "1" : "0");

        log?.Invoke($"Extended raw rebuild: telemetry {telemetryRows:N0}, Motion Ex {motionExRows:N0}, lap positions {lapPositions.Count:N0}, packet 8 {(packet8Seen ? "present" : "absent")}.");
    }

    public static string GetOfficialSessionTypeName(int id) => id switch
    {
        0 => "Unknown",
        1 => "Practice 1",
        2 => "Practice 2",
        3 => "Practice 3",
        4 => "Short Practice",
        5 => "Qualifying 1",
        6 => "Qualifying 2",
        7 => "Qualifying 3",
        8 => "Short Qualifying",
        9 => "One Shot Qualifying",
        10 => "Sprint Shootout 1",
        11 => "Sprint Shootout 2",
        12 => "Sprint Shootout 3",
        13 => "Short Sprint Shootout",
        14 => "One Shot Sprint Shootout",
        15 => "Race",
        16 => "Race 2",
        17 => "Race 3",
        18 => "Time Trial",
        _ => $"Session_{id}"
    };

    private static long CountRelevantRawPackets(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_packets WHERE packet_format=2026 AND packet_id IN (3,6,8,13,15)";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        if (TableExists(connection, "car_telemetry"))
        {
            foreach (var (name, type) in TelemetryColumns)
                EnsureColumn(connection, "car_telemetry", name, type);
        }

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS motion_ex_player(
                received_at TEXT NOT NULL,
                session_uid TEXT NOT NULL,
                session_time REAL,
                frame_identifier INTEGER,
                overall_frame_identifier INTEGER NOT NULL,
                player_car_index INTEGER,
                car_idx INTEGER,
                is_player INTEGER NOT NULL DEFAULT 1,
                suspension_position_rl REAL, suspension_position_rr REAL, suspension_position_fl REAL, suspension_position_fr REAL,
                suspension_velocity_rl REAL, suspension_velocity_rr REAL, suspension_velocity_fl REAL, suspension_velocity_fr REAL,
                suspension_acceleration_rl REAL, suspension_acceleration_rr REAL, suspension_acceleration_fl REAL, suspension_acceleration_fr REAL,
                wheel_speed_rl REAL, wheel_speed_rr REAL, wheel_speed_fl REAL, wheel_speed_fr REAL,
                wheel_slip_ratio_rl REAL, wheel_slip_ratio_rr REAL, wheel_slip_ratio_fl REAL, wheel_slip_ratio_fr REAL,
                wheel_slip_angle_rl REAL, wheel_slip_angle_rr REAL, wheel_slip_angle_fl REAL, wheel_slip_angle_fr REAL,
                wheel_lat_force_rl REAL, wheel_lat_force_rr REAL, wheel_lat_force_fl REAL, wheel_lat_force_fr REAL,
                wheel_long_force_rl REAL, wheel_long_force_rr REAL, wheel_long_force_fl REAL, wheel_long_force_fr REAL,
                height_cog REAL,
                local_velocity_x REAL, local_velocity_y REAL, local_velocity_z REAL,
                angular_velocity_x REAL, angular_velocity_y REAL, angular_velocity_z REAL,
                angular_acceleration_x REAL, angular_acceleration_y REAL, angular_acceleration_z REAL,
                front_wheels_angle REAL,
                wheel_vert_force_rl REAL, wheel_vert_force_rr REAL, wheel_vert_force_fl REAL, wheel_vert_force_fr REAL,
                front_aero_height REAL, rear_aero_height REAL,
                front_roll_angle REAL, rear_roll_angle REAL,
                chassis_yaw REAL, chassis_pitch REAL,
                wheel_camber_rl REAL, wheel_camber_rr REAL, wheel_camber_fl REAL, wheel_camber_fr REAL,
                wheel_camber_gain_rl REAL, wheel_camber_gain_rr REAL, wheel_camber_gain_fl REAL, wheel_camber_gain_fr REAL,
                PRIMARY KEY(session_uid, overall_frame_identifier)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS lap_positions(
                session_uid TEXT NOT NULL,
                received_at TEXT,
                overall_frame_identifier INTEGER,
                lap_index INTEGER NOT NULL,
                lap_num INTEGER NOT NULL,
                car_idx INTEGER NOT NULL,
                is_player INTEGER NOT NULL,
                position INTEGER NOT NULL,
                PRIMARY KEY(session_uid, lap_index, car_idx)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS idx_motion_ex_player_frame
                ON motion_ex_player(session_uid, overall_frame_identifier);
            CREATE INDEX IF NOT EXISTS idx_lap_positions_car_lap
                ON lap_positions(session_uid, car_idx, lap_num);
        """);

        if (TableExists(connection, "final_classification"))
            EnsureColumn(connection, "final_classification", "provisional", "INTEGER");
    }

    private static long EnrichCarTelemetryPacket(SqliteCommand command, byte[] payload, PacketHeader header)
    {
        if (payload.Length < HeaderSize + CarTelemetrySize) return 0;
        var count = Math.Min(MaxCars, (payload.Length - HeaderSize) / CarTelemetrySize);
        var updated = 0L;
        for (var car = 0; car < count; car++)
        {
            var offset = HeaderSize + car * CarTelemetrySize;
            var c = payload.AsSpan(offset, CarTelemetrySize);
            command.Parameters["$uid"].Value = header.SessionUid.ToString(CultureInfo.InvariantCulture);
            command.Parameters["$overall"].Value = header.OverallFrameIdentifier;
            command.Parameters["$car"].Value = car;
            command.Parameters["$clutch"].Value = c[14];
            command.Parameters["$revPct"].Value = c[19];
            command.Parameters["$revBits"].Value = U16(c, 20);
            command.Parameters["$brRl"].Value = U16(c, 22);
            command.Parameters["$brRr"].Value = U16(c, 24);
            command.Parameters["$brFl"].Value = U16(c, 26);
            command.Parameters["$brFr"].Value = U16(c, 28);
            command.Parameters["$surfRl"].Value = c[30];
            command.Parameters["$surfRr"].Value = c[31];
            command.Parameters["$surfFl"].Value = c[32];
            command.Parameters["$surfFr"].Value = c[33];
            command.Parameters["$innerRl"].Value = c[34];
            command.Parameters["$innerRr"].Value = c[35];
            command.Parameters["$innerFl"].Value = c[36];
            command.Parameters["$innerFr"].Value = c[37];
            command.Parameters["$engineTemp"].Value = c[38];
            command.Parameters["$pressureRl"].Value = CleanFloat(F32(c, 39));
            command.Parameters["$pressureRr"].Value = CleanFloat(F32(c, 43));
            command.Parameters["$pressureFl"].Value = CleanFloat(F32(c, 47));
            command.Parameters["$pressureFr"].Value = CleanFloat(F32(c, 51));
            command.Parameters["$surfaceRl"].Value = c[55];
            command.Parameters["$surfaceRr"].Value = c[56];
            command.Parameters["$surfaceFl"].Value = c[57];
            command.Parameters["$surfaceFr"].Value = c[58];
            updated += command.ExecuteNonQuery();
        }
        return updated;
    }

    private static SqliteCommand PrepareTelemetryUpdate(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE car_telemetry SET
                clutch=$clutch, rev_lights_percent=$revPct, rev_lights_bit_value=$revBits,
                brake_temp_rl=$brRl, brake_temp_rr=$brRr, brake_temp_fl=$brFl, brake_temp_fr=$brFr,
                tyre_surface_temp_rl=$surfRl, tyre_surface_temp_rr=$surfRr, tyre_surface_temp_fl=$surfFl, tyre_surface_temp_fr=$surfFr,
                tyre_inner_temp_rl=$innerRl, tyre_inner_temp_rr=$innerRr, tyre_inner_temp_fl=$innerFl, tyre_inner_temp_fr=$innerFr,
                engine_temp=$engineTemp,
                tyre_pressure_rl=$pressureRl, tyre_pressure_rr=$pressureRr, tyre_pressure_fl=$pressureFl, tyre_pressure_fr=$pressureFr,
                surface_type_rl=$surfaceRl, surface_type_rr=$surfaceRr, surface_type_fl=$surfaceFl, surface_type_fr=$surfaceFr
            WHERE session_uid=$uid AND overall_frame_identifier=$overall AND car_idx=$car
            """;
        foreach (var name in new[]
        {
            "$clutch", "$revPct", "$revBits", "$brRl", "$brRr", "$brFl", "$brFr",
            "$surfRl", "$surfRr", "$surfFl", "$surfFr", "$innerRl", "$innerRr", "$innerFl", "$innerFr",
            "$engineTemp", "$pressureRl", "$pressureRr", "$pressureFl", "$pressureFr",
            "$surfaceRl", "$surfaceRr", "$surfaceFl", "$surfaceFr", "$uid", "$overall", "$car"
        }) command.Parameters.AddWithValue(name, 0);
        return command;
    }

    private static bool InsertMotionExPacket(SqliteCommand command, string receivedAt, byte[] payload, PacketHeader header)
    {
        if (payload.Length < HeaderSize + MotionExPayloadSize) return false;
        var c = payload.AsSpan(HeaderSize, MotionExPayloadSize);
        command.Parameters["$received"].Value = receivedAt;
        command.Parameters["$uid"].Value = header.SessionUid.ToString(CultureInfo.InvariantCulture);
        command.Parameters["$session"].Value = CleanFloat(header.SessionTime);
        command.Parameters["$frame"].Value = header.FrameIdentifier;
        command.Parameters["$overall"].Value = header.OverallFrameIdentifier;
        command.Parameters["$player"].Value = header.PlayerCarIndex;
        command.Parameters["$car"].Value = header.PlayerCarIndex;

        var p = 0;
        SetWheelArray(command, "sp", c, ref p);
        SetWheelArray(command, "sv", c, ref p);
        SetWheelArray(command, "sa", c, ref p);
        SetWheelArray(command, "ws", c, ref p);
        SetWheelArray(command, "sr", c, ref p);
        SetWheelArray(command, "sla", c, ref p);
        SetWheelArray(command, "lat", c, ref p);
        SetWheelArray(command, "lon", c, ref p);
        SetFloat(command, "$cog", c, ref p);
        SetFloat(command, "$lvx", c, ref p); SetFloat(command, "$lvy", c, ref p); SetFloat(command, "$lvz", c, ref p);
        SetFloat(command, "$avx", c, ref p); SetFloat(command, "$avy", c, ref p); SetFloat(command, "$avz", c, ref p);
        SetFloat(command, "$aax", c, ref p); SetFloat(command, "$aay", c, ref p); SetFloat(command, "$aaz", c, ref p);
        SetFloat(command, "$fwa", c, ref p);
        SetWheelArray(command, "vert", c, ref p);
        SetFloat(command, "$fah", c, ref p); SetFloat(command, "$rah", c, ref p);
        SetFloat(command, "$fra", c, ref p); SetFloat(command, "$rra", c, ref p);
        SetFloat(command, "$cy", c, ref p); SetFloat(command, "$cp", c, ref p);
        SetWheelArray(command, "cam", c, ref p);
        SetWheelArray(command, "gain", c, ref p);
        command.ExecuteNonQuery();
        return true;
    }

    private static SqliteCommand PrepareMotionExInsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO motion_ex_player VALUES (
                $received,$uid,$session,$frame,$overall,$player,$car,1,
                $sp0,$sp1,$sp2,$sp3,$sv0,$sv1,$sv2,$sv3,$sa0,$sa1,$sa2,$sa3,
                $ws0,$ws1,$ws2,$ws3,$sr0,$sr1,$sr2,$sr3,$sla0,$sla1,$sla2,$sla3,
                $lat0,$lat1,$lat2,$lat3,$lon0,$lon1,$lon2,$lon3,$cog,
                $lvx,$lvy,$lvz,$avx,$avy,$avz,$aax,$aay,$aaz,$fwa,
                $vert0,$vert1,$vert2,$vert3,$fah,$rah,$fra,$rra,$cy,$cp,
                $cam0,$cam1,$cam2,$cam3,$gain0,$gain1,$gain2,$gain3)
            """;
        var names = new List<string> { "$received", "$uid", "$session", "$frame", "$overall", "$player", "$car" };
        foreach (var prefix in new[] { "sp", "sv", "sa", "ws", "sr", "sla", "lat", "lon" })
            for (var i = 0; i < 4; i++) names.Add($"${prefix}{i}");
        names.AddRange(new[] { "$cog", "$lvx", "$lvy", "$lvz", "$avx", "$avy", "$avz", "$aax", "$aay", "$aaz", "$fwa" });
        for (var i = 0; i < 4; i++) names.Add($"$vert{i}");
        names.AddRange(new[] { "$fah", "$rah", "$fra", "$rra", "$cy", "$cp" });
        foreach (var prefix in new[] { "cam", "gain" })
            for (var i = 0; i < 4; i++) names.Add($"${prefix}{i}");
        foreach (var name in names) command.Parameters.AddWithValue(name, 0);
        return command;
    }

    private static void CollectLapPositions(
        IDictionary<(string SessionUid, int LapIndex, int CarIndex), LapPositionValue> target,
        string receivedAt,
        byte[] payload,
        PacketHeader header)
    {
        if (payload.Length < HeaderSize + 2) return;
        var numLaps = Math.Min(payload[HeaderSize], (byte)LapPositionsHistoryLaps);
        var lapStart = payload[HeaderSize + 1];
        var positionsOffset = HeaderSize + 2;
        var availableRows = Math.Min(LapPositionsHistoryLaps, Math.Max(0, (payload.Length - positionsOffset) / MaxCars));
        var rows = Math.Min(numLaps, availableRows);
        var uid = header.SessionUid.ToString(CultureInfo.InvariantCulture);
        for (var row = 0; row < rows; row++)
        {
            var lapIndex = lapStart + row;
            for (var car = 0; car < MaxCars; car++)
            {
                var position = payload[positionsOffset + row * MaxCars + car];
                if (position <= 0) continue;
                target[(uid, lapIndex, car)] = new LapPositionValue(uid, receivedAt, header.OverallFrameIdentifier, lapIndex, car, position, header.PlayerCarIndex);
            }
        }
    }

    private static void ReplaceLapPositions(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<LapPositionValue> rows)
    {
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM lap_positions";
        delete.ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO lap_positions(session_uid,received_at,overall_frame_identifier,lap_index,lap_num,car_idx,is_player,position)
            VALUES($uid,$received,$overall,$index,$lap,$car,$me,$position)
            """;
        foreach (var name in new[] { "$uid", "$received", "$overall", "$index", "$lap", "$car", "$me", "$position" })
            insert.Parameters.AddWithValue(name, 0);
        foreach (var row in rows.OrderBy(x => x.SessionUid).ThenBy(x => x.LapIndex).ThenBy(x => x.CarIndex))
        {
            insert.Parameters["$uid"].Value = row.SessionUid;
            insert.Parameters["$received"].Value = row.ReceivedAt;
            insert.Parameters["$overall"].Value = row.OverallFrame;
            insert.Parameters["$index"].Value = row.LapIndex;
            insert.Parameters["$lap"].Value = row.LapIndex + 1;
            insert.Parameters["$car"].Value = row.CarIndex;
            insert.Parameters["$me"].Value = row.CarIndex == row.PlayerCarIndex ? 1 : 0;
            insert.Parameters["$position"].Value = row.Position;
            insert.ExecuteNonQuery();
        }
    }

    private static void ImproveClassification(SqliteConnection connection, bool packet8Seen)
    {
        if (!TableExists(connection, "final_classification")) return;
        EnsureColumn(connection, "final_classification", "provisional", "INTEGER");
        if (!ColumnExists(connection, "final_classification", "classification_is_official")) return;

        Execute(connection, "UPDATE final_classification SET provisional = CASE WHEN classification_is_official=1 THEN 0 ELSE 1 END;");
        if (packet8Seen) return;

        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE final_classification AS f
            SET position = COALESCE(NULLIF(f.position, 0), (
                SELECT lp.position
                FROM lap_positions lp
                WHERE lp.car_idx = f.car_idx AND lp.position > 0
                ORDER BY lp.lap_num DESC, lp.overall_frame_identifier DESC
                LIMIT 1
            ))
            WHERE COALESCE(f.classification_is_official, 0) = 0
              AND (f.position IS NULL OR f.position = 0)
            """;
        var backfilled = update.ExecuteNonQuery();
        if (backfilled > 0 && ColumnExists(connection, "final_classification", "classification_source"))
        {
            using var note = connection.CreateCommand();
            note.CommandText = """
                UPDATE final_classification
                SET classification_source='provisional_lap_data_with_lap_positions_backfill',
                    classification_note='UDP packet 8 is absent. Latest Lap Data remains the primary source; missing positions were backfilled from packet 15 lap-position history. Result remains provisional.'
                WHERE COALESCE(classification_is_official,0)=0
                """;
            note.ExecuteNonQuery();
        }
    }

    private static void UpdateSessionKindMetadata(SqliteConnection connection, IReadOnlySet<string> eventCodes, bool packet8Seen)
    {
        if (!TableExists(connection, "session_metadata")) return;
        var rawType = ReadMetaInt(connection, "raw_session_type");
        if (rawType < 0) rawType = ReadMetaInt(connection, "session_type");
        if (rawType < 0) return;

        var rawName = GetOfficialSessionTypeName(rawType);
        var rawKind = GetRawSessionKind(rawType);
        var raceEvidence = packet8Seen || eventCodes.Contains("RCWN") || (eventCodes.Contains("LGOT") && eventCodes.Contains("CHQF"));
        var inferred = raceEvidence ? "Race" : rawKind;
        var conflict = !string.Equals(rawKind, inferred, StringComparison.OrdinalIgnoreCase);

        SetMeta(connection, "raw_session_type", rawType.ToString(CultureInfo.InvariantCulture));
        SetMeta(connection, "raw_session_name", rawName);
        SetMeta(connection, "inferred_session_kind", inferred);
        SetMeta(connection, "session_type_conflict", conflict ? "1" : "0");
        SetMeta(connection, "session_type_conflict_note", conflict
            ? $"UDP reports {rawName} ({rawType}), while race events indicate a Race. Raw value is preserved."
            : "Raw UDP session type and inferred session kind are consistent.");
    }

    private static string GetRawSessionKind(int rawType) => rawType switch
    {
        >= 1 and <= 4 => "Practice",
        >= 5 and <= 9 => "Qualifying",
        >= 10 and <= 14 => "Sprint Shootout",
        >= 15 and <= 17 => "Race",
        18 => "Time Trial",
        _ => "Unknown"
    };

    private static readonly (string Name, string Type)[] TelemetryColumns =
    {
        ("clutch", "INTEGER"), ("rev_lights_percent", "INTEGER"), ("rev_lights_bit_value", "INTEGER"),
        ("brake_temp_rl", "INTEGER"), ("brake_temp_rr", "INTEGER"), ("brake_temp_fl", "INTEGER"), ("brake_temp_fr", "INTEGER"),
        ("tyre_surface_temp_rl", "INTEGER"), ("tyre_surface_temp_rr", "INTEGER"), ("tyre_surface_temp_fl", "INTEGER"), ("tyre_surface_temp_fr", "INTEGER"),
        ("tyre_inner_temp_rl", "INTEGER"), ("tyre_inner_temp_rr", "INTEGER"), ("tyre_inner_temp_fl", "INTEGER"), ("tyre_inner_temp_fr", "INTEGER"),
        ("engine_temp", "INTEGER"),
        ("tyre_pressure_rl", "REAL"), ("tyre_pressure_rr", "REAL"), ("tyre_pressure_fl", "REAL"), ("tyre_pressure_fr", "REAL"),
        ("surface_type_rl", "INTEGER"), ("surface_type_rr", "INTEGER"), ("surface_type_fl", "INTEGER"), ("surface_type_fr", "INTEGER")
    };

    private static void SetWheelArray(SqliteCommand command, string prefix, ReadOnlySpan<byte> data, ref int offset)
    {
        for (var i = 0; i < 4; i++)
        {
            command.Parameters[$"${prefix}{i}"].Value = CleanFloat(F32(data, offset));
            offset += 4;
        }
    }

    private static void SetFloat(SqliteCommand command, string name, ReadOnlySpan<byte> data, ref int offset)
    {
        command.Parameters[name].Value = CleanFloat(F32(data, offset));
        offset += 4;
    }

    private static object CleanFloat(float value) => float.IsFinite(value) ? value : DBNull.Value;
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    private static float F32(ReadOnlySpan<byte> data, int offset) => BitConverter.ToSingle(data.Slice(offset, 4));

    private static int ReadMetaInt(SqliteConnection connection, string key)
    {
        var value = ReadMeta(connection, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
    }

    private static long ReadMetaLong(SqliteConnection connection, string key)
    {
        var value = ReadMeta(connection, key);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        if (!TableExists(connection, "session_metadata")) return null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM session_metadata WHERE key=$key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetMeta(SqliteConnection connection, string key, string value)
    {
        if (!TableExists(connection, "session_metadata")) return;
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session_metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        if (ColumnExists(connection, table, column)) return;
        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        if (!TableExists(connection, table)) return false;
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
