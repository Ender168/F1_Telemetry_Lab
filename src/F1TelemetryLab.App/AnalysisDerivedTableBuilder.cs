using Microsoft.Data.Sqlite;

namespace F1TelemetryLab;

/// <summary>
/// Builds the query-friendly projection of a recorded session. Every join uses the
/// logical session UID and the monotonic overall frame identifier. This is the key
/// invariant that keeps flashbacks and a second session in the same file from
/// cross-joining unrelated samples.
/// </summary>
internal static class AnalysisDerivedTableBuilder
{
    public static void Build(SqliteConnection con)
    {
        TryAddColumn(con, "driver_aliases", "short_name", "TEXT");
        BuildContext(con);
        BuildAnalysisSamples(con);
        BuildLapSummary(con);
        BuildLapStateSummary(con);
        BuildTrace(con);
        BuildClassification(con);
        BuildIndexes(con);
    }

    private static void BuildContext(SqliteConnection con)
    {
        Execute(con, "DROP TABLE IF EXISTS analysis_context;");
        Execute(con, """
        CREATE TABLE analysis_context AS
        SELECT CAST(session_uid AS TEXT) AS active_session_uid,
               received_at AS selected_at
        FROM raw_packets
        WHERE packet_format = 2026
          AND packet_id = 2
          AND session_uid IS NOT NULL
          AND CAST(session_uid AS TEXT) NOT IN ('', '0')
        ORDER BY id DESC
        LIMIT 1;
        """);
    }

    private static void BuildAnalysisSamples(SqliteConnection con)
    {
        Execute(con, "DROP TABLE IF EXISTS analysis_samples;");
        Execute(con, """
        CREATE TABLE analysis_samples AS
        WITH
        scoped_laps AS (
            SELECT l.*,
                   q.lap_state,
                   q.clean_lap,
                   q.rewind_count,
                   q.invalid_count,
                   q.lap_time_ms AS confirmed_lap_time_ms,
                   q.sector1_ms AS confirmed_sector1_ms,
                   q.sector2_ms AS confirmed_sector2_ms,
                   q.sector3_ms AS confirmed_sector3_ms,
                   q.active_from_overall_frame,
                   ROW_NUMBER() OVER (
                       PARTITION BY l.session_uid, l.overall_frame_identifier, l.car_idx
                       ORDER BY l.received_at DESC
                   ) AS rn
            FROM lap_data l
            JOIN analysis_context c ON c.active_session_uid = l.session_uid
            JOIN lap_quality q
              ON q.session_uid = l.session_uid
             AND q.car_idx = l.car_idx
             AND q.lap_num = l.lap_num
             AND l.overall_frame_identifier >= q.active_from_overall_frame
            WHERE l.lap_num > 0
        ),
        scoped_telemetry AS (
            SELECT t.*,
                   ROW_NUMBER() OVER (
                       PARTITION BY t.session_uid, t.overall_frame_identifier, t.car_idx
                       ORDER BY t.received_at DESC
                   ) AS rn
            FROM car_telemetry t
            JOIN analysis_context c ON c.active_session_uid = t.session_uid
        ),
        scoped_motion AS (
            SELECT m.*,
                   ROW_NUMBER() OVER (
                       PARTITION BY m.session_uid, m.overall_frame_identifier, m.car_idx
                       ORDER BY m.received_at DESC
                   ) AS rn
            FROM motion_data m
            JOIN analysis_context c ON c.active_session_uid = m.session_uid
        )
        SELECT
            l.received_at,
            l.session_uid,
            l.session_time,
            l.frame_identifier,
            l.overall_frame_identifier,
            l.car_idx,
            l.is_player,
            l.lap_num,
            l.lap_distance,
            l.total_distance,
            l.position,
            l.sector,
            l.lap_invalid,
            l.current_lap_time_ms,
            l.last_lap_time_ms,
            l.sector1_time_ms,
            l.sector2_time_ms,
            l.pit_status,
            l.num_pit_stops,
            l.penalties,
            l.warnings,
            l.driver_status,
            l.result_status,
            t.speed,
            t.throttle,
            t.brake,
            t.steer,
            t.gear,
            t.engine_rpm,
            t.drs,
            m.world_position_x,
            m.world_position_y,
            m.world_position_z,
            m.yaw,
            m.g_force_lateral,
            m.g_force_longitudinal,
            l.lap_state,
            l.clean_lap,
            l.rewind_count,
            l.invalid_count,
            l.confirmed_lap_time_ms,
            l.confirmed_sector1_ms,
            l.confirmed_sector2_ms,
            l.confirmed_sector3_ms,
            l.active_from_overall_frame
        FROM scoped_laps l
        LEFT JOIN scoped_telemetry t
          ON t.session_uid = l.session_uid
         AND t.overall_frame_identifier = l.overall_frame_identifier
         AND t.car_idx = l.car_idx
         AND t.rn = 1
        LEFT JOIN scoped_motion m
          ON m.session_uid = l.session_uid
         AND m.overall_frame_identifier = l.overall_frame_identifier
         AND m.car_idx = l.car_idx
         AND m.rn = 1
        WHERE l.rn = 1;
        """);
    }

