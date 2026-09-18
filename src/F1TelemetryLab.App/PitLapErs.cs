namespace F1TelemetryLab;

public sealed record PitLapErsStatus(
    bool Active, int? Lap, bool? ButtonsPressed, ErsAutopilotOperatingMode Mode,
    string AutomationState, DateTimeOffset TelemetryAt, string Detail)
{
    public static PitLapErsStatus Off { get; } = new(false, null, null,
        ErsAutopilotOperatingMode.Off, "Off", DateTimeOffset.MinValue, "");

    public bool HasFreshTelemetry(DateTimeOffset now) =>
        TelemetryAt != DateTimeOffset.MinValue && now - TelemetryAt <= TimeSpan.FromSeconds(1);

    public string Format(bool russian, DateTimeOffset now)
    {
        var fresh = HasFreshTelemetry(now);
        var state = Active ? (russian ? $"ВКЛ · круг {Lap}" : $"ON · lap {Lap}") : (russian ? "ВЫКЛ" : "OFF");
        var buttons = !fresh || ButtonsPressed is null ? "?" : ButtonsPressed.Value
            ? (russian ? "нажато" : "pressed") : (russian ? "не нажато" : "not pressed");
        var control = Mode == ErsAutopilotOperatingMode.Off ? (russian ? "Автопилот выключен" : "Autopilot off")
            : !fresh ? (russian ? "Нет свежей телеметрии" : "Telemetry unavailable")
            : Mode == ErsAutopilotOperatingMode.DryRun ? "DRY-RUN"
            : AutomationState is "Blocked" or "Emergency stop" or "Stopped" or "No profile"
                ? (russian ? "ERS: заблокирован" : "ERS: blocked")
            : AutomationState == "Waiting for game" ? (russian ? "Ожидание окна игры" : "Waiting for game window")
            : "LIVE";
        return $"{state}\n△ + ○: {buttons} · {control}";
    }
}

// F1 UDP button flags, not XInput flags: Triangle/Y = 0x2, Circle/B = 0x4.
internal sealed class PitLapErsController
{
    public const uint ChordMask = 0x00000006;
    private bool _armed = true;
    private uint? _lastButtonFrame;
    public int? Lap { get; private set; }
    public bool Active => Lap is not null;
    public bool? ButtonsPressed { get; private set; }

    public bool ObserveButtons(uint buttons, uint overallFrame)
    {
        if (_lastButtonFrame is uint last &&
            overallFrame <= last) return false;
        _lastButtonFrame = overallFrame;
        var chord = buttons & ChordMask;
        ButtonsPressed = chord == ChordMask;
        if (chord == 0) _armed = true; // Both buttons must be released before another toggle.
        if (chord != ChordMask || !_armed) return false;
        _armed = false;
        return true;
    }

    public void Toggle(int lap) => Lap = Active ? null : lap;
    public void Cancel() => Lap = null;
    public void Reset()
    {
        Cancel();
        ButtonsPressed = null;
        _lastButtonFrame = null;
        _armed = false; // Do not re-enable from a chord held across a reset.
    }
}
