using Microsoft.Data.Sqlite;
using System.Globalization;

namespace F1TelemetryLab;

public sealed record CarSetupViewRow(
    DateTimeOffset? ReceivedAt,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    int FrontWing,
    int RearWing,
    int OnThrottle,
    int OffThrottle,
    double FrontCamber,
    double RearCamber,
    double FrontToe,
    double RearToe,
    int FrontSuspension,
    int RearSuspension,
    int FrontAntiRollBar,
    int RearAntiRollBar,
    int FrontRideHeight,
    int RearRideHeight,
    int BrakePressure,
    int BrakeBias,
    int EngineBraking,
    double RearLeftTyrePressure,
    double RearRightTyrePressure,
    double FrontLeftTyrePressure,
    double FrontRightTyrePressure,
    int Ballast,
    double FuelLoad,
    double? NextFrontWingValue)
{
    public string TimeLabel => ReceivedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "time unavailable";
    public string Label => $"{TimeLabel} | frame {OverallFrameIdentifier:N0} | wings {FrontWing}/{RearWing} | diff {OnThrottle}/{OffThrottle} | brake {BrakePressure}%/{BrakeBias}%";
    public override string ToString() => Label;

    public string Detail =>
        $"Captured: {TimeLabel}\nFrame: {FrameIdentifier:N0} | Overall frame: {OverallFrameIdentifier:N0}\n\n" +
        $"Aerodynamics\nFront wing: {FrontWing} | Rear wing: {RearWing} | Next front wing: {(NextFrontWingValue?.ToString("0.##", CultureInfo.CurrentCulture) ?? "n/a")}\n\n" +
        $"Transmission\nOn-throttle differential: {OnThrottle}% | Off-throttle differential: {OffThrottle}% | Engine braking: {EngineBraking}%\n\n" +
        $"Alignment\nFront camber: {FrontCamber:0.###} | Rear camber: {RearCamber:0.###}\nFront toe: {FrontToe:0.###} | Rear toe: {RearToe:0.###}\n\n" +
        $"Suspension\nFront/rear suspension: {FrontSuspension}/{RearSuspension}\nFront/rear anti-roll bar: {FrontAntiRollBar}/{RearAntiRollBar}\nFront/rear ride height: {FrontRideHeight}/{RearRideHeight}\n\n" +
        $"Brakes\nPressure: {BrakePressure}% | Front bias: {BrakeBias}%\n\n" +
        $"Tyre pressures\nFL {FrontLeftTyrePressure:0.00} psi | FR {FrontRightTyrePressure:0.00} psi | RL {RearLeftTyrePressure:0.00} psi | RR {RearRightTyrePressure:0.00} psi\n\n" +
        $"Ballast: {Ballast} | Fuel load: {FuelLoad:0.00} kg";
}

public static class CarSetupDataService
{
    public static List<CarSetupViewRow> LoadChanges(string sessionFolder, int carIndex)
    {
        var database = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(database)) throw new FileNotFoundException("session.sqlite not found", database);
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        if (!TableExists(connection, "car_setups")) return new List<CarSetupViewRow>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT received_at, frame_identifier, overall_frame_identifier,
                   front_wing, rear_wing, on_throttle, off_throttle,
                   front_camber, rear_camber, front_toe, rear_toe,
                   front_suspension, rear_suspension, front_anti_roll_bar, rear_anti_roll_bar,
                   front_ride_height, rear_ride_height, brake_pressure, brake_bias, engine_braking,
                   rear_left_tyre_pressure, rear_right_tyre_pressure, front_left_tyre_pressure, front_right_tyre_pressure,
                   ballast, fuel_load, next_front_wing_value
            FROM car_setups
            WHERE car_idx=$car
            ORDER BY overall_frame_identifier, received_at
            """;
        command.Parameters.AddWithValue("$car", carIndex);
        using var reader = command.ExecuteReader();
        var result = new List<CarSetupViewRow>();
        while (reader.Read())
        {
            var received = DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
            result.Add(new CarSetupViewRow(
                received,
                Convert.ToUInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToUInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                D(reader, 7), D(reader, 8), D(reader, 9), D(reader, 10),
                reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14),
                reader.GetInt32(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18), reader.GetInt32(19),
                D(reader, 20), D(reader, 21), D(reader, 22), D(reader, 23),
                reader.GetInt32(24), D(reader, 25), reader.IsDBNull(26) ? null : D(reader, 26)));
        }
        return result;
    }

    private static double D(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }
}