    private static void BuildLapSummary(SqliteConnection con)
    {
        Execute(con, "DROP TABLE IF EXISTS lap_summary;");
        Execute(con, """
        CREATE TABLE lap_summary AS
        SELECT
            session_uid,
            car_idx,
            lap_num,
            MAX(is_player) AS is_player,
            MAX(position) AS best_position_seen,
            MAX(clean_lap) AS clean_lap,
            MAX(rewind_count) AS rewind_count,
            MAX(invalid_count) AS invalid_count,
            MAX(lap_state) AS lap_state,
            COUNT(*) AS sample_count,
            MIN(lap_distance) AS min_distance,
            MAX(lap_distance) AS max_distance,
            MAX(confirmed_lap_time_ms) AS lap_time_ms,
            MAX(confirmed_sector1_ms) AS sector1_ms,
            MAX(confirmed_sector2_ms) AS sector2_ms,
            MAX(confirmed_sector3_ms) AS sector3_ms,
            MAX(speed) AS max_speed,
            AVG(speed) AS avg_speed,
            MIN(speed) AS min_speed,
            AVG(throttle) AS avg_throttle,
            AVG(brake) AS avg_brake,
            MAX(penalties) - MIN(penalties) AS penalties,
            MAX(warnings) - MIN(warnings) AS warnings,
            MAX(penalties) AS penalties_total_end,
            MAX(warnings) AS warnings_total_end
        FROM analysis_samples
        WHERE lap_num > 0
        GROUP BY session_uid, car_idx, lap_num;
        """);
    }

