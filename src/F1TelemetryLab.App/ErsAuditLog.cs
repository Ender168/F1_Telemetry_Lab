using System.Globalization;
using System.Text;

namespace F1TelemetryLab;

public sealed class ErsAuditLog : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public ErsAuditLog(string sessionFolder)
    {
        var path = Path.Combine(sessionFolder, "ers_control_log.csv");
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        _writer.WriteLine("received_at,lap,lap_distance_m,segment,battery_pct,current_mode,target_mode,gap_ahead_ms,gap_behind_ms,rule_id,action,reason");
    }

    public void Write(ErsControlDecision decision, string action)
    {
        lock (_sync)
        {
            _writer.WriteLine(string.Join(",",
                Csv(decision.ReceivedAt.ToString("O", CultureInfo.InvariantCulture)),
                decision.LapNumber.ToString(CultureInfo.InvariantCulture),
                decision.LapDistanceM.ToString("0.0", CultureInfo.InvariantCulture),
                Csv(decision.Segment),
                decision.BatteryPct.ToString("0.00", CultureInfo.InvariantCulture),
                Csv(decision.CurrentMode.ToString()),
                Csv(decision.TargetMode.ToString()),
                NullableNumber(decision.GapAheadMs),
                NullableNumber(decision.GapBehindMs),
                Csv(decision.RuleId),
                Csv(action),
                Csv(decision.Reason)));
        }
    }

    public void Dispose()
    {
        lock (_sync) _writer.Dispose();
    }

    private static string NullableNumber(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Csv(string? value)
    {
        var text = value ?? "";
        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
