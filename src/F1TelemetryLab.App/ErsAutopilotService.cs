namespace F1TelemetryLab;

public sealed class ErsAutopilotService : IDisposable
{
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
    private string _lastInternalError = "";
    private ulong _sessionUid;
    private ErsControlDecision? _lastDecision;

    public ErsAutopilotService(
        ErsAutopilotOptions options,
        ErsProfileLoadResult profiles,
        IErsInputSink inputSink,
        Action<ErsAuditRecord>? auditSink = null,
        Action<ErsControlProfile, ErsAutopilotOptions>? profileSink = null,
        Action<string>? log = null)
    {
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
                $"ERS autopilot LIVE: profile feedback controls {ErsProfileStore.VirtualKeyName(options.DecreaseVirtualKey)}/{ErsProfileStore.VirtualKeyName(options.IncreaseVirtualKey)}. Online sessions are always blocked; F12 stops input for the rest of the recording.",
            ErsAutopilotOperatingMode.DryRun => "ERS autopilot DRY-RUN: decisions are logged, no keys are sent.",
            _ => "ERS autopilot is off."
        });
    }

    public ErsAutopilotStatus Status => Volatile.Read(ref _publicStatus);

    public ErsControlDecision? LastDecision => Volatile.Read(ref _lastDecision);

    public void ProcessPacket(byte[] payload, DateTimeOffset receivedAt)
    {
        if (_options.OperatingMode == ErsAutopilotOperatingMode.Off) return;
        try
        {
            if (!F12026Parser.TryParseHeader(payload, out var header) || header.PacketFormat != AppInfo.SupportedPacketFormat) return;
            if (_options.OperatingMode == ErsAutopilotOperatingMode.Live && !PollInputRelease(receivedAt)) return;
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

            switch (header.PacketId)
            {
                case 1:
                    _session = F12026Parser.TryParseSessionControl(payload, receivedAt);
                    SelectProfileIfPossible();
                    break;
                case 2:
                    UpdateLapRows(payload, receivedAt);
                    break;
                case 6:
                    _telemetry = F12026Parser.ParseCarTelemetryPacket(payload, receivedAt)
                        .FirstOrDefault(sample => sample.IsPlayer);
                    break;
                case 7:
                    _carStatus = F12026Parser.ParseCarStatusPacket(payload, receivedAt)
                        .FirstOrDefault(sample => sample.IsPlayer);
                    break;
                default:
                    return;
            }

            Evaluate(receivedAt);
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

    public void Dispose()
    {
        _inputSink.Dispose();
        _audit.Dispose();
    }

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
            TotalLaps = _session.TotalLaps,
            DrsActive = _telemetry?.Drs == 1
        };
    }

    private string BlockReason(DateTimeOffset now)
    {
        if (_inputFault) return string.IsNullOrWhiteSpace(_inputFaultReason)
            ? "Live ERS input is blocked until the next recording."
            : _inputFaultReason;
        if (_session is null) return "Waiting for Session packet 1.";
        if (_session.IsNetworkGame) return "Online session detected. Automatic input is hard-blocked.";
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

    private bool PollInputRelease(DateTimeOffset now)
    {
        var result = _inputSink.Poll(now);
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
        var signature = $"{decision.Blocked}|{decision.RuleId}|{decision.TargetMode}|{decision.Reason}";
        if (string.Equals(signature, _lastDecisionSignature, StringComparison.Ordinal)) return;
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

    private void ResetForSession(ulong sessionUid)
    {
        _sessionUid = sessionUid;
        _session = null;
        _telemetry = null;
        _carStatus = null;
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