    private static void BuildLapStateSummary(SqliteConnection con)
    {
        Execute(con, "DROP TABLE IF EXISTS lap_state_summary;");
        Execute(con, """
        CREATE TABLE lap_state_summary AS
        WITH
        lap_ranked AS (
            SELECT a.*,
                   ROW_NUMBER() OVER (
                       PARTITION BY a.session_uid, a.car_idx, a.lap_num
                       ORDER BY a.overall_frame_identifier ASC, a.received_at ASC
                   ) AS rn_start,
                   ROW_NUMBER() OVER (
                       PARTITION BY a.session_uid, a.car_idx, a.lap_num
                       ORDER BY a.overall_frame_identifier DESC, a.received_at DESC
                   ) AS rn_end
            FROM analysis_samples a
            WHERE a.lap_num > 0
        ),
        lap_start AS (SELECT * FROM lap_ranked WHERE rn_start = 1),
        lap_end AS (SELECT * FROM lap_ranked WHERE rn_end = 1),
        lap_agg AS (
            SELECT session_uid, car_idx, lap_num,
                   MAX(pit_status) AS pit_status_max,
                   MIN(num_pit_stops) AS pit_stops_start,
                   MAX(num_pit_stops) AS pit_stops_end,
                   MAX(lap_invalid) AS lap_invalid,
                   MAX(warnings) - MIN(warnings) AS warnings_delta,
                   MAX(penalties) - MIN(penalties) AS penalties_delta,
                   MAX(warnings) AS warnings_total_end,
                   MAX(penalties) AS penalties_total_end
            FROM analysis_samples
            WHERE lap_num > 0
            GROUP BY session_uid, car_idx, lap_num
        ),
        lap_frames AS (
            SELECT DISTINCT session_uid, overall_frame_identifier, car_idx, lap_num
            FROM analysis_samples
        ),
        status_tagged AS (
            SELECT s.*, f.lap_num
            FROM car_status s
            JOIN lap_frames f
              ON f.session_uid = s.session_uid
             AND f.overall_frame_identifier = s.overall_frame_identifier
             AND f.car_idx = s.car_idx
        ),
        status_ranked AS (
            SELECT s.*,
                   ROW_NUMBER() OVER (
                       PARTITION BY s.session_uid, s.car_idx, s.lap_num
                       ORDER BY s.overall_frame_identifier ASC, s.received_at ASC
                   ) AS rn_start,
                   ROW_NUMBER() OVER (
                       PARTITION BY s.session_uid, s.car_idx, s.lap_num
                       ORDER BY s.overall_frame_identifier DESC, s.received_at DESC
                   ) AS rn_end
            FROM status_tagged s
        ),
        status_start AS (SELECT * FROM status_ranked WHERE rn_start = 1),
        status_end AS (SELECT * FROM status_ranked WHERE rn_end = 1),
        status_agg AS (
            SELECT session_uid, car_idx, lap_num,
                   MIN(ers_store_energy) AS ers_min,
                   MAX(ers_store_energy) AS ers_max,
                   MAX(ers_deployed_this_lap) AS ers_deployed_this_lap,
                   MAX(ers_harvested_this_lap_mguk) AS ers_harvest_mguk_this_lap,
                   MAX(ers_harvested_this_lap_mguh) AS ers_harvest_mguh_this_lap
            FROM status_tagged
            GROUP BY session_uid, car_idx, lap_num
        ),
        damage_tagged AS (
            SELECT d.*, f.lap_num
            FROM car_damage d
            JOIN lap_frames f
              ON f.session_uid = d.session_uid
             AND f.overall_frame_identifier = d.overall_frame_identifier
             AND f.car_idx = d.car_idx
        ),
        damage_ranked AS (
            SELECT d.*,
                   ROW_NUMBER() OVER (
                       PARTITION BY d.session_uid, d.car_idx, d.lap_num
                       ORDER BY d.overall_frame_identifier ASC, d.received_at ASC
                   ) AS rn_start,
                   ROW_NUMBER() OVER (
                       PARTITION BY d.session_uid, d.car_idx, d.lap_num
                       ORDER BY d.overall_frame_identifier DESC, d.received_at DESC
                   ) AS rn_end
            FROM damage_tagged d
        ),
        damage_start AS (SELECT * FROM damage_ranked WHERE rn_start = 1),
        damage_end AS (SELECT * FROM damage_ranked WHERE rn_end = 1),
        telemetry_agg AS (
            SELECT session_uid, car_idx, lap_num,
                   MAX(speed) AS max_speed,
                   AVG(speed) AS avg_speed,
                   100.0 * AVG(CASE WHEN throttle >= 0.98 THEN 1.0 ELSE 0.0 END) AS full_throttle_pct,
                   100.0 * AVG(CASE WHEN brake >= 0.05 THEN 1.0 ELSE 0.0 END) AS brake_pct,
                   100.0 * AVG(CASE WHEN drs > 0 THEN 1.0 ELSE 0.0 END) AS drs_pct
            FROM analysis_samples
            WHERE lap_num > 0
            GROUP BY session_uid, car_idx, lap_num
        )
        SELECT
            ls.session_uid,
            ls.car_idx,
            ls.lap_num,
            ls.is_player,
            ls.clean_lap,
            ls.rewind_count,
            ls.invalid_count,
            ls.lap_state,
            COALESCE(la.lap_invalid, 0) AS lap_invalid,
            ls.lap_time_ms,
            ls.sector1_ms,
            ls.sector2_ms,
            ls.sector3_ms,
            COALESCE(l0.position, 0) AS position_start,
            COALESCE(le.position, 0) AS position_end,
            COALESCE(la.warnings_delta, 0) AS warnings,
            COALESCE(la.penalties_delta, 0) AS penalties,
            COALESCE(la.warnings_total_end, 0) AS warnings_total_end,
            COALESCE(la.penalties_total_end, 0) AS penalties_total_end,
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
            CASE
                WHEN ss.fuel_in_tank IS NOT NULL AND se.fuel_in_tank IS NOT NULL
                THEN MAX(ss.fuel_in_tank - se.fuel_in_tank, 0.0)
                ELSE NULL
            END AS fuel_used,
            se.fuel_remaining_laps AS fuel_remaining_laps_end,
            ss.ers_store_energy AS ers_start,
            se.ers_store_energy AS ers_end,
            sa.ers_min,
            sa.ers_max,
            CASE
                WHEN ss.ers_store_energy IS NOT NULL AND se.ers_store_energy IS NOT NULL
                THEN se.ers_store_energy - ss.ers_store_energy
                ELSE NULL
            END AS ers_delta,
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
                0,
                COALESCE(de.front_left_wing_damage, 0) - COALESCE(ds.front_left_wing_damage, 0),
                COALESCE(de.front_right_wing_damage, 0) - COALESCE(ds.front_right_wing_damage, 0),
                COALESCE(de.rear_wing_damage, 0) - COALESCE(ds.rear_wing_damage, 0),
                COALESCE(de.floor_damage, 0) - COALESCE(ds.floor_damage, 0),
                COALESCE(de.diffuser_damage, 0) - COALESCE(ds.diffuser_damage, 0),
                COALESCE(de.sidepod_damage, 0) - COALESCE(ds.sidepod_damage, 0)
            ) AS damage_delta_max,
            MAX(
                0,
                COALESCE(ds.front_left_wing_damage, 0) - COALESCE(de.front_left_wing_damage, 0),
                COALESCE(ds.front_right_wing_damage, 0) - COALESCE(de.front_right_wing_damage, 0),
                COALESCE(ds.rear_wing_damage, 0) - COALESCE(de.rear_wing_damage, 0),
                COALESCE(ds.floor_damage, 0) - COALESCE(de.floor_damage, 0),
                COALESCE(ds.diffuser_damage, 0) - COALESCE(de.diffuser_damage, 0),
                COALESCE(ds.sidepod_damage, 0) - COALESCE(de.sidepod_damage, 0)
            ) AS repair_delta_max,
            ta.max_speed,
            ta.avg_speed,
            ta.full_throttle_pct,
            ta.brake_pct,
            ta.drs_pct
        FROM lap_summary ls
        LEFT JOIN lap_start l0
          ON l0.session_uid = ls.session_uid AND l0.car_idx = ls.car_idx AND l0.lap_num = ls.lap_num
        LEFT JOIN lap_end le
          ON le.session_uid = ls.session_uid AND le.car_idx = ls.car_idx AND le.lap_num = ls.lap_num
        LEFT JOIN lap_agg la
          ON la.session_uid = ls.session_uid AND la.car_idx = ls.car_idx AND la.lap_num = ls.lap_num
        LEFT JOIN status_start ss
          ON ss.session_uid = ls.session_uid AND ss.car_idx = ls.car_idx AND ss.lap_num = ls.lap_num
        LEFT JOIN status_end se
          ON se.session_uid = ls.session_uid AND se.car_idx = ls.car_idx AND se.lap_num = ls.lap_num
        LEFT JOIN status_agg sa
          ON sa.session_uid = ls.session_uid AND sa.car_idx = ls.car_idx AND sa.lap_num = ls.lap_num
        LEFT JOIN damage_start ds
          ON ds.session_uid = ls.session_uid AND ds.car_idx = ls.car_idx AND ds.lap_num = ls.lap_num
        LEFT JOIN damage_end de
          ON de.session_uid = ls.session_uid AND de.car_idx = ls.car_idx AND de.lap_num = ls.lap_num
        LEFT JOIN telemetry_agg ta
          ON ta.session_uid = ls.session_uid AND ta.car_idx = ls.car_idx AND ta.lap_num = ls.lap_num
        WHERE ls.lap_num > 0;
        """);
    }

