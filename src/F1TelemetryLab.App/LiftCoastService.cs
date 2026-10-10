using System.Buffers.Binary;

namespace F1TelemetryLab;

// Independent of ERS input mode; only the player's packets are used.
public sealed class LiftCoastService
{
    private readonly object _sync = new();
    private readonly ErsProfileLoadResult _profiles;
    private readonly Dictionary<byte, uint> _frames = new();
    private readonly HashSet<ulong> _retiredSessions = new();
    private SessionControlSample? _session;
    private LapDataSample? _lap;
    private CarTelemetrySample? _telemetry;
    private CarStatusSample? _status;
    private byte _player;
    private uint? _flashbackFrame;
    private bool _ended;
    private readonly TrackGuidanceAdvisor _guidance = new();
    private TrackGuidanceAdvice _guidanceAdvice = TrackGuidanceAdvice.Waiting;
    private readonly Dictionary<uint, LapDataSample> _guidanceLaps = new();
    private readonly Dictionary<uint, CarTelemetrySample> _guidanceTelemetry = new();
    private uint? _guidanceFrame;
    private TrackGuidancePlan? _guidancePlan;

    public TrackGuidanceAdvice GetGuidance(DateTimeOffset now, bool pitLapBurn = false)
    {
        lock (_sync)
        {
            var plan = GuidancePlan(now);
            if (plan is null || (pitLapBurn && plan.SuppressOnPitLap))
            { ResetGuidance(); return TrackGuidanceAdvice.Waiting; }
            return _guidanceAdvice;
        }
    }

    private TrackGuidancePlan? GuidancePlan(DateTimeOffset now)
    {
        if (_ended || _session is null || _lap is null || _telemetry is null || _status is null ||
            !Fresh(_session.ReceivedAt, now, 3000) || !Fresh(_lap.ReceivedAt, now, 250) ||
            !Fresh(_telemetry.ReceivedAt, now, 250) || !Fresh(_status.ReceivedAt, now, 1500) ||
            _session.GamePaused || _session.IsSpectating || _status.NetworkPaused || _session.SafetyCarStatus != 0 ||
            _lap.PitStatus != 0 || _lap.DriverStatus is not (1 or 4) || _lap.ResultStatus != 2 || _lap.LapNum <= 0)
            return null;
        var profile = _profiles.Find(_session.TrackId, _session.SessionType, _status.ActualTyreCompound,
            _status.VisualTyreCompound, _session.Weather);
        return profile?.TrackGuidance is { Enabled: true } plan && _session.TrackLengthM > 0 &&
            Math.Abs(_session.TrackLengthM - profile.TrackLengthM) <= 10 ? plan : null;
    }

    private void UpdateGuidance(DateTimeOffset now)
    {
        TrimGuidanceSamples(_guidanceLaps, now, x => x.ReceivedAt);
        TrimGuidanceSamples(_guidanceTelemetry, now, x => x.ReceivedAt);
        var plan = GuidancePlan(now);
        if (plan is null) { ResetGuidance(clearSamples: _status is not null); return; }
        if (!ReferenceEquals(plan, _guidancePlan))
        {
            _guidance.Reset(); _guidanceFrame = null;
            _guidanceAdvice = TrackGuidanceAdvice.Waiting; _guidancePlan = plan;
        }
        // Packet 2 and packet 6 can arrive in either order. Only a complete
        // same-frame pair may judge brake onset or checkpoint speed.
        foreach (var frame in _guidanceLaps.Keys.Intersect(_guidanceTelemetry.Keys).OrderBy(x => x).ToArray())
        {
            var lap = _guidanceLaps[frame]; var telemetry = _guidanceTelemetry[frame];
            _guidanceLaps.Remove(frame); _guidanceTelemetry.Remove(frame);
            if ((_guidanceFrame is uint previous && frame <= previous) ||
                !Fresh(lap.ReceivedAt, now, 250) || !Fresh(telemetry.ReceivedAt, now, 250)) continue;
            _guidanceFrame = frame;
            _guidanceAdvice = _guidance.Observe(plan, _session!.TrackLengthM, lap.LapDistance,
                telemetry.Speed, telemetry.Brake, telemetry.ReceivedAt);
        }
        TrimGuidanceSamples(_guidanceLaps, now, x => x.ReceivedAt);
        TrimGuidanceSamples(_guidanceTelemetry, now, x => x.ReceivedAt);
    }

    private void TrimGuidanceSamples<T>(Dictionary<uint, T> samples, DateTimeOffset now, Func<T, DateTimeOffset> time)
    {
        foreach (var frame in samples.Keys.Where(x =>
            (_guidanceFrame is uint previous && x <= previous) || !Fresh(time(samples[x]), now, 250)).ToArray())
            samples.Remove(frame);
        foreach (var frame in samples.Keys.OrderBy(x => x).Take(Math.Max(0, samples.Count - 32)).ToArray())
            samples.Remove(frame);
    }

    private void ResetGuidance(bool clearSamples = true)
    {
        _guidance.Reset(); _guidanceFrame = null; _guidancePlan = null;
        _guidanceAdvice = TrackGuidanceAdvice.Waiting;
        if (clearSamples) { _guidanceLaps.Clear(); _guidanceTelemetry.Clear(); }
    }

