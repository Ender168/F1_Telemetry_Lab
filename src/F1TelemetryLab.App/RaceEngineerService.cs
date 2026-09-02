namespace F1TelemetryLab;

public sealed class RaceEngineerService
{
    private sealed class LapAccumulator
    {
        public required int LapNumber { get; init; }
        public bool InvalidSeen { get; set; }
        public bool PitSeen { get; set; }
        public bool SafetyCarSeen { get; set; }
        public CarStatusSample? StartStatus { get; init; }
        public CarDamageSample? StartDamage { get; init; }
        public int LastPosition { get; set; }
    }

    private readonly RaceEngineerProfileLoadResult _profiles;
    private readonly ErsProfileLoadResult _ersProfiles;
    private readonly Action<CompletedLiveLap>? _completedLapSink;
    private readonly Action<RaceEngineerProfile>? _profileSink;
    private readonly Action<string>? _log;
    private readonly Dictionary<int, LapDataSample> _lapRows = new();
    private readonly List<CompletedLiveLap> _completedLaps = new();
    private RaceEngineerSnapshot _snapshot = RaceEngineerSnapshot.Waiting;
    private SessionControlSample? _session;
    private LapDataSample? _playerLap;
    private CarTelemetrySample? _telemetry;
    private CarStatusSample? _status;
    private CarDamageSample? _damage;
    private TyreSetPacketSample? _tyreSets;
    private RaceEngineerProfile? _profile;
    private ErsControlProfile? _ersProfile;
    private ErsAutopilotStatus? _autopilotStatus;
    private ErsControlDecision? _autopilotDecision;
    private LapAccumulator? _currentLap;
    private ulong _sessionUid;
    private string _lastError = "";

    public RaceEngineerService(
        RaceEngineerProfileLoadResult profiles,
        ErsProfileLoadResult ersProfiles,
        Action<CompletedLiveLap>? completedLapSink = null,
        Action<RaceEngineerProfile>? profileSink = null,
        Action<string>? log = null)
    {
        _profiles = profiles;
        _ersProfiles = ersProfiles;
        _completedLapSink = completedLapSink;
        _profileSink = profileSink;
        _log = log;
        foreach (var warning in profiles.Warnings) _log?.Invoke("Race engineer profile warning: " + warning);
    }

    public RaceEngineerSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void SetAutopilotDecision(ErsAutopilotStatus? status, ErsControlDecision? decision)
    {
        _autopilotStatus = status;
        _autopilotDecision = decision;
    }

    public void ProcessPacket(byte[] payload, DateTimeOffset receivedAt)
    {
        try
        {
            if (!F12026Parser.TryParseHeader(payload, out var header) || header.PacketFormat != AppInfo.SupportedPacketFormat) return;
            if (_sessionUid != 0 && _sessionUid != header.SessionUid) Reset(header.SessionUid);
            _sessionUid = header.SessionUid;
            switch (header.PacketId)
            {
                case 1:
                    _session = F12026Parser.TryParseSessionControl(payload, receivedAt);
                    SelectProfile();
                    break;
                case 2:
                    foreach (var row in F12026Parser.ParseLapDataPacket(payload, receivedAt))
                    {
                        _lapRows[row.CarIndex] = row;
                        if (row.IsPlayer) HandlePlayerLap(row);
                    }
                    break;
                case 6:
                    _telemetry = F12026Parser.ParseCarTelemetryPacket(payload, receivedAt).FirstOrDefault(x => x.IsPlayer);
                    break;
                case 7:
                    _status = F12026Parser.ParseCarStatusPacket(payload, receivedAt).FirstOrDefault(x => x.IsPlayer);
                    break;
                case 10:
                    _damage = F12026Parser.ParseCarDamagePacket(payload, receivedAt).FirstOrDefault(x => x.IsPlayer);
                    break;
                case 12:
                    var sets = F12026Parser.ParseTyreSetsPacket(payload, receivedAt);
                    if (sets is { IsPlayer: true }) _tyreSets = sets;
                    break;
                default:
                    return;
            }

            if (_currentLap is not null)
            {
                _currentLap.SafetyCarSeen |= _session?.SafetyCarStatus != 0;
                _currentLap.PitSeen |= _playerLap?.PitStatus > 0;
                _currentLap.InvalidSeen |= _playerLap?.LapInvalid == true;
            }
            Publish(receivedAt);
        }
        catch (Exception ex)
        {
            if (!string.Equals(_lastError, ex.Message, StringComparison.Ordinal))
            {
                _lastError = ex.Message;
                _log?.Invoke("Race engineer warning: " + ex.Message);
            }
        }
    }

