using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace F1TelemetryLab;

public static class AnalysisEngine
{
    private sealed record RawPacketRow(string ReceivedAt, int PacketId, byte[] Payload);
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

        var exports = Path.Combine(sessionFolder, "exports");
        var playerOnly = Path.Combine(exports, "player_only");
        var allCars = Path.Combine(exports, "all_cars");
        var comparison = Path.Combine(exports, "comparison");
        var quality = Path.Combine(exports, "quality");
        Directory.CreateDirectory(playerOnly);
        Directory.CreateDirectory(allCars);
        Directory.CreateDirectory(comparison);
        Directory.CreateDirectory(quality);

        SQLitePCL.Batteries_V2.Init();
        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();
        CreateAnalysisSchema(con);
        ClearAnalysisTables(con);

        log?.Invoke("Reading raw UDP packets...");
        var packets = LoadRawPackets(con);
        log?.Invoke($"Raw packets for analysis: {packets.Count:N0}");

        var laps = new List<LapDataSample>(capacity: 500_000);
        var stats = ProcessRawPackets(con, packets, laps, log);

        log?.Invoke("Building lap quality...");
        var trackLength = ReadMetadataInt(con, "track_length_m");
        var qualities = LapQualityAnalyzer.Analyze(laps, trackLength, out var rewindPoints);
        InsertLapQuality(con, qualities, rewindPoints);

        log?.Invoke("Building analysis samples and lap summaries...");
        BuildSqlDerivedTables(con);

        ExportCsv(con, Path.Combine(quality, "lap_quality.csv"), "SELECT * FROM lap_quality ORDER BY car_idx, lap_num");
        ExportCsv(con, Path.Combine(quality, "rewind_events.csv"), "SELECT * FROM rewind_events ORDER BY received_at, car_idx");
        ExportCsv(con, Path.Combine(allCars, "lap_summary_all_cars.csv"), "SELECT * FROM lap_summary ORDER BY car_idx, lap_num");
        ExportCsv(con, Path.Combine(allCars, "lap_state_summary_all_cars.csv"), "SELECT * FROM lap_state_summary ORDER BY car_idx, lap_num");
        ExportCsv(con, Path.Combine(allCars, "final_classification.csv"), "SELECT * FROM final_classification ORDER BY position, car_idx");
        ExportCsv(con, Path.Combine(allCars, "participants_debug.csv"), "SELECT * FROM participants_debug ORDER BY received_at");
        ExportCsv(con, Path.Combine(playerOnly, "lap_summary_player.csv"), "SELECT * FROM lap_summary WHERE is_player = 1 ORDER BY lap_num");
        ExportCsv(con, Path.Combine(comparison, "analysis_trace_10m.csv"), "SELECT * FROM analysis_trace_10m ORDER BY car_idx, lap_num, distance_bin_m");
        ExportBestLaps(con, Path.Combine(comparison, "best_laps_by_car.csv"));
        ExportPlayerVsFastest(con, Path.Combine(comparison, "player_vs_fastest_basic.csv"));

        var cleanCount = qualities.Count(x => x.CleanLap);
        var dirtyCount = qualities.Count(x => !x.CleanLap);
        var result = new AnalysisResult(
            sessionFolder,
            exports,
            packets.Count,
            stats.TelemetryRows,
            stats.LapRows,
            stats.MotionRows,
            stats.StatusRows,
            stats.DamageRows,
            stats.EventsRows,
            stats.ParticipantsRows,
            stats.FinalClassificationRows,
            cleanCount,
            dirtyCount,
            $"Processed {packets.Count:N0} packets. Clean laps: {cleanCount}, dirty laps: {dirtyCount}. Exports: {exports}");

