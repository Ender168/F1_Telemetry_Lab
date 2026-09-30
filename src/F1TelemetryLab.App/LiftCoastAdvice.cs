namespace F1TelemetryLab;

// Optional, calibrated points in the selected ERS profile. Advice only: never sends input.
public sealed class LiftCoastPlan
{
    public bool Enabled { get; set; }
    public double PreviewDistanceM { get; set; } = 300;
    public int MinimumSpeedKph { get; set; } = 100;
    public bool SuppressOnPitLap { get; set; } = true;
    public List<LiftCoastZone> Zones { get; set; } = new();
}

public sealed class LiftCoastZone
{
    public string Id { get; set; } = "";
    public string Corner { get; set; } = "";
    public double CornerDistanceM { get; set; }
    public double LiftBeforeCornerM { get; set; }
    // End the coast cue at the driver's calibrated braking point. Not a braking command.
    public double BrakeBeforeCornerM { get; set; }
}

public enum LiftCoastPhase { Waiting, NoProfile, Disabled, Suspended, Ready, Approach, Lift, Coasting, BrakeZone }

public sealed record LiftCoastAdvice(LiftCoastPhase Phase, string Corner = "", double DistanceToLiftM = 0,
    double DistanceToCornerM = 0, double LiftBeforeCornerM = 0)
{
    public static LiftCoastAdvice Waiting { get; } = new(LiftCoastPhase.Waiting);

    public string Format(bool russian) => Phase switch
    {
        LiftCoastPhase.Waiting => russian ? "Ожидание свежей телеметрии" : "Waiting for fresh telemetry",
        LiftCoastPhase.NoProfile => russian ? "Нет точек в профиле трассы" : "No points in the track profile",
        LiftCoastPhase.Disabled => russian ? "Подсказки выключены в профиле" : "Disabled in profile",
        LiftCoastPhase.Suspended => russian ? "Подсказка приостановлена" : "Advice suspended",
        LiftCoastPhase.Ready => russian ? "Ожидание следующей зоны" : "Waiting for next zone",
        LiftCoastPhase.Approach => russian
            ? $"{Corner} · сброс через {DistanceToLiftM:0} м\nСброс за {LiftBeforeCornerM:0} м до поворота"
            : $"{Corner} · lift in {DistanceToLiftM:0} m\nLift {LiftBeforeCornerM:0} m before corner",
        LiftCoastPhase.Lift => russian ? $"{Corner} · ОТПУСТИ ГАЗ\nДо поворота {DistanceToCornerM:0} м"
            : $"{Corner} · LIFT NOW\nCorner in {DistanceToCornerM:0} m",
        LiftCoastPhase.Coasting => russian ? $"{Corner} · ГАЗ ОТПУЩЕН\nДо поворота {DistanceToCornerM:0} м"
            : $"{Corner} · COASTING\nCorner in {DistanceToCornerM:0} m",
        _ => russian ? $"{Corner} · ЗОНА ТОРМОЖЕНИЯ" : $"{Corner} · BRAKING ZONE"
    };
}

public static class LiftCoastAdvisor
{
    public static void Validate(LiftCoastPlan? plan, int trackLength)
    {
        if (plan is null) return;
        if (!double.IsFinite(plan.PreviewDistanceM) || plan.PreviewDistanceM is <= 0 or > 1000 ||
            plan.MinimumSpeedKph is < 0 or > 400 || plan.Zones is null || plan.Zones.Count > 100)
            throw new InvalidDataException("Invalid lift_coast: preview_distance_m 1-1000, minimum_speed_kph 0-400, zones up to 100.");
        if (plan.Enabled && plan.Zones.Count == 0)
            throw new InvalidDataException("Enabled lift_coast requires calibrated zones.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in plan.Zones)
        {
            if (zone is null || string.IsNullOrWhiteSpace(zone.Id) || !ids.Add(zone.Id) ||
                string.IsNullOrWhiteSpace(zone.Corner) || zone.Corner.Length > 40 ||
                !double.IsFinite(zone.CornerDistanceM) || !double.IsFinite(zone.LiftBeforeCornerM) ||
                !double.IsFinite(zone.BrakeBeforeCornerM) || zone.CornerDistanceM < 0 || zone.CornerDistanceM >= trackLength ||
                zone.BrakeBeforeCornerM < 0 || zone.LiftBeforeCornerM <= zone.BrakeBeforeCornerM ||
                zone.LiftBeforeCornerM + plan.PreviewDistanceM >= trackLength)
                throw new InvalidDataException("Invalid lift_coast zone: unique id, corner, track distance and lift_before_corner_m > brake_before_corner_m >= 0 required.");
        }
        // Ambiguous overlapping approach/coast/brake windows are rejected, including across the line.
        for (var i = 0; i < plan.Zones.Count; i++)
        for (var j = i + 1; j < plan.Zones.Count; j++)
        {
            var a = plan.Zones[i]; var b = plan.Zones[j];
            var ab = Forward(a.CornerDistanceM, b.CornerDistanceM, trackLength);
            var ba = Forward(b.CornerDistanceM, a.CornerDistanceM, trackLength);
            if (ab < b.LiftBeforeCornerM + plan.PreviewDistanceM || ba < a.LiftBeforeCornerM + plan.PreviewDistanceM)
                throw new InvalidDataException("lift_coast zone windows must not overlap.");
        }
    }

    public static LiftCoastAdvice Evaluate(LiftCoastPlan plan, int trackLength, double lapDistance,
        int speed, double throttle, double brake)
    {
        if (!plan.Enabled) return new(LiftCoastPhase.Disabled);
        if (trackLength <= 0 || !double.IsFinite(lapDistance) || lapDistance < 0 || lapDistance >= trackLength ||
            !double.IsFinite(throttle) || throttle is < 0 or > 1 || !double.IsFinite(brake) || brake is < 0 or > 1)
            return LiftCoastAdvice.Waiting;
        if (speed < plan.MinimumSpeedKph) return new(LiftCoastPhase.Suspended);
        foreach (var zone in plan.Zones)
        {
            var remaining = Forward(lapDistance, zone.CornerDistanceM, trackLength);
            var toLift = remaining - zone.LiftBeforeCornerM;
            if (toLift > plan.PreviewDistanceM) continue;
            var phase = brake > 0.05 || remaining <= zone.BrakeBeforeCornerM ? LiftCoastPhase.BrakeZone
                : throttle <= 0.05 ? LiftCoastPhase.Coasting
                : toLift > 0 ? LiftCoastPhase.Approach : LiftCoastPhase.Lift;
            return new(phase, zone.Corner, Math.Max(0, toLift), remaining, zone.LiftBeforeCornerM);
        }
        return new(LiftCoastPhase.Ready);
    }

    private static double Forward(double from, double to, int length) => (to - from + length) % length;
}
