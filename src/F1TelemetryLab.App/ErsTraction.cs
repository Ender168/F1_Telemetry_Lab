namespace F1TelemetryLab;

// MotionEx contains the local player's car only; never derive these values from rivals.
public sealed record ErsPlayerMotion(DateTimeOffset ReceivedAt, ulong SessionUid, int CarIndex,
    uint Frame, double SessionTime, double FrontWheelsAngle, double YawRate,
    double RearLeftSlipAngle, double RearRightSlipAngle, double RearLeftSlipRatio, double RearRightSlipRatio)
{
    public double RearSlipAngle => Math.Max(Math.Abs(RearLeftSlipAngle), Math.Abs(RearRightSlipAngle));
    public double RearSlipRatio => Math.Max(Math.Abs(RearLeftSlipRatio), Math.Abs(RearRightSlipRatio));
}

public sealed class ErsTractionGate
{
    public double? MaximumFrontWheelsAngleRad { get; set; }
    public double? MaximumYawRateRadS { get; set; }
    public double? MaximumRearSlipAngleRad { get; set; }
    public double? MaximumRearSlipRatio { get; set; }
    public int StableForMs { get; set; } = 250;
    public int MaximumSampleGapMs { get; set; } = 100;
    public int MaximumDataAgeMs { get; set; } = 150;
}

public sealed class ErsTractionPlan
{
    public ErsTractionGate? Hotlap { get; set; }
    public ErsTractionGate? Boost { get; set; }
}

internal sealed class ErsTractionHistory
{
    private readonly List<ErsPlayerMotion> _samples = new();
    private ulong _session;
    private int _lap;
    private ErsPlayerMotion? _blockedSample;

    public void Observe(ErsControlState state)
    {
        if (_session != state.SessionUid || _lap != state.LapNumber || !state.AutomationAllowed)
            _samples.Clear();
        _session = state.SessionUid;
        _lap = state.LapNumber;
        var sample = state.PlayerMotion;
        if (!state.AutomationAllowed) _blockedSample = sample;
        if (sample is not null && sample == _blockedSample)
        {
            _samples.Clear();
            return;
        }
        if (!state.AutomationAllowed || sample is null || sample.SessionUid != state.SessionUid ||
            sample.ReceivedAt > state.ReceivedAt)
        {
            _samples.Clear();
            return;
        }
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (sample == last) return; // Other packet types cannot advance stability time.
            if (sample.Frame <= last.Frame || sample.SessionTime <= last.SessionTime ||
                sample.ReceivedAt <= last.ReceivedAt || sample.CarIndex != last.CarIndex)
                _samples.Clear();
        }
        _samples.Add(sample);
        _samples.RemoveAll(s => sample.SessionTime - s.SessionTime > 6);
        if (_samples.Count > 4096) _samples.RemoveAt(0);
    }

    public bool Allows(ErsTractionGate gate, DateTimeOffset now, out string reason)
    {
        reason = "MotionEx missing or stale";
        if (_samples.Count == 0) return false;
        var last = _samples[^1];
        if (now < last.ReceivedAt || (now - last.ReceivedAt).TotalMilliseconds > gate.MaximumDataAgeMs)
            return false;
        if (!Safe(last, gate, out reason)) return false;
        var first = last;
        for (int i = _samples.Count - 2; i >= 0; i--)
        {
            var sample = _samples[i];
            if ((first.SessionTime - sample.SessionTime) * 1000 > gate.MaximumSampleGapMs + 0.01 ||
                (first.ReceivedAt - sample.ReceivedAt).TotalMilliseconds > gate.MaximumSampleGapMs ||
                !Safe(sample, gate, out _)) break;
            first = sample;
        }
        // Both clocks must advance: queued packets delivered together cannot fabricate a safe interval.
        var stableMs = Math.Min((last.SessionTime - first.SessionTime) * 1000,
            (last.ReceivedAt - first.ReceivedAt).TotalMilliseconds);
        reason = $"stable {stableMs:0}/{gate.StableForMs} ms; angle={last.FrontWheelsAngle:0.000}, yaw={last.YawRate:0.000}, rear slip angle={last.RearSlipAngle:0.000}, ratio={last.RearSlipRatio:0.000}";
        return stableMs + 0.01 >= gate.StableForMs;
    }

    private static bool Safe(ErsPlayerMotion sample, ErsTractionGate gate, out string reason)
    {
        reason = "front wheels angle";
        if (gate.MaximumFrontWheelsAngleRad is double angle &&
            (!double.IsFinite(sample.FrontWheelsAngle) || Math.Abs(sample.FrontWheelsAngle) >= angle)) return false;
        reason = "yaw rate";
        if (gate.MaximumYawRateRadS is double yaw &&
            (!double.IsFinite(sample.YawRate) || Math.Abs(sample.YawRate) >= yaw)) return false;
        reason = "rear slip angle";
        if (gate.MaximumRearSlipAngleRad is double slip &&
            (!double.IsFinite(sample.RearSlipAngle) || sample.RearSlipAngle >= slip)) return false;
        reason = "rear slip ratio";
        if (gate.MaximumRearSlipRatio is double ratio &&
            (!double.IsFinite(sample.RearSlipRatio) || sample.RearSlipRatio >= ratio)) return false;
        reason = "";
        return true;
    }
}
