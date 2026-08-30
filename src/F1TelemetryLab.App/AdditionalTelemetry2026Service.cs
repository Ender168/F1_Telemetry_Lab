using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Globalization;

namespace F1TelemetryLab;

/// <summary>
/// Builds compact 2026-specific projections from raw packets 11, 12 and 16.
/// Packet 11/12 snapshots are retained for all cars because their final state is tiny.
/// Packet 16 is high-frequency, so only player-car rows are materialized; the all-car
/// source remains available in raw_packets for future rebuilds.
/// </summary>
public static class AdditionalTelemetry2026Service
{
    private const int ProjectionVersion = 1;
    private const int HeaderSize = F12026Parser.HeaderSize;
    private const int LapHistorySize = 14;
    private const int MaxHistoryLaps = 100;
    private const int MaxTyreStints = 8;
    private const int TyreSetSize = 10;
    private const int MaxTyreSets = 20;
    private const int CarTelemetry2Size = 10;

    private sealed record LapHistoryRow(int LapNum, uint LapTimeMs, int S1Ms, int S2Ms, int S3Ms, byte ValidFlags);
    private sealed record StintRow(int StintIndex, int EndLap, int ActualCompound, int VisualCompound);
    private sealed record HistorySnapshot(
        string ReceivedAt, string SessionUid, int CarIndex, int PlayerCarIndex,
        int BestLapNum, int BestS1LapNum, int BestS2LapNum, int BestS3LapNum,
        IReadOnlyList<LapHistoryRow> Laps, IReadOnlyList<StintRow> Stints);
    private sealed record TyreSetRow(
        int SetIndex, int ActualCompound, int VisualCompound, int Wear, int Available,
        int RecommendedSession, int LifeSpan, int UsableLife, int LapDeltaTimeMs, int Fitted);
    private sealed record TyreSetSnapshot(
        string ReceivedAt, string SessionUid, long OverallFrame, int CarIndex, int PlayerCarIndex,
        int FittedIndex, IReadOnlyList<TyreSetRow> Sets);

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

        var rawCount = CountRelevantRawPackets(write);
        if (ReadMetaInt(write, "additional_telemetry_2026_version") == ProjectionVersion &&
            ReadMetaLong(write, "additional_telemetry_2026_raw_packets") == rawCount &&
            TableExists(write, "session_history_laps") &&
            TableExists(write, "session_history_stints") &&
            TableExists(write, "tyre_sets") &&
            TableExists(write, "car_telemetry_2_player"))
            return;

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

        var histories = new Dictionary<(string Uid, int Car), HistorySnapshot>();
        var tyreSets = new Dictionary<(string Uid, int Car), TyreSetSnapshot>();
        long telemetry2Rows = 0;

        using var tx = write.BeginTransaction();
        Execute(write, tx, "DELETE FROM car_telemetry_2_player;");
        using var telemetry2Insert = PrepareTelemetry2Insert(write, tx);

