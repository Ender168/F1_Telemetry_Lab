namespace F1TelemetryLab;

public sealed class ErsAutopilotService : IDisposable
{
    private readonly object _sync = new();
    private readonly PitLapErsController _pitLap = new();
    private readonly TimeProvider _timeProvider;
    private bool _stopped;
    private bool _disposed;
    private readonly ErsAutopilotOptions _options;
    private readonly ErsProfileLoadResult _profiles;
    private readonly IErsInputSink _inputSink;
    private readonly Action<string>? _log;
    private readonly ErsAuditLog _audit;
    private readonly Action<ErsControlProfile, ErsAutopilotOptions>? _profileSink;
    private readonly Dictionary<int, LapDataSample> _lapRows = new();
    private ErsAutopilotStatus _publicStatus;
    private SessionControlSample? _session;
    private CarTelemetrySample? _telemetry;
    private CarStatusSample? _carStatus;
    private ErsPlayerMotion? _playerMotion;
    private int? _sessionPlayerCarIndex;
    private LapDataSample? _playerLap;
    private ErsControlProfile? _profile;
    private ErsDecisionEngine? _engine;
    private DateTimeOffset _lastCommandAt = DateTimeOffset.MinValue;
    private ErsDeployMode? _pendingFromMode;
    private ErsDeployMode? _pendingExpectedMode;
    private DateTimeOffset _pendingSince;
    private int _retryCount;
    private bool _inputFault;
    private string _inputFaultReason = "";
    private string _lastDecisionSignature = "";
    private DateTimeOffset _lastDecisionAuditAt;
    private string _lastInternalError = "";
    private ulong _sessionUid;
    private uint? _flashbackOverallFrame;
    private ErsControlDecision? _lastDecision;

    public ErsAutopilotService(
        ErsAutopilotOptions options,
        ErsProfileLoadResult profiles,
        IErsInputSink inputSink,
        Action<ErsAuditRecord>? auditSink = null,
        Action<ErsControlProfile, ErsAutopilotOptions>? profileSink = null,
        Action<string>? log = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options;
        _profiles = profiles;
        _inputSink = inputSink;
        _log = log;
        _profileSink = profileSink;
        _audit = new ErsAuditLog(auditSink);
        _publicStatus = ErsAutopilotStatus.Initial(options.OperatingMode);

        foreach (var warning in profiles.Warnings) _log?.Invoke("ERS profile warning: " + warning);
        _log?.Invoke(options.OperatingMode switch
        {
            ErsAutopilotOperatingMode.Live =>
                $"ERS autopilot LIVE: profile feedback controls {ErsProfileStore.VirtualKeyName(options.DecreaseVirtualKey)}/{ErsProfileStore.VirtualKeyName(options.IncreaseVirtualKey)}. Online and offline sessions are supported; F12 stops input for the rest of the recording.",
            ErsAutopilotOperatingMode.DryRun => "ERS autopilot DRY-RUN: decisions are logged, no keys are sent.",
            _ => "ERS autopilot is off."
        });
    }

    public ErsAutopilotStatus Status => Volatile.Read(ref _publicStatus);

    public ErsControlDecision? LastDecision => Volatile.Read(ref _lastDecision);

    public PitLapErsStatus PitLapStatus
    {
        get
        {
            lock (_sync) return new PitLapErsStatus(_pitLap.Active, _pitLap.Lap, _pitLap.ButtonsPressed,
                _options.OperatingMode, _publicStatus.State, _telemetry?.ReceivedAt ?? DateTimeOffset.MinValue,
                _publicStatus.Detail);
        }
    }


    public void ProcessPacket(byte[] payload, DateTimeOffset receivedAt)
    {
        lock (_sync)
        {
            if (_stopped || _disposed) return;
            ProcessPacketCore(payload, receivedAt);
        }
    }

