namespace F1TelemetryLab;

public static class LapQualityAnalyzer
{
    private sealed record Completion(uint LapTimeMs, uint ConfirmedAtOverallFrame, string Evidence);

    public static IReadOnlyList<LapQualityResult> Analyze(
        IReadOnlyList<LapDataSample> samples,
        int trackLengthMeters,
        out List<RewindEventResult> rewindEvents)
        => Analyze(samples, trackLengthMeters, Array.Empty<FlashbackSignal>(), out rewindEvents, out _);

    public static IReadOnlyList<LapQualityResult> Analyze(
        IReadOnlyList<LapDataSample> samples,
        int trackLengthMeters,
        IReadOnlyList<FlashbackSignal> flashbacks,
        out List<RewindEventResult> rewindEvents)
        => Analyze(samples, trackLengthMeters, flashbacks, out rewindEvents, out _);

    public static IReadOnlyList<LapQualityResult> Analyze(
        IReadOnlyList<LapDataSample> samples,
        int trackLengthMeters,
        IReadOnlyList<FlashbackSignal> flashbacks,
        out List<RewindEventResult> rewindEvents,
        out List<SuspectedStateResetResult> suspectedStateResets)
    {
        rewindEvents = new List<RewindEventResult>();
        suspectedStateResets = new List<SuspectedStateResetResult>();
        var completions = new Dictionary<(ulong SessionUid, int CarIndex, int LapNum), Completion>();
        var activeFrom = new Dictionary<(ulong SessionUid, int CarIndex, int LapNum), uint>();
        var rewindCounts = new Dictionary<(ulong SessionUid, int CarIndex, int LapNum), int>();

        foreach (var carGroup in samples
                     .Where(x => x.LapNum > 0)
                     .GroupBy(x => (x.SessionUid, x.CarIndex)))
        {
            var ordered = carGroup
                .OrderBy(x => x.OverallFrameIdentifier)
                .ThenBy(x => x.ReceivedAt)
                .ToList();
            var officialFlashbacks = flashbacks
                .Where(x => x.SessionUid == carGroup.Key.SessionUid)
                .OrderBy(x => x.OverallFrameIdentifier)
                .ThenBy(x => x.ReceivedAt)
                .ToList();
            var flashbackIndex = 0;
            LapDataSample? previous = null;
            foreach (var sample in ordered)
            {
                var officialRollbackHandled = false;
                while (flashbackIndex < officialFlashbacks.Count &&
                       officialFlashbacks[flashbackIndex].OverallFrameIdentifier <= sample.OverallFrameIdentifier)
                {
                    var signal = officialFlashbacks[flashbackIndex++];
                    var target = FindFlashbackTarget(ordered, signal);
                    if (target is not null)
                    {
                        RegisterRollback(
                            target.LapNum,
                            signal.OverallFrameIdentifier,
                            target,
                            $"official_flbk:target_frame={signal.TargetFrameIdentifier};target_time={signal.TargetSessionTime:0.000}",
                            activeFrom,
                            rewindCounts,
                            completions,
                            rewindEvents);
                        officialRollbackHandled = true;
                    }
                }

                var key = (sample.SessionUid, sample.CarIndex, sample.LapNum);
                activeFrom.TryAdd(key, sample.OverallFrameIdentifier);

                if (previous is not null)
                {
                    var sameLap = previous.LapNum == sample.LapNum;
                    var finishReset = IsFinishReset(previous, sample, trackLengthMeters);
                    var reasons = new List<string>();
                    if (!finishReset && sample.FrameIdentifier < previous.FrameIdentifier && sample.OverallFrameIdentifier >= previous.OverallFrameIdentifier)
                        reasons.Add("frame_identifier_backwards");
                    if (!finishReset && sample.SessionTime < previous.SessionTime - 0.25f)
                        reasons.Add("session_time_backwards");
                    if (sample.LapNum < previous.LapNum)
                        reasons.Add("lap_number_backwards");
                    if (!finishReset && sameLap && sample.LapDistance < previous.LapDistance - 50f)
                        reasons.Add("lap_distance_backwards");
                    if (!finishReset && sameLap && sample.CurrentLapTimeMs + 750 < previous.CurrentLapTimeMs)
                        reasons.Add("lap_time_backwards");

                    // Only FLBK confirms a rewind. Backwards counters without FLBK are retained
                    // as state-reset diagnostics and may delimit a new active branch, but they do
                    // not dirty the lap as a flashback.
                    if (reasons.Count > 0 && !officialRollbackHandled)
                    {
                        var rollbackLap = Math.Max(1, sample.LapNum);
                        RegisterSuspectedStateReset(
                            rollbackLap,
                            sample.OverallFrameIdentifier,
                            sample,
                            "suspected_state_reset:" + string.Join(';', reasons),
                            activeFrom,
                            completions,
                            suspectedStateResets);
                    }

                    if (sample.LapNum == previous.LapNum + 1 && sample.LastLapTimeMs > 0)
                    {
                        var completedKey = (previous.SessionUid, previous.CarIndex, previous.LapNum);
                        completions[completedKey] = new Completion(
                            sample.LastLapTimeMs,
                            sample.OverallFrameIdentifier,
                            $"next_lap_last_lap_time@{sample.OverallFrameIdentifier}");
                    }
                    else if (finishReset)
                    {
                        var completedKey = (previous.SessionUid, previous.CarIndex, previous.LapNum);
                        completions[completedKey] = new Completion(
                            sample.LastLapTimeMs,
                            sample.OverallFrameIdentifier,
                            $"finish_last_lap_time@{sample.OverallFrameIdentifier}");
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
            var invalidCount = active.Count(x => x.LapInvalid);

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
                ? completion!.Evidence
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

    private static LapDataSample? FindFlashbackTarget(IReadOnlyList<LapDataSample> ordered, FlashbackSignal signal)
    {
        return ordered
            .Where(x => x.OverallFrameIdentifier < signal.OverallFrameIdentifier)
            .OrderBy(x => Math.Abs((long)x.FrameIdentifier - signal.TargetFrameIdentifier))
            .ThenBy(x => Math.Abs(x.SessionTime - signal.TargetSessionTime))
            .ThenByDescending(x => x.OverallFrameIdentifier)
            .FirstOrDefault();
    }

    private static void RegisterRollback(
        int rollbackLap,
        uint activeOverallFrame,
        LapDataSample sample,
        string reason,
        Dictionary<(ulong SessionUid, int CarIndex, int LapNum), uint> activeFrom,
        Dictionary<(ulong SessionUid, int CarIndex, int LapNum), int> rewindCounts,
        Dictionary<(ulong SessionUid, int CarIndex, int LapNum), Completion> completions,
        List<RewindEventResult> rewindEvents)
    {
        var affectedKeys = activeFrom.Keys
            .Where(x => x.SessionUid == sample.SessionUid && x.CarIndex == sample.CarIndex && x.LapNum >= rollbackLap)
            .ToList();
        foreach (var affected in affectedKeys)
        {
            activeFrom[affected] = activeOverallFrame;
            rewindCounts[affected] = rewindCounts.GetValueOrDefault(affected) + 1;
            completions.Remove(affected);
        }

        var targetKey = (sample.SessionUid, sample.CarIndex, rollbackLap);
        activeFrom[targetKey] = activeOverallFrame;
        rewindCounts[targetKey] = Math.Max(1, rewindCounts.GetValueOrDefault(targetKey));
        completions.Remove(targetKey);
        rewindEvents.Add(new RewindEventResult(
            sample.SessionUid,
            sample.CarIndex,
            rollbackLap,
            sample.ReceivedAt,
            sample.SessionTime,
            activeOverallFrame,
            sample.LapDistance,
            sample.CurrentLapTimeMs,
            reason));
    }

    private static void RegisterSuspectedStateReset(
        int resetLap,
        uint activeOverallFrame,
        LapDataSample sample,
        string reason,
        Dictionary<(ulong SessionUid, int CarIndex, int LapNum), uint> activeFrom,
        Dictionary<(ulong SessionUid, int CarIndex, int LapNum), Completion> completions,
        List<SuspectedStateResetResult> stateResetEvents)
    {
        var affectedKeys = activeFrom.Keys
            .Where(x => x.SessionUid == sample.SessionUid && x.CarIndex == sample.CarIndex && x.LapNum >= resetLap)
            .ToList();
        foreach (var affected in affectedKeys)
        {
            activeFrom[affected] = activeOverallFrame;
            completions.Remove(affected);
        }

        var targetKey = (sample.SessionUid, sample.CarIndex, resetLap);
        activeFrom[targetKey] = activeOverallFrame;
        completions.Remove(targetKey);
        stateResetEvents.Add(new SuspectedStateResetResult(
            sample.SessionUid,
            sample.CarIndex,
            resetLap,
            sample.ReceivedAt,
            sample.SessionTime,
            activeOverallFrame,
            sample.LapDistance,
            sample.CurrentLapTimeMs,
            reason));
    }

    private static bool IsFinishReset(LapDataSample previous, LapDataSample sample, int trackLengthMeters)
    {
        if (previous.LapNum != sample.LapNum || sample.LastLapTimeMs == 0) return false;
        var startTolerance = trackLengthMeters > 0 ? Math.Max(100f, trackLengthMeters * 0.03f) : 180f;
        var endThreshold = trackLengthMeters > 0 ? trackLengthMeters * 0.80f : 1_000f;
        if (previous.LapDistance < endThreshold || sample.LapDistance > startTolerance) return false;
        if (sample.CurrentLapTimeMs > 2_000) return false;
        if (sample.LastLapTimeMs + 2_000 < previous.CurrentLapTimeMs) return false;
        return sample.ResultStatus >= 3 || sample.DriverStatus >= 3 ||
               sample.CurrentLapTimeMs + 750 < previous.CurrentLapTimeMs;
    }
}
