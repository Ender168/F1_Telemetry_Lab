namespace F1TelemetryLab;

public sealed record RaceStintGroup(int Number, IReadOnlyList<RaceLapReportRow> Rows);

public static class RaceStrategyAnalyzer
{
    public static IReadOnlyList<RaceStintGroup> BuildStints(IReadOnlyList<RaceLapReportRow> source)
    {
        var rows = source.OrderBy(x => x.LapNum).ToList();
        var result = new List<RaceStintGroup>();
        var current = new List<RaceLapReportRow>();
        RaceLapReportRow? previous = null;
        var number = 1;

        foreach (var row in rows)
        {
            var compoundChanged = previous is not null && !SameCompound(previous, row);
            var tyreAgeReset = previous is not null && row.TyreAgeEnd < previous.TyreAgeEnd;
            var boundaryBefore = previous is not null
                                 && !row.PitThisLap
                                 && !previous.PitThisLap
                                 && (compoundChanged || tyreAgeReset);
            if (boundaryBefore) CloseCurrent();

            current.Add(row);

            // A detected pit lap belongs to the stint that ended in the pit lane.
            // The following out lap begins the next stint.
            if (row.PitThisLap) CloseCurrent();
            previous = row;
        }

        CloseCurrent();
        return result;

        void CloseCurrent()
        {
            if (current.Count == 0) return;
            result.Add(new RaceStintGroup(number++, current.ToList()));
            current.Clear();
        }
    }

    public static IReadOnlyList<RaceLapReportRow> DetectPitStops(IReadOnlyList<RaceLapReportRow> source)
    {
        var rows = source.OrderBy(x => x.LapNum).ToList();
        var candidates = new List<RaceLapReportRow>();
        RaceLapReportRow? previous = null;

        foreach (var row in rows)
        {
            var compoundChanged = previous is not null && !SameCompound(previous, row);
            var tyreAgeReset = previous is not null && row.TyreAgeEnd < previous.TyreAgeEnd;
            var candidate = row.PitThisLap || compoundChanged || tyreAgeReset || row.TyreAgeEnd < row.TyreAgeStart;
            if (!candidate)
            {
                previous = row;
                continue;
            }

            if (candidates.Count > 0 && row.LapNum - candidates[^1].LapNum <= 1)
            {
                // Pit flag is stronger evidence than the compound/age change that
                // often appears one packet or one lap later for the same stop.
                if (row.PitThisLap && !candidates[^1].PitThisLap)
                    candidates[^1] = row;
            }
            else
            {
                candidates.Add(row);
            }

            previous = row;
        }

        return candidates;
    }

    public static bool SameCompound(RaceLapReportRow left, RaceLapReportRow right) =>
        CompoundKey(left.VisualCompoundEnd, left.ActualCompoundEnd) ==
        CompoundKey(right.VisualCompoundEnd, right.ActualCompoundEnd);

    private static string CompoundKey(int visual, int actual) => visual > 0 ? $"V{visual}" : $"A{actual}";
}
