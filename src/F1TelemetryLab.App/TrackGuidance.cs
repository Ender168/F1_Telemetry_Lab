namespace F1TelemetryLab;

public enum TrackCueKind { Info, Brake, Speed }
public enum TrackCuePhase { Waiting, Info, Approach, Act, Success, Missed, Unavailable }

public sealed class TrackGuidancePlan
{
    public bool Enabled { get; set; }
    public bool SuppressOnPitLap { get; set; } = true;
    public List<TrackCue> Cues { get; set; } = new();
}

public sealed class TrackCue
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public TrackCueKind Kind { get; set; }
    public int Priority { get; set; }
    public double StartM { get; set; }
    public double EndM { get; set; }
    public double TargetM { get; set; }
    public double ToleranceM { get; set; } = 15;
    public double BrakeThreshold { get; set; } = 0.05;
    public double MinimumSpeedKph { get; set; }
    public double MaximumSpeedKph { get; set; }
}

public sealed record TrackGuidanceAdvice(TrackCuePhase Phase, string Text = "", double? DistanceM = null,
    double? MeasuredSpeedKph = null)
{
    public static TrackGuidanceAdvice Waiting { get; } = new(TrackCuePhase.Waiting);
    public string Format(bool russian)
    {
        if (Phase == TrackCuePhase.Waiting) return russian ? "Ожидание следующей подсказки" : "Waiting for next cue";
        var status = Phase switch
        {
            TrackCuePhase.Approach => russian ? $"До точки {DistanceM:0} м" : $"Target in {DistanceM:0} m",
            TrackCuePhase.Act => russian ? "ВЫПОЛНИ СЕЙЧАС" : "ACT NOW",
            TrackCuePhase.Success => russian ? "ВЫПОЛНЕНО" : "MET",
            TrackCuePhase.Missed => russian ? "ВНЕ ДОПУСКА" : "OUTSIDE TARGET",
            TrackCuePhase.Unavailable => russian ? "Недостаточно данных для оценки" : "Insufficient data to judge",
            _ => ""
        };
        if (MeasuredSpeedKph is double speed) status += $" · {speed:0} " + (russian ? "км/ч" : "km/h");
        return string.IsNullOrEmpty(status) ? Text : $"{Text}\n{status}";
    }
}

// Receives player samples, never sends controls. Measures at the configured checkpoint.
public sealed class TrackGuidanceAdvisor
{
    private sealed class Progress
    {
        public double Position;
        public double Brake;
        public int Speed;
        public DateTimeOffset At;
        public TrackGuidanceAdvice? Result;
    }
    private readonly Dictionary<string, Progress> _progress = new();
    private TrackGuidancePlan? _plan;
    public void Reset() { _progress.Clear(); _plan = null; }
    public static double Forward(double from, double to, int length) => (to - from + length) % length;

