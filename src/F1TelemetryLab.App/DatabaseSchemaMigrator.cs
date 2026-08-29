using Microsoft.Data.Sqlite;

namespace F1TelemetryLab;

public static class DatabaseSchemaMigrator
{
    public static void Apply(SqliteConnection connection)
    {
        var version = ReadUserVersion(connection);
        if (version > AppInfo.DatabaseSchemaVersion)
            throw new InvalidOperationException($"Database schema {version} is newer than supported schema {AppInfo.DatabaseSchemaVersion}.");

        if (version < 2)
        {
            EnsureColumn(connection, "raw_packets", "game_year", "INTEGER");
            EnsureColumn(connection, "raw_packets", "game_major_version", "INTEGER");
            EnsureColumn(connection, "raw_packets", "game_minor_version", "INTEGER");
            EnsureColumn(connection, "raw_packets", "packet_version", "INTEGER");
            EnsureColumn(connection, "raw_packets", "overall_frame_identifier", "INTEGER");
            EnsureColumn(connection, "raw_packets", "secondary_player_car_index", "INTEGER");
            EnsureColumn(connection, "car_telemetry", "overall_frame_identifier", "INTEGER NOT NULL DEFAULT 0");
        }

        if (version < 3)
        {
            EnsureColumn(connection, "recording_quality", "missing_frames_estimated", "INTEGER NOT NULL DEFAULT 0");
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS data_quality(
                    dimension TEXT PRIMARY KEY,
                    rating TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """);
        }

        if (version < 4)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS car_setups(
                    received_at TEXT, session_uid TEXT, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER,
                    player_car_index INTEGER, car_idx INTEGER, is_player INTEGER,
                    front_wing INTEGER, rear_wing INTEGER, on_throttle INTEGER, off_throttle INTEGER,
                    front_camber REAL, rear_camber REAL, front_toe REAL, rear_toe REAL,
                    front_suspension INTEGER, rear_suspension INTEGER, front_anti_roll_bar INTEGER, rear_anti_roll_bar INTEGER,
                    front_ride_height INTEGER, rear_ride_height INTEGER, brake_pressure INTEGER, brake_bias INTEGER, engine_braking INTEGER,
                    rear_left_tyre_pressure REAL, rear_right_tyre_pressure REAL, front_left_tyre_pressure REAL, front_right_tyre_pressure REAL,
                    ballast INTEGER, fuel_load REAL, next_front_wing_value REAL
                );
                CREATE INDEX IF NOT EXISTS idx_car_setups_session_car_frame
                    ON car_setups(session_uid, car_idx, overall_frame_identifier);
                """);
        }

        if (version < 5)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS suspected_state_reset_events(
                    session_uid TEXT, car_idx INTEGER, lap_num INTEGER, received_at TEXT, session_time REAL,
                    overall_frame_identifier INTEGER, lap_distance REAL, current_lap_time_ms INTEGER, reason TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_suspected_state_resets_session_frame
                    ON suspected_state_reset_events(session_uid, overall_frame_identifier, car_idx);
                """);
            EnsureColumn(connection, "final_classification", "classification_source", "TEXT");
            EnsureColumn(connection, "final_classification", "classification_is_official", "INTEGER");
            EnsureColumn(connection, "final_classification", "classification_note", "TEXT");
            if (TableExists(connection, "final_classification"))
            {
                Execute(connection, """
                    UPDATE final_classification
                    SET classification_is_official = CASE WHEN classification_source = 'official_udp' THEN 1 ELSE 0 END
                    WHERE classification_is_official IS NULL;
                    UPDATE final_classification
                    SET classification_note = CASE
                        WHEN classification_source = 'official_udp'
                            THEN 'Official final classification from UDP packet 8.'
                        ELSE 'UDP packet 8 is absent. Positions are reconstructed from the latest Lap Data and may be incomplete.'
                    END
                    WHERE classification_note IS NULL OR classification_note = '';
                    """);
            }
        }

        Execute(connection, $"PRAGMA user_version = {AppInfo.DatabaseSchemaVersion};");
    }

    public static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        if (!TableExists(connection, table)) return;
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
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
