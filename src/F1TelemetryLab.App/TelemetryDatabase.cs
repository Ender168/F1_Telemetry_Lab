using Microsoft.Data.Sqlite;

namespace F1TelemetryLab;

public sealed class TelemetryDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insertRaw;
    private readonly SqliteCommand _insertCar;
    private int _pending;

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
            INSERT INTO raw_packets(received_at, packet_format, packet_id, session_uid, session_time, frame_identifier, player_car_index, packet_size, payload)
            VALUES ($received_at, $packet_format, $packet_id, $session_uid, $session_time, $frame_identifier, $player_car_index, $packet_size, $payload);
            """;
        _insertRaw.Parameters.Add("$received_at", SqliteType.Text);
        _insertRaw.Parameters.Add("$packet_format", SqliteType.Integer);
        _insertRaw.Parameters.Add("$packet_id", SqliteType.Integer);
        _insertRaw.Parameters.Add("$session_uid", SqliteType.Text);
        _insertRaw.Parameters.Add("$session_time", SqliteType.Real);
        _insertRaw.Parameters.Add("$frame_identifier", SqliteType.Integer);
        _insertRaw.Parameters.Add("$player_car_index", SqliteType.Integer);
        _insertRaw.Parameters.Add("$packet_size", SqliteType.Integer);
        _insertRaw.Parameters.Add("$payload", SqliteType.Blob);

        _insertCar = _connection.CreateCommand();
        _insertCar.CommandText = """
            INSERT INTO car_telemetry(received_at, session_uid, session_time, frame_identifier, player_car_index, car_idx, is_player, speed, throttle, brake, steer, gear, engine_rpm, drs)
            VALUES ($received_at, $session_uid, $session_time, $frame_identifier, $player_car_index, $car_idx, $is_player, $speed, $throttle, $brake, $steer, $gear, $engine_rpm, $drs);
            """;
        foreach (var name in new[] { "$received_at", "$session_uid", "$session_time", "$frame_identifier", "$player_car_index", "$car_idx", "$is_player", "$speed", "$throttle", "$brake", "$steer", "$gear", "$engine_rpm", "$drs" })
            _insertCar.Parameters.Add(name, SqliteType.Text);
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS raw_packets(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                received_at TEXT NOT NULL,
                packet_format INTEGER,
                packet_id INTEGER,
                session_uid TEXT,
                session_time REAL,
                frame_identifier INTEGER,
                player_car_index INTEGER,
                packet_size INTEGER NOT NULL,
                payload BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS car_telemetry(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                received_at TEXT NOT NULL,
                session_uid TEXT,
                session_time REAL,
                frame_identifier INTEGER,
                player_car_index INTEGER,
                car_idx INTEGER,
                is_player INTEGER,
                speed INTEGER,
                throttle REAL,
                brake REAL,
                steer REAL,
                gear INTEGER,
                engine_rpm INTEGER,
                drs INTEGER
            );

            CREATE TABLE IF NOT EXISTS session_metadata(
                key TEXT PRIMARY KEY,
                value TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveMetadata(SessionMetadata metadata)
    {
        SetMeta("session_name", metadata.SessionName);
        SetMeta("track_name", metadata.TrackName);
        SetMeta("track_id", metadata.TrackId.ToString());
        SetMeta("session_type", metadata.SessionType.ToString());
        SetMeta("total_laps", metadata.TotalLaps.ToString());
        SetMeta("track_length_m", metadata.TrackLengthMeters.ToString());
        SetMeta("started_at", metadata.StartedAt.ToString("O"));
        if (metadata.StoppedAt is not null) SetMeta("stopped_at", metadata.StoppedAt.Value.ToString("O"));
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO session_metadata(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public void InsertRaw(DateTimeOffset receivedAt, PacketHeader? header, byte[] payload)
    {
        _insertRaw.Parameters["$received_at"].Value = receivedAt.ToString("O");
        _insertRaw.Parameters["$packet_format"].Value = header?.PacketFormat ?? 0;
        _insertRaw.Parameters["$packet_id"].Value = header?.PacketId ?? 255;
        _insertRaw.Parameters["$session_uid"].Value = header?.SessionUid.ToString() ?? "";
        _insertRaw.Parameters["$session_time"].Value = CleanDbValue(header?.SessionTime ?? 0);
        _insertRaw.Parameters["$frame_identifier"].Value = header?.FrameIdentifier ?? 0;
        _insertRaw.Parameters["$player_car_index"].Value = header?.PlayerCarIndex ?? 255;
        _insertRaw.Parameters["$packet_size"].Value = payload.Length;
        _insertRaw.Parameters["$payload"].Value = payload;
        _insertRaw.ExecuteNonQuery();
        MaybeFlush();
    }

    public void InsertCarTelemetry(CarTelemetrySample s)
    {
        _insertCar.Parameters["$received_at"].Value = s.ReceivedAt.ToString("O");
        _insertCar.Parameters["$session_uid"].Value = s.SessionUid.ToString();
        _insertCar.Parameters["$session_time"].Value = CleanDbValue(s.SessionTime);
        _insertCar.Parameters["$frame_identifier"].Value = s.FrameIdentifier;
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
        MaybeFlush();
    }

    private static object CleanDbValue(object? value)
    {
        if (value is null) return DBNull.Value;
        if (value is float f && (float.IsNaN(f) || float.IsInfinity(f))) return DBNull.Value;
        if (value is double d && (double.IsNaN(d) || double.IsInfinity(d))) return DBNull.Value;
        return value;
    }

    private void MaybeFlush()
    {
        _pending++;
        if (_pending < 1000) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        cmd.ExecuteNonQuery();
        _pending = 0;
    }

    public void Dispose()
    {
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
