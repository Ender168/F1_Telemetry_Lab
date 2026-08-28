using System.Globalization;

namespace F1TelemetryLab;

public sealed record AnalysisTableColumn(string Key, string Header, double Width, string Help, bool AlignRight = false, bool Wrap = false, bool GroupStart = false);
public sealed record AnalysisTableRow(IReadOnlyDictionary<string, string> Values, string Severity = "normal", string Help = "");
public sealed record AnalysisTableResult(IReadOnlyList<AnalysisTableColumn> Columns, IReadOnlyList<AnalysisTableRow> Rows, string Status, string Legend);

public static class RaceAnalysisDataService
{
    public static readonly string[] CompareModes = { "Lap number", "Stint lap", "Compound" };
    public static readonly string[] MetricGroups = { "Pace", "Tyres", "Fuel/ERS", "Damage" };

    public static string BuildRaceSummary(IReadOnlyList<RaceLapReportRow> rows)
    {
        if (rows.Count == 0) return "Summary: no rows loaded.";
        var clean = rows.Where(r => r.CleanLap && !r.PitThisLap && r.LapTimeMs > 0).ToList();
        var timed = rows.Where(r => r.LapTimeMs > 0).ToList();
        var bestClean = clean.OrderBy(r => r.LapTimeMs).FirstOrDefault();
        var bestAny = timed.OrderBy(r => r.LapTimeMs).FirstOrDefault();
        var avgClean = clean.Count == 0 ? double.NaN : clean.Average(r => r.LapTimeMs);
        var avgWearLap = rows.Where(r => !r.PitThisLap && Valid(r.TyreWearAvgDelta)).Select(r => r.TyreWearAvgDelta).DefaultIfEmpty(double.NaN).Average();
        var avgFuel = rows.Where(r => !r.PitThisLap && Valid(r.FuelUsed)).Select(r => r.FuelUsed).DefaultIfEmpty(double.NaN).Average();
        var minErs = rows.Select(r => ErsPct(r.ErsEnd)).Where(Valid).DefaultIfEmpty(double.NaN).Min();
        var damageAffected = rows.Any(r => r.DamageDeltaMax > 0 || MaxDamage(r) > 0);
        var pitCount = rows.Count(r => r.PitThisLap);
        var quality = BuildQualityFlags(rows);

        return "Summary: " +
               $"Clean laps {clean.Count}/{rows.Count}; " +
               $"Best clean {Lap(bestClean?.LapTimeMs ?? double.NaN)}; " +
               $"Best any {Lap(bestAny?.LapTimeMs ?? double.NaN)}; " +
               $"Avg clean {Lap(avgClean)}; " +
               $"Avg tyre lap Δ {Num(avgWearLap)}%; " +
               $"Avg fuel {Num(avgFuel)} kg/lap; " +
               $"Min ERS {Num0(minErs)}%; " +
               $"Pit laps {pitCount}; " +
               $"Damage affected {(damageAffected ? "Yes" : "No")}. " + quality;
    }

