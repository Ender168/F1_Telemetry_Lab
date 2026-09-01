using System.Globalization;

namespace F1TelemetryLab;

public enum AdviceConfidence
{
    Unavailable,
    Low,
    Medium,
    High
}

public enum ErsAggressionAdvice
{
    Waiting,
    Critical,
    Save,
    OnPlan,
    Aggressive
}

public sealed record CompletedLiveLap(
    ulong SessionUid,
    int LapNumber,
    uint LapTimeMs,
    bool Clean,
    bool PitLap,
    bool SafetyCarAffected,
    int VisualCompound,
    int TyreAgeLaps,
    double TyreWearStartPct,
    double TyreWearEndPct,
    double TyreWearDeltaPct,
    double ErsStartPct,
    double ErsEndPct,
    double ErsDeltaPct,
    int PositionEnd,
    string CompletionEvidence);

public sealed record TyreLifeAdvice(
    bool Available,
    int VisualCompound,
    int TyreAgeLaps,
    double WearFlPct,
    double WearFrPct,
    double WearRlPct,
    double WearRrPct,
    string WorstWheel,
    double WorstWearPct,
    double? WearRatePctPerLap,
    int? RemainingLapsLow,
    int? RemainingLapsHigh,
    double SafeWearLimitPct,
    int ObservationCount,
    AdviceConfidence Confidence,
    string Reason);

public sealed record PitPositionAdvice(
    bool Available,
    int? PositionLow,
    int? PositionHigh,
    double PitLossSeconds,
    double UncertaintySeconds,
    int NearbyCars,
    AdviceConfidence Confidence,
    string Reason);

public sealed record ErsRaceAdvice(
    bool Available,
    double BatteryPct,
    int CurrentMode,
    double TargetMinPct,
    double TargetMaxPct,
    ErsAggressionAdvice Aggression,
    int? AggressiveLaps,
    string CurrentSegment,
    string NextBoostSegment,
    int? DistanceToNextBoostM,
    AdviceConfidence Confidence,
    string Reason);

public sealed record RaceEngineerSnapshot(
    DateTimeOffset UpdatedAt,
    string ProfileId,
    int CurrentLap,
    int CurrentPosition,
    IReadOnlyList<CompletedLiveLap> LastLaps,
    TyreLifeAdvice Tyres,
    PitPositionAdvice Pit,
    ErsRaceAdvice Ers)
{
    public static RaceEngineerSnapshot Waiting { get; } = new(
        DateTimeOffset.MinValue,
        "",
        0,
        0,
        Array.Empty<CompletedLiveLap>(),
        new TyreLifeAdvice(false, 0, 0, 0, 0, 0, 0, "", 0, null, null, null, 75, 0, AdviceConfidence.Unavailable, "Waiting for player tyre telemetry."),
        new PitPositionAdvice(false, null, null, 0, 0, 0, AdviceConfidence.Unavailable, "Waiting for race positions."),
        new ErsRaceAdvice(false, 0, 0, 0, 0, ErsAggressionAdvice.Waiting, null, "", "", null, AdviceConfidence.Unavailable, "Waiting for ERS telemetry."));
}

public sealed record RaceEngineerDisplay(
    string Laps,
    string Tyres,
    string Pit,
    string Ers,
    string Confidence);

public static class RaceEngineerText
{
    public static RaceEngineerDisplay Format(RaceEngineerSnapshot snapshot, bool russian)
    {
        return new RaceEngineerDisplay(
            FormatLaps(snapshot.LastLaps, russian),
            FormatTyres(snapshot.Tyres, russian),
            FormatPit(snapshot.Pit, russian),
            FormatErs(snapshot.Ers, russian),
            russian
                ? $"Доверие: шины {Confidence(snapshot.Tyres.Confidence, true)}, пит {Confidence(snapshot.Pit.Confidence, true)}, ERS {Confidence(snapshot.Ers.Confidence, true)}"
                : $"Confidence: tyres {Confidence(snapshot.Tyres.Confidence, false)}, pit {Confidence(snapshot.Pit.Confidence, false)}, ERS {Confidence(snapshot.Ers.Confidence, false)}");
    }

    public static string Confidence(AdviceConfidence value, bool russian) => (value, russian) switch
    {
        (AdviceConfidence.High, true) => "высокое",
        (AdviceConfidence.Medium, true) => "среднее",
        (AdviceConfidence.Low, true) => "низкое",
        (AdviceConfidence.Unavailable, true) => "нет оценки",
        (AdviceConfidence.High, false) => "high",
        (AdviceConfidence.Medium, false) => "medium",
        (AdviceConfidence.Low, false) => "low",
        _ => "unavailable"
    };

    private static string FormatLaps(IReadOnlyList<CompletedLiveLap> laps, bool russian)
    {
        if (laps.Count == 0) return russian ? "Ожидание первого завершённого круга" : "Waiting for the first completed lap";
        return string.Join("  |  ", laps.TakeLast(3).Select(lap =>
        {
            var flag = lap.PitLap ? " PIT" : !lap.Clean ? " INVALID" : "";
            return $"L{lap.LapNumber} {LapOption.FormatLapTime(lap.LapTimeMs)}{flag}";
        }));
    }