    private void ProcessPacketCore(byte[] payload, DateTimeOffset receivedAt)
    {
        if (_options.OperatingMode == ErsAutopilotOperatingMode.Off) return;
        var now = _options.OperatingMode == ErsAutopilotOperatingMode.Live ? _timeProvider.GetUtcNow() : receivedAt;
        try
        {
            if (!F12026Parser.TryParseHeader(payload, out var header) || header.PacketFormat != AppInfo.SupportedPacketFormat) return;
            if (_options.OperatingMode == ErsAutopilotOperatingMode.Live && !PollInputRelease(now)) return;
            // MotionEx must belong to the session/player established by Session data.
            if (header.PacketId == 13 && (_sessionUid == 0 || header.SessionUid != _sessionUid)) return;
            if (_sessionUid != 0 && header.SessionUid != _sessionUid) ResetForSession(header.SessionUid);
            _sessionUid = header.SessionUid;
            if (_options.OperatingMode == ErsAutopilotOperatingMode.Live && _inputSink.EmergencyStopRequested(_options))
            {
                if (!_inputFault)
                {
                    LatchInputFault("Emergency stop F12 was pressed. Live ERS input is disabled until the next recording.");
                    _log?.Invoke(_inputFaultReason);
                }
                SetStatus("Emergency stop", "", null, null, null, _inputFaultReason);
                return;
            }

            // Overall frames continue across a rewind; ordinary frame/session-time counters do not.
            // Ignore delayed packets and repeated FLBK deliveries from the abandoned timeline.
            if (_flashbackOverallFrame is uint cutoff && header.OverallFrameIdentifier <= cutoff) return;
            if (header.PacketId == 3)
            {
                if (F12026Parser.TryParseButtonStatus(payload, out var buttons))
                {
                    HandlePitButtons(buttons, header, receivedAt, now);
                    return;
                }
                var eventOffset = F12026Parser.HeaderSize;
                if (payload.Length >= eventOffset + 4 && payload.AsSpan(eventOffset, 4).SequenceEqual("SEND"u8))
                    CancelPitLap(now, "Session ended.");
                if (payload.Length >= eventOffset + 12 &&
                    payload.AsSpan(eventOffset, 4).SequenceEqual("FLBK"u8))
                {
                    var targetTime = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(eventOffset + 8));
                    if (float.IsFinite(targetTime) && targetTime >= 0)
                        ResetAfterFlashback(header.OverallFrameIdentifier, now);
                }
                return;
            }

            if (header.PacketId == 13 &&
                (_sessionPlayerCarIndex is null || header.PlayerCarIndex != _sessionPlayerCarIndex)) return;

            switch (header.PacketId)
            {
                case 13:
                    var motion = F12026Parser.ParsePlayerMotionEx(payload, receivedAt);
                    if (motion is not null && _playerMotion is not null &&
                        (motion.Frame <= _playerMotion.Frame || motion.SessionTime <= _playerMotion.SessionTime ||
                         motion.ReceivedAt <= _playerMotion.ReceivedAt)) return;
                    _playerMotion = motion;
                    break;
                case 1:
                    if (_sessionPlayerCarIndex != header.PlayerCarIndex) _playerMotion = null;
                    _sessionPlayerCarIndex = header.PlayerCarIndex;
                    _session = F12026Parser.TryParseSessionControl(payload, receivedAt);
                    SelectProfileIfPossible();
                    break;
                case 2:
                    UpdateLapRows(payload, receivedAt);
                    break;
                case 6:
                    _telemetry = F12026Parser.ParseCarTelemetryPacket(payload, receivedAt, onlyCarIndex: header.PlayerCarIndex)
                        .FirstOrDefault(sample => sample.IsPlayer);
                    break;
                case 7:
                    _carStatus = F12026Parser.ParseCarStatusPacket(payload, receivedAt, onlyCarIndex: header.PlayerCarIndex)
                        .FirstOrDefault(sample => sample.IsPlayer);
                    break;
                default:
                    return;
            }

            if (_pitLap.Active && _playerLap is { } lap &&
                (lap.PitStatus != 0 || lap.LapNum != _pitLap.Lap || lap.ResultStatus != 2))
                CancelPitLap(now, "Pit entry, lap change or inactive race lap.");
            Evaluate(now);
        }
        catch (Exception ex)
        {
            var detail = "ERS controller error: " + ex.Message;
            if (_options.OperatingMode == ErsAutopilotOperatingMode.Live)
            {
                LatchInputFault(detail + " Live input is disabled until the next recording.");
                detail = _inputFaultReason;
            }
            SetStatus("Blocked", "", null, null, null, detail);
            if (!string.Equals(_lastInternalError, ex.Message, StringComparison.Ordinal))
            {
                _lastInternalError = ex.Message;
                _log?.Invoke("ERS controller warning: " + ex.Message);
            }
        }
    }

    public void StopInput()
    {
        lock (_sync)
        {
            if (_stopped) return;
            CancelPitLap(_timeProvider.GetUtcNow(), "Recording stopped.");
            _stopped = true;
            _inputSink.Dispose();
            SetStatus("Stopped", "", null, null, null, "Recording stopped; ERS input is disabled.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            try { StopInput(); }
            finally { _disposed = true; _audit.Dispose(); }
        }
    }

    private void HandlePitButtons(uint buttons, PacketHeader header, DateTimeOffset receivedAt, DateTimeOffset now)
    {
        // BUTN carries the local player's controller flags, independent of DS4/XInput drivers.
        if (_session is null || _playerLap is null || header.PlayerCarIndex != _playerLap.CarIndex ||
            now - receivedAt > TimeSpan.FromMilliseconds(_options.TelemetryFreshnessMs)) return;
        var pressed = _pitLap.ObserveButtons(buttons, header.OverallFrameIdentifier);
        if (!pressed) return;
        // Cancellation is always possible; arming requires fresh, active on-track telemetry.
        if (_pitLap.Active) CancelPitLap(now, "Cancelled with Triangle + Circle.");
        else if (!_inputFault && !_session.GamePaused && !_session.IsSpectating &&
                 _carStatus?.NetworkPaused != true && _playerLap.PitStatus == 0 &&
                 _playerLap.LapNum > 0 && _playerLap.ResultStatus == 2 &&
                 now - _playerLap.ReceivedAt <= TimeSpan.FromMilliseconds(_options.TelemetryFreshnessMs) &&
                 now - _session.ReceivedAt <= TimeSpan.FromMilliseconds(_options.SessionFreshnessMs))
        {
            _pitLap.Toggle(_playerLap.LapNum);
            ClearPendingCommand();
            WritePitLapAudit(now, "pit-lap-on", "Pit this lap enabled with Triangle + Circle.");
        }
        Evaluate(now);
    }

    private void CancelPitLap(DateTimeOffset now, string reason)
    {
        if (!_pitLap.Active) return;
        _pitLap.Cancel();
        if (_options.OperatingMode == ErsAutopilotOperatingMode.Live)
            PollInputRelease(now, releaseImmediately: true);
        ClearPendingCommand();
        WritePitLapAudit(now, "pit-lap-off", reason);
    }

    private void WritePitLapAudit(DateTimeOffset now, string action, string reason) =>
        _audit.Write(InputLifecycleDecision(now) with { RuleId = "pit-lap", Reason = reason }, action);

    private void UpdateLapRows(byte[] payload, DateTimeOffset receivedAt)
    {
        foreach (var row in F12026Parser.ParseLapDataPacket(payload, receivedAt))
        {
            _lapRows[row.CarIndex] = row;
            if (row.IsPlayer) _playerLap = row;
        }
    }

    private void SelectProfileIfPossible()
    {
        if (_session is null) return;
        var selected = _profiles.Find(_session.TrackId, _session.SessionType);
        if (ReferenceEquals(selected, _profile)) return;

        _profile = selected;
        _engine = selected is null ? null : new ErsDecisionEngine(selected);
        _pendingFromMode = null;
        _pendingExpectedMode = null;
        _retryCount = 0;

        if (selected is null)
        {
            SetStatus("No profile", "", null, null, null,
                $"No ERS profile matches track_id={_session.TrackId}, session_type={_session.SessionType}.");
            return;
        }

        _profileSink?.Invoke(selected, _options);
        _log?.Invoke($"ERS profile selected: {selected.ProfileId} ({selected.DisplayName}).");
    }

    private void Evaluate(DateTimeOffset now)
    {
        if (_profile is null || _engine is null || _session is null) return;
        var state = BuildState(now);
        var decision = _engine.Evaluate(state);
        Volatile.Write(ref _lastDecision, decision);
        AuditDecisionTransition(decision);

        if (decision.Blocked)
        {
            ClearPendingCommand();
            SetStatus("Blocked", decision.Segment, decision.CurrentMode, decision.TargetMode, decision.BatteryPct, decision.Reason);
            return;
        }

        if (_options.OperatingMode == ErsAutopilotOperatingMode.DryRun)
        {
            SetStatus("Decision", decision.Segment, decision.CurrentMode, decision.TargetMode, decision.BatteryPct,
                "Dry-run only. " + decision.Reason);
            return;
        }

        ReconcileLiveInput(decision, now);
    }

    private ErsControlState BuildState(DateTimeOffset now)
    {
        var currentMode = _carStatus is { ErsDeployMode: >= 0 and <= 3 }
            ? (ErsDeployMode)_carStatus.ErsDeployMode
            : ErsDeployMode.Medium;
        var batteryPct = _carStatus is null ? 0 : Math.Clamp(_carStatus.ErsStoreEnergy / _profile!.BatteryCapacityJ * 100d, 0, 100);
        var gapAhead = ValidGap(_playerLap?.DeltaToCarInFrontMs);
        int? gapBehind = null;
        if (_playerLap is { Position: > 0 } player)
        {
            var behind = _lapRows.Values.FirstOrDefault(row => row.Position == player.Position + 1);
            gapBehind = ValidGap(behind?.DeltaToCarInFrontMs);
        }

        var block = BlockReason(now);
        return new ErsControlState(
            now,
            _session!.SessionUid,
            _session.TrackId,
            _session.SessionType,
            _session.TrackLengthM,
            _session.Weather,
            _session.GamePaused,
            _session.IsSpectating,
            _session.SafetyCarStatus,
            _session.IsNetworkGame,
            _playerLap?.LapNum ?? 0,
            _playerLap?.LapDistance ?? 0,
            _playerLap?.PitStatus ?? -1,
            _playerLap?.DriverStatus ?? 0,
            _playerLap?.ResultStatus ?? 0,
            _telemetry?.Speed ?? 0,
            (_telemetry?.Throttle ?? 0) * 100d,
            batteryPct,
            currentMode,
            _carStatus?.NetworkPaused ?? false,
            gapAhead,
            gapBehind,
            string.IsNullOrEmpty(block),
            block)
        {
            PlayerMotion = _playerMotion,
            TotalLaps = _session.TotalLaps,
            DrsActive = _telemetry?.Drs == 1,
            PitLapBurn = _pitLap.Active,
            BrakePct = (_telemetry?.Brake ?? 0) * 100d
        };
    }

    private string BlockReason(DateTimeOffset now)
    {
        if (_inputFault) return string.IsNullOrWhiteSpace(_inputFaultReason)
            ? "Live ERS input is blocked until the next recording."
            : _inputFaultReason;
        if (_session is null) return "Waiting for Session packet 1.";
        if (_options.OperatingMode == ErsAutopilotOperatingMode.Live && _session.ErsAssist < 0)
            return "Waiting for the 2026 ERS Assist flag before enabling live input.";
        if (_options.OperatingMode == ErsAutopilotOperatingMode.Live && _session.ErsAssist != 0)
            return "Turn ERS Assist off in F1 25 before enabling live input.";
        if (now - _session.ReceivedAt > TimeSpan.FromMilliseconds(_options.SessionFreshnessMs))
            return "Session safety telemetry is stale.";
        if (_session.TrackLengthM > 0 &&
            Math.Abs(_session.TrackLengthM - _profile!.TrackLengthM) > Math.Max(50, _profile.TrackLengthM * 0.01))
            return $"Track length does not match profile {_profile.ProfileId}; automatic input is blocked.";
        if (_session.GamePaused || _carStatus?.NetworkPaused == true) return "Game is paused.";
        if (_session.IsSpectating) return "Spectator mode is active.";
        if (_session.SafetyCarStatus != 0) return "Safety car, VSC or formation-lap state is active; manual control retained.";
        if (_profile!.DryOnly && _session.Weather >= 3) return "Wet-weather session; this dry profile is blocked.";
        if (_telemetry is null || _carStatus is null || _playerLap is null) return "Waiting for player Lap, Telemetry and Car Status packets.";
        if (now - _telemetry.ReceivedAt > TimeSpan.FromMilliseconds(_options.TelemetryFreshnessMs) ||
            now - _carStatus.ReceivedAt > TimeSpan.FromMilliseconds(_options.TelemetryFreshnessMs) ||
            now - _playerLap.ReceivedAt > TimeSpan.FromMilliseconds(_options.TelemetryFreshnessMs))
            return "Player telemetry is stale.";
        if (_playerLap.PitStatus != 0) return "Player is in the pit lane or pit box.";
        if (_playerLap.LapNum <= 0 || _playerLap.DriverStatus == 0 || _playerLap.ResultStatus != 2)
            return "Player is not in an active race lap.";
        if (_telemetry.Speed < _profile.MinimumControlSpeedKph) return "Below the profile's minimum control speed.";
        if (_carStatus.ErsDeployMode is < 0 or > 3) return "Unknown ERS deploy mode.";
        return "";
    }

    private void ReconcileLiveInput(ErsControlDecision decision, DateTimeOffset now)
    {
        if (_pendingExpectedMode is not null)
        {
            if (decision.CurrentMode == _pendingExpectedMode)
            {
                _audit.Write(decision, "telemetry-confirmed");
                ClearPendingCommand();
            }
            else if (_pendingFromMode is not null && decision.CurrentMode != _pendingFromMode)
            {
                _audit.Write(decision, $"feedback-unexpected:{_pendingExpectedMode}->{decision.CurrentMode}");
                ClearPendingCommand();
            }
            else if (PendingDirection() != ErsModeTransition.Next(decision.CurrentMode, decision.TargetMode))
            {
                _audit.Write(decision, $"feedback-superseded:expected-{_pendingExpectedMode}");
                ClearPendingCommand();
            }
            else if (now - _pendingSince < TimeSpan.FromMilliseconds(_options.ConfirmationTimeoutMs))
            {
                SetStatus("Awaiting feedback", decision.Segment, decision.CurrentMode, decision.TargetMode, decision.BatteryPct,
                    $"Waiting for telemetry to confirm {_pendingExpectedMode}. {decision.Reason}");
                return;
            }
            else
            {
                _pendingFromMode = null;
                _pendingExpectedMode = null;
                _retryCount++;
                if (_retryCount > _options.MaximumRetries)
                {
                    LatchInputFault("F1 did not confirm the ERS mode after repeated held scan-code inputs. Live input is disabled until the next recording.");
                    _audit.Write(decision, "feedback-timeout-blocked");
                    SetStatus("Blocked", decision.Segment, decision.CurrentMode, decision.TargetMode, decision.BatteryPct,
                        _inputFaultReason);
                    return;
                }
            }
        }

        if (decision.CurrentMode == decision.TargetMode)
        {
            SetStatus("Holding", decision.Segment, decision.CurrentMode, decision.TargetMode, decision.BatteryPct, decision.Reason);
            return;
        }

        if (now - _lastCommandAt < TimeSpan.FromMilliseconds(_options.MinimumCommandIntervalMs)) return;
        var direction = ErsModeTransition.Next(decision.CurrentMode, decision.TargetMode);
        if (direction is null) return;
        var result = _inputSink.Tap(direction.Value, _options, now);
        if (!result.Success)
        {
            if (!result.Retryable)
            {
                LatchInputFault(result.Message);
            }
            _audit.Write(decision, result.Retryable ? "input-wait: " + result.Message : "input-error: " + result.Message);
            SetStatus(result.Retryable ? "Waiting for game" : "Blocked", decision.Segment,
                decision.CurrentMode, decision.TargetMode, decision.BatteryPct, result.Message);
            return;
        }

        _lastCommandAt = now;
        _pendingFromMode = decision.CurrentMode;
        _pendingExpectedMode = ErsModeTransition.ExpectedAfter(decision.CurrentMode, direction.Value);
        _pendingSince = now;
        _audit.Write(decision, result.Message);
        SetStatus("Key sent", decision.Segment, decision.CurrentMode, decision.TargetMode, decision.BatteryPct,
            $"{result.Message} Waiting for {_pendingExpectedMode}. {decision.Reason}");
    }

    private bool PollInputRelease(DateTimeOffset now, bool releaseImmediately = false)
    {
        var result = _inputSink.Poll(releaseImmediately ? DateTimeOffset.MaxValue : now);
        if (result is null) return true;
        _audit.Write(InputLifecycleDecision(now), result.Success ? result.Message : "input-error: " + result.Message);
        if (result.Success)
        {
            _log?.Invoke("ERS input: " + result.Message);
            return true;
        }

        LatchInputFault(result.Message);
        SetStatus("Blocked", "", null, null, null, _inputFaultReason);
        return false;
    }

    private ErsControlDecision InputLifecycleDecision(DateTimeOffset now)
    {
        var current = _carStatus is { ErsDeployMode: >= 0 and <= 3 }
            ? (ErsDeployMode)_carStatus.ErsDeployMode
            : ErsDeployMode.Medium;
        var target = _pendingExpectedMode ?? current;
        var battery = _profile is null || _carStatus is null
            ? 0
            : Math.Clamp(_carStatus.ErsStoreEnergy / _profile.BatteryCapacityJ * 100d, 0, 100);
        return new ErsControlDecision(
            now,
            false,
            current,
            target,
            "input-pulse",
            "",
            "Windows scan-code pulse lifecycle.",
            battery,
            _playerLap?.LapNum ?? 0,
            _playerLap?.LapDistance ?? 0,
            ValidGap(_playerLap?.DeltaToCarInFrontMs),
            null);
    }

    private ErsInputDirection? PendingDirection()
    {
        if (_pendingFromMode is null || _pendingExpectedMode is null) return null;
        return _pendingExpectedMode.Value > _pendingFromMode.Value
            ? ErsInputDirection.Increase
            : ErsInputDirection.Decrease;
    }

    private void LatchInputFault(string message)
    {
        _inputFault = true;
        _inputFaultReason = message;
        try { _inputSink.Dispose(); }
        catch (Exception ex) { _log?.Invoke("ERS input release warning: " + ex.Message); }
    }

    private void AuditDecisionTransition(ErsControlDecision decision)
    {
        var signature = $"{decision.Blocked}|{decision.RuleId}|{decision.Segment}|{decision.CurrentMode}|{decision.TargetMode}|{decision.TacticalMode}|{decision.TacticalIntensity}|{decision.EnergyState}|{(decision.Blocked ? decision.Reason : "")}";
        if (string.Equals(signature, _lastDecisionSignature, StringComparison.Ordinal) &&
            decision.ReceivedAt - _lastDecisionAuditAt < TimeSpan.FromSeconds(1)) return;
        _lastDecisionAuditAt = decision.ReceivedAt;
        _lastDecisionSignature = signature;
        _audit.Write(decision, decision.Blocked ? "blocked" : "decision");
    }

    private void SetStatus(string state, string segment, ErsDeployMode? current, ErsDeployMode? target, double? battery, string detail)
    {
        Volatile.Write(ref _publicStatus, new ErsAutopilotStatus(
            _options.OperatingMode,
            state,
            _profile?.ProfileId ?? "",
            segment,
            current,
            target,
            battery,
            detail)
        {
            Decision = Volatile.Read(ref _lastDecision)
        });
    }

    private void ResetAfterFlashback(uint overallFrame, DateTimeOffset now)
    {
        CancelPitLap(now, "Confirmed Flashback.");
        _pitLap.Reset();
        var resetDecision = InputLifecycleDecision(now) with
        {
            Blocked = true,
            RuleId = "flashback-reset",
            Reason = "Confirmed FLBK: cleared ERS timeline state; waiting for fresh Session, Lap, Telemetry and Car Status packets."
        };
        // Release any held pulse without disposing the sink, so Live can resume afterwards.
        if (_options.OperatingMode == ErsAutopilotOperatingMode.Live)
            PollInputRelease(now, releaseImmediately: true);
        _flashbackOverallFrame = overallFrame;
        _engine = _profile is null ? null : new ErsDecisionEngine(_profile);
        _session = null;
        _telemetry = null;
        _carStatus = null;
        _playerMotion = null;
        _sessionPlayerCarIndex = null;
        _playerLap = null;
        _lapRows.Clear();
        ClearPendingCommand();
        _pendingSince = default;
        _lastCommandAt = DateTimeOffset.MinValue;
        _lastDecisionSignature = "";
        _lastDecisionAuditAt = default;
        Volatile.Write(ref _lastDecision, null);
        _audit.Write(resetDecision, "flashback-reset");
        _log?.Invoke(resetDecision.Reason);
        // A rewind never clears a latched F12, input error or feedback failure.
        SetStatus("Blocked", "", null, null, null, _inputFault ? _inputFaultReason : resetDecision.Reason);
    }

    private void ResetForSession(ulong sessionUid)
    {
        CancelPitLap(_timeProvider.GetUtcNow(), "Session changed.");
        _pitLap.Reset();
        _sessionUid = sessionUid;
        _flashbackOverallFrame = null;
        _session = null;
        _telemetry = null;
        _carStatus = null;
        _playerMotion = null;
        _sessionPlayerCarIndex = null;
        _playerLap = null;
        _profile = null;
        _engine = null;
        _lapRows.Clear();
        _pendingFromMode = null;
        _pendingExpectedMode = null;
        _retryCount = 0;
        _lastDecisionSignature = "";
        _lastInternalError = "";
        Volatile.Write(ref _lastDecision, null);
        Volatile.Write(ref _publicStatus, ErsAutopilotStatus.Initial(_options.OperatingMode));
    }

    private void ClearPendingCommand()
    {
        _pendingFromMode = null;
        _pendingExpectedMode = null;
        _retryCount = 0;
    }

    private static int? ValidGap(int? value) => value is > 0 and < 60_000 ? value : null;
}
