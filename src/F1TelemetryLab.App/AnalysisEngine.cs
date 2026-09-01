using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace F1TelemetryLab;

public static class AnalysisEngine
{
    private sealed record BestLapRow(int CarIndex, int LapNum, bool IsPlayer, uint LapTimeMs);
    private sealed record TelemetryBin(int Bin, double TimeMs, double Speed, double Throttle, double Brake, double Steer, double Gear);

    public static Task<AnalysisResult> AnalyzeSessionAsync(string sessionFolder, Action<string>? log = null)
    {
        return Task.Run(() => AnalyzeSession(sessionFolder, log));
    }

    public static AnalysisResult AnalyzeSession(string sessionFolder, Action<string>? log = null)
    {
        var dbPath = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("session.sqlite not found", dbPath);

        SQLitePCL.Batteries_V2.Init();
        var stagingDb = Path.Combine(sessionFolder, $"session.analysis.{Guid.NewGuid():N}.sqlite");
        try
        {
            log?.Invoke("Creating an atomic analysis snapshot...");
            CreateWorkingCopy(dbPath, stagingDb);
            var result = AnalyzeWorkingCopy(stagingDb, sessionFolder, log);

            ReplaceDatabaseAtomically(stagingDb, dbPath);
            WriteAnalysisRun(dbPath, result);
            SessionManifestService.Refresh(sessionFolder, analyzedAt: DateTimeOffset.Now);
            return result;
        }
        finally
        {
            TryDelete(stagingDb);
            TryDelete(stagingDb + "-wal");
            TryDelete(stagingDb + "-shm");
        }
    }

    private static AnalysisResult AnalyzeWorkingCopy(
        string dbPath,
        string sessionFolder,
        Action<string>? log)
    {
        using var con = new SqliteConnection($"Data Source={dbPath};Default Timeout=60");
        con.Open();
        CreateAnalysisSchema(con);
        ClearAnalysisTables(con);
        DatabaseSchemaMigrator.Apply(con);

        using var readCon = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Private;Default Timeout=60");
        readCon.Open();
        var rawPacketCount = CountRawPackets(readCon);
        var activeCars = LoadMaximumCarCounts(readCon);
        log?.Invoke($"Streaming {rawPacketCount:N0} raw packets...");

        var laps = new List<LapDataSample>(capacity: Math.Min(500_000, Math.Max(4_096, rawPacketCount * 3)));
        var flashbacks = new List<FlashbackSignal>();
        var eventCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stats = ProcessRawPackets(readCon, con, laps, flashbacks, eventCodes, activeCars, log);
        CreateAnalysisIndexes(con);

        log?.Invoke("Building lap quality...");
        var trackLength = ReadMetadataInt(con, "track_length_m");
        var qualities = LapQualityAnalyzer.Analyze(
            laps,
            trackLength,
            flashbacks,
            out var confirmedRewinds,
            out var suspectedStateResets);
        InsertLapQuality(con, qualities, confirmedRewinds, suspectedStateResets);

        log?.Invoke("Building analysis samples and lap summaries...");
        BuildSqlDerivedTables(con);
        UpdateDataQuality(con, qualities, stats.FinalClassificationRows, eventCodes);

        using (var checkpoint = con.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }

        var cleanCount = qualities.Count(x => x.CleanLap);
        var dirtyCount = qualities.Count(x => !x.CleanLap);
        return new AnalysisResult(
            sessionFolder,
            sessionFolder,
            rawPacketCount,
            stats.TelemetryRows,
            stats.LapRows,
            stats.MotionRows,
            stats.StatusRows,
            stats.DamageRows,
            stats.SetupRows,
            stats.EventsRows,
            stats.ParticipantsRows,
            stats.FinalClassificationRows,
            confirmedRewinds.Count,
            suspectedStateResets.Count,
            cleanCount,
            dirtyCount,
            $"Processed {rawPacketCount:N0} packets. Clean laps: {cleanCount}, dirty laps: {dirtyCount}. All automatic analysis data is stored in session.sqlite.");
    }

