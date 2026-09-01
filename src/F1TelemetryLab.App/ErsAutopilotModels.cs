using System.Text.Json.Serialization;

namespace F1TelemetryLab;

public enum ErsAutopilotOperatingMode
{
    Off,
    DryRun,
    Live
}

public enum ErsDeployMode
{
    None = 0,
    Medium = 1,
    Hotlap = 2,
    Boost = 3
}

public enum ErsRuleCondition
{
    Always,
    CriticalBattery,
    LowBattery,
    Battle,
    HighBattery,
    BattleOrHighBattery
}

public enum ErsInputDirection
{
    Decrease,
    Increase
}

public sealed class ErsAutopilotOptions
{
    public ErsAutopilotOperatingMode OperatingMode { get; init; } = ErsAutopilotOperatingMode.DryRun;
    public int DecreaseVirtualKey { get; init; } = 0x76; // F7
    public int IncreaseVirtualKey { get; init; } = 0x77; // F8
    public int EmergencyStopVirtualKey { get; init; } = 0x7B; // F12
    public int KeyHoldMilliseconds { get; init; } = 80;
    public int MinimumCommandIntervalMs { get; init; } = 350;
    public int ConfirmationTimeoutMs { get; init; } = 900;
    public int MaximumRetries { get; init; } = 3;
    public int TelemetryFreshnessMs { get; init; } = 1_000;
    public int SessionFreshnessMs { get; init; } = 3_000;

    public static ErsAutopilotOptions FromSettings(AppSettings settings) => new()
    {
        OperatingMode = ParseOperatingMode(settings.ErsAutopilotMode),
        DecreaseVirtualKey = settings.ErsDecreaseVirtualKey,
        IncreaseVirtualKey = settings.ErsIncreaseVirtualKey
    };

    public static ErsAutopilotOperatingMode ParseOperatingMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "live" => ErsAutopilotOperatingMode.Live,
        "off" => ErsAutopilotOperatingMode.Off,
        _ => ErsAutopilotOperatingMode.DryRun
    };

    public static string ToSettingValue(ErsAutopilotOperatingMode value) => value switch
    {
        ErsAutopilotOperatingMode.Live => "live",
        ErsAutopilotOperatingMode.Off => "off",
        _ => "dry-run"
    };
}

public sealed class ErsControlProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int SelectionPriority { get; set; }
    public int TrackId { get; set; } = -1;
    public string TrackName { get; set; } = "";
    public int TrackLengthM { get; set; }
    public List<int> SessionTypes { get; set; } = new();
    public bool DryOnly { get; set; } = true;
    public ErsDeployMode DefaultMode { get; set; } = ErsDeployMode.Medium;
    public double BatteryCapacityJ { get; set; } = 4_000_000;
    public double CriticalBatteryPct { get; set; } = 12;
    public double RecoveryEnterPct { get; set; } = 35;
    public double RecoveryExitPct { get; set; } = 45;
    public double HighBatteryPct { get; set; } = 65;
    public int BattleGapMs { get; set; } = 1_200;
    public int MinimumControlSpeedKph { get; set; } = 30;
    public List<ErsControlRule> Rules { get; set; } = new();

    [JsonIgnore]
    public string SourcePath { get; set; } = "";
}

public sealed class ErsControlRule
{
    public string Id { get; set; } = "";
    public string Segment { get; set; } = "";
    public string Note { get; set; } = "";
    public int Priority { get; set; }
    public double StartM { get; set; }
    public double EndM { get; set; }
    public ErsDeployMode TargetMode { get; set; } = ErsDeployMode.Medium;
    public ErsRuleCondition Condition { get; set; } = ErsRuleCondition.Always;
    public double? MinimumBatteryPct { get; set; }
    public double? MinimumThrottlePct { get; set; }
    public int? MinimumSpeedKph { get; set; }
    public int? MaximumActiveMs { get; set; }
    public bool OncePerLap { get; set; }
}

public sealed record ErsControlState(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    int TrackId,
    int SessionType,
    int TrackLengthM,
    int Weather,
    bool GamePaused,
    bool IsSpectating,
    int SafetyCarStatus,
    bool IsNetworkGame,
    int LapNumber,
    double LapDistanceM,
    int PitStatus,
    int DriverStatus,
    int ResultStatus,
    int SpeedKph,
    double ThrottlePct,
    double BatteryPct,
    ErsDeployMode CurrentMode,
    bool NetworkPaused,
    int? GapAheadMs,
    int? GapBehindMs,
    bool AutomationAllowed,
    string BlockReason)
{
    public bool InBattle(int thresholdMs) =>
        GapAheadMs is > 0 && GapAheadMs <= thresholdMs ||
        GapBehindMs is > 0 && GapBehindMs <= thresholdMs;
}

public sealed record ErsControlDecision(
    DateTimeOffset ReceivedAt,
    bool Blocked,
    ErsDeployMode CurrentMode,
    ErsDeployMode TargetMode,
    string RuleId,
    string Segment,
    string Reason,
    double BatteryPct,
    int LapNumber,
    double LapDistanceM,
    int? GapAheadMs,
    int? GapBehindMs)
{
    public static ErsControlDecision BlockedDecision(ErsControlState state, string reason) => new(
        state.ReceivedAt,
        true,
        state.CurrentMode,
        state.CurrentMode,
        "blocked",
        "",
        reason,
        state.BatteryPct,
        state.LapNumber,
        state.LapDistanceM,
        state.GapAheadMs,
        state.GapBehindMs);
}

public sealed record ErsAutopilotStatus(
    ErsAutopilotOperatingMode OperatingMode,
    string State,
    string ProfileId,
    string Segment,
    ErsDeployMode? CurrentMode,
    ErsDeployMode? TargetMode,
    double? BatteryPct,
    string Detail)
{
    public static ErsAutopilotStatus Initial(ErsAutopilotOperatingMode mode) => new(
        mode,
        mode == ErsAutopilotOperatingMode.Off ? "Off" : "Waiting",
        "",
        "",
        null,
        null,
        null,
        mode == ErsAutopilotOperatingMode.Off ? "Disabled" : "Waiting for 2026 session telemetry");

    public string Display
    {
        get
        {
            var mode = OperatingMode switch
            {
                ErsAutopilotOperatingMode.Live => "LIVE",
                ErsAutopilotOperatingMode.DryRun => "DRY-RUN",
                _ => "OFF"
            };
            var battery = BatteryPct is null ? "" : $" | {BatteryPct:0}%";
            var deploy = CurrentMode is null || TargetMode is null ? "" : $" | {CurrentMode} -> {TargetMode}";
            var profile = string.IsNullOrWhiteSpace(ProfileId) ? "" : $" | {ProfileId}";
            var segment = string.IsNullOrWhiteSpace(Segment) ? "" : $" | {Segment}";
            return $"{mode} | {State}{profile}{segment}{deploy}{battery}\n{Detail}";
        }
    }
}

public sealed record SessionControlSample(
    DateTimeOffset ReceivedAt,
    ulong SessionUid,
    float SessionTime,
    int Weather,
    int TotalLaps,
    int TrackLengthM,
    int SessionType,
    int TrackId,
    int Formula,
    bool GamePaused,
    bool IsSpectating,
    int SafetyCarStatus,
    bool IsNetworkGame,
    int ErsAssist);
