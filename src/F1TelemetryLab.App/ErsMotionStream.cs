namespace F1TelemetryLab;

// Motion has its own watermark. A rejected timestamp must never advance it.
internal sealed class ErsMotionStream
{
    private readonly Dictionary<byte, (double Time, DateTimeOffset At, uint Frame)> _anchors = new();
    private ErsPlayerMotion? _candidate;
    private int _recoveryCount;
    private bool _recovering;
    private string _rejection = "";
    private ErsPlayerMotion? _lastRejected;
    public ErsPlayerMotion? Sample { get; private set; }

    public void Reset()
    {
        _anchors.Clear(); Sample = null; _candidate = null; _recoveryCount = 0;
        _recovering = false; _rejection = ""; _lastRejected = null;
    }

    public void ObserveClock(byte packetId, double time, DateTimeOffset at, uint frame)
    {
        if (double.IsFinite(time) && time >= 0) _anchors[packetId] = (time, at, frame);
    }

    private bool Coherent(double time, DateTimeOffset at, out int witnesses)
    {
        var fresh = _anchors.Where(x => x.Key is 2 or 6 && at >= x.Value.At &&
            at - x.Value.At <= TimeSpan.FromSeconds(2)).Select(x => x.Value).ToList();
        // Session is a slower fallback, never allowed to outvote Lap + Telemetry.
        if (fresh.Count == 0 && _anchors.TryGetValue(1, out var session) &&
            at >= session.At && at - session.At <= TimeSpan.FromSeconds(2)) fresh.Add(session);
        witnesses = fresh.Count;
        return fresh.Count > 0 && fresh.All(x =>
            time >= x.Time - 2 && time <= x.Time + (at - x.At).TotalSeconds + 2);
    }

    public void Observe(ErsPlayerMotion? motion)
    {
        if (motion is null) { _rejection = "invalid packet"; return; }
        if (!Coherent(motion.SessionTime, motion.ReceivedAt, out var witnesses))
        { Reject(motion, "timestamp disagrees with racing clock or clock unavailable"); return; }
        if (Sample is { } old && !Coherent(old.SessionTime, motion.ReceivedAt, out _) &&
            motion.SessionTime < old.SessionTime && witnesses >= 2)
        {
            // Only independently corroborated clocks can retire a poisoned watermark.
            Sample = null; _recovering = true; _candidate = null; _recoveryCount = 0;
        }
        if (Sample is { } last && (motion.Frame <= last.Frame || motion.SessionTime < last.SessionTime ||
            motion.ReceivedAt <= last.ReceivedAt))
        { Reject(motion, "late/duplicate sample"); return; }
        if (_recovering)
        {
            var minimumClockFrame = _anchors.Where(x => x.Key is 2 or 6).Min(x => (long)x.Value.Frame);
            if ((long)motion.Frame < minimumClockFrame - 5)
            { _candidate = null; _recoveryCount = 0; Reject(motion, "recovery sample behind racing frames"); return; }
            if (_candidate is { } prior && (motion.Frame <= prior.Frame || motion.SessionTime < prior.SessionTime ||
                motion.ReceivedAt <= prior.ReceivedAt || (motion.ReceivedAt - prior.ReceivedAt).TotalSeconds > .25))
                _recoveryCount = 0;
            _candidate = motion;
            if (++_recoveryCount < 3) { Reject(motion, "validating coherent recovery run"); return; }
            _recovering = false;
        }
        Sample = motion; _rejection = ""; _lastRejected = null;
    }

    private void Reject(ErsPlayerMotion sample, string reason)
    { _rejection = reason; _lastRejected = sample; }

    public string Diagnostic(DateTimeOffset now)
    {
        var retained = Sample is null ? "no accepted sample" :
            FormattableString.Invariant($"retained frame={Sample.Frame} time={Sample.SessionTime:0.000}s age={(now - Sample.ReceivedAt).TotalMilliseconds:0}ms");
        if (_rejection.Length > 0)
            return "MotionEx rejected: " + _rejection + (_lastRejected is null ? "" :
                FormattableString.Invariant($"; source frame={_lastRejected.Frame} time={_lastRejected.SessionTime:0.000}s age={(now - _lastRejected.ReceivedAt).TotalMilliseconds:0}ms")) + "; " + retained;
        return Sample is null ? "No MotionEx packets" : "MotionEx " + retained;
    }
}