    public TrackGuidanceAdvice Observe(TrackGuidancePlan plan, int length, double distance, int speed,
        double brake, DateTimeOffset at)
    {
        if (!ReferenceEquals(_plan, plan)) { Reset(); _plan = plan; }
        if (!plan.Enabled || length <= 0 || !double.IsFinite(distance) || distance < 0 || distance >= length ||
            !double.IsFinite(brake) || brake is < 0 or > 1 || speed is < 0 or > 400)
        { Reset(); return TrackGuidanceAdvice.Waiting; }
        var visible = new List<(TrackCue Cue, TrackGuidanceAdvice Advice)>();
        foreach (var cue in plan.Cues)
        {
            var position = Forward(cue.StartM, distance, length);
            if (position > Forward(cue.StartM, cue.EndM, length)) { _progress.Remove(cue.Id); continue; }
            if (cue.Kind == TrackCueKind.Info)
            { visible.Add((cue, new(TrackCuePhase.Info, cue.Text))); continue; }
            var target = Forward(cue.StartM, cue.TargetM, length);
            _progress.TryGetValue(cue.Id, out var previous);
            // Gaps, teleports and rewinds cannot produce either a pass or a failure.
            var continuous = previous is not null && at >= previous.At && at - previous.At <= TimeSpan.FromMilliseconds(250) &&
                position >= previous.Position && position - previous.Position <= 100;
            var result = continuous ? previous!.Result : null;
            if (result is null)
            {
                if (cue.Kind == TrackCueKind.Brake)
                {
                    if (continuous && previous!.Brake <= cue.BrakeThreshold && brake > cue.BrakeThreshold)
                        result = new(Math.Abs(position - target) <= cue.ToleranceM ? TrackCuePhase.Success : TrackCuePhase.Missed, cue.Text);
                    else if (position > target + cue.ToleranceM)
                        result = new(continuous && previous!.Position <= target + cue.ToleranceM
                            ? TrackCuePhase.Missed : TrackCuePhase.Unavailable, cue.Text);
                    else if (!continuous && brake > cue.BrakeThreshold)
                        result = new(TrackCuePhase.Unavailable, cue.Text);
                }
                else if (position >= target)
                {
                    if (continuous && previous!.Position < target)
                    {
                        var fraction = (target - previous.Position) / (position - previous.Position);
                        var measured = previous.Speed + fraction * (speed - previous.Speed);
                        result = new(measured >= cue.MinimumSpeedKph && measured <= cue.MaximumSpeedKph
                            ? TrackCuePhase.Success : TrackCuePhase.Missed, cue.Text, MeasuredSpeedKph: measured);
                    }
                    else result = new(TrackCuePhase.Unavailable, cue.Text);
                }
            }
            var advice = result ?? new TrackGuidanceAdvice(position >= target - cue.ToleranceM
                ? TrackCuePhase.Act : TrackCuePhase.Approach, cue.Text, Math.Max(0, target - position));
            _progress[cue.Id] = new() { Position = position, Brake = brake, Speed = speed, At = at, Result = result };
            visible.Add((cue, advice));
        }
        return visible.OrderByDescending(x => x.Cue.Kind != TrackCueKind.Info)
            .ThenByDescending(x => x.Cue.Priority).ThenBy(x => x.Cue.Id, StringComparer.Ordinal)
            .Select(x => x.Advice).FirstOrDefault() ?? TrackGuidanceAdvice.Waiting;
    }

    public static void Validate(TrackGuidancePlan? plan, int length)
    {
        if (plan is null) return;
        if (plan.Cues is null || plan.Cues.Count > 100 || (plan.Enabled && plan.Cues.Count == 0))
            throw new InvalidDataException("track_guidance requires 1-100 cues when enabled.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cue in plan.Cues)
        {
            if (cue is null || string.IsNullOrWhiteSpace(cue.Id) || !ids.Add(cue.Id) ||
                string.IsNullOrWhiteSpace(cue.Text) || cue.Text.Length > 160 || !Enum.IsDefined(cue.Kind) ||
                !double.IsFinite(cue.StartM) || !double.IsFinite(cue.EndM) ||
                cue.StartM < 0 || cue.EndM < 0 || cue.StartM >= length || cue.EndM >= length || cue.StartM == cue.EndM)
                throw new InvalidDataException("Invalid track_guidance cue: unique id, text (1-160), kind and distinct track coordinates required.");
            if (cue.Kind == TrackCueKind.Info) continue;
            if (!double.IsFinite(cue.TargetM) || cue.TargetM < 0 || cue.TargetM >= length ||
                !double.IsFinite(cue.ToleranceM) || cue.ToleranceM <= 0 || cue.ToleranceM > 100 ||
                Forward(cue.StartM, cue.TargetM, length) <= cue.ToleranceM ||
                Forward(cue.StartM, cue.TargetM, length) + cue.ToleranceM >= Forward(cue.StartM, cue.EndM, length) ||
                !double.IsFinite(cue.BrakeThreshold) || cue.BrakeThreshold is <= 0 or >= 1)
                throw new InvalidDataException("Invalid track_guidance target: checkpoint and tolerance must lie inside the display window; brake_threshold must be 0-1 exclusive.");
            if (cue.Kind == TrackCueKind.Speed && (!double.IsFinite(cue.MinimumSpeedKph) || !double.IsFinite(cue.MaximumSpeedKph) ||
                cue.MinimumSpeedKph < 0 || cue.MaximumSpeedKph > 400 || cue.MinimumSpeedKph > cue.MaximumSpeedKph))
                throw new InvalidDataException("Invalid track_guidance speed range (0-400 km/h).");
        }
    }
}