        WriteAnalysisManifest(sessionFolder, result);
        return result;
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
        DROP TABLE IF EXISTS events;
        DROP TABLE IF EXISTS participants;
        DROP TABLE IF EXISTS participants_debug;
        DROP TABLE IF EXISTS final_classification_packet;
        DROP TABLE IF EXISTS lap_quality;
        DROP TABLE IF EXISTS rewind_events;

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
        DELETE FROM events;
        DELETE FROM participants;
        DELETE FROM participants_debug;
        DELETE FROM final_classification_packet;
        DELETE FROM lap_quality;
        DELETE FROM rewind_events;
        DROP TABLE IF EXISTS analysis_samples;
        DROP TABLE IF EXISTS analysis_trace_10m;
        DROP TABLE IF EXISTS lap_summary;
        DROP TABLE IF EXISTS lap_state_summary;
        DROP TABLE IF EXISTS final_classification;
        """;
        cmd.ExecuteNonQuery();
    }

    private static List<RawPacketRow> LoadRawPackets(SqliteConnection con)
    {
        var rows = new List<RawPacketRow>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT received_at, packet_id, payload
            FROM raw_packets
            WHERE packet_format = 2026 AND packet_id IN (0,2,3,4,6,7,8,10)
            ORDER BY id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RawPacketRow(reader.GetString(0), reader.GetInt32(1), (byte[])reader[2]));
        }
        return rows;
    }

    private sealed record ParseStats(int TelemetryRows, int LapRows, int MotionRows, int StatusRows, int DamageRows, int EventsRows, int ParticipantsRows, int FinalClassificationRows);

    private static ParseStats ProcessRawPackets(SqliteConnection con, List<RawPacketRow> packets, List<LapDataSample> laps, Action<string>? log)
    {
        var telemetryRows = 0;
        var lapRows = 0;
        var motionRows = 0;
        var statusRows = 0;
        var damageRows = 0;
        var eventRows = 0;
        var participantRows = 0;
        var finalRows = 0;

        using var tx = con.BeginTransaction();
        using var telemetryCmd = PrepareTelemetryInsert(con, tx);
        using var lapCmd = PrepareLapInsert(con, tx);
        using var motionCmd = PrepareMotionInsert(con, tx);
        using var statusCmd = PrepareStatusInsert(con, tx);
        using var damageCmd = PrepareDamageInsert(con, tx);
        using var eventCmd = PrepareEventInsert(con, tx);
        using var partCmd = PrepareParticipantInsert(con, tx);
        using var partDebugCmd = PrepareParticipantDebugInsert(con, tx);
        using var finalCmd = PrepareFinalClassificationInsert(con, tx);

        foreach (var row in packets)
        {
            var receivedAt = ParseDate(row.ReceivedAt);
            switch (row.PacketId)
            {
                case 6:
                    foreach (var s in F12026Parser.ParseCarTelemetryPacket(row.Payload, receivedAt))
                    {
                        InsertTelemetry(telemetryCmd, s);
                        telemetryRows++;
                    }
                    break;
                case 2:
                    foreach (var s in F12026Parser.ParseLapDataPacket(row.Payload, receivedAt))
                    {
                        InsertLap(lapCmd, s);
                        laps.Add(s);
                        lapRows++;
                    }
                    break;
                case 0:
                    foreach (var s in F12026Parser.ParseMotionPacket(row.Payload, receivedAt))
                    {
                        InsertMotion(motionCmd, s);
                        motionRows++;
                    }
                    break;
                case 7:
                    foreach (var s in F12026Parser.ParseCarStatusPacket(row.Payload, receivedAt))
                    {
                        InsertStatus(statusCmd, s);
                        statusRows++;
                    }
                    break;
                case 10:
                    foreach (var s in F12026Parser.ParseCarDamagePacket(row.Payload, receivedAt))
                    {
                        InsertDamage(damageCmd, s);
                        damageRows++;
                    }
                    break;
                case 3:
                    var ev = F12026Parser.ParseEventPacket(row.Payload, receivedAt);
                    if (ev is not null)
                    {
                        InsertEvent(eventCmd, ev);
                        eventRows++;
                    }
                    break;
                case 4:
                    var dbg = F12026Parser.ParseParticipantsDebug(row.Payload, receivedAt);
                    if (dbg is not null) InsertParticipantDebug(partDebugCmd, dbg);
                    foreach (var p in F12026Parser.ParseParticipantsPacket(row.Payload, receivedAt))
                    {
                        InsertParticipant(partCmd, p);
                        participantRows++;
                    }
                    break;
                case 8:
                    foreach (var s in F12026Parser.ParseFinalClassificationPacket(row.Payload, receivedAt))
                    {
                        InsertFinalClassification(finalCmd, s);
                        finalRows++;
                    }
                    break;
            }
        }

        tx.Commit();
        log?.Invoke($"Parsed rows: telemetry {telemetryRows:N0}, lap {lapRows:N0}, motion {motionRows:N0}, status {statusRows:N0}, damage {damageRows:N0}, events {eventRows:N0}, participants {participantRows:N0}, final {finalRows:N0}");
        return new ParseStats(telemetryRows, lapRows, motionRows, statusRows, damageRows, eventRows, participantRows, finalRows);
    }

    private static void InsertLapQuality(SqliteConnection con, IReadOnlyList<LapQualityResult> qualities, IReadOnlyList<RewindEventResult> rewindPoints)
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
        foreach (var row in rewindPoints)
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

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }

    private static void WriteAnalysisManifest(string sessionFolder, AnalysisResult result)
    {
        var manifestPath = Path.Combine(sessionFolder, "analysis_manifest.json");
        var json = JsonSerializer.Serialize(new
        {
            app = AppInfo.Name,
            version = AppInfo.Version,
            schema_version = AppInfo.DatabaseSchemaVersion,
            analyzed_at = DateTimeOffset.Now.ToString("O"),
            raw_packets_processed = result.RawPacketsProcessed,
            telemetry_rows = result.TelemetryRows,
            lap_rows = result.LapRows,
            motion_rows = result.MotionRows,
            status_rows = result.StatusRows,
            damage_rows = result.DamageRows,
            events_rows = result.EventsRows,
            participants_rows = result.ParticipantsRows,
            final_classification_rows = result.FinalClassificationRows,
            clean_lap_count = result.CleanLapCount,
            dirty_lap_count = result.DirtyLapCount,
            exports = "exports"
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json, Encoding.UTF8);
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
