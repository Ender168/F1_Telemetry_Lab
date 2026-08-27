using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace F1TelemetryLab;

public static class AnalysisEngine
{
    private sealed record RawPacketRow(string ReceivedAt, int PacketId, byte[] Payload);
    private sealed record RewindPoint(int CarIndex, int LapNum, string ReceivedAt, float SessionTime, float LapDistance, uint CurrentLapTimeMs, string Reason);
    private sealed record LapQualityRow(int CarIndex, int LapNum, bool IsPlayer, bool CleanLap, int RewindCount, int InvalidCount, int SampleCount, float MinDistance, float MaxDistance, uint LapTimeMs);
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
        var rewindPoints = new List<RewindPoint>();
        var stats = ProcessRawPackets(con, packets, laps, log);

        log?.Invoke("Building lap quality...");
        var qualities = BuildLapQuality(laps, rewindPoints);
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
            stats.LapRows,
            stats.MotionRows,
            stats.StatusRows,
            stats.DamageRows,
            stats.EventsRows,
            stats.ParticipantsRows,
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
        CREATE TABLE IF NOT EXISTS lap_data(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER,
            player_car_index INTEGER, car_idx INTEGER, is_player INTEGER, last_lap_time_ms INTEGER, current_lap_time_ms INTEGER,
            sector1_time_ms INTEGER, sector2_time_ms INTEGER, delta_to_front_ms INTEGER, delta_to_leader_ms INTEGER,
            lap_distance REAL, total_distance REAL, position INTEGER, lap_num INTEGER, pit_status INTEGER, num_pit_stops INTEGER,
            sector INTEGER, lap_invalid INTEGER, penalties INTEGER, warnings INTEGER, driver_status INTEGER, result_status INTEGER
        );
        CREATE TABLE IF NOT EXISTS motion_data(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, player_car_index INTEGER, car_idx INTEGER, is_player INTEGER,
            world_position_x REAL, world_position_y REAL, world_position_z REAL, world_velocity_x REAL, world_velocity_y REAL, world_velocity_z REAL,
            g_force_lateral REAL, g_force_longitudinal REAL, g_force_vertical REAL, yaw REAL, pitch REAL, roll REAL
        );
        CREATE TABLE IF NOT EXISTS car_status(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, player_car_index INTEGER, car_idx INTEGER, is_player INTEGER,
            front_brake_bias INTEGER, fuel_in_tank REAL, fuel_remaining_laps REAL, actual_tyre_compound INTEGER, visual_tyre_compound INTEGER,
            tyres_age_laps INTEGER, engine_power_ice REAL, engine_power_mguk REAL, ers_store_energy REAL, ers_deploy_mode INTEGER,
            ers_harvested_this_lap_mguk REAL, ers_harvested_this_lap_mguh REAL, ers_harvest_limit_per_lap REAL, ers_deployed_this_lap REAL
        );
        CREATE TABLE IF NOT EXISTS car_damage(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, player_car_index INTEGER, car_idx INTEGER, is_player INTEGER,
            tyre_wear_rl REAL, tyre_wear_rr REAL, tyre_wear_fl REAL, tyre_wear_fr REAL, tyre_wear_avg REAL,
            tyre_damage_rl INTEGER, tyre_damage_rr INTEGER, tyre_damage_fl INTEGER, tyre_damage_fr INTEGER,
            front_left_wing_damage INTEGER, front_right_wing_damage INTEGER, rear_wing_damage INTEGER, floor_damage INTEGER, diffuser_damage INTEGER, sidepod_damage INTEGER
        );
        CREATE TABLE IF NOT EXISTS events(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, player_car_index INTEGER,
            event_code TEXT, event_name TEXT, vehicle_idx INTEGER, other_vehicle_idx INTEGER, details_json TEXT
        );
        CREATE TABLE IF NOT EXISTS participants(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, car_idx INTEGER,
            ai_controlled INTEGER, driver_id INTEGER, team_id INTEGER, race_number INTEGER, name TEXT, your_telemetry INTEGER, show_online_names INTEGER
        );
        CREATE TABLE IF NOT EXISTS participants_debug(
            received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER,
            packet_size_bytes INTEGER, num_active_cars INTEGER, rows_if_60_bytes INTEGER, rows_if_58_bytes INTEGER, first_names TEXT
        );
        CREATE TABLE IF NOT EXISTS driver_aliases(
            car_idx INTEGER PRIMARY KEY,
            original_name TEXT,
            display_name TEXT,
            short_name TEXT,
            updated_at TEXT
        );
        CREATE TABLE IF NOT EXISTS lap_quality(
            car_idx INTEGER, lap_num INTEGER, is_player INTEGER, clean_lap INTEGER, rewind_count INTEGER, invalid_count INTEGER,
            sample_count INTEGER, min_distance REAL, max_distance REAL, lap_time_ms INTEGER
        );
        CREATE TABLE IF NOT EXISTS rewind_events(
            car_idx INTEGER, lap_num INTEGER, received_at TEXT, session_time REAL, lap_distance REAL, current_lap_time_ms INTEGER, reason TEXT
        );
        """;
        cmd.ExecuteNonQuery();
    }

    private static void ClearAnalysisTables(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        DELETE FROM lap_data;
        DELETE FROM motion_data;
        DELETE FROM car_status;
        DELETE FROM car_damage;
        DELETE FROM events;
        DELETE FROM participants;
        DELETE FROM participants_debug;
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
            WHERE packet_format = 2026 AND packet_id IN (0,2,3,4,7,10)
            ORDER BY id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RawPacketRow(reader.GetString(0), reader.GetInt32(1), (byte[])reader[2]));
        }
        return rows;
    }

    private sealed record ParseStats(int LapRows, int MotionRows, int StatusRows, int DamageRows, int EventsRows, int ParticipantsRows);

    private static ParseStats ProcessRawPackets(SqliteConnection con, List<RawPacketRow> packets, List<LapDataSample> laps, Action<string>? log)
    {
        var lapRows = 0;
        var motionRows = 0;
        var statusRows = 0;
        var damageRows = 0;
        var eventRows = 0;
        var participantRows = 0;

        using var tx = con.BeginTransaction();
        using var lapCmd = PrepareLapInsert(con, tx);
        using var motionCmd = PrepareMotionInsert(con, tx);
        using var statusCmd = PrepareStatusInsert(con, tx);
        using var damageCmd = PrepareDamageInsert(con, tx);
        using var eventCmd = PrepareEventInsert(con, tx);
        using var partCmd = PrepareParticipantInsert(con, tx);
        using var partDebugCmd = PrepareParticipantDebugInsert(con, tx);

        foreach (var row in packets)
        {
            var receivedAt = ParseDate(row.ReceivedAt);
            switch (row.PacketId)
            {
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
            }
        }

        tx.Commit();
        log?.Invoke($"Parsed rows: lap {lapRows:N0}, motion {motionRows:N0}, status {statusRows:N0}, damage {damageRows:N0}, events {eventRows:N0}, participants {participantRows:N0}");
        return new ParseStats(lapRows, motionRows, statusRows, damageRows, eventRows, participantRows);
    }

    private static List<LapQualityRow> BuildLapQuality(List<LapDataSample> laps, List<RewindPoint> rewindPoints)
    {
        var dirty = new Dictionary<(int car, int lap), int>();
        var invalid = new Dictionary<(int car, int lap), int>();
        foreach (var carGroup in laps.OrderBy(x => x.ReceivedAt).GroupBy(x => x.CarIndex))
        {
            LapDataSample? prev = null;
            foreach (var s in carGroup)
            {
                if (s.LapNum <= 0) { prev = s; continue; }
                var key = (s.CarIndex, s.LapNum);
                if (s.LapInvalid) invalid[key] = invalid.GetValueOrDefault(key) + 1;

                if (prev is not null)
                {
                    var sameLap = prev.LapNum == s.LapNum;
                    var reasons = new List<string>();
                    if (s.SessionTime < prev.SessionTime - 0.25f) reasons.Add("session_time_backwards");
                    if (s.LapNum < prev.LapNum) reasons.Add("lap_number_backwards");
                    if (sameLap && s.LapDistance < prev.LapDistance - 50f) reasons.Add("lap_distance_backwards");
                    if (sameLap && s.CurrentLapTimeMs + 750 < prev.CurrentLapTimeMs) reasons.Add("lap_time_backwards");

                    // Session time can jump backwards globally around flashback/replay transitions.
                    // Mark the lap dirty only when the car/lap itself rolled back, not when the
                    // only symptom is global session_time. Otherwise one flashback can poison the
                    // entire field like a spreadsheet with feelings.
                    var lapActuallyRolledBack = reasons.Any(r => r != "session_time_backwards");
                    if (lapActuallyRolledBack)
                    {
                        dirty[key] = dirty.GetValueOrDefault(key) + 1;
                    }
                    if (reasons.Count > 0)
                    {
                        rewindPoints.Add(new RewindPoint(s.CarIndex, s.LapNum, s.ReceivedAt.ToString("O"), s.SessionTime, s.LapDistance, s.CurrentLapTimeMs, string.Join(";", reasons)));
                    }
                }
                prev = s;
            }
        }

        var result = new List<LapQualityRow>();
        foreach (var g in laps.Where(x => x.LapNum > 0).GroupBy(x => (x.CarIndex, x.LapNum)).OrderBy(x => x.Key.CarIndex).ThenBy(x => x.Key.LapNum))
        {
            var key = (g.Key.CarIndex, g.Key.LapNum);
            var rewindCount = dirty.GetValueOrDefault(key);
            var invalidCount = invalid.GetValueOrDefault(key);
            var lapTime = g.Max(x => x.CurrentLapTimeMs);
            var clean = rewindCount == 0 && invalidCount == 0 && lapTime > 0;
            result.Add(new LapQualityRow(g.Key.CarIndex, g.Key.LapNum, g.Any(x => x.IsPlayer), clean, rewindCount, invalidCount, g.Count(), g.Min(x => x.LapDistance), g.Max(x => x.LapDistance), lapTime));
        }
        return result;
    }

    private static void InsertLapQuality(SqliteConnection con, List<LapQualityRow> qualities, List<RewindPoint> rewindPoints)
    {
        using var tx = con.BeginTransaction();
        using var q = con.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT INTO lap_quality VALUES ($car,$lap,$me,$clean,$rew,$invalid,$samples,$min,$max,$time)";
        foreach (var name in new[] { "$car", "$lap", "$me", "$clean", "$rew", "$invalid", "$samples", "$min", "$max", "$time" }) q.Parameters.AddWithValue(name, 0);
        foreach (var row in qualities)
        {
            q.Parameters["$car"].Value = row.CarIndex;
            q.Parameters["$lap"].Value = row.LapNum;
            q.Parameters["$me"].Value = row.IsPlayer ? 1 : 0;
            q.Parameters["$clean"].Value = row.CleanLap ? 1 : 0;
            q.Parameters["$rew"].Value = row.RewindCount;
            q.Parameters["$invalid"].Value = row.InvalidCount;
            q.Parameters["$samples"].Value = row.SampleCount;
            q.Parameters["$min"].Value = row.MinDistance;
            q.Parameters["$max"].Value = row.MaxDistance;
            q.Parameters["$time"].Value = row.LapTimeMs;
            q.ExecuteNonQuery();
        }

        using var r = con.CreateCommand();
        r.Transaction = tx;
        r.CommandText = "INSERT INTO rewind_events VALUES ($car,$lap,$received,$session,$distance,$lapTime,$reason)";
        foreach (var name in new[] { "$car", "$lap", "$received", "$session", "$distance", "$lapTime", "$reason" }) r.Parameters.AddWithValue(name, 0);
        foreach (var row in rewindPoints)
        {
            r.Parameters["$car"].Value = row.CarIndex;
            r.Parameters["$lap"].Value = row.LapNum;
            r.Parameters["$received"].Value = row.ReceivedAt;
            r.Parameters["$session"].Value = row.SessionTime;
            r.Parameters["$distance"].Value = row.LapDistance;
            r.Parameters["$lapTime"].Value = row.CurrentLapTimeMs;
            r.Parameters["$reason"].Value = row.Reason;
            r.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void BuildSqlDerivedTables(SqliteConnection con)
    {
        TryAddColumn(con, "driver_aliases", "short_name", "TEXT");
        // Execute derived-table statements one by one. Some providers pretend
        // multi-statement commands are fine, then quietly leave humanity with
        // "no such table". We are not giving them that opportunity.
        ExecuteNonQuery(con, "DROP TABLE IF EXISTS analysis_samples;");
        ExecuteNonQuery(con, """
        CREATE TABLE analysis_samples AS
        SELECT
            l.received_at, l.session_uid, l.session_time, l.frame_identifier, l.car_idx, l.is_player,
            l.lap_num, l.lap_distance, l.total_distance, l.position, l.sector, l.lap_invalid,
            l.current_lap_time_ms, l.last_lap_time_ms, l.penalties, l.warnings,
            t.speed, t.throttle, t.brake, t.steer, t.gear, t.engine_rpm, t.drs,
            m.world_position_x, m.world_position_y, m.world_position_z, m.yaw, m.g_force_lateral, m.g_force_longitudinal,
            q.clean_lap, q.rewind_count, q.invalid_count
        FROM lap_data l
        LEFT JOIN car_telemetry t ON t.frame_identifier = l.frame_identifier AND t.car_idx = l.car_idx
        LEFT JOIN motion_data m ON m.frame_identifier = l.frame_identifier AND m.car_idx = l.car_idx
        LEFT JOIN lap_quality q ON q.car_idx = l.car_idx AND q.lap_num = l.lap_num;
        """);
        ExecuteNonQuery(con, "DROP TABLE IF EXISTS lap_summary;");
        ExecuteNonQuery(con, """
        CREATE TABLE lap_summary AS
        SELECT
            a.car_idx,
            a.lap_num,
            MAX(a.is_player) AS is_player,
            MAX(a.position) AS best_position_seen,
            MAX(a.clean_lap) AS clean_lap,
            MAX(a.rewind_count) AS rewind_count,
            MAX(a.invalid_count) AS invalid_count,
            COUNT(*) AS sample_count,
            MIN(a.lap_distance) AS min_distance,
            MAX(a.lap_distance) AS max_distance,
            MAX(a.current_lap_time_ms) AS lap_time_ms,
            MAX(a.speed) AS max_speed,
            AVG(a.speed) AS avg_speed,
            MIN(a.speed) AS min_speed,
            AVG(a.throttle) AS avg_throttle,
            AVG(a.brake) AS avg_brake,
            MAX(a.penalties) AS penalties,
            MAX(a.warnings) AS warnings
        FROM analysis_samples a
        WHERE a.lap_num > 0
        GROUP BY a.car_idx, a.lap_num;
        """);

        ExecuteNonQuery(con, "DROP TABLE IF EXISTS lap_state_summary;");
        ExecuteNonQuery(con, """
        CREATE TABLE lap_state_summary AS
        WITH
        lap_ranked AS (
            SELECT l.*,
                   ROW_NUMBER() OVER (PARTITION BY l.car_idx, l.lap_num ORDER BY l.frame_identifier ASC, l.session_time ASC) AS rn_start,
                   ROW_NUMBER() OVER (PARTITION BY l.car_idx, l.lap_num ORDER BY l.frame_identifier DESC, l.session_time DESC) AS rn_end
            FROM lap_data l
            WHERE l.lap_num > 0
        ),
        lap_start AS (SELECT * FROM lap_ranked WHERE rn_start = 1),
        lap_end AS (SELECT * FROM lap_ranked WHERE rn_end = 1),
        lap_agg AS (
            SELECT car_idx, lap_num,
                   MAX(pit_status) AS pit_status_max,
                   MIN(num_pit_stops) AS pit_stops_start,
                   MAX(num_pit_stops) AS pit_stops_end,
                   MAX(lap_invalid) AS lap_invalid,
                   MAX(warnings) AS warnings,
                   MAX(penalties) AS penalties
            FROM lap_data
            WHERE lap_num > 0
            GROUP BY car_idx, lap_num
        ),
        status_tagged AS (
            SELECT s.*, l.lap_num
            FROM car_status s
            JOIN lap_data l ON l.frame_identifier = s.frame_identifier AND l.car_idx = s.car_idx
            WHERE l.lap_num > 0
        ),
        status_ranked AS (
            SELECT s.*,
                   ROW_NUMBER() OVER (PARTITION BY s.car_idx, s.lap_num ORDER BY s.frame_identifier ASC, s.session_time ASC) AS rn_start,
                   ROW_NUMBER() OVER (PARTITION BY s.car_idx, s.lap_num ORDER BY s.frame_identifier DESC, s.session_time DESC) AS rn_end
            FROM status_tagged s
        ),
        status_start AS (SELECT * FROM status_ranked WHERE rn_start = 1),
        status_end AS (SELECT * FROM status_ranked WHERE rn_end = 1),
        status_agg AS (
            SELECT car_idx, lap_num,
                   MIN(ers_store_energy) AS ers_min,
                   MAX(ers_store_energy) AS ers_max,
                   MAX(ers_deployed_this_lap) AS ers_deployed_this_lap,
                   MAX(ers_harvested_this_lap_mguk) AS ers_harvest_mguk_this_lap,
                   MAX(ers_harvested_this_lap_mguh) AS ers_harvest_mguh_this_lap
            FROM status_tagged
            GROUP BY car_idx, lap_num
        ),
        damage_tagged AS (
            SELECT d.*, l.lap_num
            FROM car_damage d
            JOIN lap_data l ON l.frame_identifier = d.frame_identifier AND l.car_idx = d.car_idx
            WHERE l.lap_num > 0
        ),
        damage_ranked AS (
            SELECT d.*,
                   ROW_NUMBER() OVER (PARTITION BY d.car_idx, d.lap_num ORDER BY d.frame_identifier ASC, d.session_time ASC) AS rn_start,
                   ROW_NUMBER() OVER (PARTITION BY d.car_idx, d.lap_num ORDER BY d.frame_identifier DESC, d.session_time DESC) AS rn_end
            FROM damage_tagged d
        ),
        damage_start AS (SELECT * FROM damage_ranked WHERE rn_start = 1),
        damage_end AS (SELECT * FROM damage_ranked WHERE rn_end = 1),
        telemetry_agg AS (
            SELECT car_idx, lap_num,
                   MAX(speed) AS max_speed,
                   AVG(speed) AS avg_speed,
                   100.0 * AVG(CASE WHEN throttle >= 0.98 THEN 1.0 ELSE 0.0 END) AS full_throttle_pct,
                   100.0 * AVG(CASE WHEN brake >= 0.05 THEN 1.0 ELSE 0.0 END) AS brake_pct,
                   100.0 * AVG(CASE WHEN drs > 0 THEN 1.0 ELSE 0.0 END) AS drs_pct
            FROM analysis_samples
            WHERE lap_num > 0
            GROUP BY car_idx, lap_num
        )
        SELECT
            ls.car_idx,
            ls.lap_num,
            ls.is_player,
            ls.clean_lap,
            ls.rewind_count,
            ls.invalid_count,
            COALESCE(la.lap_invalid, 0) AS lap_invalid,
            ls.lap_time_ms,
            COALESCE(le.sector1_time_ms, 0) AS sector1_ms,
            COALESCE(le.sector2_time_ms, 0) AS sector2_ms,
            CASE
                WHEN ls.lap_time_ms > 0 AND COALESCE(le.sector1_time_ms, 0) > 0 AND COALESCE(le.sector2_time_ms, 0) > 0
                THEN ls.lap_time_ms - le.sector1_time_ms - le.sector2_time_ms
                ELSE 0
            END AS sector3_ms,
            COALESCE(l0.position, 0) AS position_start,
            COALESCE(le.position, 0) AS position_end,
            COALESCE(la.warnings, ls.warnings, 0) AS warnings,
            COALESCE(la.penalties, ls.penalties, 0) AS penalties,
            CASE
                WHEN COALESCE(la.pit_status_max, 0) > 0 THEN 1
                WHEN COALESCE(la.pit_stops_end, 0) > COALESCE(la.pit_stops_start, 0) THEN 1
                WHEN COALESCE(se.tyres_age_laps, 0) > 0 AND COALESCE(se.tyres_age_laps, 0) < COALESCE(ss.tyres_age_laps, 0) THEN 1
                WHEN COALESCE(ss.actual_tyre_compound, 0) > 0 AND COALESCE(se.actual_tyre_compound, 0) > 0 AND ss.actual_tyre_compound <> se.actual_tyre_compound THEN 1
                WHEN COALESCE(ss.visual_tyre_compound, 0) > 0 AND COALESCE(se.visual_tyre_compound, 0) > 0 AND ss.visual_tyre_compound <> se.visual_tyre_compound THEN 1
                ELSE 0
            END AS pit_this_lap,
            COALESCE(la.pit_status_max, 0) AS pit_status_max,
            COALESCE(la.pit_stops_start, 0) AS pit_stops_start,
            COALESCE(la.pit_stops_end, 0) AS pit_stops_end,
            COALESCE(ss.actual_tyre_compound, 0) AS actual_tyre_compound_start,
            COALESCE(se.actual_tyre_compound, 0) AS actual_tyre_compound_end,
            COALESCE(ss.visual_tyre_compound, 0) AS visual_tyre_compound_start,
            COALESCE(se.visual_tyre_compound, 0) AS visual_tyre_compound_end,
            COALESCE(ss.tyres_age_laps, 0) AS tyres_age_start,
            COALESCE(se.tyres_age_laps, 0) AS tyres_age_end,
            ss.fuel_in_tank AS fuel_start,
            se.fuel_in_tank AS fuel_end,
            CASE WHEN ss.fuel_in_tank IS NOT NULL AND se.fuel_in_tank IS NOT NULL THEN ss.fuel_in_tank - se.fuel_in_tank ELSE NULL END AS fuel_used,
            se.fuel_remaining_laps AS fuel_remaining_laps_end,
            ss.ers_store_energy AS ers_start,
            se.ers_store_energy AS ers_end,
            sa.ers_min,
            sa.ers_max,
            CASE WHEN ss.ers_store_energy IS NOT NULL AND se.ers_store_energy IS NOT NULL THEN se.ers_store_energy - ss.ers_store_energy ELSE NULL END AS ers_delta,
            sa.ers_deployed_this_lap,
            sa.ers_harvest_mguk_this_lap,
            sa.ers_harvest_mguh_this_lap,
            COALESCE(se.ers_deploy_mode, 0) AS ers_deploy_mode_end,
            ds.tyre_wear_fl AS tyre_wear_fl_start,
            de.tyre_wear_fl AS tyre_wear_fl_end,
            CASE WHEN ds.tyre_wear_fl IS NOT NULL AND de.tyre_wear_fl IS NOT NULL THEN de.tyre_wear_fl - ds.tyre_wear_fl ELSE NULL END AS tyre_wear_fl_delta,
            ds.tyre_wear_fr AS tyre_wear_fr_start,
            de.tyre_wear_fr AS tyre_wear_fr_end,
            CASE WHEN ds.tyre_wear_fr IS NOT NULL AND de.tyre_wear_fr IS NOT NULL THEN de.tyre_wear_fr - ds.tyre_wear_fr ELSE NULL END AS tyre_wear_fr_delta,
            ds.tyre_wear_rl AS tyre_wear_rl_start,
            de.tyre_wear_rl AS tyre_wear_rl_end,
            CASE WHEN ds.tyre_wear_rl IS NOT NULL AND de.tyre_wear_rl IS NOT NULL THEN de.tyre_wear_rl - ds.tyre_wear_rl ELSE NULL END AS tyre_wear_rl_delta,
            ds.tyre_wear_rr AS tyre_wear_rr_start,
            de.tyre_wear_rr AS tyre_wear_rr_end,
            CASE WHEN ds.tyre_wear_rr IS NOT NULL AND de.tyre_wear_rr IS NOT NULL THEN de.tyre_wear_rr - ds.tyre_wear_rr ELSE NULL END AS tyre_wear_rr_delta,
            de.tyre_wear_avg AS tyre_wear_avg_end,
            CASE WHEN ds.tyre_wear_avg IS NOT NULL AND de.tyre_wear_avg IS NOT NULL THEN de.tyre_wear_avg - ds.tyre_wear_avg ELSE NULL END AS tyre_wear_avg_delta,
            COALESCE(de.tyre_damage_fl, 0) AS tyre_damage_fl_end,
            COALESCE(de.tyre_damage_fr, 0) AS tyre_damage_fr_end,
            COALESCE(de.tyre_damage_rl, 0) AS tyre_damage_rl_end,
            COALESCE(de.tyre_damage_rr, 0) AS tyre_damage_rr_end,
            COALESCE(de.front_left_wing_damage, 0) AS front_left_wing_damage_end,
            COALESCE(de.front_right_wing_damage, 0) AS front_right_wing_damage_end,
            COALESCE(de.rear_wing_damage, 0) AS rear_wing_damage_end,
            COALESCE(de.floor_damage, 0) AS floor_damage_end,
            COALESCE(de.diffuser_damage, 0) AS diffuser_damage_end,
            COALESCE(de.sidepod_damage, 0) AS sidepod_damage_end,
            MAX(
                ABS(COALESCE(de.front_left_wing_damage, 0) - COALESCE(ds.front_left_wing_damage, 0)),
                ABS(COALESCE(de.front_right_wing_damage, 0) - COALESCE(ds.front_right_wing_damage, 0)),
                ABS(COALESCE(de.rear_wing_damage, 0) - COALESCE(ds.rear_wing_damage, 0)),
                ABS(COALESCE(de.floor_damage, 0) - COALESCE(ds.floor_damage, 0)),
                ABS(COALESCE(de.diffuser_damage, 0) - COALESCE(ds.diffuser_damage, 0)),
                ABS(COALESCE(de.sidepod_damage, 0) - COALESCE(ds.sidepod_damage, 0))
            ) AS damage_delta_max,
            ta.max_speed,
            ta.avg_speed,
            ta.full_throttle_pct,
            ta.brake_pct,
            ta.drs_pct
        FROM lap_summary ls
        LEFT JOIN lap_start l0 ON l0.car_idx = ls.car_idx AND l0.lap_num = ls.lap_num
        LEFT JOIN lap_end le ON le.car_idx = ls.car_idx AND le.lap_num = ls.lap_num
        LEFT JOIN lap_agg la ON la.car_idx = ls.car_idx AND la.lap_num = ls.lap_num
        LEFT JOIN status_start ss ON ss.car_idx = ls.car_idx AND ss.lap_num = ls.lap_num
        LEFT JOIN status_end se ON se.car_idx = ls.car_idx AND se.lap_num = ls.lap_num
        LEFT JOIN status_agg sa ON sa.car_idx = ls.car_idx AND sa.lap_num = ls.lap_num
        LEFT JOIN damage_start ds ON ds.car_idx = ls.car_idx AND ds.lap_num = ls.lap_num
        LEFT JOIN damage_end de ON de.car_idx = ls.car_idx AND de.lap_num = ls.lap_num
        LEFT JOIN telemetry_agg ta ON ta.car_idx = ls.car_idx AND ta.lap_num = ls.lap_num
        WHERE ls.lap_num > 0;
        """);
        ExecuteNonQuery(con, "CREATE INDEX IF NOT EXISTS idx_lap_state_summary_car_lap ON lap_state_summary(car_idx, lap_num);");

        ExecuteNonQuery(con, "DROP TABLE IF EXISTS analysis_trace_10m;");
        ExecuteNonQuery(con, """
        CREATE TABLE analysis_trace_10m AS
        SELECT
            a.car_idx,
            a.lap_num,
            MAX(a.is_player) AS is_player,
            MAX(a.clean_lap) AS clean_lap,
            CAST(a.lap_distance / 10 AS INTEGER) * 10 AS distance_bin_m,
            MAX(a.current_lap_time_ms) AS time_ms,
            AVG(a.speed) AS speed,
            AVG(a.throttle) AS throttle,
            AVG(a.brake) AS brake,
            AVG(a.steer) AS steer,
            AVG(a.gear) AS gear,
            AVG(a.world_position_x) AS world_position_x,
            AVG(a.world_position_z) AS world_position_z,
            AVG(a.yaw) AS yaw,
            AVG(a.g_force_lateral) AS g_force_lateral,
            AVG(a.g_force_longitudinal) AS g_force_longitudinal
        FROM analysis_samples a
        WHERE a.lap_num > 0 AND a.lap_distance >= 0
          AND NOT (a.current_lap_time_ms <= 100 AND a.lap_distance > 200)
        GROUP BY a.car_idx, a.lap_num, distance_bin_m;
        """);
        ExecuteNonQuery(con, "DROP TABLE IF EXISTS final_classification;");
        ExecuteNonQuery(con, """
        CREATE TABLE final_classification AS
        WITH latest_lap AS (
            SELECT l.*
            FROM lap_data l
            JOIN (
                SELECT car_idx, MAX(frame_identifier) AS max_frame
                FROM lap_data
                GROUP BY car_idx
            ) x ON x.car_idx = l.car_idx AND x.max_frame = l.frame_identifier
        ), latest_names AS (
            SELECT p.car_idx, p.name, p.ai_controlled, p.driver_id, p.team_id, p.race_number
            FROM participants p
            JOIN (
                SELECT car_idx, MAX(frame_identifier) AS max_frame
                FROM participants
                GROUP BY car_idx
            ) x ON x.car_idx = p.car_idx AND x.max_frame = p.frame_identifier
        ), best_laps AS (
            SELECT car_idx, MIN(lap_time_ms) AS best_lap_ms
            FROM lap_summary
            WHERE lap_time_ms > 0
            GROUP BY car_idx
        )
        SELECT
            l.position,
            l.car_idx,
            l.is_player,
            COALESCE(n.name, CASE WHEN l.is_player = 1 THEN 'YOU' ELSE 'F1 Generic' END) AS name,
            COALESCE(n.name, CASE WHEN l.is_player = 1 THEN 'YOU' ELSE 'F1 Generic' END) AS original_name,
            COALESCE(NULLIF(a.display_name, ''), COALESCE(n.name, CASE WHEN l.is_player = 1 THEN 'YOU' ELSE 'F1 Generic' END)) AS display_name,
            COALESCE(NULLIF(a.short_name, ''), CASE WHEN l.is_player = 1 THEN 'YOU' ELSE printf('C%02d', l.car_idx) END) AS short_name,
            COALESCE(n.ai_controlled, 0) AS ai_controlled,
            COALESCE(n.driver_id, -1) AS driver_id,
            COALESCE(n.team_id, -1) AS team_id,
            COALESCE(n.race_number, -1) AS race_number,
            l.lap_num,
            l.last_lap_time_ms,
            COALESCE(b.best_lap_ms, 0) AS best_lap_ms,
            l.penalties,
            l.warnings,
            l.result_status,
            l.driver_status
        FROM latest_lap l
        LEFT JOIN latest_names n ON n.car_idx = l.car_idx
        LEFT JOIN driver_aliases a ON a.car_idx = l.car_idx
        LEFT JOIN best_laps b ON b.car_idx = l.car_idx
        WHERE l.position > 0
        ORDER BY l.position ASC, l.car_idx ASC;
        """);
        ExecuteNonQuery(con, "CREATE INDEX IF NOT EXISTS idx_analysis_samples_car_lap_dist ON analysis_samples(car_idx, lap_num, lap_distance);");
        ExecuteNonQuery(con, "CREATE INDEX IF NOT EXISTS idx_analysis_trace_car_lap_dist ON analysis_trace_10m(car_idx, lap_num, distance_bin_m);");
        ExecuteNonQuery(con, "CREATE INDEX IF NOT EXISTS idx_lap_summary_clean_time ON lap_summary(clean_lap, lap_time_ms);");
    }

    private static void TryAddColumn(SqliteConnection con, string table, string column, string type)
    {
        try
        {
            using var info = con.CreateCommand();
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
            ExecuteNonQuery(con, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
        }
        catch { }
    }

    private static void ExecuteNonQuery(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ExportBestLaps(SqliteConnection con, string path)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT s.*
        FROM lap_summary s
        JOIN (
            SELECT car_idx, MIN(lap_time_ms) AS best_time
            FROM lap_summary
            WHERE clean_lap = 1 AND lap_time_ms > 0
            GROUP BY car_idx
        ) b ON b.car_idx = s.car_idx AND b.best_time = s.lap_time_ms
        WHERE s.clean_lap = 1
        ORDER BY s.lap_time_ms ASC, s.car_idx ASC
        """;
        ExportReaderToCsv(cmd, path);
    }

    private static void ExportPlayerVsFastest(SqliteConnection con, string path)
    {
        var player = GetBestLap(con, "is_player = 1");
        var reference = GetBestLap(con, "is_player = 0");
        if (player is null || reference is null)
        {
            File.WriteAllText(path, "message\nNo clean player/reference lap found\n", Encoding.UTF8);
            return;
        }

        var p = LoadBins(con, player.CarIndex, player.LapNum);
        var r = LoadBins(con, reference.CarIndex, reference.LapNum);
        var bins = p.Keys.Union(r.Keys).OrderBy(x => x).ToList();
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("distance_bin_m,player_car,player_lap,ref_car,ref_lap,player_time_ms,ref_time_ms,delta_ms,player_speed,ref_speed,speed_delta,player_throttle,ref_throttle,player_brake,ref_brake,player_steer,ref_steer");
        foreach (var b in bins)
        {
            p.TryGetValue(b, out var pb);
            r.TryGetValue(b, out var rb);
            writer.WriteLine(string.Join(',', new[]
            {
                b.ToString(CultureInfo.InvariantCulture),
                player.CarIndex.ToString(CultureInfo.InvariantCulture), player.LapNum.ToString(CultureInfo.InvariantCulture),
                reference.CarIndex.ToString(CultureInfo.InvariantCulture), reference.LapNum.ToString(CultureInfo.InvariantCulture),
                Num(pb?.TimeMs), Num(rb?.TimeMs), Num(pb is null || rb is null ? null : pb.TimeMs - rb.TimeMs),
                Num(pb?.Speed), Num(rb?.Speed), Num(pb is null || rb is null ? null : pb.Speed - rb.Speed),
                Num(pb?.Throttle), Num(rb?.Throttle), Num(pb?.Brake), Num(rb?.Brake), Num(pb?.Steer), Num(rb?.Steer)
            }));
        }
    }

    private static BestLapRow? GetBestLap(SqliteConnection con, string where)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT car_idx, lap_num, is_player, lap_time_ms FROM lap_summary WHERE clean_lap = 1 AND lap_time_ms > 0 AND {where} ORDER BY lap_time_ms ASC LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new BestLapRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) == 1, Convert.ToUInt32(reader.GetValue(3), CultureInfo.InvariantCulture));
    }

    private static Dictionary<int, TelemetryBin> LoadBins(SqliteConnection con, int carIdx, int lapNum)
    {
        var result = new Dictionary<int, TelemetryBin>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT CAST(lap_distance / 10 AS INTEGER) * 10 AS bin,
               AVG(current_lap_time_ms), AVG(speed), AVG(throttle), AVG(brake), AVG(steer), AVG(gear)
        FROM analysis_samples
        WHERE car_idx = $car AND lap_num = $lap AND clean_lap = 1 AND lap_distance >= 0
        GROUP BY bin
        ORDER BY bin
        """;
        cmd.Parameters.AddWithValue("$car", carIdx);
        cmd.Parameters.AddWithValue("$lap", lapNum);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bin = reader.GetInt32(0);
            result[bin] = new TelemetryBin(bin, D(reader, 1), D(reader, 2), D(reader, 3), D(reader, 4), D(reader, 5), D(reader, 6));
        }
        return result;
    }

    private static double D(SqliteDataReader reader, int idx) => reader.IsDBNull(idx) ? double.NaN : Convert.ToDouble(reader.GetValue(idx), CultureInfo.InvariantCulture);
    private static string Num(double? value) => value is null || double.IsNaN(value.Value) ? "" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

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
            app = "F1 Telemetry Lab C# MVP",
            version = "0.3.8",
            analyzed_at = DateTimeOffset.Now.ToString("O"),
            raw_packets_processed = result.RawPacketsProcessed,
            lap_rows = result.LapRows,
            motion_rows = result.MotionRows,
            status_rows = result.StatusRows,
            damage_rows = result.DamageRows,
            events_rows = result.EventsRows,
            participants_rows = result.ParticipantsRows,
            clean_lap_count = result.CleanLapCount,
            dirty_lap_count = result.DirtyLapCount,
            exports = "exports"
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json, Encoding.UTF8);
    }

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTimeOffset.Now;

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
        cmd.CommandText = "INSERT INTO motion_data VALUES ($received,$uid,$session,$frame,$player,$car,$me,$x,$y,$z,$vx,$vy,$vz,$glat,$glong,$gvert,$yaw,$pitch,$roll)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$player", "$car", "$me", "$x", "$y", "$z", "$vx", "$vy", "$vz", "$glat", "$glong", "$gvert", "$yaw", "$pitch", "$roll");
        return cmd;
    }
    private static void InsertMotion(SqliteCommand c, MotionSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$x", s.WorldPositionX); Set(c, "$y", s.WorldPositionY); Set(c, "$z", s.WorldPositionZ); Set(c, "$vx", s.WorldVelocityX); Set(c, "$vy", s.WorldVelocityY); Set(c, "$vz", s.WorldVelocityZ); Set(c, "$glat", s.GForceLateral); Set(c, "$glong", s.GForceLongitudinal); Set(c, "$gvert", s.GForceVertical); Set(c, "$yaw", s.Yaw); Set(c, "$pitch", s.Pitch); Set(c, "$roll", s.Roll); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareStatusInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO car_status VALUES ($received,$uid,$session,$frame,$player,$car,$me,$bias,$fuel,$fuellaps,$actual,$visual,$age,$ice,$mguk,$ers,$mode,$hmgu,$hmgh,$limit,$deployed)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$player", "$car", "$me", "$bias", "$fuel", "$fuellaps", "$actual", "$visual", "$age", "$ice", "$mguk", "$ers", "$mode", "$hmgu", "$hmgh", "$limit", "$deployed");
        return cmd;
    }
    private static void InsertStatus(SqliteCommand c, CarStatusSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$bias", s.FrontBrakeBias); Set(c, "$fuel", s.FuelInTank); Set(c, "$fuellaps", s.FuelRemainingLaps); Set(c, "$actual", s.ActualTyreCompound); Set(c, "$visual", s.VisualTyreCompound); Set(c, "$age", s.TyresAgeLaps); Set(c, "$ice", s.EnginePowerIce); Set(c, "$mguk", s.EnginePowerMguk); Set(c, "$ers", s.ErsStoreEnergy); Set(c, "$mode", s.ErsDeployMode); Set(c, "$hmgu", s.ErsHarvestedThisLapMguk); Set(c, "$hmgh", s.ErsHarvestedThisLapMguh); Set(c, "$limit", s.ErsHarvestLimitPerLap); Set(c, "$deployed", s.ErsDeployedThisLap); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareDamageInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO car_damage VALUES ($received,$uid,$session,$frame,$player,$car,$me,$wrl,$wrr,$wfl,$wfr,$wavg,$drl,$drr,$dfl,$dfr,$flwing,$frwing,$rear,$floor,$diff,$side)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$player", "$car", "$me", "$wrl", "$wrr", "$wfl", "$wfr", "$wavg", "$drl", "$drr", "$dfl", "$dfr", "$flwing", "$frwing", "$rear", "$floor", "$diff", "$side");
        return cmd;
    }
    private static void InsertDamage(SqliteCommand c, CarDamageSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$car", s.CarIndex); Set(c, "$me", s.IsPlayer ? 1 : 0); Set(c, "$wrl", s.TyreWearRl); Set(c, "$wrr", s.TyreWearRr); Set(c, "$wfl", s.TyreWearFl); Set(c, "$wfr", s.TyreWearFr); Set(c, "$wavg", s.TyreWearAvg); Set(c, "$drl", s.TyreDamageRl); Set(c, "$drr", s.TyreDamageRr); Set(c, "$dfl", s.TyreDamageFl); Set(c, "$dfr", s.TyreDamageFr); Set(c, "$flwing", s.FrontLeftWingDamage); Set(c, "$frwing", s.FrontRightWingDamage); Set(c, "$rear", s.RearWingDamage); Set(c, "$floor", s.FloorDamage); Set(c, "$diff", s.DiffuserDamage); Set(c, "$side", s.SidepodDamage); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareEventInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events VALUES ($received,$uid,$session,$frame,$player,$code,$name,$vehicle,$other,$details)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$player", "$code", "$name", "$vehicle", "$other", "$details");
        return cmd;
    }
    private static void InsertEvent(SqliteCommand c, EventSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$player", s.PlayerCarIndex); Set(c, "$code", s.EventCode); Set(c, "$name", s.EventName); Set(c, "$vehicle", s.VehicleIdx); Set(c, "$other", s.OtherVehicleIdx); Set(c, "$details", s.DetailsJson); c.ExecuteNonQuery();
    }


    private static SqliteCommand PrepareParticipantDebugInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO participants_debug VALUES ($received,$uid,$session,$frame,$size,$active,$rows58,$rows57,$names)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$size", "$active", "$rows58", "$rows57", "$names");
        return cmd;
    }
    private static void InsertParticipantDebug(SqliteCommand c, ParticipantPacketDebug s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$size", s.PacketSizeBytes); Set(c, "$active", s.NumActiveCars); Set(c, "$rows58", s.RowsIf58Bytes); Set(c, "$rows57", s.RowsIf57Bytes); Set(c, "$names", s.FirstNames); c.ExecuteNonQuery();
    }

    private static SqliteCommand PrepareParticipantInsert(SqliteConnection con, SqliteTransaction tx)
    {
        var cmd = con.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO participants VALUES ($received,$uid,$session,$frame,$car,$ai,$driver,$team,$number,$name,$telemetry,$online)";
        AddParams(cmd, "$received", "$uid", "$session", "$frame", "$car", "$ai", "$driver", "$team", "$number", "$name", "$telemetry", "$online");
        return cmd;
    }
    private static void InsertParticipant(SqliteCommand c, ParticipantSample s)
    {
        Set(c, "$received", s.ReceivedAt.ToString("O")); Set(c, "$uid", s.SessionUid.ToString()); Set(c, "$session", s.SessionTime); Set(c, "$frame", s.FrameIdentifier); Set(c, "$car", s.CarIndex); Set(c, "$ai", s.AiControlled); Set(c, "$driver", s.DriverId); Set(c, "$team", s.TeamId); Set(c, "$number", s.RaceNumber); Set(c, "$name", s.Name); Set(c, "$telemetry", s.YourTelemetry); Set(c, "$online", s.ShowOnlineNames); c.ExecuteNonQuery();
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