    private static void CreateAnalysisSchema(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        DROP TABLE IF EXISTS car_telemetry;
        DROP TABLE IF EXISTS lap_data;
        DROP TABLE IF EXISTS motion_data;
        DROP TABLE IF EXISTS car_status;
        DROP TABLE IF EXISTS car_damage;
        DROP TABLE IF EXISTS car_setups;
        DROP TABLE IF EXISTS events;
        DROP TABLE IF EXISTS participants;
        DROP TABLE IF EXISTS participants_debug;
        DROP TABLE IF EXISTS final_classification_packet;
        DROP TABLE IF EXISTS lap_quality;
        DROP TABLE IF EXISTS rewind_events;
        DROP TABLE IF EXISTS suspected_state_reset_events;

        CREATE TABLE car_telemetry(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER,
            player_car_index INTEGER, car_idx INTEGER, is_player INTEGER, speed INTEGER, throttle REAL, brake REAL, steer REAL,
            gear INTEGER, engine_rpm INTEGER, drs INTEGER,
            PRIMARY KEY(session_uid, overall_frame_identifier, car_idx)
        ) WITHOUT ROWID;
        CREATE TABLE lap_data(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER,
            player_car_index INTEGER, car_idx INTEGER, is_player INTEGER, last_lap_time_ms INTEGER, current_lap_time_ms INTEGER,
            sector1_time_ms INTEGER, sector2_time_ms INTEGER, delta_to_front_ms INTEGER, delta_to_leader_ms INTEGER,
            lap_distance REAL, total_distance REAL, position INTEGER, lap_num INTEGER, pit_status INTEGER, num_pit_stops INTEGER,
            sector INTEGER, lap_invalid INTEGER, penalties INTEGER, warnings INTEGER, driver_status INTEGER, result_status INTEGER
        );
        CREATE TABLE motion_data(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER, player_car_index INTEGER, car_idx INTEGER, is_player INTEGER,
            world_position_x REAL, world_position_y REAL, world_position_z REAL, world_velocity_x REAL, world_velocity_y REAL, world_velocity_z REAL,
            g_force_lateral REAL, g_force_longitudinal REAL, g_force_vertical REAL, yaw REAL, pitch REAL, roll REAL
        );
        CREATE TABLE car_status(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER, player_car_index INTEGER, car_idx INTEGER, is_player INTEGER,
            front_brake_bias INTEGER, fuel_in_tank REAL, fuel_remaining_laps REAL, actual_tyre_compound INTEGER, visual_tyre_compound INTEGER,
            tyres_age_laps INTEGER, engine_power_ice REAL, engine_power_mguk REAL, ers_store_energy REAL, ers_deploy_mode INTEGER,
            ers_harvested_this_lap_mguk REAL, ers_harvested_this_lap_mguh REAL, ers_harvest_limit_per_lap REAL, ers_deployed_this_lap REAL
        );
        CREATE TABLE car_damage(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER, player_car_index INTEGER, car_idx INTEGER, is_player INTEGER,
            tyre_wear_rl REAL, tyre_wear_rr REAL, tyre_wear_fl REAL, tyre_wear_fr REAL, tyre_wear_avg REAL,
            tyre_damage_rl INTEGER, tyre_damage_rr INTEGER, tyre_damage_fl INTEGER, tyre_damage_fr INTEGER,
            front_left_wing_damage INTEGER, front_right_wing_damage INTEGER, rear_wing_damage INTEGER, floor_damage INTEGER, diffuser_damage INTEGER, sidepod_damage INTEGER
        );
        CREATE TABLE car_setups(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER,
            player_car_index INTEGER, car_idx INTEGER, is_player INTEGER,
            front_wing INTEGER, rear_wing INTEGER, on_throttle INTEGER, off_throttle INTEGER,
            front_camber REAL, rear_camber REAL, front_toe REAL, rear_toe REAL,
            front_suspension INTEGER, rear_suspension INTEGER, front_anti_roll_bar INTEGER, rear_anti_roll_bar INTEGER,
            front_ride_height INTEGER, rear_ride_height INTEGER, brake_pressure INTEGER, brake_bias INTEGER, engine_braking INTEGER,
            rear_left_tyre_pressure REAL, rear_right_tyre_pressure REAL, front_left_tyre_pressure REAL, front_right_tyre_pressure REAL,
            ballast INTEGER, fuel_load REAL, next_front_wing_value REAL
        );
        CREATE TABLE events(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER, player_car_index INTEGER,
            event_code TEXT, event_name TEXT, vehicle_idx INTEGER, other_vehicle_idx INTEGER, details_json TEXT
        );
        CREATE TABLE participants(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER, car_idx INTEGER,
            ai_controlled INTEGER, driver_id INTEGER, team_id INTEGER, race_number INTEGER, name TEXT, your_telemetry INTEGER, show_online_names INTEGER
        );
        CREATE TABLE participants_debug(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER,
            packet_size_bytes INTEGER, num_active_cars INTEGER, rows_if_60_bytes INTEGER, rows_if_58_bytes INTEGER, first_names TEXT
        );
        CREATE TABLE final_classification_packet(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER,
            car_idx INTEGER, is_player INTEGER, position INTEGER, num_laps INTEGER, grid_position INTEGER, points INTEGER,
            num_pit_stops INTEGER, result_status INTEGER, best_lap_time_ms INTEGER, total_race_time_seconds REAL,
            penalties_time_seconds INTEGER, num_penalties INTEGER, num_tyre_stints INTEGER, result_reason INTEGER
        );
        CREATE TABLE IF NOT EXISTS driver_aliases(
            car_idx INTEGER PRIMARY KEY,
            original_name TEXT,
            display_name TEXT,
            short_name TEXT,
            updated_at TEXT
        );
        CREATE TABLE lap_quality(
            session_uid TEXT, car_idx INTEGER, lap_num INTEGER, is_player INTEGER, lap_state TEXT, clean_lap INTEGER,
            rewind_count INTEGER, invalid_count INTEGER, sample_count INTEGER, min_distance REAL, max_distance REAL,
            lap_time_ms INTEGER, sector1_ms INTEGER, sector2_ms INTEGER, sector3_ms INTEGER,
            active_from_overall_frame INTEGER, completion_evidence TEXT,
            PRIMARY KEY(session_uid, car_idx, lap_num)
        );
        CREATE TABLE rewind_events(
            session_uid TEXT, car_idx INTEGER, lap_num INTEGER, received_at TEXT, session_time REAL,
            overall_frame_identifier INTEGER, lap_distance REAL, current_lap_time_ms INTEGER, reason TEXT
        );
        CREATE TABLE suspected_state_reset_events(
            session_uid TEXT, car_idx INTEGER, lap_num INTEGER, received_at TEXT, session_time REAL,
            overall_frame_identifier INTEGER, lap_distance REAL, current_lap_time_ms INTEGER, reason TEXT
        );
        """;
        cmd.ExecuteNonQuery();
    }

    private static void ClearAnalysisTables(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        DELETE FROM car_telemetry;
        DELETE FROM lap_data;
        DELETE FROM motion_data;
        DELETE FROM car_status;
        DELETE FROM car_damage;
        DELETE FROM car_setups;
        DELETE FROM events;
        DELETE FROM participants;
        DELETE FROM participants_debug;
        DELETE FROM final_classification_packet;
        DELETE FROM lap_quality;
        DELETE FROM rewind_events;
        DELETE FROM suspected_state_reset_events;
        DROP TABLE IF EXISTS analysis_samples;
        DROP TABLE IF EXISTS analysis_trace_10m;
        DROP TABLE IF EXISTS lap_summary;
        DROP TABLE IF EXISTS lap_state_summary;
        DROP TABLE IF EXISTS final_classification;
        """;
        cmd.ExecuteNonQuery();
    }