    public LiftCoastService(ErsProfileLoadResult profiles) => _profiles = profiles;

    public void ProcessPacket(byte[] payload, DateTimeOffset receivedAt) => ProcessPacket(new LivePacket(payload, receivedAt));

    internal void ProcessPacket(LivePacket packet)
    {
        var payload = packet.Payload;
        var receivedAt = packet.ReceivedAt;
        lock (_sync)
        {
            if (!F12026Parser.TryParseHeader(payload, out var h) || h.PacketFormat != AppInfo.SupportedPacketFormat ||
                h.PlayerCarIndex >= F12026Parser.MaxCars2026 || _retiredSessions.Contains(h.SessionUid)) return;
            if (h.PacketId == 1)
            {
                var session = F12026Parser.TryParseSessionControl(payload, receivedAt);
                if (session is null) return;
                if (_session is null || _session.SessionUid != h.SessionUid)
                {
                    if (_session is not null) _retiredSessions.Add(_session.SessionUid);
                    ClearPlayer(); _frames.Clear(); _flashbackFrame = null; _ended = false;
                }
                else if (_flashbackFrame is uint cut && h.OverallFrameIdentifier <= cut) return;
                if (!AcceptFrame(h)) return;
                if (_player != h.PlayerCarIndex || _session?.TrackId != session.TrackId ||
                    _session?.SessionType != session.SessionType || _session?.TrackLengthM != session.TrackLengthM)
                    ClearPlayer();
                _player = h.PlayerCarIndex;
                _session = session;
                UpdateGuidance(receivedAt);
                return;
            }
            if (_session is null || _session.SessionUid != h.SessionUid || _player != h.PlayerCarIndex ||
                _ended || (_flashbackFrame is uint cutoff && h.OverallFrameIdentifier <= cutoff)) return;
            if (h.PacketId is not (2 or 3 or 6 or 7) || !AcceptFrame(h)) return;
            switch (h.PacketId)
            {
                case 2:
                    _lap = packet.Laps.FirstOrDefault(x => x.IsPlayer);
                    if (_lap is not null) _guidanceLaps[h.OverallFrameIdentifier] = _lap;
                    break;
                case 6:
                    _telemetry = packet.PlayerTelemetry;
                    if (_telemetry is not null) _guidanceTelemetry[h.OverallFrameIdentifier] = _telemetry;
                    break;
                case 7:
                    _status = packet.PlayerStatus;
                    break;
                case 3:
                    var offset = F12026Parser.HeaderSize;
                    if (payload.Length >= offset + 4 && payload.AsSpan(offset, 4).SequenceEqual("SEND"u8))
                    { ClearPlayer(); _ended = true; }
                    else if (payload.Length >= offset + 12 && payload.AsSpan(offset, 4).SequenceEqual("FLBK"u8))
                    {
                        var targetTime = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(offset + 8));
                        if (float.IsFinite(targetTime) && targetTime >= 0)
                        { ClearPlayer(); _flashbackFrame = h.OverallFrameIdentifier; }
                    }
                    break;
            }
            UpdateGuidance(receivedAt);
        }
    }

    public LiftCoastAdvice GetAdvice(DateTimeOffset now, bool pitLapBurn = false)
    {
        lock (_sync)
        {
            if (_ended || _session is null || _lap is null || _telemetry is null || _status is null ||
                !Fresh(_session.ReceivedAt, now, 3000) || !Fresh(_lap.ReceivedAt, now, 250) ||
                !Fresh(_telemetry.ReceivedAt, now, 250) || !Fresh(_status.ReceivedAt, now, 1500))
                return LiftCoastAdvice.Waiting;
            var profile = _profiles.Find(_session.TrackId, _session.SessionType, _status.ActualTyreCompound,
                _status.VisualTyreCompound, _session.Weather);
            if (profile?.LiftCoast is not { } plan) return new(LiftCoastPhase.NoProfile);
            if (!plan.Enabled) return new(LiftCoastPhase.Disabled);
            if (_session.GamePaused || _session.IsSpectating || _status.NetworkPaused || _session.SafetyCarStatus != 0 ||
                _lap.PitStatus != 0 || _lap.DriverStatus is not (1 or 4) || _lap.ResultStatus != 2 || _lap.LapNum <= 0 ||
                _session.TrackLengthM <= 0 || Math.Abs(_session.TrackLengthM - profile.TrackLengthM) > 10 ||
                (pitLapBurn && plan.SuppressOnPitLap)) return new(LiftCoastPhase.Suspended);
            return LiftCoastAdvisor.Evaluate(plan, _session.TrackLengthM, _lap.LapDistance,
                _telemetry.Speed, _telemetry.Throttle, _telemetry.Brake);
        }
    }

    private bool AcceptFrame(PacketHeader h)
    {
        if (_frames.TryGetValue(h.PacketId, out var frame) && h.OverallFrameIdentifier <= frame) return false;
        _frames[h.PacketId] = h.OverallFrameIdentifier;
        return true;
    }

    private void ClearPlayer() { _lap = null; _telemetry = null; _status = null; ResetGuidance(); }
    private static bool Fresh(DateTimeOffset at, DateTimeOffset now, int milliseconds) =>
        now >= at && now - at <= TimeSpan.FromMilliseconds(milliseconds);
}

