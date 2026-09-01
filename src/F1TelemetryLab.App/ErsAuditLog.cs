namespace F1TelemetryLab;

public sealed record ErsAuditRecord(
    DateTimeOffset ReceivedAt,
    int LapNumber,
    double LapDistanceM,
    string Segment,
    double BatteryPct,
    string CurrentMode,
    string TargetMode,
    int? GapAheadMs,
    int? GapBehindMs,
    string RuleId,
    string Action,
    string Reason);

public sealed class ErsAuditLog : IDisposable
{
    private readonly Action<ErsAuditRecord>? _sink;

    public ErsAuditLog(Action<ErsAuditRecord>? sink)
    {
        _sink = sink;
    }

    public void Write(ErsControlDecision decision, string action)
    {
        _sink?.Invoke(new ErsAuditRecord(
            decision.ReceivedAt,
            decision.LapNumber,
            decision.LapDistanceM,
            decision.Segment,
            decision.BatteryPct,
            decision.CurrentMode.ToString(),
            decision.TargetMode.ToString(),
            decision.GapAheadMs,
            decision.GapBehindMs,
            decision.RuleId,
            action,
            decision.Reason));
    }

    public void Dispose()
    {
    }
}