    public static AnalysisTableResult BuildDriverCompare(string sessionFolder, IReadOnlyList<RaceReportDriverOption> selectedDrivers, string mode, string metricGroup)
    {
        var drivers = selectedDrivers.Take(3).Where(d => d is not null).ToList();
        if (drivers.Count < 2)
        {
            return new AnalysisTableResult(Array.Empty<AnalysisTableColumn>(), Array.Empty<AnalysisTableRow>(), "Select at least two drivers.", CompareLegend(mode, metricGroup));
        }

        var rowsByDriver = drivers.ToDictionary(d => d.CarIndex, d => RaceReportDataService.LoadRows(sessionFolder, d.CarIndex));
        var rowKeys = rowsByDriver.Values.SelectMany(r => r).Select(r => CompareKey(r, mode)).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().OrderBy(k => KeySort(k)).ToList();

        var columns = new List<AnalysisTableColumn> { Col("key", mode, 105, "Comparison row key", groupStart: false) };
        foreach (var d in drivers)
        {
            columns.AddRange(DriverCompareColumns(d, metricGroup));
        }

        var resultRows = new List<AnalysisTableRow>();
        foreach (var key in rowKeys)
        {
            var values = new Dictionary<string, string> { ["key"] = key };
            var lapTimes = new List<double>();
            foreach (var d in drivers)
            {
                var row = rowsByDriver[d.CarIndex].FirstOrDefault(r => CompareKey(r, mode) == key);
                if (row is not null && row.LapTimeMs > 0) lapTimes.Add(row.LapTimeMs);
            }
            var best = lapTimes.Count == 0 ? double.NaN : lapTimes.Min();

            foreach (var d in drivers)
            {
                var row = rowsByDriver[d.CarIndex].FirstOrDefault(r => CompareKey(r, mode) == key);
                FillDriverCompareValues(values, d, row, best, metricGroup);
            }
            var severity = values.Values.Any(v => v.Contains("Dirty", StringComparison.OrdinalIgnoreCase) || v.Contains("Low", StringComparison.OrdinalIgnoreCase) || v.Contains("Dmg", StringComparison.OrdinalIgnoreCase)) ? "warn" : "normal";
            resultRows.Add(new AnalysisTableRow(values, severity));
        }

        var status = $"Driver Compare: {drivers.Count} drivers, {resultRows.Count} rows, mode {mode}, group {metricGroup}.";
        return new AnalysisTableResult(columns, resultRows, status, CompareLegend(mode, metricGroup));
    }

    public static IReadOnlyList<LapChartSeries> BuildDriverCompareChart(string sessionFolder, IReadOnlyList<RaceReportDriverOption> selectedDrivers, string metricGroup)
    {
        var result = new List<LapChartSeries>();
        foreach (var d in selectedDrivers.Take(3))
        {
            var rows = RaceReportDataService.LoadRows(sessionFolder, d.CarIndex).Where(r => r.LapTimeMs > 0).OrderBy(r => r.LapNum).ToList();
            var points = rows.Select(r => new LapChartPoint(r.LapNum, ChartValue(r, metricGroup))).Where(p => Valid(p.Value)).ToList();
            result.Add(new LapChartSeries(d.Code, points));
        }
        return result;
    }

    public static (string Title, string Unit) ChartTitle(string metricGroup) => metricGroup switch
    {
        "Tyres" => ("Average tyre wear gained per lap", "%"),
        "Fuel/ERS" => ("ERS end by lap", "%"),
        "Damage" => ("Damage increase by lap", "%"),
        _ => ("Lap time by lap", "s")
    };

    public static AnalysisTableResult BuildStintReport(string sessionFolder, RaceReportDriverOption? driver)
    {
        if (driver is null) return new AnalysisTableResult(Array.Empty<AnalysisTableColumn>(), Array.Empty<AnalysisTableRow>(), "Select a driver.", StintLegend());
        var rows = RaceReportDataService.LoadRows(sessionFolder, driver.CarIndex).Where(r => r.LapNum > 0).OrderBy(r => r.LapNum).ToList();
        var stints = RaceStrategyAnalyzer.BuildStints(rows);
        var columns = new[]
        {
            Col("stint", "Stint", 65, "Stint number", true),
            Col("laps", "Laps", 95, "Lap range in this stint"),
            Col("compound", "Compound", 95, "Tyre compound used in this stint", groupStart: true),
            Col("length", "Length", 70, "Number of laps", true),
            Col("clean", "Clean", 70, "Clean laps in this stint", true),
            Col("best", "Best", 95, "Best lap in stint", groupStart: true),
            Col("avg", "Avg clean", 95, "Average clean lap in stint"),
            Col("deg", "Deg sec/lap", 105, "Simple pace degradation slope over clean laps, seconds per lap", true),
            Col("wear", "Wear Δ/lap %", 110, "Average tyre wear gained per lap", true, groupStart: true),
            Col("fuel", "Fuel/lap kg", 100, "Average fuel used per lap", true),
            Col("ers", "Avg ERS end %", 110, "Average ERS charge at lap end", true, groupStart: true),
            Col("minErs", "Min ERS %", 90, "Lowest ERS end in stint", true),
            Col("dmg", "Damage Δ", 90, "Total maximum damage increase across stint", true, groupStart: true),
            Col("notes", "Notes", 420, "Stint flags", wrap: true, groupStart: true)
        };
        var tableRows = stints.Select(s => new AnalysisTableRow(StintValues(s), StintSeverity(s))).ToList();
        var status = $"Stint Report: {driver.Code} / {driver.DisplayName}, {tableRows.Count} stints.";
        return new AnalysisTableResult(columns, tableRows, status, StintLegend());
    }