    private void HandlePlayerLap(LapDataSample row)
    {
        _playerLap = row;
        if (row.LapNum <= 0) return;
        if (_currentLap is null)
        {
            _currentLap = StartLap(row);
            return;
        }

        if (row.LapNum < _currentLap.LapNumber)
        {
            _completedLaps.RemoveAll(x => x.LapNumber >= row.LapNum);
            _currentLap = StartLap(row);
            return;
        }

        if (row.LapNum > _currentLap.LapNumber)
        {
            CompleteLap(_currentLap, row.LastLapTimeMs);
            _currentLap = StartLap(row);
            return;
        }

        _currentLap.InvalidSeen |= row.LapInvalid;
        _currentLap.PitSeen |= row.PitStatus > 0;
        _currentLap.SafetyCarSeen |= _session?.SafetyCarStatus != 0;
        _currentLap.LastPosition = row.Position;
    }

    private LapAccumulator StartLap(LapDataSample row) => new()
    {
        LapNumber = row.LapNum,
        InvalidSeen = row.LapInvalid,
        PitSeen = row.PitStatus > 0,
        SafetyCarSeen = _session?.SafetyCarStatus != 0,
        StartStatus = _status,
        StartDamage = _damage,
        LastPosition = row.Position
    };

    private void CompleteLap(LapAccumulator accumulator, uint lapTimeMs)
    {
        if (lapTimeMs is < 30_000 or > 900_000) return;
        var startWear = MaxWear(accumulator.StartDamage);
        var endWear = MaxWear(_damage);
        var deltaWear = endWear >= startWear ? endWear - startWear : 0;
        var capacity = _profile?.ErsBatteryCapacityJ ?? _ersProfile?.BatteryCapacityJ ?? 4_000_000;
        var startErs = EnergyPct(accumulator.StartStatus?.ErsStoreEnergy, capacity);
        var endErs = EnergyPct(_status?.ErsStoreEnergy, capacity);
        var pit = accumulator.PitSeen ||
                  (accumulator.StartStatus is not null && _status is not null &&
                   (accumulator.StartStatus.VisualTyreCompound != _status.VisualTyreCompound ||
                    _status.TyresAgeLaps < accumulator.StartStatus.TyresAgeLaps));
        var row = new CompletedLiveLap(
            _sessionUid,
            accumulator.LapNumber,
            lapTimeMs,
            !accumulator.InvalidSeen && !pit && !accumulator.SafetyCarSeen,
            pit,
            accumulator.SafetyCarSeen,
            _status?.VisualTyreCompound ?? accumulator.StartStatus?.VisualTyreCompound ?? 0,
            _status?.TyresAgeLaps ?? accumulator.StartStatus?.TyresAgeLaps ?? 0,
            startWear,
            endWear,
            deltaWear,
            startErs,
            endErs,
            endErs - startErs,
            accumulator.LastPosition,
            "last_lap_time_ms + lap transition");
        _completedLaps.RemoveAll(x => x.LapNumber == row.LapNumber);
        _completedLaps.Add(row);
        if (_completedLaps.Count > 30) _completedLaps.RemoveRange(0, _completedLaps.Count - 30);
        _completedLapSink?.Invoke(row);
    }

    private void SelectProfile()
    {
        if (_session is null) return;
        if (_session.SessionType is < 15 or > 17) return;
        var selected = _profiles.Find(_session.TrackId, _session.SessionType) ?? BuildFallback(_session);
        if (_profile?.ProfileId == selected.ProfileId) return;
        _profile = selected;
        _ersProfile = _ersProfiles.Find(_session.TrackId, _session.SessionType);
        _profileSink?.Invoke(selected);
        _log?.Invoke($"Race engineer profile selected: {selected.ProfileId}.");
    }

    private void Publish(DateTimeOffset now)
    {
        var tyres = BuildTyreAdvice();
        var pit = BuildPitAdvice();
        var ers = BuildErsAdvice();
        Volatile.Write(ref _snapshot, new RaceEngineerSnapshot(
            now,
            _profile?.ProfileId ?? "",
            _playerLap?.LapNum ?? 0,
            _playerLap?.Position ?? 0,
            _completedLaps.TakeLast(3).ToArray(),
            tyres,
            pit,
            ers));
    }