    private static string FormatTyres(TyreLifeAdvice value, bool russian)
    {
        if (!value.Available) return russian ? "Ожидание данных шин" : "Waiting for tyre data";
        var rate = value.WearRatePctPerLap is null
            ? (russian ? "темп неизвестен" : "rate unavailable")
            : $"{value.WearRatePctPerLap:0.0}%/{(russian ? "круг" : "lap")}";
        var remaining = value.RemainingLapsLow is null || value.RemainingLapsHigh is null
            ? (russian ? "ресурс уточняется" : "life estimate pending")
            : $"~{value.RemainingLapsLow}-{value.RemainingLapsHigh} {(russian ? "кругов" : "laps")} {(russian ? "до" : "to")} {value.SafeWearLimitPct:0}%";
        return $"{Compound(value.VisualCompound)} · {value.TyreAgeLaps}L · {value.WorstWheel} {value.WorstWearPct:0.0}% · {rate} · {remaining}";
    }

    private static string FormatPit(PitPositionAdvice value, bool russian)
    {
        if (!value.Available || value.PositionLow is null || value.PositionHigh is null)
            return russian ? "Позиция после пита пока не рассчитана" : "Post-pit position is not available yet";
        var position = value.PositionLow == value.PositionHigh
            ? $"P{value.PositionLow}"
            : $"P{value.PositionLow}-P{value.PositionHigh}";
        var traffic = value.NearbyCars switch
        {
            >= 3 => russian ? "высокий" : "high",
            >= 1 => russian ? "средний" : "medium",
            _ => russian ? "низкий" : "low"
        };
        return russian
            ? $"После пита {position} · потеря {value.PitLossSeconds:0.0} ± {value.UncertaintySeconds:0.0} с · трафик {traffic}"
            : $"After pit {position} · loss {value.PitLossSeconds:0.0} ± {value.UncertaintySeconds:0.0}s · traffic {traffic}";
    }

    private static string FormatErs(ErsRaceAdvice value, bool russian)
    {
        if (!value.Available) return russian ? "Ожидание данных ERS" : "Waiting for ERS data";
        var stance = (value.Aggression, russian) switch
        {
            (ErsAggressionAdvice.Critical, true) => "КРИТИЧНО: минимальный расход",
            (ErsAggressionAdvice.Save, true) => "Экономить",
            (ErsAggressionAdvice.OnPlan, true) => "По плану",
            (ErsAggressionAdvice.Aggressive, true) => "Можно агрессивнее",
            (ErsAggressionAdvice.Critical, false) => "CRITICAL: minimum deployment",
            (ErsAggressionAdvice.Save, false) => "Save",
            (ErsAggressionAdvice.OnPlan, false) => "On plan",
            (ErsAggressionAdvice.Aggressive, false) => "Can be more aggressive",
            _ => russian ? "Ожидание" : "Waiting"
        };
        var duration = value.AggressiveLaps is > 0
            ? $" · ~{value.AggressiveLaps} {(russian ? "круг" : "lap")}{(value.AggressiveLaps == 1 ? "" : russian ? "а" : "s")}"
            : "";
        var next = value.DistanceToNextBoostM is not null && !string.IsNullOrWhiteSpace(value.NextBoostSegment)
            ? $" · {(russian ? "следующий Boost" : "next Boost")} {value.NextBoostSegment}"
            : "";
        var explanation = value.Aggression switch
        {
            ErsAggressionAdvice.Critical => russian ? "ниже аварийного резерва" : "below critical reserve",
            ErsAggressionAdvice.Save => russian
                ? $"ниже цели на {Math.Max(0, value.TargetMinPct - value.BatteryPct):0}%"
                : $"{Math.Max(0, value.TargetMinPct - value.BatteryPct):0}% below target",
            ErsAggressionAdvice.Aggressive when value.BatteryPct > value.TargetMaxPct => russian
                ? $"выше цели на {value.BatteryPct - value.TargetMaxPct:0}%"
                : $"{value.BatteryPct - value.TargetMaxPct:0}% above target",
            ErsAggressionAdvice.Aggressive => russian ? "доступен тактический запас" : "tactical reserve is available",
            _ => russian ? "заряд в целевом коридоре" : "battery is inside the target corridor"
        };
        return $"{value.BatteryPct:0}% · {stance}{duration} · {(russian ? "цель" : "target")} {value.TargetMinPct:0}-{value.TargetMaxPct:0}% · {explanation}{next}";
    }

    public static string Compound(int visualCompound) => visualCompound switch
    {
        16 => "Soft",
        17 => "Medium",
        18 => "Hard",
        7 => "Intermediate",
        8 => "Wet",
        _ => visualCompound > 0 ? $"Tyre {visualCompound.ToString(CultureInfo.InvariantCulture)}" : "Tyre ?"
    };
}
