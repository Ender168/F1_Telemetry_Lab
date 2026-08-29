using System.Text.Json;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed class MelbourneRegressionTests
{
    [Fact]
    public void GoldenThreeLapRaceUsesOfficialFlashbacksAndCompletesFinishLap()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("melbourne_3lap_golden.json")));
        var golden = document.RootElement;
        var sessionUid = golden.GetProperty("session_uid").GetUInt64();
        var trackLength = golden.GetProperty("track_length_m").GetInt32();
        var player = golden.GetProperty("player_car_index").GetInt32();

        var samples = BuildThreeLapRace(sessionUid, player, trackLength);
        var flashbacks = new[]
        {
            Flashback(sessionUid, 150, 100, 1f),
            Flashback(sessionUid, 200, 101, 2f),
            Flashback(sessionUid, 250, 102, 3f),
            Flashback(sessionUid, 300, 103, 4f)
        };

        var result = LapQualityAnalyzer.Analyze(samples, trackLength, flashbacks, out var rewindEvents)
            .Where(x => x.CarIndex == player)
            .OrderBy(x => x.LapNum)
            .ToList();

        var expected = golden.GetProperty("laps").EnumerateArray().ToList();
        Assert.Equal(expected.Count, result.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].GetProperty("lap").GetInt32(), result[index].LapNum);
            Assert.Equal(expected[index].GetProperty("state").GetString(), result[index].State.ToString());
            Assert.Equal(expected[index].GetProperty("clean").GetBoolean(), result[index].CleanLap);
            Assert.Equal(expected[index].GetProperty("rewinds").GetInt32(), result[index].RewindCount);
            Assert.Equal(expected[index].GetProperty("time_ms").GetUInt32(), result[index].LapTimeMs);
        }

        Assert.Equal(golden.GetProperty("official_flashbacks").GetInt32(), rewindEvents.Count);
        Assert.All(rewindEvents, x => Assert.StartsWith("official_flbk:", x.Reason, StringComparison.Ordinal));
        Assert.True(result[2].CompletionEvidence.Contains("finish_last_lap_time", StringComparison.Ordinal));

        var referenceMs = golden.GetProperty("reference").GetProperty("time_ms").GetInt32();
        var playerBest = result.Where(x => x.CleanLap).Min(x => checked((int)x.LapTimeMs));
        Assert.Equal(golden.GetProperty("expected_player_gap_ms").GetInt32(), playerBest - referenceMs);
    }

    [Fact]
    public void MelbourneProfileUsesGeometricDistanceNormalizedToGameLength()
    {
        var root = RepositoryRoot();
        var profile = TrackMapDataService.LoadTrackProfile(root, 0, "Melbourne", 5_276);

        Assert.NotNull(profile);
        Assert.Equal("geometric_xz", profile!.DistanceSource);
        Assert.Equal(5_276d, profile.TrackLengthM, 3);
        Assert.Equal(5_276d, profile.Points[^1].DistanceM, 3);
        Assert.Equal(profile.Points[0].X, profile.Points[^1].X, 6);
        Assert.Equal(profile.Points[0].Z, profile.Points[^1].Z, 6);

        var prost = Assert.Single(profile.Corners, x => x.Label.Contains("Prost", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(prost.DistanceM, 4_750, 4_900);
        var zoneStart = Nearest(profile.Points, 4_450);
        var zoneEnd = Nearest(profile.Points, 5_260);
        Assert.True(Math.Abs(zoneStart.X - zoneEnd.X) + Math.Abs(zoneStart.Z - zoneEnd.Z) > 20,
            "The final 800m of Melbourne must not collapse into one map point.");
    }

    [Fact]
    public void SessionTimeResetWithoutFlbkIsDiagnosticNotConfirmedRewind()
    {
        const ulong sessionUid = 8080;
        const int car = 11;
        const int trackLength = 5_276;
        var samples = new[]
        {
            Lap(sessionUid, car, 100, 100, 1, 5_200, 90_000, sessionTime: 100),
            Lap(sessionUid, car, 101, 1, 1, 0, 0, sessionTime: 0),
            Lap(sessionUid, car, 102, 2, 1, 5_275, 81_000, sessionTime: 81),
            Lap(sessionUid, car, 103, 3, 2, 1, 100, lastMs: 81_100, sessionTime: 82)
        };

        var result = LapQualityAnalyzer.Analyze(
            samples,
            trackLength,
            Array.Empty<FlashbackSignal>(),
            out var confirmedRewinds,
            out var suspectedStateResets);

        Assert.Empty(confirmedRewinds);
        var reset = Assert.Single(suspectedStateResets);
        Assert.StartsWith("suspected_state_reset:", reset.Reason, StringComparison.Ordinal);
        var lap = Assert.Single(result, x => x.LapNum == 1);
        Assert.Equal(LapState.Complete, lap.State);
        Assert.True(lap.CleanLap);
        Assert.Equal(0, lap.RewindCount);
    }

    private static IReadOnlyList<LapDataSample> BuildThreeLapRace(ulong uid, int car, int trackLength)
    {
        return new[]
        {
            Lap(uid, car, 100, 100, 1, 0, 100, sessionTime: 1),
            Lap(uid, car, 149, 104, 1, 1_400, 22_000, sessionTime: 22),
            Lap(uid, car, 151, 101, 1, 0, 100, sessionTime: 1),
            Lap(uid, car, 199, 105, 1, 1_600, 24_000, sessionTime: 24),
            Lap(uid, car, 201, 102, 1, 0, 100, sessionTime: 2),
            Lap(uid, car, 249, 106, 1, 1_800, 27_000, sessionTime: 27),
            Lap(uid, car, 251, 103, 1, 0, 100, sessionTime: 3),
            Lap(uid, car, 299, 107, 1, 2_000, 30_000, sessionTime: 30),
            Lap(uid, car, 301, 104, 1, 0, 100, sessionTime: 4),
            Lap(uid, car, 302, 105, 1, trackLength - 3, 89_900, sessionTime: 90, s1: 31_000, s2: 27_000),
            Lap(uid, car, 303, 106, 2, 1, 50, lastMs: 90_000, sessionTime: 91),
            Lap(uid, car, 304, 107, 2, trackLength - 2, 84_050, sessionTime: 175, s1: 28_513, s2: 19_101),
            Lap(uid, car, 305, 108, 3, 1, 40, lastMs: 84_161, sessionTime: 176),
            Lap(uid, car, 306, 109, 3, 120, 1_800, sessionTime: 178),
            Lap(uid, car, 307, 110, 3, trackLength - 1, 83_500, sessionTime: 259, s1: 28_200, s2: 18_900),
            Lap(uid, car, 308, 111, 3, 1, 0, lastMs: 83_559, sessionTime: 260, driverStatus: 4, resultStatus: 4)
        };
    }

    private static FlashbackSignal Flashback(ulong uid, uint overall, uint targetFrame, float targetTime) =>
        new(uid, DateTimeOffset.UnixEpoch.AddSeconds(overall), targetTime + 1, overall, targetFrame, targetTime);

    private static LapDataSample Lap(
        ulong uid,
        int car,
        uint overall,
        uint frame,
        int lap,
        float distance,
        uint currentMs,
        uint lastMs = 0,
        float sessionTime = 0,
        int s1 = 0,
        int s2 = 0,
        int driverStatus = 1,
        int resultStatus = 2) =>
        new(DateTimeOffset.UnixEpoch.AddMilliseconds(overall), uid, sessionTime, frame, overall, (byte)car, car, true,
            lastMs, currentMs, s1, s2, 0, 0, distance, distance, 1, lap, 0, 0, 0, false, 0, 0,
            driverStatus, resultStatus);

    private static TrackPoint Nearest(IEnumerable<TrackPoint> points, double distance) =>
        points.MinBy(x => Math.Abs(x.DistanceM - distance))!;

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "F1TelemetryLab.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