    private TyreLifeAdvice BuildTyreAdvice()
    {
        var profile = _profile;
        var damage = _damage;
        var status = _status;
        if (profile is null || damage is null || status is null)
            return RaceEngineerSnapshot.Waiting.Tyres;

        var values = new Dictionary<string, double>
        {
            ["FL"] = damage.TyreWearFl,
            ["FR"] = damage.TyreWearFr,
            ["RL"] = damage.TyreWearRl,
            ["RR"] = damage.TyreWearRr
        };
        var worst = values.OrderByDescending(x => x.Value).First();
        var observations = _completedLaps
            .Where(x => x.Clean && x.VisualCompound == status.VisualTyreCompound && x.TyreWearDeltaPct is > 0.02 and < 10)
            .TakeLast(7)
            .Select(x => x.TyreWearDeltaPct)
            .ToList();
        double? rate = observations.Count > 0 ? Median(observations) : null;
        var prior = profile.TyrePrior(status.VisualTyreCompound);
        if (rate is null && prior > 0) rate = prior;
        if (rate is null or <= 0)
        {
            return new TyreLifeAdvice(true, status.VisualTyreCompound, status.TyresAgeLaps,
                damage.TyreWearFl, damage.TyreWearFr, damage.TyreWearRl, damage.TyreWearRr,
                worst.Key, worst.Value, null, null, null, profile.SafeTyreWearPct, 0,
                AdviceConfidence.Low, "No completed clean lap is available for the current tyre set.");
        }

        var rawRemaining = Math.Max(0, (profile.SafeTyreWearPct - worst.Value) / rate.Value);
        var uncertainty = observations.Count switch
        {
            >= 5 => Math.Max(1, rawRemaining * 0.15),
            >= 2 => Math.Max(2, rawRemaining * 0.25),
            _ => Math.Max(3, rawRemaining * 0.35)
        };
        var low = Math.Max(0, (int)Math.Floor(rawRemaining - uncertainty));
        var high = Math.Max(low, (int)Math.Ceiling(rawRemaining + uncertainty));
        var fitted = _tyreSets?.Sets.FirstOrDefault(x => x.Fitted) ??
                     _tyreSets?.Sets.FirstOrDefault(x => x.SetIndex == _tyreSets.FittedIndex);
        if (fitted is { UsableLifeLaps: > 0 })
        {
            var gameRemaining = Math.Max(0, fitted.UsableLifeLaps - status.TyresAgeLaps);
            high = Math.Min(high, gameRemaining);
            low = Math.Min(low, high);
        }

        var learned = profile.LearnedTyreSamples.GetValueOrDefault(status.VisualTyreCompound);
        var confidence = observations.Count >= 5 || learned >= 8
            ? AdviceConfidence.High
            : observations.Count >= 2 || learned >= 3
                ? AdviceConfidence.Medium
                : AdviceConfidence.Low;
        return new TyreLifeAdvice(true, status.VisualTyreCompound, status.TyresAgeLaps,
            damage.TyreWearFl, damage.TyreWearFr, damage.TyreWearRl, damage.TyreWearRr,
            worst.Key, worst.Value, rate, low, high, profile.SafeTyreWearPct, observations.Count,
            confidence, observations.Count > 0 ? "Observed clean completed laps on this set." : "Track and compound prior; live observations are still limited.");
    }

    private PitPositionAdvice BuildPitAdvice()
    {
        var profile = _profile;
        var player = _playerLap;
        if (profile is null || player is null || player.Position <= 0)
            return RaceEngineerSnapshot.Waiting.Pit;
        var ordered = _lapRows.Values
            .Where(x => x.Position > 0 && x.ResultStatus is 2 or 3 or 4)
            .GroupBy(x => x.Position)
            .Select(x => x.OrderByDescending(r => r.ReceivedAt).First())
            .OrderBy(x => x.Position)
            .ToList();
        if (ordered.Count < 2) return RaceEngineerSnapshot.Waiting.Pit;

        var gapMap = BuildLeaderGaps(ordered);
        if (!gapMap.TryGetValue(player.CarIndex, out var playerGap)) return RaceEngineerSnapshot.Waiting.Pit;
        var loss = _session?.SafetyCarStatus switch
        {
            1 => profile.PitLossSafetyCarSeconds,
            2 => profile.PitLossVscSeconds,
            _ => profile.PitLossGreenSeconds
        };
        var uncertainty = profile.PitLossUncertaintySeconds + (profile.LearnedPitSamples == 0 ? 0.7 : 0);
        var best = PositionAfterLoss(gapMap, player.CarIndex, playerGap + Math.Max(0, loss - uncertainty) * 1000);
        var worst = PositionAfterLoss(gapMap, player.CarIndex, playerGap + (loss + uncertainty) * 1000);
        var center = playerGap + loss * 1000;
        var nearby = gapMap.Count(x => x.Key != player.CarIndex && Math.Abs(x.Value - center) <= 5000);
        var generic = profile.ProfileId.StartsWith("generic-track-", StringComparison.Ordinal);
        var confidence = generic
            ? AdviceConfidence.Low
            : ordered.Count >= 10 && profile.LearnedPitSamples >= 2
                ? AdviceConfidence.High
                : ordered.Count >= 6
                    ? AdviceConfidence.Medium
                    : AdviceConfidence.Low;
        return new PitPositionAdvice(true, Math.Min(best, worst), Math.Max(best, worst), loss, uncertainty, nearby,
            confidence, profile.LearnedPitSamples > 0
                ? $"Track pit loss learned from {profile.LearnedPitSamples} recorded stop(s)."
                : "Track baseline plus current live gaps; no learned stop is available yet.");
    }