        using (var command = read.CreateCommand())
        {
            command.CommandText = """
                SELECT received_at, packet_id, payload
                FROM raw_packets
                WHERE packet_format=2026 AND packet_id IN (11,12,16)
                ORDER BY id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var receivedAt = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var packetId = reader.GetInt32(1);
                var payload = (byte[])reader[2];
                if (!F12026Parser.TryParseHeader(payload, out var header)) continue;

                switch (packetId)
                {
                    case 11:
                        var history = ParseHistory(receivedAt, payload, header);
                        if (history is not null) histories[(history.SessionUid, history.CarIndex)] = history;
                        break;
                    case 12:
                        var sets = ParseTyreSets(receivedAt, payload, header);
                        if (sets is not null) tyreSets[(sets.SessionUid, sets.CarIndex)] = sets;
                        break;
                    case 16:
                        if (InsertPlayerTelemetry2(telemetry2Insert, receivedAt, payload, header)) telemetry2Rows++;
                        break;
                }
            }
        }

        ReplaceHistory(write, tx, histories.Values);
        ReplaceTyreSets(write, tx, tyreSets.Values);
        tx.Commit();

        SetMeta(write, "additional_telemetry_2026_version", ProjectionVersion.ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "additional_telemetry_2026_raw_packets", rawCount.ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "session_history_lap_rows", histories.Values.Sum(x => x.Laps.Count).ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "session_history_stint_rows", histories.Values.Sum(x => x.Stints.Count).ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "tyre_set_rows", tyreSets.Values.Sum(x => x.Sets.Count).ToString(CultureInfo.InvariantCulture));
        SetMeta(write, "car_telemetry_2_player_rows", telemetry2Rows.ToString(CultureInfo.InvariantCulture));

        log?.Invoke($"2026 extra rebuild: history laps {histories.Values.Sum(x => x.Laps.Count):N0}, stints {histories.Values.Sum(x => x.Stints.Count):N0}, tyre sets {tyreSets.Values.Sum(x => x.Sets.Count):N0}, player telemetry 2 {telemetry2Rows:N0}.");
    }

    private static HistorySnapshot? ParseHistory(string receivedAt, byte[] payload, PacketHeader header)
    {
        // 29-byte header + 7-byte packet metadata + 100*14-byte laps + 8*3-byte stints = 1460.
        if (payload.Length < HeaderSize + 7) return null;
        var car = payload[HeaderSize];
        if (car >= F12026Parser.MaxCars2026) return null;
        var numLaps = Math.Min(payload[HeaderSize + 1], (byte)MaxHistoryLaps);
        var numStints = Math.Min(payload[HeaderSize + 2], (byte)MaxTyreStints);
        var bestLap = payload[HeaderSize + 3];
        var bestS1 = payload[HeaderSize + 4];
        var bestS2 = payload[HeaderSize + 5];
        var bestS3 = payload[HeaderSize + 6];
        var lapBase = HeaderSize + 7;
        if (payload.Length < lapBase + MaxHistoryLaps * LapHistorySize) return null;

        var laps = new List<LapHistoryRow>(numLaps);
        for (var i = 0; i < numLaps; i++)
        {
            var row = payload.AsSpan(lapBase + i * LapHistorySize, LapHistorySize);
            laps.Add(new LapHistoryRow(
                i + 1,
                U32(row, 0),
                SectorMs(U16(row, 4), row[6]),
                SectorMs(U16(row, 7), row[9]),
                SectorMs(U16(row, 10), row[12]),
                row[13]));
        }

        var stintBase = lapBase + MaxHistoryLaps * LapHistorySize;
        if (payload.Length < stintBase + numStints * 3) return null;
        var stints = new List<StintRow>(numStints);
        for (var i = 0; i < numStints; i++)
        {
            var offset = stintBase + i * 3;
            stints.Add(new StintRow(i, payload[offset], payload[offset + 1], payload[offset + 2]));
        }

        return new HistorySnapshot(
            receivedAt,
            header.SessionUid.ToString(CultureInfo.InvariantCulture),
            car,
            header.PlayerCarIndex,
            bestLap, bestS1, bestS2, bestS3,
            laps, stints);
    }

    private static TyreSetSnapshot? ParseTyreSets(string receivedAt, byte[] payload, PacketHeader header)
    {
        if (payload.Length < HeaderSize + 1 + MaxTyreSets * TyreSetSize + 1) return null;
        var car = payload[HeaderSize];
        if (car >= F12026Parser.MaxCars2026) return null;
        var baseOffset = HeaderSize + 1;
        var sets = new List<TyreSetRow>(MaxTyreSets);
        for (var i = 0; i < MaxTyreSets; i++)
        {
            var row = payload.AsSpan(baseOffset + i * TyreSetSize, TyreSetSize);
            sets.Add(new TyreSetRow(
                i, row[0], row[1], row[2], row[3], row[4], row[5], row[6], I16(row, 7), row[9]));
        }

        return new TyreSetSnapshot(
            receivedAt,
            header.SessionUid.ToString(CultureInfo.InvariantCulture),
            header.OverallFrameIdentifier,
            car,
            header.PlayerCarIndex,
            payload[baseOffset + MaxTyreSets * TyreSetSize],
            sets);
    }

    private static bool InsertPlayerTelemetry2(SqliteCommand command, string receivedAt, byte[] payload, PacketHeader header)
    {
        if (header.PlayerCarIndex >= F12026Parser.MaxCars2026) return false;
        var offset = HeaderSize + header.PlayerCarIndex * CarTelemetry2Size;
        if (payload.Length < offset + CarTelemetry2Size) return false;
        var row = payload.AsSpan(offset, CarTelemetry2Size);

        command.Parameters["$received"].Value = receivedAt;
        command.Parameters["$uid"].Value = header.SessionUid.ToString(CultureInfo.InvariantCulture);
        command.Parameters["$session"].Value = CleanFloat(header.SessionTime);
        command.Parameters["$frame"].Value = header.FrameIdentifier;
        command.Parameters["$overall"].Value = header.OverallFrameIdentifier;
        command.Parameters["$player"].Value = header.PlayerCarIndex;
        command.Parameters["$mode"].Value = row[0];
        command.Parameters["$aeroAvailable"].Value = row[1];
        command.Parameters["$aeroDistance"].Value = U16(row, 2);
        command.Parameters["$overtakeAvailable"].Value = row[4];
        command.Parameters["$overtakeActive"].Value = row[5];
        command.Parameters["$overtakeDistance"].Value = U16(row, 6);
        command.Parameters["$regulations"].Value = row[8];
        command.Parameters["$wrongWay"].Value = row[9];
        command.ExecuteNonQuery();
        return true;
    }

    private static void ReplaceHistory(SqliteConnection connection, SqliteTransaction tx, IEnumerable<HistorySnapshot> snapshots)
    {
        Execute(connection, tx, "DELETE FROM session_history_laps; DELETE FROM session_history_stints;");

        using var lap = connection.CreateCommand();
        lap.Transaction = tx;
        lap.CommandText = """
            INSERT INTO session_history_laps(
                received_at,session_uid,car_idx,is_player,lap_num,lap_time_ms,sector1_ms,sector2_ms,sector3_ms,
                lap_valid,sector1_valid,sector2_valid,sector3_valid,is_best_lap,is_best_sector1,is_best_sector2,is_best_sector3)
            VALUES($received,$uid,$car,$me,$lap,$time,$s1,$s2,$s3,$valid,$v1,$v2,$v3,$best,$b1,$b2,$b3)
            """;
        foreach (var name in new[] { "$received", "$uid", "$car", "$me", "$lap", "$time", "$s1", "$s2", "$s3", "$valid", "$v1", "$v2", "$v3", "$best", "$b1", "$b2", "$b3" })
            lap.Parameters.AddWithValue(name, 0);

        using var stint = connection.CreateCommand();
        stint.Transaction = tx;
        stint.CommandText = """
            INSERT INTO session_history_stints(received_at,session_uid,car_idx,is_player,stint_index,end_lap,is_current,actual_tyre_compound,visual_tyre_compound)
            VALUES($received,$uid,$car,$me,$idx,$end,$current,$actual,$visual)
            """;
        foreach (var name in new[] { "$received", "$uid", "$car", "$me", "$idx", "$end", "$current", "$actual", "$visual" })
            stint.Parameters.AddWithValue(name, 0);

        foreach (var snapshot in snapshots.OrderBy(x => x.SessionUid).ThenBy(x => x.CarIndex))
        {
            foreach (var row in snapshot.Laps)
            {
                lap.Parameters["$received"].Value = snapshot.ReceivedAt;
                lap.Parameters["$uid"].Value = snapshot.SessionUid;
                lap.Parameters["$car"].Value = snapshot.CarIndex;
                lap.Parameters["$me"].Value = snapshot.CarIndex == snapshot.PlayerCarIndex ? 1 : 0;
                lap.Parameters["$lap"].Value = row.LapNum;
                lap.Parameters["$time"].Value = row.LapTimeMs;
                lap.Parameters["$s1"].Value = row.S1Ms;
                lap.Parameters["$s2"].Value = row.S2Ms;
                lap.Parameters["$s3"].Value = row.S3Ms;
                lap.Parameters["$valid"].Value = (row.ValidFlags & 0x01) != 0 ? 1 : 0;
                lap.Parameters["$v1"].Value = (row.ValidFlags & 0x02) != 0 ? 1 : 0;
                lap.Parameters["$v2"].Value = (row.ValidFlags & 0x04) != 0 ? 1 : 0;
                lap.Parameters["$v3"].Value = (row.ValidFlags & 0x08) != 0 ? 1 : 0;
                lap.Parameters["$best"].Value = row.LapNum == snapshot.BestLapNum ? 1 : 0;
                lap.Parameters["$b1"].Value = row.LapNum == snapshot.BestS1LapNum ? 1 : 0;
                lap.Parameters["$b2"].Value = row.LapNum == snapshot.BestS2LapNum ? 1 : 0;
                lap.Parameters["$b3"].Value = row.LapNum == snapshot.BestS3LapNum ? 1 : 0;
                lap.ExecuteNonQuery();
            }

            foreach (var row in snapshot.Stints)
            {
                stint.Parameters["$received"].Value = snapshot.ReceivedAt;
                stint.Parameters["$uid"].Value = snapshot.SessionUid;
                stint.Parameters["$car"].Value = snapshot.CarIndex;
                stint.Parameters["$me"].Value = snapshot.CarIndex == snapshot.PlayerCarIndex ? 1 : 0;
                stint.Parameters["$idx"].Value = row.StintIndex;
                stint.Parameters["$end"].Value = row.EndLap;
                stint.Parameters["$current"].Value = row.EndLap == 255 ? 1 : 0;
                stint.Parameters["$actual"].Value = row.ActualCompound;
                stint.Parameters["$visual"].Value = row.VisualCompound;
                stint.ExecuteNonQuery();
            }
        }
    }

    private static void ReplaceTyreSets(SqliteConnection connection, SqliteTransaction tx, IEnumerable<TyreSetSnapshot> snapshots)
    {
        Execute(connection, tx, "DELETE FROM tyre_sets;");
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO tyre_sets(
                received_at,session_uid,overall_frame_identifier,car_idx,is_player,set_idx,actual_tyre_compound,visual_tyre_compound,
                wear,available,recommended_session,life_span,usable_life,lap_delta_time_ms,fitted,packet_fitted_idx)
            VALUES($received,$uid,$overall,$car,$me,$idx,$actual,$visual,$wear,$available,$recommended,$life,$usable,$delta,$fitted,$fittedIdx)
            """;
        foreach (var name in new[] { "$received", "$uid", "$overall", "$car", "$me", "$idx", "$actual", "$visual", "$wear", "$available", "$recommended", "$life", "$usable", "$delta", "$fitted", "$fittedIdx" })
            insert.Parameters.AddWithValue(name, 0);

        foreach (var snapshot in snapshots.OrderBy(x => x.SessionUid).ThenBy(x => x.CarIndex))
        foreach (var row in snapshot.Sets)
        {
            insert.Parameters["$received"].Value = snapshot.ReceivedAt;
            insert.Parameters["$uid"].Value = snapshot.SessionUid;
            insert.Parameters["$overall"].Value = snapshot.OverallFrame;
            insert.Parameters["$car"].Value = snapshot.CarIndex;
            insert.Parameters["$me"].Value = snapshot.CarIndex == snapshot.PlayerCarIndex ? 1 : 0;
            insert.Parameters["$idx"].Value = row.SetIndex;
            insert.Parameters["$actual"].Value = row.ActualCompound;
            insert.Parameters["$visual"].Value = row.VisualCompound;
            insert.Parameters["$wear"].Value = row.Wear;
            insert.Parameters["$available"].Value = row.Available;
            insert.Parameters["$recommended"].Value = row.RecommendedSession;
            insert.Parameters["$life"].Value = row.LifeSpan;
            insert.Parameters["$usable"].Value = row.UsableLife;
            insert.Parameters["$delta"].Value = row.LapDeltaTimeMs;
            insert.Parameters["$fitted"].Value = row.Fitted;
            insert.Parameters["$fittedIdx"].Value = snapshot.FittedIndex;
            insert.ExecuteNonQuery();
        }
    }