    public static AnalysisTableResult BuildPitReport(string sessionFolder, RaceReportDriverOption? driver)
    {
        if (driver is null) return new AnalysisTableResult(Array.Empty<AnalysisTableColumn>(), Array.Empty<AnalysisTableRow>(), "Select a driver.", PitLegend());
        var rows = RaceReportDataService.LoadRows(sessionFolder, driver.CarIndex).Where(r => r.LapNum > 0).OrderBy(r => r.LapNum).ToList();
        var cleanAvg = rows.Where(r => r.CleanLap && r.LapTimeMs > 0 && !r.PitThisLap).Select(r => r.LapTimeMs).DefaultIfEmpty(double.NaN).Average();
        var pitRows = RaceStrategyAnalyzer.DetectPitStops(rows);
        var columns = new[]
        {
            Col("lap", "Pit lap", 75, "Lap where pit / compound change was detected", true),
            Col("before", "Before", 90, "Compound before stop", groupStart: true),
            Col("after", "After", 90, "Compound after stop"),
            Col("age", "Tyre age", 85, "Tyre age before stop", true),
            Col("inlap", "In / pit lap", 100, "Lap time on pit lap", groupStart: true),
            Col("outlap", "Out lap", 100, "Next lap time"),
            Col("loss", "Loss est.", 95, "Rough pit loss estimate versus average clean non-pit lap", true),
            Col("wear", "Wear before %", 110, "Average tyre wear before stop", true, groupStart: true),
            Col("fuel", "Fuel end kg", 95, "Fuel remaining at end of pit lap", true),
            Col("dmg", "Dmg Δ", 80, "Damage increase on pit lap", true),
            Col("notes", "Notes", 420, "Pit stop flags", wrap: true, groupStart: true)
        };
        var resultRows = new List<AnalysisTableRow>();
        foreach (var r in pitRows)
        {
            var prev = Prev(rows, r);
            var next = rows.FirstOrDefault(x => x.LapNum == r.LapNum + 1);
            var after = next is not null && !RaceStrategyAnalyzer.SameCompound(r, next) ? next : r;
            var beforeCompound = r.VisualCompoundStart > 0 || r.ActualCompoundStart > 0
                ? Compound(r.VisualCompoundStart, r.ActualCompoundStart)
                : prev is null ? Compound(r) : Compound(prev);
            var wearBefore = AverageWearStart(r);
            var loss = Valid(cleanAvg) && r.LapTimeMs > 0 ? (r.LapTimeMs - cleanAvg) / 1000.0 : double.NaN;
            var values = new Dictionary<string, string>
            {
                ["lap"] = r.LapNum.ToString(CultureInfo.InvariantCulture),
                ["before"] = beforeCompound,
                ["after"] = Compound(after),
                ["age"] = r.TyreAgeStart.ToString(CultureInfo.InvariantCulture),
                ["inlap"] = Lap(r.LapTimeMs),
                ["outlap"] = next is null ? "-" : Lap(next.LapTimeMs),
                ["loss"] = Valid(loss) ? loss.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "s" : "-",
                ["wear"] = Num(wearBefore),
                ["fuel"] = Num(r.FuelEnd),
                ["dmg"] = r.DamageDeltaMax > 0 ? "+" + r.DamageDeltaMax.ToString(CultureInfo.InvariantCulture) : "-",
                ["notes"] = PitNote(r, prev, next)
            };
            resultRows.Add(new AnalysisTableRow(values, r.DamageDeltaMax > 0 ? "bad" : "normal"));
        }

        var status = resultRows.Count == 0
            ? $"Pit Report: {driver.Code} / {driver.DisplayName}, no pit stop or compound-change laps detected."
            : $"Pit Report: {driver.Code} / {driver.DisplayName}, {resultRows.Count} pit/compound-change rows.";
        return new AnalysisTableResult(columns, resultRows, status, PitLegend());
    }

