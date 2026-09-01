namespace F1TelemetryLab;

public sealed class ErsDecisionEngine
{
    private sealed record GapObservation(DateTimeOffset At, int? AheadMs, int? BehindMs);

    private sealed record TacticalContext(
        ErsTacticalMode Mode,
        ErsTacticalIntensity Intensity,
        double? AheadRateMsPerSecond,
        double? BehindRateMsPerSecond);

    private sealed record EnergyContext(
        ErsEnergyState State,
        double TargetPct,
        double MinimumPct,
        double DeltaToTargetPct,
        string NextCheckpointId,
        double NextTargetPct,
        double ProjectedNextPct);

    private readonly ErsControlProfile _profile;
    private readonly List<ErsEnergyCheckpoint> _checkpoints;
    private readonly Queue<GapObservation> _gapHistory = new();
    private readonly HashSet<(int Lap, string RuleId)> _finishedRules = new();
    private bool _recoveryActive;
    private ErsTacticalMode _tacticalMode = ErsTacticalMode.Neutral;
    private ErsTacticalIntensity _tacticalIntensity = ErsTacticalIntensity.None;
    private int _lastLap = -1;
    private string? _activeRuleId;
    private int _activeRuleLap = -1;
    private DateTimeOffset _activeRuleStartedAt;

    public ErsDecisionEngine(ErsControlProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _checkpoints = profile.EnergyPlan?.Checkpoints
            .OrderBy(point => point.DistanceM)
            .ToList() ?? new List<ErsEnergyCheckpoint>();
    }

