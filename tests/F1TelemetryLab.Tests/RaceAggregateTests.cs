using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed class RaceAggregateTests
{
    [Fact]
    public void PartialAndPitLapsAreExcludedFromConsumptionAggregates()
    {
        var rows = new[]
        {
            Row(1, 90_000, wear: 2, fuel: 1.0, ersEnd: 800_000),
            Row(2, 91_000, wear: 4, fuel: 1.4, ersEnd: 400_000),
            Row(3, 0, wear: 99, fuel: 99, ersEnd: 1, clean: false),
            Row(4, 120_000, wear: 50, fuel: 50, ersEnd: 1, clean: false, pit: true)
        };

        var summary = RaceAnalysisDataService.BuildRaceSummary(rows);

        Assert.True(summary.Contains("Avg tyre lap Δ 3% (n=2)", StringComparison.Ordinal));
        Assert.True(summary.Contains("Avg fuel 1.2 kg/lap (n=2)", StringComparison.Ordinal));
        Assert.True(summary.Contains("Min ERS 10% (n=2)", StringComparison.Ordinal));
    }

    [Fact]
    public void PitLapEndsOldStintAndCompoundChangeStartsNewOne()
    {
        var rows = new[]
        {
            Row(1, 90_000, visualCompound: 16, tyreAge: 4),
            Row(2, 112_000, visualCompound: 16, tyreAge: 5, clean: false, pit: true),
            Row(3, 94_000, visualCompound: 17, tyreAge: 1),
            Row(4, 93_000, visualCompound: 17, tyreAge: 2)
        };

        var stints = RaceStrategyAnalyzer.BuildStints(rows);
        Assert.Equal(2, stints.Count);
        Assert.Equal(new[] { 1, 2 }, stints[0].Rows.Select(x => x.LapNum));
        Assert.Equal(new[] { 3, 4 }, stints[1].Rows.Select(x => x.LapNum));
        Assert.Single(RaceStrategyAnalyzer.DetectPitStops(rows));
    }

    private static RaceLapReportRow Row(
        int lap,
        double lapTime,
        double wear = 1,
        double fuel = 1,
        double ersEnd = 2_000_000,
        bool clean = true,
        bool pit = false,
        int visualCompound = 16,
        int tyreAge = 1) => new()
        {
            LapNum = lap,
            LapTimeMs = lapTime,
            CleanLap = clean,
            PitThisLap = pit,
            TyreWearAvgDelta = wear,
            FuelUsed = fuel,
            ErsEnd = ersEnd,
            VisualCompoundStart = visualCompound,
            VisualCompoundEnd = visualCompound,
            ActualCompoundStart = visualCompound,
            ActualCompoundEnd = visualCompound,
            TyreAgeStart = tyreAge,
            TyreAgeEnd = tyreAge
        };
}
