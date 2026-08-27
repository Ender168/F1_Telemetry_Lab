namespace F1TelemetryLab;

public static class LapQualityAnalyzer
{
    private sealed record Completion(uint LapTimeMs, uint ConfirmedAtOverallFrame);

    public static IReadOnlyList<LapQualityResult> Analyze(
        IReadOnlyList<LapDataSample> samples,
        int trackLengthMeters,
        out List<RewindEventResult> rewindEvents)
    {
        rewindEvents = new List<RewindEventResult>();
        var completions = new Dictionary<(ulong SessionUid, int CarIndex, int LapNum), Completion>();
        var activeFrom = new Dictionary<(ulong SessionUid, int CarIndex, int LapNum), uint>();
        var rewindCounts = new Dictionary<(ulong SessionUid, int CarIndex, int LapNum), int>();
        var invalidCounts = new Dictionary<(ulong SessionUid, int CarIndex, int LapNum), int>();

        foreach (var carGroup in samples
                     .Where(x => x.LapNum > 0)
                     .GroupBy(x => (x.SessionUid, x.CarIndex)))
        {
            LapDataSample? previous = null;
            foreach (var sample in carGroup.OrderBy(x => x.OverallFrameIdentifier).ThenBy(x => x.ReceivedAt))
            {
                var key = (sample.SessionUid, sample.CarIndex, sample.LapNum);
                activeFrom.TryAdd(key, sample.OverallFrameIdentifier);
                if (sample.LapInvalid)
                    invalidCounts[key] = invalidCounts.GetValueOrDefault(key) + 1;

                if (previous is not null)
                {
                    var sameLap = previous.LapNum == sample.LapNum;
                    var reasons = new List<string>();
                    if (sample.FrameIdentifier < previous.FrameIdentifier && sample.OverallFrameIdentifier >= previous.OverallFrameIdentifier)
                        reasons.Add("frame_identifier_backwards");
                    if (sample.SessionTime < previous.SessionTime - 0.25f)
                        reasons.Add("session_time_backwards");
                    if (sample.LapNum < previous.LapNum)
                        reasons.Add("lap_number_backwards");
                    if (sameLap && sample.LapDistance < previous.LapDistance - 50f)
                        reasons.Add("lap_distance_backwards");
                    if (sameLap && sample.CurrentLapTimeMs + 750 < previous.CurrentLapTimeMs)
                        reasons.Add("lap_time_backwards");

                    if (reasons.Count > 0)
                    {
                        var rollbackLap = Math.Max(1, sample.LapNum);
                        var affectedKeys = activeFrom.Keys
                            .Where(x => x.SessionUid == sample.SessionUid && x.CarIndex == sample.CarIndex && x.LapNum >= rollbackLap)
                            .ToList();
                        foreach (var affected in affectedKeys)
                        {
                            activeFrom[affected] = sample.OverallFrameIdentifier;
                            rewindCounts[affected] = rewindCounts.GetValueOrDefault(affected) + 1;
                            completions.Remove(affected);
                        }
                        rewindCounts[key] = Math.Max(1, rewindCounts.GetValueOrDefault(key));
                        rewindEvents.Add(new RewindEventResult(
                            sample.SessionUid,
                            sample.CarIndex,
                            sample.LapNum,
                            sample.ReceivedAt,
                            sample.SessionTime,
                            sample.OverallFrameIdentifier,
                            sample.LapDistance,
                            sample.CurrentLapTimeMs,
                            string.Join(';', reasons)));
                    }

                    if (sample.LapNum == previous.LapNum + 1 && sample.LastLapTimeMs > 0)
                    {
                        var completedKey = (previous.SessionUid, previous.CarIndex, previous.LapNum);
                        completions[completedKey] = new Completion(sample.LastLapTimeMs, sample.OverallFrameIdentifier);
                    }
                }

                previous = sample;
            }
        }

        var result = new List<LapQualityResult>();
        foreach (var group in samples
                     .Where(x => x.LapNum > 0)
                     .GroupBy(x => (x.SessionUid, x.CarIndex, x.LapNum))
                     .OrderBy(x => x.Key.SessionUid)
                     .ThenBy(x => x.Key.CarIndex)
                     .ThenBy(x => x.Key.LapNum))
        {
            var key = group.Key;
            var activeStart = activeFrom.GetValueOrDefault(key, group.Min(x => x.OverallFrameIdentifier));
            var active = group
                .Where(x => x.OverallFrameIdentifier >= activeStart)
                .OrderBy(x => x.OverallFrameIdentifier)
                .ThenBy(x => x.ReceivedAt)
                .ToList();
            if (active.Count == 0) active = group.ToList();

            var minDistance = active.Min(x => x.LapDistance);
            var maxDistance = active.Max(x => x.LapDistance);
            var startTolerance = trackLengthMeters > 0 ? Math.Max(100f, trackLengthMeters * 0.025f) : 150f;
            var startCovered = minDistance <= startTolerance;
            var endCovered = trackLengthMeters > 0
                ? maxDistance >= trackLengthMeters * 0.88f
                : maxDistance - minDistance >= 1000f;
            var hasCompletion = completions.TryGetValue(key, out var completion);
            var complete = hasCompletion && startCovered && endCovered;
            var rewindCount = rewindCounts.GetValueOrDefault(key);
            var invalidCount = invalidCounts.GetValueOrDefault(key);

            var state = rewindCount > 0
                ? LapState.Rewound
                : invalidCount > 0
                    ? LapState.Invalid
                    : complete
                        ? LapState.Complete
                        : !startCovered
                            ? LapState.PartialStart
                            : LapState.PartialEnd;

            var sector1 = active.Max(x => x.Sector1TimeMs);
            var sector2 = active.Max(x => x.Sector2TimeMs);
            var lapTime = complete ? completion!.LapTimeMs : 0u;
            var sector3 = lapTime > sector1 + sector2 ? (int)lapTime - sector1 - sector2 : 0;
            var evidence = complete
                ? $"next_lap_last_lap_time@{completion!.ConfirmedAtOverallFrame}"
                : hasCompletion
                    ? "transition_confirmed_but_distance_coverage_incomplete"
                    : "no_confirmed_next_lap_transition";

            result.Add(new LapQualityResult(
                key.SessionUid,
                key.CarIndex,
                key.LapNum,
                active.Any(x => x.IsPlayer),
                state,
                state == LapState.Complete,
                rewindCount,
                invalidCount,
                active.Count,
                minDistance,
                maxDistance,
                lapTime,
                sector1,
                sector2,
                sector3,
                activeStart,
                evidence));
        }

        return result;
    }
}