    private static Dictionary<string, string> StintValues(RaceStintGroup stint)
    {
        var rows = stint.Rows;
        var representative = rows.Where(r => !r.PitThisLap).ToList();
        var clean = representative.Where(r => r.CleanLap && r.LapTimeMs > 0).ToList();
        var best = clean.OrderBy(r => r.LapTimeMs).FirstOrDefault()
                   ?? representative.Where(r => r.LapTimeMs > 0).OrderBy(r => r.LapTimeMs).FirstOrDefault();
        var avg = clean.Count == 0 ? double.NaN : clean.Average(r => r.LapTimeMs);
        var deg = DegradationSlope(clean);
        var minErs = rows.Select(r => ErsPct(r.ErsEnd)).Where(Valid).DefaultIfEmpty(double.NaN).Min();
        var avgErs = rows.Select(r => ErsPct(r.ErsEnd)).Where(Valid).DefaultIfEmpty(double.NaN).Average();
        var avgWear = representative.Where(r => Valid(r.TyreWearAvgDelta) && r.TyreWearAvgDelta >= 0)
            .Select(r => r.TyreWearAvgDelta)
            .DefaultIfEmpty(double.NaN)
            .Average();
        var notes = new List<string>();
        if (clean.Count == 0) notes.Add("No clean laps");
        if (rows.Any(r => r.PitThisLap)) notes.Add("Pit stop detected");
        if (rows.Any(r => r.DamageDeltaMax > 0)) notes.Add("Damage appeared");
        if (Valid(minErs) && minErs <= 10) notes.Add("Low ERS");
        if (Valid(avgWear) && avgWear >= 3) notes.Add("High tyre wear");

        return new Dictionary<string, string>
        {
            ["stint"] = stint.Number.ToString(CultureInfo.InvariantCulture),
            ["laps"] = rows.First().LapNum == rows.Last().LapNum ? rows.First().LapNum.ToString(CultureInfo.InvariantCulture) : $"{rows.First().LapNum}-{rows.Last().LapNum}",
            ["compound"] = Compound(rows.Last()),
            ["length"] = rows.Count.ToString(CultureInfo.InvariantCulture),
            ["clean"] = clean.Count.ToString(CultureInfo.InvariantCulture),
            ["best"] = best is null ? "-" : Lap(best.LapTimeMs),
            ["avg"] = Lap(avg),
            ["deg"] = Valid(deg) ? deg.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) : "-",
            ["wear"] = Num(avgWear),
            ["fuel"] = Num(representative.Where(r => Valid(r.FuelUsed)).Select(r => r.FuelUsed).DefaultIfEmpty(double.NaN).Average()),
            ["ers"] = Num0(avgErs),
            ["minErs"] = Num0(minErs),
            ["dmg"] = rows.Sum(r => Math.Max(0, r.DamageDeltaMax)).ToString(CultureInfo.InvariantCulture),
            ["notes"] = notes.Count == 0 ? "-" : string.Join(", ", notes)
        };
    }

    private static string StintSeverity(RaceStintGroup stint)
    {
        if (stint.Rows.Any(r => r.DamageDeltaMax >= 10 || (!r.CleanLap && !r.PitThisLap))) return "warn";
        return "normal";
    }

    private static IEnumerable<AnalysisTableColumn> DriverCompareColumns(RaceReportDriverOption d, string group)
    {
        var prefix = d.Code + " ";
        var key = d.CarIndex.ToString(CultureInfo.InvariantCulture) + "_";
        if (group == "Tyres") return new[]
        {
            Col(key+"cmp", prefix + "Cmp", 80, "Compound", groupStart: true),
            Col(key+"age", prefix + "Age", 65, "Tyre age", true),
            Col(key+"wear", prefix + "Avg end", 85, "Average tyre wear at lap end", true),
            Col(key+"lapwear", prefix + "Lap Δ", 78, "Average tyre wear gained this lap", true),
            Col(key+"bias", prefix + "F/R", 78, "Front minus rear wear delta", true),
            Col(key+"notes", prefix + "Notes", 210, "Notes", wrap: true)
        };
        if (group == "Fuel/ERS") return new[]
        {
            Col(key+"fuel", prefix + "Fuel kg", 80, "Fuel used", true, groupStart: true),
            Col(key+"ers", prefix + "ERS end", 80, "ERS end %", true),
            Col(key+"deploy", prefix + "Deploy", 80, "ERS deployed MJ", true),
            Col(key+"harv", prefix + "Harvest", 90, "MGU-K + MGU-H harvest MJ", true),
            Col(key+"mode", prefix + "Mode", 70, "ERS mode"),
            Col(key+"notes", prefix + "Notes", 210, "Notes", wrap: true)
        };
        if (group == "Damage") return new[]
        {
            Col(key+"dmg", prefix + "Dmg Δ", 80, "Damage increase", true, groupStart: true),
            Col(key+"wing", prefix + "Wing L/R", 90, "Front wing left/right"),
            Col(key+"floor", prefix + "Floor", 70, "Floor damage", true),
            Col(key+"tyredmg", prefix + "Tyre dmg", 88, "Average tyre damage", true),
            Col(key+"notes", prefix + "Notes", 230, "Notes", wrap: true)
        };
        return new[]
        {
            Col(key+"time", prefix + "Time", 90, "Lap time", groupStart: true),
            Col(key+"gap", prefix + "Gap", 75, "Gap to best selected driver on this row", true),
            Col(key+"s1", prefix + "S1", 82, "Sector 1"),
            Col(key+"s2", prefix + "S2", 82, "Sector 2"),
            Col(key+"s3", prefix + "S3", 82, "Sector 3"),
            Col(key+"clean", prefix + "Clean", 70, "Clean lap"),
            Col(key+"notes", prefix + "Notes", 210, "Notes", wrap: true)
        };
    }

    private static void FillDriverCompareValues(Dictionary<string, string> values, RaceReportDriverOption d, RaceLapReportRow? r, double bestMs, string group)
    {
        var key = d.CarIndex.ToString(CultureInfo.InvariantCulture) + "_";
        if (r is null)
        {
            foreach (var suffix in new[] { "time", "gap", "s1", "s2", "s3", "clean", "notes", "cmp", "age", "wear", "lapwear", "bias", "fuel", "ers", "deploy", "harv", "mode", "dmg", "wing", "floor", "tyredmg" })
                values[key + suffix] = "-";
            return;
        }
        if (group == "Tyres")
        {
            values[key+"cmp"] = Compound(r);
            values[key+"age"] = r.TyreAgeEnd.ToString(CultureInfo.InvariantCulture);
            values[key+"wear"] = Num(r.TyreWearAvgEnd);
            values[key+"lapwear"] = Signed(r.TyreWearAvgDelta);
            values[key+"bias"] = Signed(((r.TyreWearFlDelta + r.TyreWearFrDelta) / 2.0) - ((r.TyreWearRlDelta + r.TyreWearRrDelta) / 2.0));
            values[key+"notes"] = r.Notes;
            return;
        }
        if (group == "Fuel/ERS")
        {
            values[key+"fuel"] = Num(r.FuelUsed);
            values[key+"ers"] = Num0(ErsPct(r.ErsEnd));
            values[key+"deploy"] = Num(ToMj(r.ErsDeployed));
            values[key+"harv"] = Num(ToMj(r.ErsHarvestMguk) + ToMj(r.ErsHarvestMguh));
            values[key+"mode"] = ErsMode(r.ErsDeployModeEnd);
            values[key+"notes"] = r.Notes;
            return;
        }
        if (group == "Damage")
        {
            values[key+"dmg"] = r.DamageDeltaMax > 0 ? "+" + r.DamageDeltaMax.ToString(CultureInfo.InvariantCulture) : "-";
            values[key+"wing"] = $"{r.FrontLeftWingDamageEnd}/{r.FrontRightWingDamageEnd}";
            values[key+"floor"] = r.FloorDamageEnd.ToString(CultureInfo.InvariantCulture);
            values[key+"tyredmg"] = Num((r.TyreDamageFlEnd + r.TyreDamageFrEnd + r.TyreDamageRlEnd + r.TyreDamageRrEnd) / 4.0);
            values[key+"notes"] = r.Notes;
            return;
        }
        values[key+"time"] = Lap(r.LapTimeMs);
        values[key+"gap"] = Valid(bestMs) && r.LapTimeMs > 0 ? ((r.LapTimeMs - bestMs) / 1000.0).ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) : "-";
        values[key+"s1"] = Lap(r.Sector1Ms);
        values[key+"s2"] = Lap(r.Sector2Ms);
        values[key+"s3"] = Lap(r.Sector3Ms);
        values[key+"clean"] = r.CleanLap ? "Yes" : "No";
        values[key+"notes"] = r.Notes;
    }

    private static string CompareKey(RaceLapReportRow r, string mode)
    {
        if (mode == "Stint lap") return "S" + Math.Max(0, r.TyreAgeEnd).ToString("00", CultureInfo.InvariantCulture);
        if (mode == "Compound") return Compound(r) + " " + Math.Max(0, r.TyreAgeEnd).ToString("00", CultureInfo.InvariantCulture);
        return "L" + r.LapNum.ToString("00", CultureInfo.InvariantCulture);
    }

    private static int KeySort(string key)
    {
        var digits = new string(key.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string CompareLegend(string mode, string group) => $"Compare mode {mode}. Pace gap is against the best selected driver on the same row. Tyres F/R means front average minus rear average wear gained. Fuel/ERS harvest is MGU-K + MGU-H in MJ. Yes, it is finally comparing drivers instead of making you do spreadsheet archaeology.";
    private static string StintLegend() => "Stint report groups laps by compound changes, pit stops and tyre-age resets. Deg sec/lap is a simple clean-lap pace slope, useful as a first degradation signal, not divine prophecy.";
    private static string PitLegend() => "Pit report detects pit laps by pit flag, compound change or tyre-age reset. Loss estimate is rough: pit lap versus average clean non-pit lap.";

    private static AnalysisTableColumn Col(string key, string header, double width, string help, bool alignRight = false, bool wrap = false, bool groupStart = false)
        => new(key, header, width, help, alignRight, wrap, groupStart);

    private static double ChartValue(RaceLapReportRow r, string metricGroup) => metricGroup switch
    {
        "Tyres" => r.TyreWearAvgDelta,
        "Fuel/ERS" => ErsPct(r.ErsEnd),
        "Damage" => r.DamageDeltaMax,
        _ => r.LapTimeMs / 1000.0
    };

    private static double DegradationSlope(IReadOnlyList<RaceLapReportRow> rows)
    {
        var pts = rows.Where(r => r.LapTimeMs > 0).Select((r, i) => (X: (double)i, Y: r.LapTimeMs / 1000.0)).ToList();
        if (pts.Count < 2) return double.NaN;
        var avgX = pts.Average(p => p.X);
        var avgY = pts.Average(p => p.Y);
        var den = pts.Sum(p => (p.X - avgX) * (p.X - avgX));
        if (Math.Abs(den) < 0.0001) return double.NaN;
        return pts.Sum(p => (p.X - avgX) * (p.Y - avgY)) / den;
    }

    private static RaceLapReportRow? Prev(IReadOnlyList<RaceLapReportRow> rows, RaceLapReportRow row) => rows.LastOrDefault(r => r.LapNum < row.LapNum);

    private static string PitNote(RaceLapReportRow r, RaceLapReportRow? prev, RaceLapReportRow? next)
    {
        var notes = new List<string>();
        if (r.PitThisLap) notes.Add("Pit flag");
        if (prev is not null && Compound(prev) != Compound(r)) notes.Add("Compound changed");
        else if (next is not null && Compound(r) != Compound(next)) notes.Add("Compound changed on out lap");
        if (r.TyreAgeEnd < r.TyreAgeStart) notes.Add("Tyre age reset");
        else if (next is not null && next.TyreAgeEnd < r.TyreAgeEnd) notes.Add("Tyre age reset on out lap");
        if (next is null) notes.Add("No out lap yet");
        if (r.DamageDeltaMax > 0) notes.Add("Damage on pit lap");
        return notes.Count == 0 ? r.Notes : string.Join(", ", notes);
    }

    private static double AverageWearStart(RaceLapReportRow row)
    {
        var values = new[] { row.TyreWearFlStart, row.TyreWearFrStart, row.TyreWearRlStart, row.TyreWearRrStart }
            .Where(Valid)
            .ToList();
        return values.Count == 0 ? double.NaN : values.Average();
    }

    private static string BuildQualityFlags(IReadOnlyList<RaceLapReportRow> rows)
    {
        var flags = new List<string>();
        if (rows.Any(r => r.Sector1Ms <= 0 || r.Sector2Ms <= 0 || r.Sector3Ms <= 0)) flags.Add("missing sector data");
        if (rows.Any(r => r.RewindCount > 0)) flags.Add("flashback/rewind detected");
        if (rows.Any(r => MaxDamage(r) >= 95)) flags.Add("suspicious high damage value");
        if (rows.Any(r => r.LapInvalid || r.InvalidCount > 0)) flags.Add("invalid laps detected");
        return flags.Count == 0 ? "Data quality: OK." : "Data quality: " + string.Join(", ", flags) + ".";
    }

    private static bool Valid(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static string Lap(double ms) => Valid(ms) && ms > 0 ? LapOption.FormatLapTime(ms) : "-";
    private static string Num(double value) => Valid(value) ? value.ToString("0.##", CultureInfo.InvariantCulture) : "-";
    private static string Num0(double value) => Valid(value) ? value.ToString("0", CultureInfo.InvariantCulture) : "-";
    private static string Signed(double value) => Valid(value) ? value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "-";
    private static string Compound(RaceLapReportRow r) => Compound(r.VisualCompoundEnd, r.ActualCompoundEnd);
    private static string Compound(int visual, int actual) => RaceReportDataService.CellText(new RaceLapReportRow { VisualCompoundEnd = visual, ActualCompoundEnd = actual }, "compound");
    private static int MaxDamage(RaceLapReportRow r) => new[] { r.FrontLeftWingDamageEnd, r.FrontRightWingDamageEnd, r.RearWingDamageEnd, r.FloorDamageEnd, r.DiffuserDamageEnd, r.SidepodDamageEnd }.Max();
    private static double ErsPct(double joules) => Valid(joules) && joules >= 0 && joules <= 4_000_000 ? Math.Clamp(joules / 4_000_000.0 * 100.0, 0.0, 100.0) : double.NaN;
    private static double ToMj(double joules) => Valid(joules) && joules >= -100_000 && joules <= 10_000_000 ? joules / 1_000_000.0 : double.NaN;
    private static string ErsMode(int mode) => mode switch { 0 => "none", 1 => "med", 2 => "hotlap", 3 => "boost", _ => mode.ToString(CultureInfo.InvariantCulture) };
}
