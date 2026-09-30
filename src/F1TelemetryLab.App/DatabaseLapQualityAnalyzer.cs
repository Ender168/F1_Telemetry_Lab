using System.Globalization;
using Microsoft.Data.Sqlite;

namespace F1TelemetryLab;

/// <summary>Keeps only one car's history in memory, including its flashback targets.</summary>
internal static class DatabaseLapQualityAnalyzer
{
    public static IReadOnlyList<LapQualityResult> Analyze(
        SqliteConnection connection, int trackLengthMeters,
        IReadOnlyList<FlashbackSignal> flashbacks,
        out List<RewindEventResult> rewinds,
        out List<SuspectedStateResetResult> resets)
    {
        var cars = new List<(string Session, int Car)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT session_uid, car_idx FROM lap_data WHERE lap_num > 0 ORDER BY session_uid, car_idx";
            using var reader = command.ExecuteReader();
            while (reader.Read()) cars.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        var results = new List<LapQualityResult>();
        rewinds = new();
        resets = new();
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT received_at, session_uid, session_time, frame_identifier, overall_frame_identifier,
                   player_car_index, car_idx, is_player, last_lap_time_ms, current_lap_time_ms,
                   sector1_time_ms, sector2_time_ms, delta_to_front_ms, delta_to_leader_ms,
                   lap_distance, total_distance, position, lap_num, pit_status, num_pit_stops,
                   sector, lap_invalid, penalties, warnings, driver_status, result_status
            FROM lap_data WHERE session_uid = $session AND car_idx = $car AND lap_num > 0
            ORDER BY overall_frame_identifier, received_at
            """;
        select.Parameters.Add("$session", SqliteType.Text);
        select.Parameters.Add("$car", SqliteType.Integer);
        foreach (var car in cars)
        {
            select.Parameters["$session"].Value = car.Session;
            select.Parameters["$car"].Value = car.Car;
            var samples = new List<LapDataSample>();
            using (var r = select.ExecuteReader())
            {
                while (r.Read())
                    samples.Add(new LapDataSample(
                        DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture),
                        ulong.Parse(r.GetString(1), CultureInfo.InvariantCulture), Float(r, 2),
                        (uint)r.GetInt64(3), (uint)r.GetInt64(4), (byte)r.GetInt32(5),
                        r.GetInt32(6), r.GetBoolean(7), (uint)r.GetInt64(8), (uint)r.GetInt64(9),
                        r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13),
                        Float(r, 14), Float(r, 15), r.GetInt32(16), r.GetInt32(17),
                        r.GetInt32(18), r.GetInt32(19), r.GetInt32(20), r.GetBoolean(21),
                        r.GetInt32(22), r.GetInt32(23), r.GetInt32(24), r.GetInt32(25)));
            }
            results.AddRange(LapQualityAnalyzer.Analyze(samples, trackLengthMeters, flashbacks,
                out var carRewinds, out var carResets));
            rewinds.AddRange(carRewinds);
            resets.AddRange(carResets);
        }
        return results;
    }

    private static float Float(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? float.NaN : reader.GetFloat(column);
}