    private ErsRaceAdvice BuildErsAdvice()
    {
        var profile = _profile;
        var status = _status;
        var player = _playerLap;
        if (profile is null || status is null || player is null)
            return RaceEngineerSnapshot.Waiting.Ers;

        if (_autopilotDecision is { } decision)
        {
            var aggression = decision.EnergyState switch
            {
                ErsEnergyState.Critical => ErsAggressionAdvice.Critical,
                ErsEnergyState.Conserve => ErsAggressionAdvice.Save,
                ErsEnergyState.Surplus => ErsAggressionAdvice.Aggressive,
                _ when decision.TargetMode > ErsDeployMode.Medium => ErsAggressionAdvice.Aggressive,
                _ => ErsAggressionAdvice.OnPlan
            };
            var next = FindNextBoost(player.LapDistance, profile.TrackLengthM);
            return new ErsRaceAdvice(
                true,
                decision.BatteryPct,
                (int)decision.CurrentMode,
                decision.EnergyMinimumPct,
                decision.EnergyTargetPct,
                aggression,
                null,
                decision.Segment,
                next.Segment,
                next.DistanceM,
                AdviceConfidence.High,
                decision.Reason)
            {
                TacticalMode = decision.TacticalMode,
                TacticalIntensity = decision.TacticalIntensity,
                EnergyState = decision.EnergyState,
                TargetMode = decision.TargetMode,
                RuleId = decision.RuleId,
                ProjectedNextPct = decision.ProjectedNextPct,
                NextMinimumPct = decision.NextMinimumPct,
                NextCheckpointId = decision.NextCheckpointId,
                ProjectionSource = decision.ProjectionSource,
                RuleBudgetRemainingPct = decision.RuleBudgetRemainingPct,
                AutomationState = _autopilotStatus?.State ?? "Decision",
                GapAheadMs = decision.GapAheadMs,
                GapBehindMs = decision.GapBehindMs
            };
        }

        var battery = EnergyPct(status.ErsStoreEnergy, profile.ErsBatteryCapacityJ);
        var band = profile.EnergyBand(player.LapDistance);
        var gapAhead = player.DeltaToCarInFrontMs;
        var behind = _lapRows.Values.FirstOrDefault(x => x.Position == player.Position + 1);
        var inBattle = gapAhead is > 0 and <= 1200 || behind?.DeltaToCarInFrontMs is > 0 and <= 1200;
        var nearFinish = _session is { TotalLaps: > 0 } && player.LapNum >= _session.TotalLaps - 1;
        var aggression = battery <= profile.ErsCriticalPct
            ? ErsAggressionAdvice.Critical
            : battery < band.TargetMinPct
                ? ErsAggressionAdvice.Save
                : battery > band.TargetMaxPct || (inBattle && battery >= band.TargetMinPct + 5) || (nearFinish && battery >= band.TargetMinPct)
                    ? ErsAggressionAdvice.Aggressive
                    : ErsAggressionAdvice.OnPlan;
        int? aggressiveLaps = null;
        if (aggression == ErsAggressionAdvice.Aggressive)
        {
            var surplusJ = Math.Max(profile.ErsAggressiveEnergyPerLapJ, (battery - band.TargetMaxPct) / 100d * profile.ErsBatteryCapacityJ);
            aggressiveLaps = Math.Clamp((int)Math.Floor(surplusJ / profile.ErsAggressiveEnergyPerLapJ), 1, 9);
        }
        var next = FindNextBoost(player.LapDistance, profile.TrackLengthM);
        var reason = aggression switch
        {
            ErsAggressionAdvice.Critical => $"Battery is at or below the {profile.ErsCriticalPct:0}% reserve.",
            ErsAggressionAdvice.Save => $"Battery is {band.TargetMinPct - battery:0}% below the segment target.",
            ErsAggressionAdvice.Aggressive when inBattle => "A car is within the configured battle gap and reserve is available.",
            ErsAggressionAdvice.Aggressive => $"Battery is {Math.Max(0, battery - band.TargetMaxPct):0}% above the target corridor.",
            _ => "Battery is inside the track-profile target corridor."
        };
        return new ErsRaceAdvice(true, battery, status.ErsDeployMode, band.TargetMinPct, band.TargetMaxPct,
            aggression, aggressiveLaps, band.Segment, next.Segment, next.DistanceM,
            profile.ProfileId.StartsWith("generic-track-", StringComparison.Ordinal)
                ? AdviceConfidence.Low
                : _ersProfile is null ? AdviceConfidence.Medium : AdviceConfidence.High,
            reason);
    }

