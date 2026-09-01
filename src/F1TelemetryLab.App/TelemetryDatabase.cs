using Microsoft.Data.Sqlite;

namespace F1TelemetryLab;

public sealed class TelemetryDatabase : IDisposable
{
    private const int MaxBatchOperations = 750;
    private static readonly TimeSpan MaxBatchAge = TimeSpan.FromMilliseconds(200);

    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insertRaw;
    private readonly SqliteCommand _insertCar;
    private SqliteTransaction? _batchTransaction;
    private DateTimeOffset _batchStartedAt;
    private int _batchOperations;
    private bool _disposed;

    public string Path { get; }

    public TelemetryDatabase(string path)
    {
        SQLitePCL.Batteries_V2.Init();
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        CreateSchema();

        _insertRaw = _connection.CreateCommand();
        _insertRaw.CommandText = """
            INSERT INTO raw_packets(
                received_at, packet_format, game_year, game_major_version, game_minor_version, packet_version,
                packet_id, session_uid, session_time, frame_identifier, overall_frame_identifier,
                player_car_index, secondary_player_car_index, packet_size, payload)
            VALUES (
                $received_at, $packet_format, $game_year, $game_major, $game_minor, $packet_version,
                $packet_id, $session_uid, $session_time, $frame_identifier, $overall_frame_identifier,
                $player_car_index, $secondary_player_car_index, $packet_size, $payload);
            """;
        AddParameter(_insertRaw, "$received_at", SqliteType.Text);
        AddParameter(_insertRaw, "$packet_format", SqliteType.Integer);
        AddParameter(_insertRaw, "$game_year", SqliteType.Integer);
        AddParameter(_insertRaw, "$game_major", SqliteType.Integer);
        AddParameter(_insertRaw, "$game_minor", SqliteType.Integer);
        AddParameter(_insertRaw, "$packet_version", SqliteType.Integer);
        AddParameter(_insertRaw, "$packet_id", SqliteType.Integer);
        AddParameter(_insertRaw, "$session_uid", SqliteType.Text);
        AddParameter(_insertRaw, "$session_time", SqliteType.Real);
        AddParameter(_insertRaw, "$frame_identifier", SqliteType.Integer);
        AddParameter(_insertRaw, "$overall_frame_identifier", SqliteType.Integer);
        AddParameter(_insertRaw, "$player_car_index", SqliteType.Integer);
        AddParameter(_insertRaw, "$secondary_player_car_index", SqliteType.Integer);
        AddParameter(_insertRaw, "$packet_size", SqliteType.Integer);
        AddParameter(_insertRaw, "$payload", SqliteType.Blob);

        _insertCar = _connection.CreateCommand();
        _insertCar.CommandText = """
            INSERT OR REPLACE INTO car_telemetry(
                received_at, session_uid, session_time, frame_identifier, overall_frame_identifier,
                player_car_index, car_idx, is_player, speed, throttle, brake, steer, gear, engine_rpm, drs)
            VALUES (
                $received_at, $session_uid, $session_time, $frame_identifier, $overall_frame_identifier,
                $player_car_index, $car_idx, $is_player, $speed, $throttle, $brake, $steer, $gear, $engine_rpm, $drs);
            """;
        AddParameter(_insertCar, "$received_at", SqliteType.Text);
        AddParameter(_insertCar, "$session_uid", SqliteType.Text);
        AddParameter(_insertCar, "$session_time", SqliteType.Real);
        AddParameter(_insertCar, "$frame_identifier", SqliteType.Integer);
        AddParameter(_insertCar, "$overall_frame_identifier", SqliteType.Integer);
        AddParameter(_insertCar, "$player_car_index", SqliteType.Integer);
        AddParameter(_insertCar, "$car_idx", SqliteType.Integer);
        AddParameter(_insertCar, "$is_player", SqliteType.Integer);
        AddParameter(_insertCar, "$speed", SqliteType.Integer);
        AddParameter(_insertCar, "$throttle", SqliteType.Real);
        AddParameter(_insertCar, "$brake", SqliteType.Real);
        AddParameter(_insertCar, "$steer", SqliteType.Real);
        AddParameter(_insertCar, "$gear", SqliteType.Integer);
        AddParameter(_insertCar, "$engine_rpm", SqliteType.Integer);
        AddParameter(_insertCar, "$drs", SqliteType.Integer);
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS raw_packets(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                received_at TEXT NOT NULL,
                packet_format INTEGER,
                game_year INTEGER,
                game_major_version INTEGER,
                game_minor_version INTEGER,
                packet_version INTEGER,
                packet_id INTEGER,
                session_uid TEXT,
                session_time REAL,
                frame_identifier INTEGER,
                overall_frame_identifier INTEGER,
                player_car_index INTEGER,
                secondary_player_car_index INTEGER,
                packet_size INTEGER NOT NULL,
                payload BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS car_telemetry(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                received_at TEXT NOT NULL,
                session_uid TEXT NOT NULL DEFAULT '',
                session_time REAL,
                frame_identifier INTEGER,
                overall_frame_identifier INTEGER NOT NULL DEFAULT 0,
                player_car_index INTEGER,
                car_idx INTEGER,
                is_player INTEGER,
                speed INTEGER,
                throttle REAL,
                brake REAL,
                steer REAL,
                gear INTEGER,
                engine_rpm INTEGER,
                drs INTEGER,
                UNIQUE(session_uid, overall_frame_identifier, car_idx)
            );

            CREATE TABLE IF NOT EXISTS session_metadata(
                key TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS session_segments(
                session_uid TEXT PRIMARY KEY,
                first_received_at TEXT NOT NULL,
                last_received_at TEXT NOT NULL,
                first_overall_frame INTEGER,
                last_overall_frame INTEGER,
                packet_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS recording_quality(
                id INTEGER PRIMARY KEY CHECK(id = 1),
                packets_received INTEGER NOT NULL,
                car_samples_written INTEGER NOT NULL,
                invalid_headers INTEGER NOT NULL,
                unsupported_packets INTEGER NOT NULL,
                duplicate_frames INTEGER NOT NULL,
                out_of_order_frames INTEGER NOT NULL,
                estimated_missing_frames INTEGER NOT NULL,
                queue_drops INTEGER NOT NULL,
                queue_high_watermark INTEGER NOT NULL,
                session_changes INTEGER NOT NULL,
                rating TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                missing_frames_estimated INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_raw_packets_session_overall
                ON raw_packets(session_uid, overall_frame_identifier, packet_id);
            CREATE INDEX IF NOT EXISTS idx_raw_packets_packet_id
                ON raw_packets(packet_format, packet_id, id);
            CREATE INDEX IF NOT EXISTS idx_car_telemetry_session_overall_car
                ON car_telemetry(session_uid, overall_frame_identifier, car_idx);
            """;
        cmd.ExecuteNonQuery();
        DatabaseSchemaMigrator.Apply(_connection);
    }

    public void SaveMetadata(SessionMetadata metadata)
    {
        Flush();
        SetMeta("schema_version", AppInfo.DatabaseSchemaVersion.ToString());
        SetMeta("app_version", AppInfo.Version);
        SetMeta("session_name", metadata.SessionName);
        SetMeta("track_name", metadata.TrackName);
        SetMeta("track_id", metadata.TrackId.ToString());
        SetMeta("session_type", metadata.SessionType.ToString());
        SetMeta("total_laps", metadata.TotalLaps.ToString());
        SetMeta("track_length_m", metadata.TrackLengthMeters.ToString());
        SetMeta("session_uid", metadata.SessionUid.ToString());
        SetMeta("started_at", metadata.StartedAt.ToString("O"));
        if (metadata.StoppedAt is not null) SetMeta("stopped_at", metadata.StoppedAt.Value.ToString("O"));
    }

    public void SaveQuality(RecordingQualitySnapshot quality)
    {
        Flush();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recording_quality(
                id, packets_received, car_samples_written, invalid_headers, unsupported_packets,
                duplicate_frames, out_of_order_frames, estimated_missing_frames, queue_drops,
                queue_high_watermark, session_changes, rating, updated_at, missing_frames_estimated)
            VALUES (1,$packets,$cars,$invalid,$unsupported,$duplicates,$out_of_order,$missing,$drops,$high,$changes,$rating,$updated,$missingEstimated)
            ON CONFLICT(id) DO UPDATE SET
                packets_received=excluded.packets_received,
                car_samples_written=excluded.car_samples_written,
                invalid_headers=excluded.invalid_headers,
                unsupported_packets=excluded.unsupported_packets,
                duplicate_frames=excluded.duplicate_frames,
                out_of_order_frames=excluded.out_of_order_frames,
                estimated_missing_frames=excluded.estimated_missing_frames,
                queue_drops=excluded.queue_drops,
                queue_high_watermark=excluded.queue_high_watermark,
                session_changes=excluded.session_changes,
                rating=excluded.rating,
                updated_at=excluded.updated_at,
                missing_frames_estimated=excluded.missing_frames_estimated;
            """;
        cmd.Parameters.AddWithValue("$packets", quality.PacketsReceived);
        cmd.Parameters.AddWithValue("$cars", quality.CarSamplesWritten);
        cmd.Parameters.AddWithValue("$invalid", quality.InvalidHeaders);
        cmd.Parameters.AddWithValue("$unsupported", quality.UnsupportedPackets);
        cmd.Parameters.AddWithValue("$duplicates", quality.DuplicateFrames);
        cmd.Parameters.AddWithValue("$out_of_order", quality.OutOfOrderFrames);
        cmd.Parameters.AddWithValue("$missing", quality.EstimatedMissingFrames);
        cmd.Parameters.AddWithValue("$drops", quality.QueueDrops);
        cmd.Parameters.AddWithValue("$high", quality.QueueHighWatermark);
        cmd.Parameters.AddWithValue("$changes", quality.SessionChanges);
        cmd.Parameters.AddWithValue("$rating", quality.Rating);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$missingEstimated", quality.MissingFrameEstimateAvailable ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void InsertErsControlEvent(ErsAuditRecord row)
    {
        EnsureBatch();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _batchTransaction;
        cmd.CommandText = """
            INSERT INTO ers_control_events(
                received_at,lap_num,lap_distance_m,segment,battery_pct,current_mode,target_mode,
                gap_ahead_ms,gap_behind_ms,rule_id,action,reason)
            VALUES($received,$lap,$distance,$segment,$battery,$current,$target,$ahead,$behind,$rule,$action,$reason)
            """;
        cmd.Parameters.AddWithValue("$received", row.ReceivedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$lap", row.LapNumber);
        cmd.Parameters.AddWithValue("$distance", row.LapDistanceM);
        cmd.Parameters.AddWithValue("$segment", row.Segment);
        cmd.Parameters.AddWithValue("$battery", row.BatteryPct);
        cmd.Parameters.AddWithValue("$current", row.CurrentMode);
        cmd.Parameters.AddWithValue("$target", row.TargetMode);
        cmd.Parameters.AddWithValue("$ahead", (object?)row.GapAheadMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$behind", (object?)row.GapBehindMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rule", row.RuleId);
        cmd.Parameters.AddWithValue("$action", row.Action);
        cmd.Parameters.AddWithValue("$reason", row.Reason);
        cmd.ExecuteNonQuery();
        CountOperation();
    }

    public void SaveErsProfileSnapshot(ErsControlProfile profile, ErsAutopilotOptions options)
    {
        EnsureBatch();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _batchTransaction;
        cmd.CommandText = """
            INSERT INTO ers_profile_snapshots(id,captured_at,profile_id,operating_mode,profile_json,app_version)
            VALUES(1,$captured,$profile,$mode,$json,$version)
            ON CONFLICT(id) DO UPDATE SET captured_at=excluded.captured_at,profile_id=excluded.profile_id,
                operating_mode=excluded.operating_mode,profile_json=excluded.profile_json,app_version=excluded.app_version
            """;
        cmd.Parameters.AddWithValue("$captured", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$profile", profile.ProfileId);
        cmd.Parameters.AddWithValue("$mode", ErsAutopilotOptions.ToSettingValue(options.OperatingMode));
        cmd.Parameters.AddWithValue("$json", ErsProfileStore.CreateSessionSnapshotJson(profile, options));
        cmd.Parameters.AddWithValue("$version", AppInfo.Version);
        cmd.ExecuteNonQuery();
        CountOperation();
    }

    public void SaveRaceProfileSnapshot(RaceEngineerProfile profile)
    {
        EnsureBatch();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _batchTransaction;
        cmd.CommandText = """
            INSERT INTO race_profile_snapshots(id,captured_at,profile_id,track_id,profile_json,app_version)
            VALUES(1,$captured,$profile,$track,$json,$version)
            ON CONFLICT(id) DO UPDATE SET captured_at=excluded.captured_at,profile_id=excluded.profile_id,
                track_id=excluded.track_id,profile_json=excluded.profile_json,app_version=excluded.app_version
            """;
        cmd.Parameters.AddWithValue("$captured", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$profile", profile.ProfileId);
        cmd.Parameters.AddWithValue("$track", profile.TrackId);
        cmd.Parameters.AddWithValue("$json", RaceEngineerProfileStore.SerializeSnapshot(profile));
        cmd.Parameters.AddWithValue("$version", AppInfo.Version);
        cmd.ExecuteNonQuery();
        CountOperation();
    }

    public void InsertRaceEngineerLap(CompletedLiveLap row)
    {
        EnsureBatch();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _batchTransaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO race_engineer_laps(
                session_uid,lap_num,lap_time_ms,clean_lap,pit_lap,safety_car_affected,visual_compound,
                tyre_age_laps,tyre_wear_start_pct,tyre_wear_end_pct,tyre_wear_delta_pct,
                ers_start_pct,ers_end_pct,ers_delta_pct,position_end,completion_evidence)
            VALUES($uid,$lap,$time,$clean,$pit,$sc,$compound,$age,$wearStart,$wearEnd,$wearDelta,
                $ersStart,$ersEnd,$ersDelta,$position,$evidence)
            """;
        cmd.Parameters.AddWithValue("$uid", row.SessionUid.ToString());
        cmd.Parameters.AddWithValue("$lap", row.LapNumber);
        cmd.Parameters.AddWithValue("$time", row.LapTimeMs);
        cmd.Parameters.AddWithValue("$clean", row.Clean ? 1 : 0);
        cmd.Parameters.AddWithValue("$pit", row.PitLap ? 1 : 0);
        cmd.Parameters.AddWithValue("$sc", row.SafetyCarAffected ? 1 : 0);
        cmd.Parameters.AddWithValue("$compound", row.VisualCompound);
        cmd.Parameters.AddWithValue("$age", row.TyreAgeLaps);
        cmd.Parameters.AddWithValue("$wearStart", row.TyreWearStartPct);
        cmd.Parameters.AddWithValue("$wearEnd", row.TyreWearEndPct);
        cmd.Parameters.AddWithValue("$wearDelta", row.TyreWearDeltaPct);
        cmd.Parameters.AddWithValue("$ersStart", row.ErsStartPct);
        cmd.Parameters.AddWithValue("$ersEnd", row.ErsEndPct);
        cmd.Parameters.AddWithValue("$ersDelta", row.ErsDeltaPct);
        cmd.Parameters.AddWithValue("$position", row.PositionEnd);
        cmd.Parameters.AddWithValue("$evidence", row.CompletionEvidence);
        cmd.ExecuteNonQuery();
        CountOperation();
    }

    public void InsertRaw(DateTimeOffset receivedAt, PacketHeader? header, byte[] payload)
    {
        EnsureBatch();
        _insertRaw.Parameters["$received_at"].Value = receivedAt.ToString("O");
        _insertRaw.Parameters["$packet_format"].Value = header?.PacketFormat ?? 0;
        _insertRaw.Parameters["$game_year"].Value = header?.GameYear ?? 0;
        _insertRaw.Parameters["$game_major"].Value = header?.GameMajorVersion ?? 0;
        _insertRaw.Parameters["$game_minor"].Value = header?.GameMinorVersion ?? 0;
        _insertRaw.Parameters["$packet_version"].Value = header?.PacketVersion ?? 0;
        _insertRaw.Parameters["$packet_id"].Value = header?.PacketId ?? 255;
        _insertRaw.Parameters["$session_uid"].Value = header?.SessionUid.ToString() ?? "";
        _insertRaw.Parameters["$session_time"].Value = CleanDbValue(header?.SessionTime ?? 0);
        _insertRaw.Parameters["$frame_identifier"].Value = header?.FrameIdentifier ?? 0;
        _insertRaw.Parameters["$overall_frame_identifier"].Value = header?.OverallFrameIdentifier ?? 0;
        _insertRaw.Parameters["$player_car_index"].Value = header?.PlayerCarIndex ?? 255;
        _insertRaw.Parameters["$secondary_player_car_index"].Value = header?.SecondaryPlayerCarIndex ?? 255;
        _insertRaw.Parameters["$packet_size"].Value = payload.Length;
        _insertRaw.Parameters["$payload"].Value = payload;
        _insertRaw.ExecuteNonQuery();
        CountOperation();

        if (header is not null) UpsertSessionSegment(receivedAt, header);
    }

    public void InsertCarTelemetry(CarTelemetrySample s)
    {
        EnsureBatch();
        _insertCar.Parameters["$received_at"].Value = s.ReceivedAt.ToString("O");
        _insertCar.Parameters["$session_uid"].Value = s.SessionUid.ToString();
        _insertCar.Parameters["$session_time"].Value = CleanDbValue(s.SessionTime);
        _insertCar.Parameters["$frame_identifier"].Value = s.FrameIdentifier;
        _insertCar.Parameters["$overall_frame_identifier"].Value = s.OverallFrameIdentifier;
        _insertCar.Parameters["$player_car_index"].Value = s.PlayerCarIndex;
        _insertCar.Parameters["$car_idx"].Value = s.CarIndex;
        _insertCar.Parameters["$is_player"].Value = s.IsPlayer ? 1 : 0;
        _insertCar.Parameters["$speed"].Value = s.Speed;
        _insertCar.Parameters["$throttle"].Value = CleanDbValue(s.Throttle);
        _insertCar.Parameters["$brake"].Value = CleanDbValue(s.Brake);
        _insertCar.Parameters["$steer"].Value = CleanDbValue(s.Steer);
        _insertCar.Parameters["$gear"].Value = s.Gear;
        _insertCar.Parameters["$engine_rpm"].Value = s.EngineRpm;
        _insertCar.Parameters["$drs"].Value = s.Drs;
        _insertCar.ExecuteNonQuery();
        CountOperation();
    }

    public void Flush()
    {
        if (_batchTransaction is null) return;
        _batchTransaction.Commit();
        _batchTransaction.Dispose();
        _batchTransaction = null;
        _insertRaw.Transaction = null;
        _insertCar.Transaction = null;
        _batchOperations = 0;
    }

    private void EnsureBatch()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_batchTransaction is not null) return;
        _batchTransaction = _connection.BeginTransaction();
        _insertRaw.Transaction = _batchTransaction;
        _insertCar.Transaction = _batchTransaction;
        _batchStartedAt = DateTimeOffset.UtcNow;
        _batchOperations = 0;
    }

    private void CountOperation()
    {
        _batchOperations++;
        if (_batchOperations >= MaxBatchOperations || DateTimeOffset.UtcNow - _batchStartedAt >= MaxBatchAge)
            Flush();
    }

    private void UpsertSessionSegment(DateTimeOffset receivedAt, PacketHeader header)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _batchTransaction;
        cmd.CommandText = """
            INSERT INTO session_segments(session_uid, first_received_at, last_received_at, first_overall_frame, last_overall_frame, packet_count)
            VALUES ($uid,$received,$received,$overall,$overall,1)
            ON CONFLICT(session_uid) DO UPDATE SET
                last_received_at=excluded.last_received_at,
                last_overall_frame=CASE
                    WHEN excluded.last_overall_frame > 0
                    THEN MAX(session_segments.last_overall_frame, excluded.last_overall_frame)
                    ELSE session_segments.last_overall_frame
                END,
                packet_count=session_segments.packet_count + 1;
            """;
        cmd.Parameters.AddWithValue("$uid", header.SessionUid.ToString());
        cmd.Parameters.AddWithValue("$received", receivedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$overall", header.OverallFrameIdentifier);
        cmd.ExecuteNonQuery();
        CountOperation();
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO session_metadata(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, SqliteType type) => command.Parameters.Add(name, type);

    private static object CleanDbValue(object? value)
    {
        if (value is null) return DBNull.Value;
        if (value is float f && (float.IsNaN(f) || float.IsInfinity(f))) return DBNull.Value;
        if (value is double d && (double.IsNaN(d) || double.IsInfinity(d))) return DBNull.Value;
        return value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Flush(); } catch { }
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { }
        _insertRaw.Dispose();
        _insertCar.Dispose();
        _connection.Dispose();
    }
}