    private static void BuildTrace(SqliteConnection con)
    {
        Execute(con, "DROP TABLE IF EXISTS analysis_trace_10m;");
        Execute(con, """
        CREATE TABLE analysis_trace_10m AS
        SELECT
            session_uid,
            car_idx,
            lap_num,
            MAX(is_player) AS is_player,
            MAX(clean_lap) AS clean_lap,
            CAST(lap_distance / 10 AS INTEGER) * 10 AS distance_bin_m,
            AVG(current_lap_time_ms) AS time_ms,
            AVG(speed) AS speed,
            AVG(throttle) AS throttle,
            AVG(brake) AS brake,
            AVG(steer) AS steer,
            AVG(gear) AS gear,
            AVG(world_position_x) AS world_position_x,
            AVG(world_position_z) AS world_position_z,
            AVG(yaw) AS yaw,
            AVG(g_force_lateral) AS g_force_lateral,
            AVG(g_force_longitudinal) AS g_force_longitudinal
        FROM analysis_samples
        WHERE lap_num > 0
          AND lap_distance >= 0
          AND NOT (current_lap_time_ms <= 100 AND lap_distance > 200)
        GROUP BY session_uid, car_idx, lap_num, distance_bin_m;
        """);
    }

    private static void BuildClassification(SqliteConnection con)
    {
        Execute(con, "DROP TABLE IF EXISTS final_classification;");
        Execute(con, """
        CREATE TABLE final_classification AS
        WITH
        participant_ranked AS (
            SELECT p.*,
                   ROW_NUMBER() OVER (
                       PARTITION BY p.session_uid, p.car_idx
                       ORDER BY p.overall_frame_identifier DESC, p.received_at DESC
                   ) AS rn
            FROM participants p
            JOIN analysis_context c ON c.active_session_uid = p.session_uid
        ),
        latest_names AS (
            SELECT * FROM participant_ranked WHERE rn = 1
        ),
        lap_ranked AS (
            SELECT a.*,
                   ROW_NUMBER() OVER (
                       PARTITION BY a.session_uid, a.car_idx
                       ORDER BY a.overall_frame_identifier DESC, a.received_at DESC
                   ) AS rn
            FROM analysis_samples a
        ),
        latest_lap AS (
            SELECT * FROM lap_ranked WHERE rn = 1
        ),
        final_ranked AS (
            SELECT f.*,
                   DENSE_RANK() OVER (ORDER BY f.overall_frame_identifier DESC, f.received_at DESC) AS packet_rank,
                   ROW_NUMBER() OVER (
                       PARTITION BY f.session_uid, f.overall_frame_identifier, f.car_idx
                       ORDER BY f.received_at DESC
                   ) AS car_rank
            FROM final_classification_packet f
            JOIN analysis_context c ON c.active_session_uid = f.session_uid
        ),
        official AS (
            SELECT * FROM final_ranked WHERE packet_rank = 1 AND car_rank = 1
        ),
        best_laps AS (
            SELECT s.session_uid, s.car_idx, MIN(s.lap_time_ms) AS best_lap_ms
            FROM lap_summary s
            JOIN lap_state_summary state
              ON state.session_uid = s.session_uid
             AND state.car_idx = s.car_idx
             AND state.lap_num = s.lap_num
            WHERE s.clean_lap = 1 AND state.pit_this_lap = 0 AND s.lap_time_ms > 0
            GROUP BY s.session_uid, s.car_idx
        ),
        official_rows AS (
            SELECT
                f.position,
                f.car_idx,
                f.is_player,
                COALESCE(n.name, CASE WHEN f.is_player = 1 THEN 'YOU' ELSE 'F1 Generic' END) AS name,
                COALESCE(n.name, CASE WHEN f.is_player = 1 THEN 'YOU' ELSE 'F1 Generic' END) AS original_name,
                COALESCE(NULLIF(a.display_name, ''), COALESCE(n.name, CASE WHEN f.is_player = 1 THEN 'YOU' ELSE 'F1 Generic' END)) AS display_name,
                COALESCE(NULLIF(a.short_name, ''), CASE WHEN f.is_player = 1 THEN 'YOU' ELSE printf('C%02d', f.car_idx) END) AS short_name,
                COALESCE(n.ai_controlled, 0) AS ai_controlled,
                COALESCE(n.driver_id, -1) AS driver_id,
                COALESCE(n.team_id, -1) AS team_id,
                COALESCE(n.race_number, -1) AS race_number,
                f.num_laps AS lap_num,
                COALESCE(l.last_lap_time_ms, 0) AS last_lap_time_ms,
                CASE WHEN f.best_lap_time_ms > 0 THEN f.best_lap_time_ms ELSE COALESCE(b.best_lap_ms, 0) END AS best_lap_ms,
                f.penalties_time_seconds AS penalties,
                COALESCE(l.warnings, 0) AS warnings,
                f.result_status,
                COALESCE(l.driver_status, 0) AS driver_status,
                'official_udp' AS classification_source,
                f.grid_position,
                f.points,
                f.num_pit_stops,
                f.total_race_time_seconds,
                f.num_penalties,
                f.num_tyre_stints,
                f.result_reason
            FROM official f
            LEFT JOIN latest_names n ON n.session_uid = f.session_uid AND n.car_idx = f.car_idx
            LEFT JOIN latest_lap l ON l.session_uid = f.session_uid AND l.car_idx = f.car_idx
            LEFT JOIN driver_aliases a ON a.car_idx = f.car_idx
            LEFT JOIN best_laps b ON b.session_uid = f.session_uid AND b.car_idx = f.car_idx
        ),
        provisional_rows AS (
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
                l.driver_status,
                'provisional_lap_data' AS classification_source,
                NULL AS grid_position,
                NULL AS points,
                l.num_pit_stops,
                NULL AS total_race_time_seconds,
                NULL AS num_penalties,
                NULL AS num_tyre_stints,
                NULL AS result_reason
            FROM latest_lap l
            LEFT JOIN latest_names n ON n.session_uid = l.session_uid AND n.car_idx = l.car_idx
            LEFT JOIN driver_aliases a ON a.car_idx = l.car_idx
            LEFT JOIN best_laps b ON b.session_uid = l.session_uid AND b.car_idx = l.car_idx
            WHERE l.position > 0 AND NOT EXISTS (SELECT 1 FROM official)
        )
        SELECT * FROM official_rows
        UNION ALL
        SELECT * FROM provisional_rows;
        """);
    }

    private static void BuildIndexes(SqliteConnection con)
    {
        Execute(con, "CREATE INDEX IF NOT EXISTS idx_analysis_samples_car_lap_dist ON analysis_samples(car_idx, lap_num, lap_distance);");
        Execute(con, "CREATE INDEX IF NOT EXISTS idx_analysis_trace_car_lap_dist ON analysis_trace_10m(car_idx, lap_num, distance_bin_m);");
        Execute(con, "CREATE INDEX IF NOT EXISTS idx_lap_summary_clean_time ON lap_summary(clean_lap, lap_time_ms);");
        Execute(con, "CREATE INDEX IF NOT EXISTS idx_lap_state_summary_car_lap ON lap_state_summary(car_idx, lap_num);");
    }

    private static void TryAddColumn(SqliteConnection con, string table, string column, string type)
    {
        using var info = con.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        Execute(con, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private static void Execute(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