    private (string Segment, int? DistanceM) FindNextBoost(double currentDistance, int trackLength)
    {
        if (_ersProfile is null || trackLength <= 0) return ("", null);
        var candidates = _ersProfile.Rules
            .Where(x => x.TargetMode == ErsDeployMode.Boost)
            .Select(x => new
            {
                x.Segment,
                Distance = x.StartM >= currentDistance ? x.StartM - currentDistance : trackLength - currentDistance + x.StartM
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        return candidates is null ? ("", null) : (candidates.Segment, (int)Math.Round(candidates.Distance));
    }

    private static Dictionary<int, double> BuildLeaderGaps(IReadOnlyList<LapDataSample> ordered)
    {
        var result = new Dictionary<int, double>();
        double cumulative = 0;
        foreach (var row in ordered)
        {
            if (row.Position == 1) cumulative = 0;
            else if (row.DeltaToRaceLeaderMs > 0) cumulative = row.DeltaToRaceLeaderMs;
            else if (row.DeltaToCarInFrontMs is > 0 and < 120_000) cumulative += row.DeltaToCarInFrontMs;
            else cumulative += 3000;
            result[row.CarIndex] = cumulative;
        }
        return result;
    }

    private static int PositionAfterLoss(IReadOnlyDictionary<int, double> gaps, int playerCar, double projectedGap)
    {
        return 1 + gaps.Count(x => x.Key != playerCar && x.Value < projectedGap);
    }

    private static RaceEngineerProfile BuildFallback(SessionControlSample session) => new()
    {
        ProfileId = $"generic-track-{session.TrackId}",
        DisplayName = "Generic low-confidence race profile",
        TrackId = session.TrackId,
        TrackName = TrackNames.GetTrackName(session.TrackId),
        TrackLengthM = Math.Max(1, session.TrackLengthM),
        SessionTypes = new List<int> { session.SessionType },
        SafeTyreWearPct = 75,
        PitLossGreenSeconds = 23,
        PitLossVscSeconds = 15,
        PitLossSafetyCarSeconds = 12,
        PitLossUncertaintySeconds = 3,
        TyreWearPriors = new List<TyreWearPrior>
        {
            new() { VisualCompound = 16, WearPctPerLap = 2.5 },
            new() { VisualCompound = 17, WearPctPerLap = 1.8 },
            new() { VisualCompound = 18, WearPctPerLap = 1.4 }
        },
        ErsEnergyBands = new List<ErsEnergyBand>
        {
            new() { Segment = "Generic", StartM = 0, EndM = Math.Max(1, session.TrackLengthM), TargetMinPct = 35, TargetMaxPct = 65 }
        }
    };

    private void Reset(ulong sessionUid)
    {
        _sessionUid = sessionUid;
        _session = null;
        _playerLap = null;
        _telemetry = null;
        _status = null;
        _damage = null;
        _tyreSets = null;
        _profile = null;
        _ersProfile = null;
        _autopilotStatus = null;
        _autopilotDecision = null;
        _currentLap = null;
        _lapRows.Clear();
        _completedLaps.Clear();
        Volatile.Write(ref _snapshot, RaceEngineerSnapshot.Waiting);
    }

    private static double MaxWear(CarDamageSample? value) => value is null
        ? 0
        : Math.Max(Math.Max(value.TyreWearFl, value.TyreWearFr), Math.Max(value.TyreWearRl, value.TyreWearRr));

    private static double EnergyPct(float? energyJ, double capacityJ) => energyJ is null || capacityJ <= 0
        ? 0
        : Math.Clamp(energyJ.Value / capacityJ * 100d, 0, 100);

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2d;
    }
}