    private static SqliteCommand PrepareTelemetry2Insert(SqliteConnection connection, SqliteTransaction tx)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT OR REPLACE INTO car_telemetry_2_player(
                received_at,session_uid,session_time,frame_identifier,overall_frame_identifier,car_idx,
                active_aero_mode,active_aero_available,active_aero_activation_distance,
                overtake_available,overtake_active,overtake_activation_distance,regulations_2026,driving_wrong_way)
            VALUES($received,$uid,$session,$frame,$overall,$player,$mode,$aeroAvailable,$aeroDistance,$overtakeAvailable,$overtakeActive,$overtakeDistance,$regulations,$wrongWay)
            """;
        foreach (var name in new[] { "$received", "$uid", "$session", "$frame", "$overall", "$player", "$mode", "$aeroAvailable", "$aeroDistance", "$overtakeAvailable", "$overtakeActive", "$overtakeDistance", "$regulations", "$wrongWay" })
            command.Parameters.AddWithValue(name, 0);
        return command;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS session_history_laps(
                received_at TEXT, session_uid TEXT NOT NULL, car_idx INTEGER NOT NULL, is_player INTEGER NOT NULL,
                lap_num INTEGER NOT NULL, lap_time_ms INTEGER, sector1_ms INTEGER, sector2_ms INTEGER, sector3_ms INTEGER,
                lap_valid INTEGER, sector1_valid INTEGER, sector2_valid INTEGER, sector3_valid INTEGER,
                is_best_lap INTEGER, is_best_sector1 INTEGER, is_best_sector2 INTEGER, is_best_sector3 INTEGER,
                PRIMARY KEY(session_uid,car_idx,lap_num)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS session_history_stints(
                received_at TEXT, session_uid TEXT NOT NULL, car_idx INTEGER NOT NULL, is_player INTEGER NOT NULL,
                stint_index INTEGER NOT NULL, end_lap INTEGER, is_current INTEGER, actual_tyre_compound INTEGER, visual_tyre_compound INTEGER,
                PRIMARY KEY(session_uid,car_idx,stint_index)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS tyre_sets(
                received_at TEXT, session_uid TEXT NOT NULL, overall_frame_identifier INTEGER, car_idx INTEGER NOT NULL, is_player INTEGER NOT NULL,
                set_idx INTEGER NOT NULL, actual_tyre_compound INTEGER, visual_tyre_compound INTEGER, wear INTEGER, available INTEGER,
                recommended_session INTEGER, life_span INTEGER, usable_life INTEGER, lap_delta_time_ms INTEGER, fitted INTEGER, packet_fitted_idx INTEGER,
                PRIMARY KEY(session_uid,car_idx,set_idx)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS car_telemetry_2_player(
                received_at TEXT, session_uid TEXT NOT NULL, session_time REAL, frame_identifier INTEGER, overall_frame_identifier INTEGER NOT NULL,
                car_idx INTEGER NOT NULL, active_aero_mode INTEGER, active_aero_available INTEGER, active_aero_activation_distance INTEGER,
                overtake_available INTEGER, overtake_active INTEGER, overtake_activation_distance INTEGER, regulations_2026 INTEGER, driving_wrong_way INTEGER,
                PRIMARY KEY(session_uid,overall_frame_identifier)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS idx_session_history_laps_car ON session_history_laps(session_uid,car_idx,lap_num);
            CREATE INDEX IF NOT EXISTS idx_tyre_sets_car ON tyre_sets(session_uid,car_idx,set_idx);
        """);
    }

    private static long CountRelevantRawPackets(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_packets WHERE packet_format=2026 AND packet_id IN (11,12,16)";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

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

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object CleanFloat(float value) => float.IsFinite(value) ? value : DBNull.Value;
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    private static short I16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static int SectorMs(ushort ms, byte minutes) => minutes * 60_000 + ms;
}
