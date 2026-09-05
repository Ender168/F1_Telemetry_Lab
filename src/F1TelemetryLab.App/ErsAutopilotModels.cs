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

public enum ErsTacticalMode
{
    Neutral,
    Attack,
    Defend
}

public enum ErsTacticalIntensity
{
    None,
    Pressure,
    Critical
}

public enum ErsEnergyState
{
    Critical,
    Conserve,
    Balanced,
    Surplus
}

public enum ErsDrsRequirement
{
    Any,
    Active,
    Inactive
}

public enum ErsRuleCondition
{
    Always,
    CriticalBattery,
    LowBattery,
    Neutral,
    Attack,
    Defend,
    AttackPressure,
    AttackCritical,
    DefendPressure,
    DefendCritical,
    Battle,
    HighBattery,
    EnergyDeficit,
    EnergySurplus,
    ClosingLaps,
    FinalLap,
    AttackOrHighBattery,
    DefendOrHighBattery,
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
    public int ProfileRevision { get; set; } = 1;
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

    // Legacy threshold used by v0.10.0 profiles and the Battle/BattleOrHighBattery conditions.
    public int BattleGapMs { get; set; } = 1_200;

    // Tactical-mode thresholds. Attack watches the car ahead, Defend watches the car behind.
    public int AttackGapMs { get; set; } = 1_200;
    public int DefendGapMs { get; set; } = 1_000;
    public int TacticalExitMarginMs { get; set; } = 250;
    public int DefendPriorityMarginMs { get; set; } = 200;

    public int MinimumControlSpeedKph { get; set; } = 30;
    public ErsTacticalPlan? Tactical { get; set; }
    public ErsEnergyPlan? EnergyPlan { get; set; }
    public List<ErsControlRule> Rules { get; set; } = new();

    [JsonIgnore]
    public string SourcePath { get; set; } = "";
}

public sealed class ErsTacticalPlan
{
    public int AttackPressureGapMs { get; set; } = 1_800;
    public int AttackCriticalGapMs { get; set; } = 900;
    public int DefendPressureGapMs { get; set; } = 1_500;
    public int DefendCriticalGapMs { get; set; } = 700;
    public int ExitMarginMs { get; set; } = 250;
    public int DefendPriorityMarginMs { get; set; } = 200;
    public int ClosingRateWindowMs { get; set; } = 3_000;
    public double RapidClosingRateMsPerSecond { get; set; } = -80;
    public int ClosingRateGapExtensionMs { get; set; } = 400;
}

public sealed class ErsEnergyPlan
{
    public double TargetTolerancePct { get; set; } = 3;
    public double SurplusReleasePct { get; set; } = 8;
    public double LowValueReservePct { get; set; } = 10;
    public double ConserveEnterMarginPct { get; set; } = 1;
    public double ConserveExitMarginPct { get; set; } = 4;
    public double LearningRate { get; set; } = 0.25;
    public ErsDeployMode ConserveMode { get; set; } = ErsDeployMode.None;
    public int ClosingLaps { get; set; } = 3;
    public bool FinalLapRelease { get; set; } = true;
    public double FinalLapFloorPct { get; set; } = 8;
    public List<ErsEnergyCheckpoint> Checkpoints { get; set; } = new();
}

public sealed class ErsEnergyCheckpoint
{
    public string Id { get; set; } = "";
    public double DistanceM { get; set; }
    public double TargetPct { get; set; }
    public double MinimumPct { get; set; }
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
    public double? MaximumDeployPct { get; set; }
    public bool OncePerLap { get; set; }
    public double DeploymentValue { get; set; } = 1;
    public double? MinimumEnergySurplusPct { get; set; }
    public double? FinalLapMinimumBatteryPct { get; set; }
    public ErsDrsRequirement DrsRequirement { get; set; } = ErsDrsRequirement.Any;
    public int? MinimumLapNumber { get; set; }
    public int? MaximumLapsRemaining { get; set; }
    public int? MinimumLapsRemaining { get; set; }
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
    public bool InAttackRange(int thresholdMs) => GapAheadMs is > 0 && GapAheadMs <= thresholdMs;

    public bool InDefendRange(int thresholdMs) => GapBehindMs is > 0 && GapBehindMs <= thresholdMs;

    public bool InBattle(int thresholdMs) => InAttackRange(thresholdMs) || InDefendRange(thresholdMs);

    public int TotalLaps { get; init; }

    public bool DrsActive { get; init; }

    public int? LapsRemaining => TotalLaps > 0 && LapNumber > 0
        ? Math.Max(0, TotalLaps - LapNumber + 1)
        : null;
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
    public ErsTacticalMode TacticalMode { get; init; } = ErsTacticalMode.Neutral;

    public ErsTacticalIntensity TacticalIntensity { get; init; } = ErsTacticalIntensity.None;

    public ErsEnergyState EnergyState { get; init; } = ErsEnergyState.Balanced;

    public double EnergyTargetPct { get; init; }

    public double EnergyMinimumPct { get; init; }

    public double ProjectedNextPct { get; init; }

    public double NextMinimumPct { get; init; }

    public string NextCheckpointId { get; init; } = "";

    public string ProjectionSource { get; init; } = "profile";

    public double? RuleBudgetRemainingPct { get; init; }

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
    public ErsControlDecision? Decision { get; init; }

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
            var energy = Decision is null ? "" : $" | {Decision.EnergyState}";
            return $"{mode} | {State}{profile}{segment}{deploy}{battery}{energy}\n{Detail}";
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