    public ErsControlDecision Evaluate(ErsControlState state)
    {
        UpdateGapHistory(state);
        var tactical = UpdateTacticalContext(state);
        var energy = BuildEnergyContext(state);
        if (!state.AutomationAllowed)
            return ErsControlDecision.BlockedDecision(state, $"{ModePrefix(tactical)} {state.BlockReason}");

        UpdateLapState(state);
        UpdateRecoveryState(state.BatteryPct);

        var activeRule = ContinueActiveRule(state, tactical, energy);
        if (activeRule is not null)
            return Decision(state, activeRule, Explain(activeRule, state, tactical, energy));

        var selected = _profile.Rules
            .Where(rule => !_finishedRules.Contains((state.LapNumber, rule.Id)))
            .Where(rule => Matches(rule, state, tactical, energy))
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.DeploymentValue)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is not null)
        {
            _activeRuleId = selected.Id;
            _activeRuleLap = state.LapNumber;
            _activeRuleStartedAt = state.ReceivedAt;
            MarkOncePerLapSelection(selected, state);
            return Decision(state, selected, Explain(selected, state, tactical, energy));
        }

        return new ErsControlDecision(
            state.ReceivedAt,
            false,
            state.CurrentMode,
            _profile.DefaultMode,
            "default",
            "Normal lap baseline",
            $"{ModePrefix(tactical)} Baseline {_profile.DefaultMode}. {EnergySummary(state, energy, tactical)}",
            state.BatteryPct,
            state.LapNumber,
            state.LapDistanceM,
            state.GapAheadMs,
            state.GapBehindMs);
    }

    private ErsControlRule? ContinueActiveRule(ErsControlState state, TacticalContext tactical, EnergyContext energy)
    {
        if (_activeRuleId is null || _activeRuleLap != state.LapNumber) return null;
        var rule = _profile.Rules.FirstOrDefault(candidate => string.Equals(candidate.Id, _activeRuleId, StringComparison.Ordinal));
        if (rule is null)
        {
            ClearActiveRule(markFinished: false);
            return null;
        }

        var stillMatches = Matches(rule, state, tactical, energy);
        var expired = rule.MaximumActiveMs is > 0 &&
            state.ReceivedAt - _activeRuleStartedAt >= TimeSpan.FromMilliseconds(rule.MaximumActiveMs.Value);
        var higherPriorityRuleMatches = _profile.Rules.Any(candidate =>
            candidate.Priority > rule.Priority &&
            !_finishedRules.Contains((state.LapNumber, candidate.Id)) &&
            Matches(candidate, state, tactical, energy));
        if (stillMatches && !expired && !higherPriorityRuleMatches) return rule;

        ClearActiveRule(markFinished: rule.OncePerLap);
        return null;
    }

    private void ClearActiveRule(bool markFinished)
    {
        if (markFinished && _activeRuleId is not null && _activeRuleLap >= 0)
            _finishedRules.Add((_activeRuleLap, _activeRuleId));
        _activeRuleId = null;
        _activeRuleLap = -1;
        _activeRuleStartedAt = default;
    }

    private bool Matches(ErsControlRule rule, ErsControlState state, TacticalContext tactical, EnergyContext energy)
    {
        if (!ContainsDistance(rule, state.LapDistanceM)) return false;
        if (rule.MinimumThrottlePct is not null && state.ThrottlePct < rule.MinimumThrottlePct.Value) return false;
        if (rule.MinimumSpeedKph is not null && state.SpeedKph < rule.MinimumSpeedKph.Value) return false;
        if (rule.MinimumLapNumber is not null && state.LapNumber < rule.MinimumLapNumber.Value) return false;
        if (rule.MaximumLapsRemaining is not null &&
            (state.LapsRemaining is null || state.LapsRemaining > rule.MaximumLapsRemaining.Value)) return false;
        if (rule.MinimumLapsRemaining is not null &&
            (state.LapsRemaining is null || state.LapsRemaining < rule.MinimumLapsRemaining.Value)) return false;
        if (rule.DrsRequirement == ErsDrsRequirement.Active && !state.DrsActive) return false;
        if (rule.DrsRequirement == ErsDrsRequirement.Inactive && state.DrsActive) return false;

        var finalLapRelease = _profile.EnergyPlan is { FinalLapRelease: true } && state.LapsRemaining is <= 1;
        var configuredFloor = finalLapRelease && rule.FinalLapMinimumBatteryPct is not null
            ? rule.FinalLapMinimumBatteryPct
            : rule.MinimumBatteryPct;
        if (configuredFloor is not null && state.BatteryPct < configuredFloor.Value) return false;

        if (_profile.EnergyPlan is { } plan && rule.TargetMode > ErsDeployMode.Medium)
        {
            var dynamicFloor = energy.MinimumPct + (1 - rule.DeploymentValue) * plan.LowValueReservePct;
            if (finalLapRelease && rule.FinalLapMinimumBatteryPct is not null)
                dynamicFloor = Math.Max(plan.FinalLapFloorPct, rule.FinalLapMinimumBatteryPct.Value);
            if (state.BatteryPct < dynamicFloor) return false;
            if (rule.MinimumEnergySurplusPct is not null &&
                energy.DeltaToTargetPct < rule.MinimumEnergySurplusPct.Value) return false;
        }

        var legacyBattle = state.InBattle(_profile.BattleGapMs);
        return rule.Condition switch
        {
            ErsRuleCondition.Always => true,
            ErsRuleCondition.CriticalBattery => state.BatteryPct <= _profile.CriticalBatteryPct,
            ErsRuleCondition.LowBattery => _recoveryActive,
            ErsRuleCondition.Neutral => tactical.Mode == ErsTacticalMode.Neutral,
            ErsRuleCondition.Attack => tactical.Mode == ErsTacticalMode.Attack,
            ErsRuleCondition.Defend => tactical.Mode == ErsTacticalMode.Defend,
            ErsRuleCondition.AttackPressure => tactical is { Mode: ErsTacticalMode.Attack, Intensity: ErsTacticalIntensity.Pressure },
            ErsRuleCondition.AttackCritical => tactical is { Mode: ErsTacticalMode.Attack, Intensity: ErsTacticalIntensity.Critical },
            ErsRuleCondition.DefendPressure => tactical is { Mode: ErsTacticalMode.Defend, Intensity: ErsTacticalIntensity.Pressure },
            ErsRuleCondition.DefendCritical => tactical is { Mode: ErsTacticalMode.Defend, Intensity: ErsTacticalIntensity.Critical },
            ErsRuleCondition.Battle => legacyBattle,
            ErsRuleCondition.HighBattery => state.BatteryPct >= _profile.HighBatteryPct,
            ErsRuleCondition.EnergyDeficit => energy.State == ErsEnergyState.Deficit,
            ErsRuleCondition.EnergySurplus => energy.State == ErsEnergyState.Surplus,
            ErsRuleCondition.ClosingLaps => IsClosingLaps(state),
            ErsRuleCondition.FinalLap => state.LapsRemaining is <= 1,
            ErsRuleCondition.AttackOrHighBattery => tactical.Mode == ErsTacticalMode.Attack || state.BatteryPct >= _profile.HighBatteryPct,
            ErsRuleCondition.DefendOrHighBattery => tactical.Mode == ErsTacticalMode.Defend || state.BatteryPct >= _profile.HighBatteryPct,
            ErsRuleCondition.BattleOrHighBattery => legacyBattle || state.BatteryPct >= _profile.HighBatteryPct,
            _ => false
        };
    }

    private TacticalContext UpdateTacticalContext(ErsControlState state)
    {
        var rates = GapRates(state);
        if (_profile.Tactical is null)
        {
            var attackThreshold = _profile.AttackGapMs + (_tacticalMode == ErsTacticalMode.Attack ? _profile.TacticalExitMarginMs : 0);
            var defendThreshold = _profile.DefendGapMs + (_tacticalMode == ErsTacticalMode.Defend ? _profile.TacticalExitMarginMs : 0);
            var attack = state.InAttackRange(attackThreshold);
            var defend = state.InDefendRange(defendThreshold);
            _tacticalMode = ResolveMode(state, attack, defend, ErsTacticalIntensity.Pressure, ErsTacticalIntensity.Pressure,
                _profile.DefendPriorityMarginMs);
            _tacticalIntensity = _tacticalMode == ErsTacticalMode.Neutral
                ? ErsTacticalIntensity.None
                : ErsTacticalIntensity.Pressure;
            return new TacticalContext(_tacticalMode, _tacticalIntensity, rates.Ahead, rates.Behind);
        }

        var plan = _profile.Tactical;
        var attackExit = _tacticalMode == ErsTacticalMode.Attack ? plan.ExitMarginMs : 0;
        var defendExit = _tacticalMode == ErsTacticalMode.Defend ? plan.ExitMarginMs : 0;
        var attackCritical = state.InAttackRange(plan.AttackCriticalGapMs +
            (_tacticalMode == ErsTacticalMode.Attack && _tacticalIntensity == ErsTacticalIntensity.Critical ? plan.ExitMarginMs : 0));
        var defendCritical = state.InDefendRange(plan.DefendCriticalGapMs +
            (_tacticalMode == ErsTacticalMode.Defend && _tacticalIntensity == ErsTacticalIntensity.Critical ? plan.ExitMarginMs : 0));
        var attackPressure = state.InAttackRange(plan.AttackPressureGapMs + attackExit) ||
            IsRapidApproach(state.GapAheadMs, rates.Ahead, plan.AttackPressureGapMs, plan);
        var defendPressure = state.InDefendRange(plan.DefendPressureGapMs + defendExit) ||
            IsRapidApproach(state.GapBehindMs, rates.Behind, plan.DefendPressureGapMs, plan);
        var attackIntensity = attackCritical ? ErsTacticalIntensity.Critical :
            attackPressure ? ErsTacticalIntensity.Pressure : ErsTacticalIntensity.None;
        var defendIntensity = defendCritical ? ErsTacticalIntensity.Critical :
            defendPressure ? ErsTacticalIntensity.Pressure : ErsTacticalIntensity.None;

        _tacticalMode = ResolveMode(state, attackPressure, defendPressure, attackIntensity, defendIntensity,
            plan.DefendPriorityMarginMs);
        _tacticalIntensity = _tacticalMode switch
        {
            ErsTacticalMode.Attack => attackIntensity,
            ErsTacticalMode.Defend => defendIntensity,
            _ => ErsTacticalIntensity.None
        };
        return new TacticalContext(_tacticalMode, _tacticalIntensity, rates.Ahead, rates.Behind);
    }

    private static ErsTacticalMode ResolveMode(
        ErsControlState state,
        bool attack,
        bool defend,
        ErsTacticalIntensity attackIntensity,
        ErsTacticalIntensity defendIntensity,
        int defendPriorityMarginMs)
    {
        if (!attack && !defend) return ErsTacticalMode.Neutral;
        if (attack && !defend) return ErsTacticalMode.Attack;
        if (!attack) return ErsTacticalMode.Defend;
        if (defendIntensity > attackIntensity) return ErsTacticalMode.Defend;
        if (attackIntensity > defendIntensity) return ErsTacticalMode.Attack;
        if (state.GapAheadMs is not > 0) return ErsTacticalMode.Defend;
        if (state.GapBehindMs is not > 0) return ErsTacticalMode.Attack;
        return state.GapBehindMs.Value + defendPriorityMarginMs <= state.GapAheadMs.Value
            ? ErsTacticalMode.Defend
            : ErsTacticalMode.Attack;
    }

    private static bool IsRapidApproach(int? gapMs, double? rate, int pressureGapMs, ErsTacticalPlan plan) =>
        gapMs is > 0 &&
        gapMs <= pressureGapMs + plan.ClosingRateGapExtensionMs &&
        rate <= plan.RapidClosingRateMsPerSecond;

    private void UpdateGapHistory(ErsControlState state)
    {
        var windowMs = Math.Max(500, _profile.Tactical?.ClosingRateWindowMs ?? 3_000);
        while (_gapHistory.Count > 0 && state.ReceivedAt - _gapHistory.Peek().At > TimeSpan.FromMilliseconds(windowMs))
            _gapHistory.Dequeue();
        if (_gapHistory.Count == 0 || state.ReceivedAt > _gapHistory.Last().At)
            _gapHistory.Enqueue(new GapObservation(state.ReceivedAt, state.GapAheadMs, state.GapBehindMs));
    }

    private (double? Ahead, double? Behind) GapRates(ErsControlState state)
    {
        var ahead = Rate(_gapHistory, state.ReceivedAt, state.GapAheadMs, observation => observation.AheadMs);
        var behind = Rate(_gapHistory, state.ReceivedAt, state.GapBehindMs, observation => observation.BehindMs);
        return (ahead, behind);
    }

    private static double? Rate(
        IEnumerable<GapObservation> observations,
        DateTimeOffset now,
        int? current,
        Func<GapObservation, int?> selector)
    {
        if (current is not > 0) return null;
        var first = observations.FirstOrDefault(observation => selector(observation) is > 0);
        if (first is null) return null;
        var elapsed = (now - first.At).TotalSeconds;
        if (elapsed < 0.5) return null;
        return (current.Value - selector(first)!.Value) / elapsed;
    }

    private EnergyContext BuildEnergyContext(ErsControlState state)
    {
        if (_profile.EnergyPlan is null || _checkpoints.Count < 2)
        {
            var legacyState = state.BatteryPct <= _profile.RecoveryEnterPct
                ? ErsEnergyState.Deficit
                : state.BatteryPct >= _profile.HighBatteryPct
                    ? ErsEnergyState.Surplus
                    : ErsEnergyState.OnPlan;
            return new EnergyContext(legacyState, _profile.RecoveryExitPct, _profile.RecoveryEnterPct,
                state.BatteryPct - _profile.RecoveryExitPct, "legacy", _profile.RecoveryExitPct, state.BatteryPct);
        }

        var distance = NormalizedDistance(state.LapDistanceM);
        var nextIndex = _checkpoints.FindIndex(point => point.DistanceM > distance);
        if (nextIndex < 0) nextIndex = 0;
        var previousIndex = nextIndex == 0 ? _checkpoints.Count - 1 : nextIndex - 1;
        var previous = _checkpoints[previousIndex];
        var next = _checkpoints[nextIndex];
        var previousDistance = previous.DistanceM;
        var nextDistance = next.DistanceM;
        var currentDistance = distance;
        if (nextIndex == 0)
        {
            nextDistance += _profile.TrackLengthM;
            if (currentDistance < previousDistance) currentDistance += _profile.TrackLengthM;
        }
        var span = Math.Max(1, nextDistance - previousDistance);
        var progress = Math.Clamp((currentDistance - previousDistance) / span, 0, 1);
        var target = Lerp(previous.TargetPct, next.TargetPct, progress);
        var minimum = Lerp(previous.MinimumPct, next.MinimumPct, progress);
        var delta = state.BatteryPct - target;
        var energyState = state.BatteryPct < minimum || delta < -_profile.EnergyPlan.TargetTolerancePct
            ? ErsEnergyState.Deficit
            : delta >= _profile.EnergyPlan.SurplusReleasePct
                ? ErsEnergyState.Surplus
                : ErsEnergyState.OnPlan;
        var projectedNext = state.BatteryPct + (next.TargetPct - target);
        return new EnergyContext(energyState, target, minimum, delta, next.Id, next.TargetPct, projectedNext);
    }

    private static double Lerp(double start, double end, double progress) => start + (end - start) * progress;

    private bool IsClosingLaps(ErsControlState state) =>
        _profile.EnergyPlan is { ClosingLaps: > 0 } plan &&
        state.LapsRemaining is > 0 && state.LapsRemaining <= plan.ClosingLaps;

    private bool ContainsDistance(ErsControlRule rule, double distanceM)
    {
        var distance = NormalizedDistance(distanceM);
        if (rule.StartM <= rule.EndM) return distance >= rule.StartM && distance <= rule.EndM;
        return distance >= rule.StartM || distance <= rule.EndM;
    }

    private void UpdateRecoveryState(double batteryPct)
    {
        if (_recoveryActive && batteryPct >= _profile.RecoveryExitPct) _recoveryActive = false;
        else if (!_recoveryActive && batteryPct <= _profile.RecoveryEnterPct) _recoveryActive = true;
    }

    private void UpdateLapState(ErsControlState state)
    {
        var lap = state.LapNumber;
        if (lap == _lastLap) return;
        if (lap < _lastLap)
        {
            _finishedRules.Clear();
            _gapHistory.Clear();
        }
        else
        {
            _finishedRules.RemoveWhere(item => item.Lap < lap - 1);
        }

        var carryAcrossLine = lap == _lastLap + 1 && ActiveRuleCrossesStartFinish(state.LapDistanceM);
        if (carryAcrossLine)
            _activeRuleLap = lap;
        else
            ClearActiveRule(markFinished: false);
        _lastLap = lap;
    }

    private void MarkOncePerLapSelection(ErsControlRule rule, ErsControlState state)
    {
        if (!rule.OncePerLap) return;
        _finishedRules.Add((state.LapNumber, rule.Id));
        if (rule.StartM > rule.EndM && NormalizedDistance(state.LapDistanceM) >= rule.StartM)
            _finishedRules.Add((state.LapNumber + 1, rule.Id));
    }

    private bool ActiveRuleCrossesStartFinish(double distanceM)
    {
        if (_activeRuleId is null) return false;
        var rule = _profile.Rules.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _activeRuleId, StringComparison.Ordinal));
        return rule is { StartM: > 0 } && rule.StartM > rule.EndM && NormalizedDistance(distanceM) <= rule.EndM;
    }

    private double NormalizedDistance(double distanceM)
    {
        var length = Math.Max(1, _profile.TrackLengthM);
        return ((distanceM % length) + length) % length;
    }

    private static ErsControlDecision Decision(ErsControlState state, ErsControlRule rule, string reason) => new(
        state.ReceivedAt,
        false,
        state.CurrentMode,
        rule.TargetMode,
        rule.Id,
        rule.Segment,
        reason,
        state.BatteryPct,
        state.LapNumber,
        state.LapDistanceM,
        state.GapAheadMs,
        state.GapBehindMs);

    private string Explain(ErsControlRule rule, ErsControlState state, TacticalContext tactical, EnergyContext energy)
    {
        var condition = rule.Condition switch
        {
            ErsRuleCondition.CriticalBattery => $"critical battery {state.BatteryPct:0}%",
            ErsRuleCondition.LowBattery => $"legacy recovery below {_profile.RecoveryExitPct:0}%",
            ErsRuleCondition.Neutral => "neutral race state",
            ErsRuleCondition.Attack => "car ahead within the attack window",
            ErsRuleCondition.Defend => "car behind within the defence window",
            ErsRuleCondition.AttackPressure => "attack pressure",
            ErsRuleCondition.AttackCritical => "critical attack opportunity",
            ErsRuleCondition.DefendPressure => "defence pressure",
            ErsRuleCondition.DefendCritical => "critical rear threat",
            ErsRuleCondition.Battle => "legacy battle gap",
            ErsRuleCondition.HighBattery => $"battery at least {_profile.HighBatteryPct:0}%",
            ErsRuleCondition.EnergyDeficit => "SOC below the local minimum corridor",
            ErsRuleCondition.EnergySurplus => "SOC surplus above the local target",
            ErsRuleCondition.ClosingLaps => "closing-laps release",
            ErsRuleCondition.FinalLap => "final-lap release",
            ErsRuleCondition.AttackOrHighBattery => tactical.Mode == ErsTacticalMode.Attack
                ? "attack window"
                : $"battery at least {_profile.HighBatteryPct:0}%",
            ErsRuleCondition.DefendOrHighBattery => tactical.Mode == ErsTacticalMode.Defend
                ? "defence window"
                : $"battery at least {_profile.HighBatteryPct:0}%",
            ErsRuleCondition.BattleOrHighBattery => state.InBattle(_profile.BattleGapMs)
                ? "legacy battle gap"
                : $"battery at least {_profile.HighBatteryPct:0}%",
            _ => "profile rule"
        };
        var note = string.IsNullOrWhiteSpace(rule.Note) ? "" : $" {rule.Note}";
        return $"{ModePrefix(tactical)} {condition}.{note} {EnergySummary(state, energy, tactical)}";
    }

    private static string EnergySummary(ErsControlState state, EnergyContext energy, TacticalContext tactical)
    {
        var aheadRate = tactical.AheadRateMsPerSecond is null ? "n/a" : $"{tactical.AheadRateMsPerSecond:+0;-0;0} ms/s";
        var behindRate = tactical.BehindRateMsPerSecond is null ? "n/a" : $"{tactical.BehindRateMsPerSecond:+0;-0;0} ms/s";
        var laps = state.LapsRemaining?.ToString() ?? "?";
        return $"SOC {state.BatteryPct:0.0}% vs plan {energy.TargetPct:0.0}%/{energy.MinimumPct:0.0}% ({energy.State}); " +
               $"next {energy.NextCheckpointId} projected {energy.ProjectedNextPct:0.0}% vs {energy.NextTargetPct:0.0}%; " +
               $"DRS {(state.DrsActive ? "active" : "inactive")}; gaps trend A {aheadRate}, D {behindRate}; laps left {laps}.";
    }

    private static string ModePrefix(TacticalContext tactical) => tactical.Mode == ErsTacticalMode.Neutral
        ? "[NEUTRAL]"
        : $"[{tactical.Mode.ToString().ToUpperInvariant()}][{tactical.Intensity.ToString().ToUpperInvariant()}]";
}

public static class ErsModeTransition
{
    public static ErsInputDirection? Next(ErsDeployMode current, ErsDeployMode target)
    {
        if (current == target) return null;
        return target > current ? ErsInputDirection.Increase : ErsInputDirection.Decrease;
    }

    public static ErsDeployMode ExpectedAfter(ErsDeployMode current, ErsInputDirection direction) => direction switch
    {
        ErsInputDirection.Increase => (ErsDeployMode)Math.Min((int)ErsDeployMode.Boost, (int)current + 1),
        _ => (ErsDeployMode)Math.Max((int)ErsDeployMode.None, (int)current - 1)
    };
}