    private static void CreateAnalysisIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_lap_data_session_car_lap_frame
                ON lap_data(session_uid, car_idx, lap_num, overall_frame_identifier);
            CREATE INDEX IF NOT EXISTS idx_motion_data_session_car_frame
                ON motion_data(session_uid, car_idx, overall_frame_identifier);
            CREATE INDEX IF NOT EXISTS idx_car_status_session_car_frame
                ON car_status(session_uid, car_idx, overall_frame_identifier);
            CREATE INDEX IF NOT EXISTS idx_car_damage_session_car_frame
                ON car_damage(session_uid, car_idx, overall_frame_identifier);
            CREATE INDEX IF NOT EXISTS idx_car_setups_session_car_frame
                ON car_setups(session_uid, car_idx, overall_frame_identifier);
            CREATE INDEX IF NOT EXISTS idx_events_session_code_frame
                ON events(session_uid, event_code, overall_frame_identifier);
            CREATE INDEX IF NOT EXISTS idx_suspected_state_resets_session_frame
                ON suspected_state_reset_events(session_uid, overall_frame_identifier, car_idx);
            CREATE INDEX IF NOT EXISTS idx_participants_session_car_frame
                ON participants(session_uid, car_idx, overall_frame_identifier);
            """;
        command.ExecuteNonQuery();
    }

    private static int CountRawPackets(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM raw_packets
            WHERE packet_format = 2026 AND packet_id IN (0,2,3,4,5,6,7,8,10)
            """;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static Dictionary<ulong, int> LoadMaximumCarCounts(SqliteConnection con)
    {
        var result = new Dictionary<ulong, int>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT payload
            FROM raw_packets
            WHERE packet_format = 2026 AND packet_id = 4
            ORDER BY id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var payload = (byte[])reader[0];
            if (!F12026Parser.TryParseHeader(payload, out var header)) continue;
            var debug = F12026Parser.ParseParticipantsDebug(payload, DateTimeOffset.UnixEpoch);
            if (debug is not { NumActiveCars: > 0 }) continue;
            var observedExtent = debug.NumActiveCars;
            if (header.PlayerCarIndex < F12026Parser.MaxCars2026)
                observedExtent = Math.Max(observedExtent, header.PlayerCarIndex + 1);
            if (header.SecondaryPlayerCarIndex < F12026Parser.MaxCars2026)
                observedExtent = Math.Max(observedExtent, header.SecondaryPlayerCarIndex + 1);
            observedExtent = Math.Clamp(observedExtent, 1, F12026Parser.MaxCars2026);
            result[header.SessionUid] = Math.Max(result.GetValueOrDefault(header.SessionUid), observedExtent);
        }
        return result;
    }

    private sealed record ParseStats(int TelemetryRows, int LapRows, int MotionRows, int StatusRows, int DamageRows, int SetupRows, int EventsRows, int ParticipantsRows, int FinalClassificationRows);

    private static ParseStats ProcessRawPackets(
        SqliteConnection readCon,
        SqliteConnection writeCon,
        List<LapDataSample> laps,
        List<FlashbackSignal> flashbacks,
        HashSet<string> eventCodes,
        IReadOnlyDictionary<ulong, int> activeCarsBySession,
        Action<string>? log)
    {
        var telemetryRows = 0;
        var lapRows = 0;
        var motionRows = 0;
        var statusRows = 0;
        var damageRows = 0;
        var setupRows = 0;
        var eventRows = 0;
        var participantRows = 0;
        var finalRows = 0;

        using var tx = writeCon.BeginTransaction();
        using var telemetryCmd = PrepareTelemetryInsert(writeCon, tx);
        using var lapCmd = PrepareLapInsert(writeCon, tx);
        using var motionCmd = PrepareMotionInsert(writeCon, tx);
        using var statusCmd = PrepareStatusInsert(writeCon, tx);
        using var damageCmd = PrepareDamageInsert(writeCon, tx);
        using var setupCmd = PrepareSetupInsert(writeCon, tx);
        using var eventCmd = PrepareEventInsert(writeCon, tx);
        using var partCmd = PrepareParticipantInsert(writeCon, tx);
        using var partDebugCmd = PrepareParticipantDebugInsert(writeCon, tx);
        using var finalCmd = PrepareFinalClassificationInsert(writeCon, tx);
        var previousSetups = new Dictionary<(ulong SessionUid, int CarIndex), CarSetupSample>();

        using var raw = readCon.CreateCommand();
        raw.CommandText = """
            SELECT received_at, packet_id, payload
            FROM raw_packets
            WHERE packet_format = 2026 AND packet_id IN (0,2,3,4,5,6,7,8,10)
            ORDER BY id
            """;
        using var reader = raw.ExecuteReader();
        var packetNumber = 0;
        while (reader.Read())
        {
            packetNumber++;
            var receivedAt = ParseDate(reader.GetString(0));
            var packetId = reader.GetInt32(1);
            var payload = (byte[])reader[2];
            int? activeCars = null;
            if (F12026Parser.TryParseHeader(payload, out var header) && activeCarsBySession.TryGetValue(header.SessionUid, out var count))
                activeCars = count;

            switch (packetId)
            {
                case 6:
                    foreach (var s in F12026Parser.ParseCarTelemetryPacket(payload, receivedAt, activeCars))
                    {
                        InsertTelemetry(telemetryCmd, s);
                        telemetryRows++;
                    }
                    break;
                case 2:
                    foreach (var s in F12026Parser.ParseLapDataPacket(payload, receivedAt, activeCars))
                    {
                        InsertLap(lapCmd, s);
                        laps.Add(s);
                        lapRows++;
                    }
                    break;
                case 0:
                    foreach (var s in F12026Parser.ParseMotionPacket(payload, receivedAt, activeCars))
                    {
                        InsertMotion(motionCmd, s);
                        motionRows++;
                    }
                    break;
                case 7:
                    foreach (var s in F12026Parser.ParseCarStatusPacket(payload, receivedAt, activeCars))
                    {
                        InsertStatus(statusCmd, s);
                        statusRows++;
                    }
                    break;
                case 10:
                    foreach (var s in F12026Parser.ParseCarDamagePacket(payload, receivedAt, activeCars))
                    {
                        InsertDamage(damageCmd, s);
                        damageRows++;
                    }
                    break;
                case 5:
                    foreach (var s in F12026Parser.ParseCarSetupPacket(payload, receivedAt, activeCars))
                    {
                        var key = (s.SessionUid, s.CarIndex);
                        if (previousSetups.TryGetValue(key, out var previous) && SameSetup(previous, s)) continue;
                        InsertSetup(setupCmd, s);
                        previousSetups[key] = s;
                        setupRows++;
                    }
                    break;
                case 3:
                    var ev = F12026Parser.ParseEventPacket(payload, receivedAt);
                    if (ev is not null)
                    {
                        InsertEvent(eventCmd, ev);
                        eventCodes.Add(ev.EventCode);
                        if (TryCreateFlashbackSignal(ev, out var signal)) flashbacks.Add(signal);
                        eventRows++;
                    }
                    break;
                case 4:
                    var dbg = F12026Parser.ParseParticipantsDebug(payload, receivedAt);
                    if (dbg is not null) InsertParticipantDebug(partDebugCmd, dbg);
                    foreach (var p in F12026Parser.ParseParticipantsPacket(payload, receivedAt))
                    {
                        InsertParticipant(partCmd, p);
                        participantRows++;
                    }
                    break;
                case 8:
                    foreach (var s in F12026Parser.ParseFinalClassificationPacket(payload, receivedAt))
                    {
                        InsertFinalClassification(finalCmd, s);
                        finalRows++;
                    }
                    break;
            }

            if (packetNumber % 10_000 == 0) log?.Invoke($"Parsed {packetNumber:N0} raw packets...");
        }

        tx.Commit();
        log?.Invoke($"Parsed rows: telemetry {telemetryRows:N0}, lap {lapRows:N0}, motion {motionRows:N0}, status {statusRows:N0}, damage {damageRows:N0}, setup changes {setupRows:N0}, events {eventRows:N0}, participants {participantRows:N0}, final {finalRows:N0}");
        return new ParseStats(telemetryRows, lapRows, motionRows, statusRows, damageRows, setupRows, eventRows, participantRows, finalRows);
    }

    private static bool TryCreateFlashbackSignal(EventSample sample, out FlashbackSignal signal)
    {
        signal = default!;
        if (!sample.EventCode.Equals("FLBK", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var json = JsonDocument.Parse(sample.DetailsJson);
            if (!json.RootElement.TryGetProperty("flashback_frame_identifier", out var frameNode)) return false;
            if (!json.RootElement.TryGetProperty("flashback_session_time", out var timeNode)) return false;
            signal = new FlashbackSignal(
                sample.SessionUid,
                sample.ReceivedAt,
                sample.SessionTime,
                sample.OverallFrameIdentifier,
                frameNode.GetUInt32(),
                timeNode.GetSingle());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void InsertLapQuality(
        SqliteConnection con,
        IReadOnlyList<LapQualityResult> qualities,
        IReadOnlyList<RewindEventResult> confirmedRewinds,
        IReadOnlyList<SuspectedStateResetResult> suspectedStateResets)
    {
        using var tx = con.BeginTransaction();
        using var q = con.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT INTO lap_quality VALUES ($uid,$car,$lap,$me,$state,$clean,$rew,$invalid,$samples,$min,$max,$time,$s1,$s2,$s3,$active,$evidence)";
        foreach (var name in new[] { "$uid", "$car", "$lap", "$me", "$state", "$clean", "$rew", "$invalid", "$samples", "$min", "$max", "$time", "$s1", "$s2", "$s3", "$active", "$evidence" }) q.Parameters.AddWithValue(name, 0);
        foreach (var row in qualities)
        {
            q.Parameters["$uid"].Value = row.SessionUid.ToString();
            q.Parameters["$car"].Value = row.CarIndex;
            q.Parameters["$lap"].Value = row.LapNum;
            q.Parameters["$me"].Value = row.IsPlayer ? 1 : 0;
            q.Parameters["$state"].Value = row.State.ToString();
            q.Parameters["$clean"].Value = row.CleanLap ? 1 : 0;
            q.Parameters["$rew"].Value = row.RewindCount;
            q.Parameters["$invalid"].Value = row.InvalidCount;
            q.Parameters["$samples"].Value = row.SampleCount;
            q.Parameters["$min"].Value = row.MinDistance;
            q.Parameters["$max"].Value = row.MaxDistance;
            q.Parameters["$time"].Value = row.LapTimeMs;
            q.Parameters["$s1"].Value = row.Sector1TimeMs;
            q.Parameters["$s2"].Value = row.Sector2TimeMs;
            q.Parameters["$s3"].Value = row.Sector3TimeMs;
            q.Parameters["$active"].Value = row.ActiveFromOverallFrame;
            q.Parameters["$evidence"].Value = row.CompletionEvidence;
            q.ExecuteNonQuery();
        }

        using var r = con.CreateCommand();
        r.Transaction = tx;
        r.CommandText = "INSERT INTO rewind_events VALUES ($uid,$car,$lap,$received,$session,$overall,$distance,$lapTime,$reason)";
        foreach (var name in new[] { "$uid", "$car", "$lap", "$received", "$session", "$overall", "$distance", "$lapTime", "$reason" }) r.Parameters.AddWithValue(name, 0);
        foreach (var row in confirmedRewinds)
        {
            r.Parameters["$uid"].Value = row.SessionUid.ToString();
            r.Parameters["$car"].Value = row.CarIndex;
            r.Parameters["$lap"].Value = row.LapNum;
            r.Parameters["$received"].Value = row.ReceivedAt.ToString("O");
            r.Parameters["$session"].Value = row.SessionTime;
            r.Parameters["$overall"].Value = row.OverallFrameIdentifier;
            r.Parameters["$distance"].Value = row.LapDistance;
            r.Parameters["$lapTime"].Value = row.CurrentLapTimeMs;
            r.Parameters["$reason"].Value = row.Reason;
            r.ExecuteNonQuery();
        }

        using var s = con.CreateCommand();
        s.Transaction = tx;
        s.CommandText = "INSERT INTO suspected_state_reset_events VALUES ($uid,$car,$lap,$received,$session,$overall,$distance,$lapTime,$reason)";
        foreach (var name in new[] { "$uid", "$car", "$lap", "$received", "$session", "$overall", "$distance", "$lapTime", "$reason" }) s.Parameters.AddWithValue(name, 0);
        foreach (var row in suspectedStateResets)
        {
            s.Parameters["$uid"].Value = row.SessionUid.ToString();
            s.Parameters["$car"].Value = row.CarIndex;
            s.Parameters["$lap"].Value = row.LapNum;
            s.Parameters["$received"].Value = row.ReceivedAt.ToString("O");
            s.Parameters["$session"].Value = row.SessionTime;
            s.Parameters["$overall"].Value = row.OverallFrameIdentifier;
            s.Parameters["$distance"].Value = row.LapDistance;
            s.Parameters["$lapTime"].Value = row.CurrentLapTimeMs;
            s.Parameters["$reason"].Value = row.Reason;
            s.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void BuildSqlDerivedTables(SqliteConnection con) => AnalysisDerivedTableBuilder.Build(con);

    private static void ExportBestLaps(SqliteConnection con, string path)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT s.*
        FROM lap_summary s
        JOIN (
            SELECT summary.session_uid, summary.car_idx, MIN(summary.lap_time_ms) AS best_time
            FROM lap_summary summary
            JOIN lap_state_summary state
              ON state.session_uid = summary.session_uid
             AND state.car_idx = summary.car_idx
             AND state.lap_num = summary.lap_num
            WHERE summary.clean_lap = 1 AND state.pit_this_lap = 0 AND summary.lap_time_ms > 0
            GROUP BY summary.session_uid, summary.car_idx
        ) b ON b.session_uid = s.session_uid AND b.car_idx = s.car_idx AND b.best_time = s.lap_time_ms
        JOIN lap_state_summary state
          ON state.session_uid = s.session_uid AND state.car_idx = s.car_idx AND state.lap_num = s.lap_num
        WHERE s.clean_lap = 1 AND state.pit_this_lap = 0
        ORDER BY s.lap_time_ms ASC, s.car_idx ASC
        """;
        ExportReaderToCsv(cmd, path);
    }

    private static void ExportPlayerVsFastest(SqliteConnection con, string path)
    {
        var player = GetBestLap(con, "s.is_player = 1");
        var reference = GetBestLap(con, "s.is_player = 0");
        if (player is null || reference is null)
        {
            File.WriteAllText(path, "message\nNo clean player/reference lap found\n", Encoding.UTF8);
            return;
        }

        var playerBins = LoadBins(con, player.CarIndex, player.LapNum);
        var referenceBins = LoadBins(con, reference.CarIndex, reference.LapNum);
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("distance_bin_m,player_car,player_lap,ref_car,ref_lap,player_time_ms,ref_time_ms,delta_ms,player_speed,ref_speed,speed_delta,player_throttle,ref_throttle,player_brake,ref_brake,player_steer,ref_steer");
        foreach (var referenceBin in referenceBins)
        {
            var playerTime = InterpolateBin(playerBins, referenceBin.Bin, x => x.TimeMs);
            var playerSpeed = InterpolateBin(playerBins, referenceBin.Bin, x => x.Speed);
            var playerThrottle = InterpolateBin(playerBins, referenceBin.Bin, x => x.Throttle);
            var playerBrake = InterpolateBin(playerBins, referenceBin.Bin, x => x.Brake);
            var playerSteer = InterpolateBin(playerBins, referenceBin.Bin, x => x.Steer);
            writer.WriteLine(string.Join(',', new[]
            {
                referenceBin.Bin.ToString(CultureInfo.InvariantCulture),
                player.CarIndex.ToString(CultureInfo.InvariantCulture), player.LapNum.ToString(CultureInfo.InvariantCulture),
                reference.CarIndex.ToString(CultureInfo.InvariantCulture), reference.LapNum.ToString(CultureInfo.InvariantCulture),
                Num(playerTime), Num(referenceBin.TimeMs), Num(playerTime - referenceBin.TimeMs),
                Num(playerSpeed), Num(referenceBin.Speed), Num(playerSpeed - referenceBin.Speed),
                Num(playerThrottle), Num(referenceBin.Throttle), Num(playerBrake), Num(referenceBin.Brake), Num(playerSteer), Num(referenceBin.Steer)
            }));
        }
    }

    private static BestLapRow? GetBestLap(SqliteConnection con, string where)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
        SELECT s.car_idx, s.lap_num, s.is_player, s.lap_time_ms
        FROM lap_summary s
        JOIN lap_state_summary state
          ON state.session_uid = s.session_uid AND state.car_idx = s.car_idx AND state.lap_num = s.lap_num
        WHERE s.clean_lap = 1 AND state.pit_this_lap = 0 AND s.lap_time_ms > 0 AND {where}
        ORDER BY s.lap_time_ms ASC
        LIMIT 1
        """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new BestLapRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) == 1, Convert.ToUInt32(reader.GetValue(3), CultureInfo.InvariantCulture));
    }

    private static List<TelemetryBin> LoadBins(SqliteConnection con, int carIdx, int lapNum)
    {
        var result = new List<TelemetryBin>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT distance_bin_m, time_ms, speed, throttle, brake, steer, gear
        FROM analysis_trace_10m
        WHERE car_idx = $car AND lap_num = $lap AND clean_lap = 1
        ORDER BY distance_bin_m
        """;
        cmd.Parameters.AddWithValue("$car", carIdx);
        cmd.Parameters.AddWithValue("$lap", lapNum);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TelemetryBin(reader.GetInt32(0), D(reader, 1), D(reader, 2), D(reader, 3), D(reader, 4), D(reader, 5), D(reader, 6)));
        }
        return result;
    }

    private static double? InterpolateBin(IReadOnlyList<TelemetryBin> bins, int distanceM, Func<TelemetryBin, double> valueSelector) =>
        DistanceSeriesInterpolator.Linear(bins, distanceM, x => x.Bin, valueSelector);

    private static double D(SqliteDataReader reader, int idx) => reader.IsDBNull(idx) ? double.NaN : Convert.ToDouble(reader.GetValue(idx), CultureInfo.InvariantCulture);
    private static string Num(double? value) => value is null || !double.IsFinite(value.Value) ? "" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void ExportCsv(SqliteConnection con, string path, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        ExportReaderToCsv(cmd, path);
    }

    private static void ExportReaderToCsv(SqliteCommand cmd, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var reader = cmd.ExecuteReader();
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0) writer.Write(',');
            writer.Write(Escape(reader.GetName(i)));
        }
        writer.WriteLine();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) writer.Write(',');
                writer.Write(Escape(reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? ""));
            }
            writer.WriteLine();
        }
    }

    private static void CreateWorkingCopy(string sourcePath, string destinationPath)
    {
        TryDelete(destinationPath);
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Cache=Private;Default Timeout=60");
        using var destination = new SqliteConnection($"Data Source={destinationPath};Mode=ReadWriteCreate;Cache=Private;Default Timeout=60");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void ReplaceDatabaseAtomically(string stagingPath, string destinationPath)
    {
        SqliteConnection.ClearAllPools();
        TryDelete(destinationPath + "-wal");
        TryDelete(destinationPath + "-shm");
        var backupPath = destinationPath + ".pre-analysis.bak";
        TryDelete(backupPath);
        File.Replace(stagingPath, destinationPath, backupPath, ignoreMetadataErrors: true);
        TryDelete(backupPath);
    }

    private static void UpdateDataQuality(
        SqliteConnection connection,
        IReadOnlyList<LapQualityResult> qualities,
        int finalClassificationRows,
        IReadOnlySet<string> eventCodes)
    {
        var captureRating = "Not recorded";
        using (var capture = connection.CreateCommand())
        {
            capture.CommandText = "SELECT rating FROM recording_quality WHERE id=1";
            captureRating = Convert.ToString(capture.ExecuteScalar(), CultureInfo.InvariantCulture) ?? captureRating;
        }

        var playerLaps = qualities.Where(x => x.IsPlayer).ToList();
        var completePlayerLaps = playerLaps.Count(x => x.LapTimeMs > 0);
        var cleanPlayerLaps = playerLaps.Count(x => x.CleanLap);
        var terminalEvent = eventCodes.Overlaps(new[] { "SEND", "CHQF", "RCWN" });
        var completenessRating = finalClassificationRows > 0
            ? "Complete"
            : terminalEvent
                ? "Provisional"
                : "Partial";
        var completenessSummary = finalClassificationRows > 0
            ? $"Official UDP classification contains {finalClassificationRows:N0} rows."
            : terminalEvent
                ? "A terminal event was recorded, but packet 8 is absent; classification is derived from the latest lap data."
                : "No official classification or terminal event was recorded.";

        var confidenceRating = completePlayerLaps == 0
            ? "Low"
            : playerLaps.Any(x => x.State is LapState.PartialStart or LapState.PartialEnd)
                ? "Medium"
                : "High";
        var confidenceSummary = $"Player laps with confirmed time: {completePlayerLaps}/{playerLaps.Count}; clean: {cleanPlayerLaps}. " +
                                "Only an FLBK event confirms a rewind; backwards counters without FLBK are reported separately as suspected state resets.";

        UpsertQualityDimension(connection, "capture", captureRating, "Transport and write-path health from the recorder.");
        UpsertQualityDimension(connection, "session_completeness", completenessRating, completenessSummary);
        UpsertQualityDimension(connection, "analysis_confidence", confidenceRating, confidenceSummary);
    }

    private static void UpsertQualityDimension(SqliteConnection connection, string dimension, string rating, string summary)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO data_quality(dimension, rating, summary, updated_at)
            VALUES ($dimension, $rating, $summary, $updated)
            ON CONFLICT(dimension) DO UPDATE SET
                rating=excluded.rating,
                summary=excluded.summary,
                updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$rating", rating);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup failure must not hide the original analysis result or exception.
        }
    }

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }

    private static void WriteAnalysisRun(string databasePath, AnalysisResult result)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite;Cache=Private;Default Timeout=30");
        connection.Open();
        DatabaseSchemaMigrator.Apply(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO analysis_runs(
                analyzed_at,app_version,schema_version,raw_packets_processed,clean_laps,dirty_laps,status,summary)
            VALUES($at,$version,$schema,$raw,$clean,$dirty,$status,$summary)
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$version", AppInfo.Version);
        command.Parameters.AddWithValue("$schema", AppInfo.DatabaseSchemaVersion);
        command.Parameters.AddWithValue("$raw", result.RawPacketsProcessed);
        command.Parameters.AddWithValue("$clean", result.CleanLapCount);
        command.Parameters.AddWithValue("$dirty", result.DirtyLapCount);
        command.Parameters.AddWithValue("$status", "completed");
        command.Parameters.AddWithValue("$summary", result.Summary);
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTimeOffset.Now;

    private static int ReadMetadataInt(SqliteConnection con, string key)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT value FROM session_metadata WHERE key = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$key", key);
        var value = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static SqliteCommand PrepareTelemetryInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO car_telemetry VALUES ($received,$uid,$session,$frame,$overall,$player,$car,$me,$speed,$throttle,$brake,$steer,$gear,$rpm,$drs)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$player", "$car", "$me", "$speed", "$throttle", "$brake", "$steer", "$gear", "$rpm", "$drs");
        return cmd;
    }

    private static void InsertTelemetry(SqliteCommand c, CarTelemetrySample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$speed", s.Speed); Set(c, "$throttle", s.Throttle); Set(c, "$brake", s.Brake); Set(c, "$steer", s.Steer); Set(c, "$gear", s.Gear); Set(c, "$rpm", s.EngineRpm); Set(c, "$drs", s.Drs); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareLapInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO lap_data VALUES ($received,$uid,$session,$frame,$overall,$player,$car,$me,$last,$current,$s1,$s2,$front,$leader,$lapdist,$totaldist,$pos,$lap,$pit,$pits,$sector,$invalid,$pens,$warn,$driver,$result)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$player", "$car", "$me", "$last", "$current", "$s1", "$s2", "$front", "$leader", "$lapdist", "$totaldist", "$pos", "$lap", "$pit", "$pits", "$sector", "$invalid", "$pens", "$warn", "$driver", "$result");
        return cmd;
    }
    private static void InsertLap(SqliteCommand c, LapDataSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$last", s.LastLapTimeMs); Set(c, "$current", s.CurrentLapTimeMs); Set(c, "$s1", s.Sector1TimeMs); Set(c, "$s2", s.Sector2TimeMs); Set(c, "$front", s.DeltaToCarInFrontMs); Set(c, "$leader", s.DeltaToRaceLeaderMs); Set(c, "$lapdist", s.LapDistance); Set(c, "$totaldist", s.TotalDistance); Set(c, "$pos", s.Position); Set(c, "$lap", s.LapNum); Set(c, "$pit", s.PitStatus); Set(c, "$pits", s.NumPitStops); Set(c, "$sector", s.Sector); Set(c, "$invalid", s.LapInvalid ? 1 : 0); Set(c, "$pens", s.Penalties); Set(c, "$warn", s.Warnings); Set(c, "$driver", s.DriverStatus); Set(c, "$result", s.ResultStatus); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareMotionInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO motion_data VALUES ($received,$uid,$session,$frame,$overall,$player,$car,$me,$x,$y,$z,$vx,$vy,$vz,$glat,$glong,$gvert,$yaw,$pitch,$roll)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$player", "$car", "$me", "$x", "$y", "$z", "$vx", "$vy", "$vz", "$glat", "$glong", "$gvert", "$yaw", "$pitch", "$roll");
        return cmd;
    }
    private static void InsertMotion(SqliteCommand c, MotionSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$x", s.WorldPositionX); Set(c, "$y", s.WorldPositionY); Set(c, "$z", s.WorldPositionZ); Set(c, "$vx", s.WorldVelocityX); Set(c, "$vy", s.WorldVelocityY); Set(c, "$vz", s.WorldVelocityZ); Set(c, "$glat", s.GForceLateral); Set(c, "$glong", s.GForceLongitudinal); Set(c, "$gvert", s.GForceVertical); Set(c, "$yaw", s.Yaw); Set(c, "$pitch", s.Pitch); Set(c, "$roll", s.Roll); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareStatusInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO car_status VALUES ($received,$uid,$session,$frame,$overall,$player,$car,$me,$bias,$fuel,$fuellaps,$actual,$visual,$age,$ice,$mguk,$ers,$mode,$hmgu,$hmgh,$limit,$deployed)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$player", "$car", "$me", "$bias", "$fuel", "$fuellaps", "$actual", "$visual", "$age", "$ice", "$mguk", "$ers", "$mode", "$hmgu", "$hmgh", "$limit", "$deployed");
        return cmd;
    }
    private static void InsertStatus(SqliteCommand c, CarStatusSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$bias", s.FrontBrakeBias); Set(c, "$fuel", s.FuelInTank); Set(c, "$fuellaps", s.FuelRemainingLaps); Set(c, "$actual", s.ActualTyreCompound); Set(c, "$visual", s.VisualTyreCompound); Set(c, "$age", s.TyresAgeLaps); Set(c, "$ice", s.EnginePowerIce); Set(c, "$mguk", s.EnginePowerMguk); Set(c, "$ers", s.ErsStoreEnergy); Set(c, "$mode", s.ErsDeployMode); Set(c, "$hmgu", s.ErsHarvestedThisLapMguk); Set(c, "$hmgh", s.ErsHarvestedThisLapMguh); Set(c, "$limit", s.ErsHarvestLimitPerLap); Set(c, "$deployed", s.ErsDeployedThisLap); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareDamageInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO car_damage VALUES ($received,$uid,$session,$frame,$overall,$player,$car,$me,$wrl,$wrr,$wfl,$wfr,$wavg,$drl,$drr,$dfl,$dfr,$flwing,$frwing,$rear,$floor,$diff,$side)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$player", "$car", "$me", "$wrl", "$wrr", "$wfl", "$wfr", "$wavg", "$drl", "$drr", "$dfl", "$dfr", "$flwing", "$frwing", "$rear", "$floor", "$diff", "$side");
        return cmd;
    }
    private static void InsertDamage(SqliteCommand c, CarDamageSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$wrl", s.TyreWearRl); Set(c, "$wrr", s.TyreWearRr); Set(c, "$wfl", s.TyreWearFl); Set(c, "$wfr", s.TyreWearFr); Set(c, "$wavg", s.TyreWearAvg); Set(c, "$drl", s.TyreDamageRl); Set(c, "$drr", s.TyreDamageRr); Set(c, "$dfl", s.TyreDamageFl); Set(c, "$dfr", s.TyreDamageFr); Set(c, "$flwing", s.FrontLeftWingDamage); Set(c, "$frwing", s.FrontRightWingDamage); Set(c, "$rear", s.RearWingDamage); Set(c, "$floor", s.FloorDamage); Set(c, "$diff", s.DiffuserDamage); Set(c, "$side", s.SidepodDamage); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareSetupInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO car_setups VALUES ($received,$uid,$session,$frame,$overall,$player,$car,$me,$fw,$rw,$on,$off,$fc,$rc,$ft,$rt,$fs,$rs,$farb,$rarb,$frh,$rrh,$bp,$bb,$eb,$rlp,$rrp,$flp,$frp,$ballast,$fuel,$nextWing)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$player", "$car", "$me", "$fw", "$rw", "$on", "$off", "$fc", "$rc", "$ft", "$rt", "$fs", "$rs", "$farb", "$rarb", "$frh", "$rrh", "$bp", "$bb", "$eb", "$rlp", "$rrp", "$flp", "$frp", "$ballast", "$fuel", "$nextWing");
        return cmd;
    }

    private static void InsertSetup(SqliteCommand c, CarSetupSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime);
        Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$player", s.PlayerCarIndex);
        Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$fw", s.FrontWing); Set(c, "$rw", s.RearWing);
        Set(c, "$on", s.OnThrottle); Set(c, "$off", s.OffThrottle); Set(c, "$fc", s.FrontCamber); Set(c, "$rc", s.RearCamber);
        Set(c, "$ft", s.FrontToe); Set(c, "$rt", s.RearToe); Set(c, "$fs", s.FrontSuspension); Set(c, "$rs", s.RearSuspension);
        Set(c, "$farb", s.FrontAntiRollBar); Set(c, "$rarb", s.RearAntiRollBar); Set(c, "$frh", s.FrontRideHeight); Set(c, "$rrh", s.RearRideHeight);
        Set(c, "$bp", s.BrakePressure); Set(c, "$bb", s.BrakeBias); Set(c, "$eb", s.EngineBraking); Set(c, "$rlp", s.RearLeftTyrePressure);
        Set(c, "$rrp", s.RearRightTyrePressure); Set(c, "$flp", s.FrontLeftTyrePressure); Set(c, "$frp", s.FrontRightTyrePressure);
        Set(c, "$ballast", s.Ballast); Set(c, "$fuel", s.FuelLoad); Set(c, "$nextWing", s.NextFrontWingValue); c.ExecuteNonQuery();
    }

    private static bool SameSetup(CarSetupSample left, CarSetupSample right) =>
        left.FrontWing == right.FrontWing && left.RearWing == right.RearWing &&
        left.OnThrottle == right.OnThrottle && left.OffThrottle == right.OffThrottle &&
        left.FrontCamber.Equals(right.FrontCamber) && left.RearCamber.Equals(right.RearCamber) &&
        left.FrontToe.Equals(right.FrontToe) && left.RearToe.Equals(right.RearToe) &&
        left.FrontSuspension == right.FrontSuspension && left.RearSuspension == right.RearSuspension &&
        left.FrontAntiRollBar == right.FrontAntiRollBar && left.RearAntiRollBar == right.RearAntiRollBar &&
        left.FrontRideHeight == right.FrontRideHeight && left.RearRideHeight == right.RearRideHeight &&
        left.BrakePressure == right.BrakePressure && left.BrakeBias == right.BrakeBias && left.EngineBraking == right.EngineBraking &&
        left.RearLeftTyrePressure.Equals(right.RearLeftTyrePressure) && left.RearRightTyrePressure.Equals(right.RearRightTyrePressure) &&
        left.FrontLeftTyrePressure.Equals(right.FrontLeftTyrePressure) && left.FrontRightTyrePressure.Equals(right.FrontRightTyrePressure) &&
        left.Ballast == right.Ballast && left.FuelLoad.Equals(right.FuelLoad) && Nullable.Equals(left.NextFrontWingValue, right.NextFrontWingValue);

    private static SqliteCommand PrepareEventInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events VALUES ($received,$uid,$session,$frame,$overall,$player,$code,$name,$vehicle,$other,$details)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$player", "$code", "$name", "$vehicle", "$other", "$details");
        return cmd;
    }
    private static void InsertEvent(SqliteCommand c, EventSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$code", s.EventCode); Set(c, "$name", s.EventName); Set(c, "$vehicle", s.VehicleIdx); Set(c, "$other", s.OtherVehicleIdx); Set(c, "$details", s.DetailsJson); c.ExecuteNonQuery();
    }


    private static SqliteCommand PrepareParticipantDebugInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO participants_debug VALUES ($received,$uid,$session,$frame,$overall,$size,$active,$rows58,$rows57,$names)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$size", "$active", "$rows58", "$rows57", "$names");
        return cmd;
    }
    private static void InsertParticipantDebug(SqliteCommand c, ParticipantPacketDebug s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$size", s.PacketSizeBytes); Set(c, "$active", s.NumActiveCars); Set(c, "$rows58", s.RowsIf60Bytes); Set(c, "$rows57", s.RowsIf58Bytes); Set(c, "$names", s.FirstNames); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareParticipantInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO participants VALUES ($received,$uid,$session,$frame,$overall,$car,$ai,$driver,$team,$number,$name,$telemetry,$online)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$car", "$ai", "$driver", "$team", "$number", "$name", "$telemetry", "$online");
        return cmd;
    }
    private static void InsertParticipant(SqliteCommand c, ParticipantSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$car", s.CarIndex); Set(c, "$ai", s.AiControlled); Set(c, "$driver", s.DriverId); Set(c, "$team", s.TeamId); Set(c, "$number", s.RaceNumber); Set(c, "$name", s.Name); Set(c, "$telemetry", s.YourTelemetry); Set(c, "$online", s.ShowOnlineNames); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareFinalClassificationInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO final_classification_packet VALUES ($received,$uid,$session,$frame,$overall,$car,$me,$position,$laps,$grid,$points,$pits,$status,$best,$total,$penaltySeconds,$penalties,$stints,$reason)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$overall", "$car", "$me", "$position", "$laps", "$grid", "$points", "$pits", "$status", "$best", "$total", "$penaltySeconds", "$penalties", "$stints", "$reason");
        return cmd;
    }

    private static void InsertFinalClassification(SqliteCommand c, FinalClassificationSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$overall", s.OverallFrameIdentifier); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$position", s.Position); Set(c, "$laps", s.NumLaps); Set(c, "$grid", s.GridPosition); Set(c, "$points", s.Points); Set(c, "$pits", s.NumPitStops); Set(c, "$status", s.ResultStatus); Set(c, "$best", s.BestLapTimeMs); Set(c, "$total", s.TotalRaceTimeSeconds); Set(c, "$penaltySeconds", s.PenaltiesTimeSeconds); Set(c, "$penalties", s.NumPenalties); Set(c, "$stints", s.NumTyreStints); Set(c, "$reason", s.ResultReason); c.ExecuteNonQuery();
    }

    private static void AddParams(SqliteCommand cmd, params string[] names)
    {
        foreach (var name in names) cmd.Parameters.AddWithValue(name, DBNull.Value);
    }

    private static void Set(SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters[name].Value = CleanDbValue(value);
    }

    private static object CleanDbValue(object? value)
    {
        if (value is null) return DBNull.Value;
        if (value is float f && (float.IsNaN(f) || float.IsInfinity(f))) return DBNull.Value;
        if (value is double d && (double.IsNaN(d) || double.IsInfinity(d))) return DBNull.Value;
        return value;
    }
}
