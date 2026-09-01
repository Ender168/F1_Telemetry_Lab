namespace F1TelemetryLab;

public sealed class ErsDecisionEngine
{
    private readonly ErsControlProfile _profile;
    private readonly HashSet<(int Lap, string RuleId)> _finishedRules = new();
    private bool _recoveryActive;
    private int _lastLap = -1;
    private string? _activeRuleId;
    private int _activeRuleLap = -1;
    private DateTimeOffset _activeRuleStartedAt;

    public ErsDecisionEngine(ErsControlProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public ErsControlDecision Evaluate(ErsControlState state)
    {
        if (!state.AutomationAllowed)
            return ErsControlDecision.BlockedDecision(state, state.BlockReason);

        UpdateLapState(state);
        UpdateRecoveryState(state.BatteryPct);

        var activeRule = ContinueActiveRule(state);
        if (activeRule is not null)
            return Decision(state, activeRule, Explain(activeRule, state));

        var selected = _profile.Rules
            .Where(rule => !_finishedRules.Contains((state.LapNumber, rule.Id)))
            .Where(rule => Matches(rule, state))
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is not null)
        {
            _activeRuleId = selected.Id;
            _activeRuleLap = state.LapNumber;
            _activeRuleStartedAt = state.ReceivedAt;
            MarkOncePerLapSelection(selected, state);
            return Decision(state, selected, Explain(selected, state));
        }

        return new ErsControlDecision(
            state.ReceivedAt,
            false,
            state.CurrentMode,
            _profile.DefaultMode,
            "default",
            "Normal lap baseline",
            _recoveryActive
                ? $"Recovery is active, but this segment stays {_profile.DefaultMode}; wait for a configured recovery zone."
                : $"Baseline {_profile.DefaultMode} mode.",
            state.BatteryPct,
            state.LapNumber,
            state.LapDistanceM,
            state.GapAheadMs,
            state.GapBehindMs);
    }

    private ErsControlRule? ContinueActiveRule(ErsControlState state)
    {
        if (_activeRuleId is null || _activeRuleLap != state.LapNumber) return null;
        var rule = _profile.Rules.FirstOrDefault(candidate => string.Equals(candidate.Id, _activeRuleId, StringComparison.Ordinal));
        if (rule is null)
        {
            ClearActiveRule(markFinished: false);
            return null;
        }

        var stillMatches = Matches(rule, state);
        var expired = rule.MaximumActiveMs is > 0 &&
            state.ReceivedAt - _activeRuleStartedAt >= TimeSpan.FromMilliseconds(rule.MaximumActiveMs.Value);
        var higherPriorityRuleMatches = _profile.Rules.Any(candidate =>
            candidate.Priority > rule.Priority &&
            !_finishedRules.Contains((state.LapNumber, candidate.Id)) &&
            Matches(candidate, state));
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

    private bool Matches(ErsControlRule rule, ErsControlState state)
    {
        if (!ContainsDistance(rule, state.LapDistanceM)) return false;
        if (rule.MinimumBatteryPct is not null && state.BatteryPct < rule.MinimumBatteryPct.Value) return false;
        if (rule.MinimumThrottlePct is not null && state.ThrottlePct < rule.MinimumThrottlePct.Value) return false;
        if (rule.MinimumSpeedKph is not null && state.SpeedKph < rule.MinimumSpeedKph.Value) return false;

        var battle = state.InBattle(_profile.BattleGapMs);
        return rule.Condition switch
        {
            ErsRuleCondition.Always => true,
            ErsRuleCondition.CriticalBattery => state.BatteryPct <= _profile.CriticalBatteryPct,
            ErsRuleCondition.LowBattery => _recoveryActive,
            ErsRuleCondition.Battle => battle,
            ErsRuleCondition.HighBattery => state.BatteryPct >= _profile.HighBatteryPct,
            ErsRuleCondition.BattleOrHighBattery => battle || state.BatteryPct >= _profile.HighBatteryPct,
            _ => false
        };
    }

    private bool ContainsDistance(ErsControlRule rule, double distanceM)
    {
        var length = Math.Max(1, _profile.TrackLengthM);
        var distance = ((distanceM % length) + length) % length;
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
        }
        else
        {
            _finishedRules.RemoveWhere(item => item.Lap < lap - 1);
        }

        var carryAcrossLine = lap == _lastLap + 1 && ActiveRuleCrossesStartFinish(state.LapDistanceM);
        if (carryAcrossLine)
        {
            _activeRuleLap = lap;
        }
        else
        {
            ClearActiveRule(markFinished: false);
        }
        _lastLap = lap;
    }

    private void MarkOncePerLapSelection(ErsControlRule rule, ErsControlState state)
    {
        if (!rule.OncePerLap) return;
        _finishedRules.Add((state.LapNumber, rule.Id));

        // A wrap-around segment selected before the timing line belongs to the same
        // activation after the lap counter increments. Reserve the next lap key too,
        // while ContinueActiveRule carries the current activation across the line.
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

    private string Explain(ErsControlRule rule, ErsControlState state)
    {
        var condition = rule.Condition switch
        {
            ErsRuleCondition.CriticalBattery => $"critical battery {state.BatteryPct:0}%",
            ErsRuleCondition.LowBattery => $"recovery below {_profile.RecoveryExitPct:0}%",
            ErsRuleCondition.Battle => "attack or defence gap",
            ErsRuleCondition.HighBattery => $"battery at least {_profile.HighBatteryPct:0}%",
            ErsRuleCondition.BattleOrHighBattery => state.InBattle(_profile.BattleGapMs)
                ? "attack or defence gap"
                : $"battery at least {_profile.HighBatteryPct:0}%",
            _ => "profile rule"
        };
        return string.IsNullOrWhiteSpace(rule.Note) ? condition : $"{condition}. {rule.Note}";
    }
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
