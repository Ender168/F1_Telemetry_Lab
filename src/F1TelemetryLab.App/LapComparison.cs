using System.Globalization;

namespace F1TelemetryLab;

public sealed record LapComparisonCell(string Text, double? DeltaSeconds = null);
public sealed record LapComparisonRow(string Driver, IReadOnlyList<LapComparisonCell> Laps);

public static class LapComparison
{
    public static IReadOnlyList<LapComparisonRow> Build(RaceEngineerSnapshot snapshot, bool russian, DateTimeOffset now)
    {
        LapComparisonRow Opponent(int offset, string label)
        {
            var car = RaceEngineerText.Neighbour(snapshot, offset, now);
            var laps = car?.LastLaps.OrderBy(x => x.LapNumber).TakeLast(3).Select(lap =>
            {
                var own = snapshot.LastLaps.LastOrDefault(x => x.LapNumber == lap.LapNumber);
                var comparable = lap.Valid == true && !lap.PitLap && !lap.SafetyCarAffected && own is { Clean: true, PitLap: false, SafetyCarAffected: false };
                double? delta = comparable ? ((double)lap.LapTimeMs - own!.LapTimeMs) / 1000 : null;
                return Cell(lap.LapNumber, lap.LapTimeMs, lap.Valid, lap.PitLap, lap.SafetyCarAffected, delta);
            }).ToList() ?? new();
            return new(label + "\n" + (car is null ? "?" : RaceEngineerText.DriverName(car, russian)), Pad(laps));
        }
        var mine = snapshot.LastLaps.OrderBy(x => x.LapNumber).TakeLast(3)
            .Select(x => Cell(x.LapNumber, x.LapTimeMs, x.Clean, x.PitLap, x.SafetyCarAffected, null)).ToList();
        return new[] { Opponent(-1, russian ? "ВПЕРЕДИ" : "AHEAD"),
            new LapComparisonRow(russian ? "ТЫ" : "YOU", Pad(mine)), Opponent(1, russian ? "СЗАДИ" : "BEHIND") };
    }

    private static IReadOnlyList<LapComparisonCell> Pad(List<LapComparisonCell> laps)
    {
        while (laps.Count < 3) laps.Insert(0, new("·"));
        return laps;
    }

    private static LapComparisonCell Cell(int lap, uint time, bool? valid, bool pit, bool sc, double? delta)
    {
        var flag = pit ? " PIT" : sc ? " SC/VSC" : valid == false ? " INVALID" : valid is null ? " ?" : "";
        var difference = delta is double d ? "\n" + d.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) + " s" : "";
        return new($"L{lap} {LapOption.FormatLapTime(time)}{flag}{difference}", delta);
    }

    // Only the overlay consumes this snapshot. It never enters the recorder or ERS.
    public static RaceEngineerSnapshot Demo(DateTimeOffset now)
    {
        var mine = Enumerable.Range(12, 3).Select(lap => new CompletedLiveLap(1, lap,
            (uint)(92000 - (lap - 12) * 180), true, false, false, 17, lap - 8,
            15, 17, 2, 60, 55, -5, 5, "DEMO")).ToArray();
        NearbyCarSnapshot Car(int pos, string name, bool player, int delta) => new(pos, pos, player, name,
            17, 6, false, now, now, mine.Select(x => new OpponentLap(x.LapNumber,
                (uint)(x.LapTimeMs + delta), true, false, false)).ToArray());
        return RaceEngineerSnapshot.Waiting with { UpdatedAt = now, CurrentLap = 15, CurrentPosition = 5,
            LastLaps = mine, NearbyCars = new[] { Car(4, "DRIVER A", false, -220), Car(5, "YOU", true, 0), Car(6, "DRIVER B", false, 340) },
            Tyres = new(true, 17, 6, 22, 20, 17, 18, "FL", 22, 2, 20, 25, 75, 5, AdviceConfidence.Medium, "DEMO"),
            Pit = new(true, 10, 12, 23, 2, 5, AdviceConfidence.Medium, "DEMO"),
            Ers = new(true, 62, 1, 40, 70, ErsAggressionAdvice.OnPlan, 2, "T3", "T4", 450, AdviceConfidence.Medium, "DEMO") };
    }
}
