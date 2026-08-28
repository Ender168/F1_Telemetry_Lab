using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;

namespace F1TelemetryLab;

public sealed record RaceReportDriverOption(int CarIndex, bool IsPlayer, string DriverName, string ShortName, double BestLapMs, int TotalLapCount)
{
    public string DisplayName => IsPlayer ? "YOU" : CleanName(DriverName);
    public string Code => CleanShort(ShortName, CarIndex, IsPlayer);
    public string Identity => LapOption.CompactIdentity(CarIndex, IsPlayer, Code, DisplayName);
    public string Label => $"#{CarIndex:00} {Identity}  best {LapOption.FormatLapTime(BestLapMs)}  laps:{TotalLapCount}";
    public override string ToString() => Label;

    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "CAR";
        if (name.Any(ch => char.IsControl(ch) || ch == '\uFFFD')) return "CAR";
        var trimmed = name.Trim();
        return trimmed.Length > 24 ? trimmed[..24] : trimmed;
    }

    private static string CleanShort(string value, int carIndex, bool isPlayer)
    {
        var clean = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length >= 2) return clean.Length > 4 ? clean[..4] : clean;
        return isPlayer ? "YOU" : $"C{carIndex:00}";
    }
}

public sealed class RaceLapReportRow
{
    public int CarIndex { get; init; }
    public int LapNum { get; init; }
    public bool IsPlayer { get; init; }
    public bool CleanLap { get; init; }
    public int RewindCount { get; init; }
    public int InvalidCount { get; init; }
    public bool LapInvalid { get; init; }
    public double LapTimeMs { get; init; }
    public double Sector1Ms { get; init; }
    public double Sector2Ms { get; init; }
    public double Sector3Ms { get; init; }
    public int PositionStart { get; init; }
    public int PositionEnd { get; init; }
    public int Warnings { get; init; }
    public int Penalties { get; init; }
    public bool PitThisLap { get; init; }
    public int PitStatusMax { get; init; }
    public int PitStopsStart { get; init; }
    public int PitStopsEnd { get; init; }
    public int ActualCompoundStart { get; init; }
    public int ActualCompoundEnd { get; init; }
    public int VisualCompoundStart { get; init; }
    public int VisualCompoundEnd { get; init; }
    public int TyreAgeStart { get; init; }
    public int TyreAgeEnd { get; init; }
    public double FuelStart { get; init; }
    public double FuelEnd { get; init; }
    public double FuelUsed { get; init; }
    public double FuelRemainingLapsEnd { get; init; }
    public double ErsStart { get; init; }
    public double ErsEnd { get; init; }
    public double ErsMin { get; init; }
    public double ErsMax { get; init; }
    public double ErsDelta { get; init; }
    public double ErsDeployed { get; init; }
    public double ErsHarvestMguk { get; init; }
    public double ErsHarvestMguh { get; init; }
    public int ErsDeployModeEnd { get; init; }
    public double TyreWearFlStart { get; init; }
    public double TyreWearFlEnd { get; init; }
    public double TyreWearFlDelta { get; init; }
    public double TyreWearFrStart { get; init; }
    public double TyreWearFrEnd { get; init; }
    public double TyreWearFrDelta { get; init; }
    public double TyreWearRlStart { get; init; }
    public double TyreWearRlEnd { get; init; }
    public double TyreWearRlDelta { get; init; }
    public double TyreWearRrStart { get; init; }
    public double TyreWearRrEnd { get; init; }
    public double TyreWearRrDelta { get; init; }
    public double TyreWearAvgEnd { get; init; }
    public double TyreWearAvgDelta { get; init; }
    public int TyreDamageFlEnd { get; init; }
    public int TyreDamageFrEnd { get; init; }
    public int TyreDamageRlEnd { get; init; }
    public int TyreDamageRrEnd { get; init; }
    public int FrontLeftWingDamageEnd { get; init; }
    public int FrontRightWingDamageEnd { get; init; }
    public int RearWingDamageEnd { get; init; }
    public int FloorDamageEnd { get; init; }
    public int DiffuserDamageEnd { get; init; }
    public int SidepodDamageEnd { get; init; }
    public int DamageDeltaMax { get; init; }
    public double MaxSpeed { get; init; }
    public double AvgSpeed { get; init; }
    public double FullThrottlePct { get; init; }
    public double BrakePct { get; init; }
    public double DrsPct { get; init; }
    public bool PersonalBest { get; set; }
    public string Notes { get; set; } = "";
    public bool HasProblem => Notes.Contains("PIT", StringComparison.OrdinalIgnoreCase)
                              || Notes.Contains("INVALID", StringComparison.OrdinalIgnoreCase)
                              || Notes.Contains("DIRTY", StringComparison.OrdinalIgnoreCase)
                              || Notes.Contains("NEW DAMAGE", StringComparison.OrdinalIgnoreCase)
                              || Notes.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                              || Notes.Contains("PEN", StringComparison.OrdinalIgnoreCase)
                              || Notes.Contains("ERS LOW", StringComparison.OrdinalIgnoreCase)
                              || Notes.Contains("LOW ERS", StringComparison.OrdinalIgnoreCase);
}


public sealed record RaceReportColumn(
    string Key,
    string Header,
    double Width,
    string Help,
    bool AlignRight = false,
    bool Wrap = false,
    bool GroupStart = false);

public static class RaceReportDataService
{
    public static readonly string[] Views = { "Overview", "Tyres", "Fuel/ERS", "Damage", "Full" };

    public static List<RaceReportDriverOption> LoadDrivers(string sessionFolder)
    {
        var db = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(db)) throw new FileNotFoundException("session.sqlite not found", db);

        using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
        con.Open();
        RequireTable(con, "lap_state_summary");

        var hasFinal = TableExists(con, "final_classification");
        var hasDisplay = hasFinal && ColumnExists(con, "final_classification", "display_name");
        var hasShort = hasFinal && ColumnExists(con, "final_classification", "short_name");
        var namesCte = hasFinal
            ? $"""
        WITH names AS (
            SELECT car_idx,
                   COALESCE(NULLIF({(hasDisplay ? "display_name" : "name")},''), NULLIF(name,''), 'F1 Generic') AS name,
                   COALESCE(NULLIF({(hasShort ? "short_name" : "''")},''), '') AS short_name
            FROM final_classification
        )
        """
            : """
        WITH names AS (
            SELECT p.car_idx, p.name, '' AS short_name
            FROM participants p
            JOIN (
                SELECT car_idx, MAX(frame_identifier) AS max_frame
                FROM participants
                GROUP BY car_idx
            ) x ON x.car_idx = p.car_idx AND x.max_frame = p.frame_identifier
        )
        """;
        using var cmd = con.CreateCommand();
        cmd.CommandText = namesCte + """
        SELECT s.car_idx, MAX(s.is_player) AS is_player, COALESCE(n.name, '') AS name, COALESCE(n.short_name, '') AS short_name,
               COALESCE(
                   MIN(CASE WHEN s.clean_lap = 1 AND s.pit_this_lap = 0 AND s.lap_time_ms > 0 THEN s.lap_time_ms END),
                   MIN(CASE WHEN s.pit_this_lap = 0 AND s.lap_time_ms > 0 THEN s.lap_time_ms END)
               ) AS best_lap_ms,
               COUNT(*) AS total_laps
        FROM lap_state_summary s
        LEFT JOIN names n ON n.car_idx = s.car_idx
        WHERE s.lap_num > 0
        GROUP BY s.car_idx, n.name, n.short_name
        ORDER BY is_player DESC, best_lap_ms ASC, s.car_idx ASC;
        """;
        using var reader = cmd.ExecuteReader();
        var result = new List<RaceReportDriverOption>();
        while (reader.Read())
        {
            result.Add(new RaceReportDriverOption(
                reader.GetInt32(0),
                reader.GetInt32(1) == 1,
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                D(reader, 4),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5)));
        }
        return result;
    }

    public static List<RaceLapReportRow> LoadRows(string sessionFolder, int carIndex)
    {
        var db = Path.Combine(sessionFolder, "session.sqlite");
        if (!File.Exists(db)) throw new FileNotFoundException("session.sqlite not found", db);

        using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
        con.Open();
        RequireTable(con, "lap_state_summary");

        using var cmd = con.CreateCommand();
        cmd.CommandText = """
        SELECT car_idx, lap_num, is_player, clean_lap, rewind_count, invalid_count, lap_invalid,
               lap_time_ms, sector1_ms, sector2_ms, sector3_ms,
               position_start, position_end, warnings, penalties, pit_this_lap, pit_status_max, pit_stops_start, pit_stops_end,
               actual_tyre_compound_start, actual_tyre_compound_end, visual_tyre_compound_start, visual_tyre_compound_end,
               tyres_age_start, tyres_age_end,
               fuel_start, fuel_end, fuel_used, fuel_remaining_laps_end,
               ers_start, ers_end, ers_min, ers_max, ers_delta, ers_deployed_this_lap, ers_harvest_mguk_this_lap, ers_harvest_mguh_this_lap, ers_deploy_mode_end,
               tyre_wear_fl_start, tyre_wear_fl_end, tyre_wear_fl_delta,
               tyre_wear_fr_start, tyre_wear_fr_end, tyre_wear_fr_delta,
               tyre_wear_rl_start, tyre_wear_rl_end, tyre_wear_rl_delta,
               tyre_wear_rr_start, tyre_wear_rr_end, tyre_wear_rr_delta,
               tyre_wear_avg_end, tyre_wear_avg_delta,
               tyre_damage_fl_end, tyre_damage_fr_end, tyre_damage_rl_end, tyre_damage_rr_end,
               front_left_wing_damage_end, front_right_wing_damage_end, rear_wing_damage_end, floor_damage_end, diffuser_damage_end, sidepod_damage_end, damage_delta_max,
               max_speed, avg_speed, full_throttle_pct, brake_pct, drs_pct
        FROM lap_state_summary
        WHERE car_idx = $car
        ORDER BY lap_num ASC;
        """;
        cmd.Parameters.AddWithValue("$car", carIndex);
        using var reader = cmd.ExecuteReader();
        var rows = new List<RaceLapReportRow>();
        while (reader.Read()) rows.Add(ReadRow(reader));

        var best = rows.Where(x => x.CleanLap && !x.PitThisLap && x.LapTimeMs > 0).OrderBy(x => x.LapTimeMs).FirstOrDefault()
                   ?? rows.Where(x => x.LapTimeMs > 0).OrderBy(x => x.LapTimeMs).FirstOrDefault();
        foreach (var row in rows)
        {
            row.PersonalBest = best is not null && row.LapNum == best.LapNum;
            row.Notes = BuildNotes(row);
        }
        return rows;
    }

    public static string ExportCsv(string sessionFolder, int carIndex)
    {
        var rows = LoadRows(sessionFolder, carIndex);
        var exports = Path.Combine(sessionFolder, "exports", "race_report");
        Directory.CreateDirectory(exports);
        var path = Path.Combine(exports, $"race_report_car_{carIndex:00}.csv");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("lap,lap_time,s1,s2,s3,clean,invalid,pit,compound,tyre_age,wear_avg_end,wear_avg_delta,fuel_start,fuel_end,fuel_used,ers_start_pct,ers_end_pct,ers_deployed_mj,damage_max,damage_delta_max,max_speed,avg_speed,full_throttle_pct,brake_pct,drs_pct,notes");
        foreach (var r in rows)
        {
            writer.WriteLine(string.Join(',', new[]
            {
                r.LapNum.ToString(CultureInfo.InvariantCulture),
                Csv(LapOption.FormatLapTime(r.LapTimeMs)),
                Csv(LapOption.FormatLapTime(r.Sector1Ms)),
                Csv(LapOption.FormatLapTime(r.Sector2Ms)),
                Csv(LapOption.FormatLapTime(r.Sector3Ms)),
                r.CleanLap ? "1" : "0",
                r.LapInvalid ? "1" : "0",
                r.PitThisLap ? "1" : "0",
                Csv(CompoundName(r.VisualCompoundEnd, r.ActualCompoundEnd)),
                r.TyreAgeEnd.ToString(CultureInfo.InvariantCulture),
                Num(r.TyreWearAvgEnd), Num(r.TyreWearAvgDelta),
                Num(r.FuelStart), Num(r.FuelEnd), Num(r.FuelUsed),
                Num(ErsPct(r.ErsStart)), Num(ErsPct(r.ErsEnd)), Num(ToMj(r.ErsDeployed)),
                MaxDamage(r).ToString(CultureInfo.InvariantCulture),
                r.DamageDeltaMax.ToString(CultureInfo.InvariantCulture),
                Num(r.MaxSpeed), Num(r.AvgSpeed), Num(r.FullThrottlePct), Num(r.BrakePct), Num(r.DrsPct),
                Csv(r.Notes)
            }));
        }
        return path;
    }

    public static string HeaderForView(string view) => view switch
    {
        "Tyres" => "Lap | Lap time || Tyres: Compound | Age laps || Wear end %: FL | FR | RL | RR || Wear this lap %: FL | FR | RL | RR || Avg end % | Avg lap Δ % || Tyre damage: FL | FR | RL | RR || Notes",
        "Fuel/ERS" => "Lap | Lap time || Fuel kg: Start | End | Used | Laps left || ERS %: Start | End | Min | Max || Energy MJ: Deploy | Harvest K | Harvest H || Mode || Notes",
        "Damage" => "Lap | Lap time || Wing damage %: Front L | Front R | Rear || Body damage %: Floor | Diffuser | Sidepod || Damage change max % || Tyre damage %: FL | FR | RL | RR || Notes",
        "Full" => "Lap | Lap time | Sector 1 | Sector 2 | Sector 3 || Position || Tyres: Compound | Age | Wear change % || Fuel used kg || ERS end % || Pit stop || Damage change % || Speed km/h || Throttle % || Brake % || DRS % || Notes",
        _ => "Lap | Lap time | Sector 1 | Sector 2 | Sector 3 || Clean lap | Position || Tyres: Compound | Age | Wear change % || Fuel used kg || ERS end % || Pit stop || Damage change % || Notes"
    };

    public static string SeparatorForView(string view) => new string('-', HeaderForView(view).Length);

    public static string LegendForView(string view)
    {
        var common = "Definitions: S1/S2/S3 = sectors, FL/FR/RL/RR = tyre corners, Δ/change = change during this lap, ERS = Energy Recovery System, DRS = Drag Reduction System, MGU-K/MGU-H = harvested kinetic/heat energy, Clean lap = no invalidation or rewind, Position = position at lap end.";
        var viewText = view switch
        {
            "Tyres" => " Tyres: Wear FL/FR/RL/RR is end-of-lap wear. Lap FL/FR/RL/RR is wear gained during the selected lap. Avg end is mean end wear. Avg lap Δ is mean wear gained. Tyre dmg is carcass damage. Age laps is set age.",
            "Fuel/ERS" => " Fuel/ERS: Fuel start/end/used/laps left are kilograms and estimated remaining laps. ERS start/end/min/max is battery state, Deploy is spent energy, Harvest K/H is recovered energy, Mode is deployment mode.",
            "Damage" => " Damage: values are percentages. Wing covers aerodynamic wings, Body covers floor/diffuser/sidepods, and Damage change max is the largest component increase during the lap.",
            "Full" => " Full: Speed is lap maximum. Throttle/Brake/DRS are the shares of the lap at full throttle, under braking, and with DRS open.",
            _ => " Overview: Compound is the visual tyre compound, Age is set age, Wear change is mean tyre wear gained, and Damage change is the largest component increase during the lap."
        };
        return common + viewText;
    }

    public static IReadOnlyList<RaceReportColumn> ColumnsForView(string view) => view switch
    {
        "Tyres" => new[]
        {
            Col("lap", "Lap", 54, "Lap number", true),
            Col("time", "Lap time", 95, "Final lap time"),
            Col("compound", "Compound", 92, "Visual tyre compound at the end of the lap", groupStart: true),
            Col("tyreAge", "Age", 68, "Tyre age in laps at the end of this lap", true),
            Col("wearFl", "Wear FL %", 80, "Front-left tyre wear at lap end", true, groupStart: true),
            Col("wearFr", "Wear FR %", 80, "Front-right tyre wear at lap end", true),
            Col("wearRl", "Wear RL %", 80, "Rear-left tyre wear at lap end", true),
            Col("wearRr", "Wear RR %", 80, "Rear-right tyre wear at lap end", true),
            Col("wearDeltaFl", "Lap FL %", 78, "Front-left tyre wear gained during this lap", true, groupStart: true),
            Col("wearDeltaFr", "Lap FR %", 78, "Front-right tyre wear gained during this lap", true),
            Col("wearDeltaRl", "Lap RL %", 78, "Rear-left tyre wear gained during this lap", true),
            Col("wearDeltaRr", "Lap RR %", 78, "Rear-right tyre wear gained during this lap", true),
            Col("wearAvg", "Avg end %", 88, "Average tyre wear at lap end: (FL + FR + RL + RR) / 4", true, groupStart: true),
            Col("wearDeltaAvg", "Avg lap Δ %", 95, "Average tyre wear gained during this lap: average of the four Lap FL/FR/RL/RR columns", true),
            Col("tyreDmgFl", "Tyre dmg FL %", 105, "Front-left tyre damage", true, groupStart: true),
            Col("tyreDmgFr", "Tyre dmg FR %", 105, "Front-right tyre damage", true),
            Col("tyreDmgRl", "Tyre dmg RL %", 105, "Rear-left tyre damage", true),
            Col("tyreDmgRr", "Tyre dmg RR %", 105, "Rear-right tyre damage", true),
            Col("notes", "Notes", 420, "Automatic lap flags and warnings", wrap: true, groupStart: true)
        },
        "Fuel/ERS" => new[]
        {
            Col("lap", "Lap", 54, "Lap number", true),
            Col("time", "Lap time", 95, "Final lap time"),
            Col("fuelStart", "Fuel start kg", 105, "Fuel at lap start, kg", true, groupStart: true),
            Col("fuelEnd", "Fuel end kg", 100, "Fuel at lap end, kg", true),
            Col("fuelUsed", "Fuel used kg", 105, "Fuel consumed during this lap, kg", true),
            Col("fuelLaps", "Laps left", 90, "Estimated remaining laps at lap end", true),
            Col("ersStart", "ERS start %", 95, "ERS battery charge at lap start", true, groupStart: true),
            Col("ersEnd", "ERS end %", 88, "ERS battery charge at lap end", true),
            Col("ersMin", "ERS min %", 82, "Minimum ERS battery charge on this lap", true),
            Col("ersMax", "ERS max %", 82, "Maximum ERS battery charge on this lap", true),
            Col("ersDeploy", "Deploy MJ", 92, "ERS energy deployed during this lap, MJ", true, groupStart: true),
            Col("ersHarvestK", "Harvest K MJ", 105, "Energy harvested by MGU-K during this lap, MJ", true),
            Col("ersHarvestH", "Harvest H MJ", 105, "Energy harvested by MGU-H during this lap, MJ", true),
            Col("ersMode", "ERS mode", 85, "ERS deployment mode at lap end", groupStart: true),
            Col("notes", "Notes", 420, "Automatic lap flags and warnings", wrap: true, groupStart: true)
        },
        "Damage" => new[]
        {
            Col("lap", "Lap", 54, "Lap number", true),
            Col("time", "Lap time", 95, "Final lap time"),
            Col("fwLeft", "Front wing L %", 112, "Left side front wing damage", true, groupStart: true),
            Col("fwRight", "Front wing R %", 112, "Right side front wing damage", true),
            Col("rearWing", "Rear wing %", 92, "Rear wing damage", true),
            Col("floor", "Floor %", 78, "Floor damage", true, groupStart: true),
            Col("diffuser", "Diffuser %", 88, "Diffuser damage", true),
            Col("sidepod", "Sidepod %", 88, "Sidepod damage", true),
            Col("damageDelta", "Damage Δ %", 105, "Maximum damage increase on any car element during this lap", true, groupStart: true),
            Col("tyreDmgFl", "Tyre FL %", 78, "Front-left tyre damage", true, groupStart: true),
            Col("tyreDmgFr", "Tyre FR %", 78, "Front-right tyre damage", true),
            Col("tyreDmgRl", "Tyre RL %", 78, "Rear-left tyre damage", true),
            Col("tyreDmgRr", "Tyre RR %", 78, "Rear-right tyre damage", true),
            Col("notes", "Notes", 420, "Automatic lap flags and warnings", wrap: true, groupStart: true)
        },
        "Full" => new[]
        {
            Col("lap", "Lap", 54, "Lap number", true),
            Col("time", "Lap time", 95, "Final lap time"),
            Col("s1", "Sector 1", 92, "Sector 1 time"),
            Col("s2", "Sector 2", 92, "Sector 2 time"),
            Col("s3", "Sector 3", 92, "Sector 3 time"),
            Col("position", "Position", 78, "Race position at the end of the lap", true, groupStart: true),
            Col("compound", "Compound", 92, "Visual tyre compound at the end of the lap", groupStart: true),
            Col("tyreAge", "Age", 60, "Tyre age in laps", true),
            Col("wearDeltaAvg", "Wear Δ %", 88, "Average tyre wear gained during this lap", true),
            Col("fuelUsed", "Fuel kg", 82, "Fuel consumed during this lap, kg", true, groupStart: true),
            Col("ersEnd", "ERS end %", 88, "ERS battery charge at lap end", true),
            Col("pit", "Pit stop", 76, "Whether a pit stop was detected on this lap", groupStart: true),
            Col("damageDelta", "Damage Δ %", 105, "Maximum damage increase on any car element during this lap", true),
            Col("maxSpeed", "Max km/h", 85, "Maximum speed on this lap", true, groupStart: true),
            Col("throttle", "Throttle %", 88, "Share of lap at full throttle", true),
            Col("brake", "Brake %", 78, "Share of lap with braking input", true),
            Col("drs", "DRS %", 68, "Share of lap with DRS open", true),
            Col("notes", "Notes", 460, "Automatic lap flags and warnings", wrap: true, groupStart: true)
        },
        _ => new[]
        {
            Col("lap", "Lap", 54, "Lap number", true),
            Col("time", "Lap time", 95, "Final lap time"),
            Col("s1", "Sector 1", 92, "Sector 1 time"),
            Col("s2", "Sector 2", 92, "Sector 2 time"),
            Col("s3", "Sector 3", 92, "Sector 3 time"),
            Col("clean", "Clean lap", 88, "Yes means no invalid lap or flashback/rewind was detected", groupStart: true),
            Col("position", "Position", 78, "Race position at the end of the lap", true),
            Col("compound", "Compound", 92, "Visual tyre compound at the end of the lap", groupStart: true),
            Col("tyreAge", "Age", 60, "Tyre age in laps", true),
            Col("wearDeltaAvg", "Wear Δ %", 88, "Average tyre wear gained during this lap", true),
            Col("fuelUsed", "Fuel kg", 82, "Fuel consumed during this lap, kg", true, groupStart: true),
            Col("ersEnd", "ERS end %", 88, "ERS battery charge at lap end", true),
            Col("pit", "Pit stop", 76, "Whether a pit stop was detected on this lap", groupStart: true),
            Col("damageDelta", "Damage Δ %", 105, "Maximum damage increase on any car element during this lap", true),
            Col("notes", "Notes", 420, "Automatic lap flags and warnings", wrap: true, groupStart: true)
        }
    };

    public static string CellText(RaceLapReportRow r, string key) => key switch
    {
        "lap" => r.LapNum.ToString(CultureInfo.InvariantCulture),
        "time" => LapOption.FormatLapTime(r.LapTimeMs),
        "s1" => LapOption.FormatLapTime(r.Sector1Ms),
        "s2" => LapOption.FormatLapTime(r.Sector2Ms),
        "s3" => LapOption.FormatLapTime(r.Sector3Ms),
        "clean" => CleanText(r),
        "position" => Pos(r),
        "compound" => CompoundName(r.VisualCompoundEnd, r.ActualCompoundEnd),
        "tyreAge" => r.TyreAgeEnd.ToString(CultureInfo.InvariantCulture),
        "wearDeltaAvg" => Signed(r.TyreWearAvgDelta),
        "wearFl" => OneDecimal(r.TyreWearFlEnd),
        "wearFr" => OneDecimal(r.TyreWearFrEnd),
        "wearRl" => OneDecimal(r.TyreWearRlEnd),
        "wearRr" => OneDecimal(r.TyreWearRrEnd),
        "wearDeltaFl" => Signed(r.TyreWearFlDelta),
        "wearDeltaFr" => Signed(r.TyreWearFrDelta),
        "wearDeltaRl" => Signed(r.TyreWearRlDelta),
        "wearDeltaRr" => Signed(r.TyreWearRrDelta),
        "wearAvg" => OneDecimal(r.TyreWearAvgEnd),
        "tyreDmgFl" => r.TyreDamageFlEnd.ToString(CultureInfo.InvariantCulture),
        "tyreDmgFr" => r.TyreDamageFrEnd.ToString(CultureInfo.InvariantCulture),
        "tyreDmgRl" => r.TyreDamageRlEnd.ToString(CultureInfo.InvariantCulture),
        "tyreDmgRr" => r.TyreDamageRrEnd.ToString(CultureInfo.InvariantCulture),
        "fuelStart" => Num(r.FuelStart),
        "fuelEnd" => Num(r.FuelEnd),
        "fuelUsed" => Num(r.FuelUsed),
        "fuelLaps" => Num(r.FuelRemainingLapsEnd),
        "ersStart" => ErsPctText(r.ErsStart),
        "ersEnd" => ErsPctText(r.ErsEnd),
        "ersMin" => ErsPctText(r.ErsMin),
        "ersMax" => ErsPctText(r.ErsMax),
        "ersDeploy" => MjText(r.ErsDeployed),
        "ersHarvestK" => MjText(r.ErsHarvestMguk),
        "ersHarvestH" => MjText(r.ErsHarvestMguh),
        "ersMode" => ErsModeName(r.ErsDeployModeEnd),
        "pit" => PitText(r),
        "fwLeft" => r.FrontLeftWingDamageEnd.ToString(CultureInfo.InvariantCulture),
        "fwRight" => r.FrontRightWingDamageEnd.ToString(CultureInfo.InvariantCulture),
        "rearWing" => r.RearWingDamageEnd.ToString(CultureInfo.InvariantCulture),
        "floor" => r.FloorDamageEnd.ToString(CultureInfo.InvariantCulture),
        "diffuser" => r.DiffuserDamageEnd.ToString(CultureInfo.InvariantCulture),
        "sidepod" => r.SidepodDamageEnd.ToString(CultureInfo.InvariantCulture),
        "damageDelta" => DeltaDamage(r),
        "maxSpeed" => Num(r.MaxSpeed),
        "throttle" => Num(r.FullThrottlePct),
        "brake" => Num(r.BrakePct),
        "drs" => Num(r.DrsPct),
        "notes" => r.Notes,
        _ => "-"
    };

    public static string CompactLegendForView(string view)
    {
        var common = "Column help: S1/S2/S3 = sector 1/2/3; FL/FR/RL/RR = front-left/front-right/rear-left/rear-right; Δ = change during this lap; ERS = Energy Recovery System; DRS = Drag Reduction System; Clean lap = no invalid lap or flashback; Position = position at lap finish.";
        var viewText = view switch
        {
            "Tyres" => " Tyres: FL/FR/RL/RR = front-left/front-right/rear-left/rear-right. Wear FL/FR/RL/RR = tyre wear at lap end for each wheel. Lap FL/FR/RL/RR = wear gained during this exact lap. Avg end = average tyre wear at lap end. Avg lap Δ = average tyre wear gained during this exact lap. Tyre dmg = tyre carcass damage at lap end. Age = tyre age in laps.",
            "Fuel/ERS" => " Fuel/ERS: Fuel values are kg; Laps left is estimated remaining laps; Deploy/Harvest are MJ; MGU-K harvest is kinetic recovery, MGU-H harvest is heat recovery; Mode = ERS deployment mode.",
            "Damage" => " Damage: all values are %. Front wing/body values are current damage at lap end; Damage Δ is the maximum damage increase on any element during the lap.",
            "Full" => " Full: Max km/h = top speed; Throttle/Brake/DRS % = share of lap with full throttle, braking input, and DRS open.",
            _ => " Overview: Compound = visual tyre compound; Age = tyre age; Wear Δ = average tyre wear gained; Damage Δ = maximum damage increase on any element."
        };
        return common + viewText;
    }

    private static RaceReportColumn Col(string key, string header, double width, string help, bool alignRight = false, bool wrap = false, bool groupStart = false)
        => new(key, header, width, help, alignRight, wrap, groupStart);

    public static string FormatRow(RaceLapReportRow r, string view) => view switch
    {
        "Tyres" => FormatTyres(r),
        "Fuel/ERS" => FormatFuelErs(r),
        "Damage" => FormatDamage(r),
        "Full" => FormatFull(r),
        _ => FormatOverview(r)
    };

    private static string FormatOverview(RaceLapReportRow r)
    {
        return $"{r.LapNum,3} | {LapOption.FormatLapTime(r.LapTimeMs),8} | {LapOption.FormatLapTime(r.Sector1Ms),8} | {LapOption.FormatLapTime(r.Sector2Ms),8} | {LapOption.FormatLapTime(r.Sector3Ms),8} || {CleanText(r),9} | {Pos(r),8} || {CompoundName(r.VisualCompoundEnd, r.ActualCompoundEnd),15} | {r.TyreAgeEnd,3} | {Signed(r.TyreWearAvgDelta),13} || {Num(r.FuelUsed),12} || {ErsPctText(r.ErsEnd),9} || {PitText(r),8} || {DeltaDamage(r),15} || {r.Notes}";
    }

    private static string FormatTyres(RaceLapReportRow r)
    {
        return $"{r.LapNum,3} | {LapOption.FormatLapTime(r.LapTimeMs),8} || {CompoundName(r.VisualCompoundEnd, r.ActualCompoundEnd),15} | {r.TyreAgeEnd,8} || {TyreSet(r.TyreWearFlEnd, r.TyreWearFrEnd, r.TyreWearRlEnd, r.TyreWearRrEnd),28} || {TyreSetSigned(r.TyreWearFlDelta, r.TyreWearFrDelta, r.TyreWearRlDelta, r.TyreWearRrDelta),31} || {r.TyreWearAvgEnd,10:0.0} | {Signed(r.TyreWearAvgDelta),10} || {TyreDmgSet(r),28} || {r.Notes}";
    }

    private static string FormatFuelErs(RaceLapReportRow r)
    {
        return $"{r.LapNum,3} | {LapOption.FormatLapTime(r.LapTimeMs),8} || {Num(r.FuelStart),10} | {Num(r.FuelEnd),8} | {Num(r.FuelUsed),7} | {Num(r.FuelRemainingLapsEnd),9} || {ErsPctText(r.ErsStart),9} | {ErsPctText(r.ErsEnd),7} | {ErsPctText(r.ErsMin),5} | {ErsPctText(r.ErsMax),5} || {MjText(r.ErsDeployed),9} | {MjText(r.ErsHarvestMguk),9} | {MjText(r.ErsHarvestMguh),9} || {ErsModeName(r.ErsDeployModeEnd),6} || {r.Notes}";
    }

    private static string FormatDamage(RaceLapReportRow r)
    {
        return $"{r.LapNum,3} | {LapOption.FormatLapTime(r.LapTimeMs),8} || {r.FrontLeftWingDamageEnd,7} | {r.FrontRightWingDamageEnd,7} | {r.RearWingDamageEnd,4} || {r.FloorDamageEnd,5} | {r.DiffuserDamageEnd,8} | {r.SidepodDamageEnd,7} || {r.DamageDeltaMax,19} || {TyreDmgSet(r),28} || {r.Notes}";
    }

    private static string FormatFull(RaceLapReportRow r)
    {
        return $"{r.LapNum,3} | {LapOption.FormatLapTime(r.LapTimeMs),8} | {LapOption.FormatLapTime(r.Sector1Ms),8} | {LapOption.FormatLapTime(r.Sector2Ms),8} | {LapOption.FormatLapTime(r.Sector3Ms),8} || {Pos(r),8} || {CompoundName(r.VisualCompoundEnd, r.ActualCompoundEnd),15} | {r.TyreAgeEnd,3} | {Signed(r.TyreWearAvgDelta),13} || {Num(r.FuelUsed),12} || {ErsPctText(r.ErsEnd),9} || {PitText(r),8} || {DeltaDamage(r),15} || {r.MaxSpeed,10:0} || {r.FullThrottlePct,10:0} || {r.BrakePct,7:0} || {r.DrsPct,5:0} || {r.Notes}";
    }

    private static RaceLapReportRow ReadRow(SqliteDataReader r)
    {
        var i = 0;
        return new RaceLapReportRow
        {
            CarIndex = I(r, i++),
            LapNum = I(r, i++),
            IsPlayer = I(r, i++) == 1,
            CleanLap = I(r, i++) == 1,
            RewindCount = I(r, i++),
            InvalidCount = I(r, i++),
            LapInvalid = I(r, i++) == 1,
            LapTimeMs = D(r, i++),
            Sector1Ms = D(r, i++),
            Sector2Ms = D(r, i++),
            Sector3Ms = D(r, i++),
            PositionStart = I(r, i++),
            PositionEnd = I(r, i++),
            Warnings = I(r, i++),
            Penalties = I(r, i++),
            PitThisLap = I(r, i++) == 1,
            PitStatusMax = I(r, i++),
            PitStopsStart = I(r, i++),
            PitStopsEnd = I(r, i++),
            ActualCompoundStart = I(r, i++),
            ActualCompoundEnd = I(r, i++),
            VisualCompoundStart = I(r, i++),
            VisualCompoundEnd = I(r, i++),
            TyreAgeStart = I(r, i++),
            TyreAgeEnd = I(r, i++),
            FuelStart = D(r, i++),
            FuelEnd = D(r, i++),
            FuelUsed = D(r, i++),
            FuelRemainingLapsEnd = D(r, i++),
            ErsStart = D(r, i++),
            ErsEnd = D(r, i++),
            ErsMin = D(r, i++),
            ErsMax = D(r, i++),
            ErsDelta = D(r, i++),
            ErsDeployed = D(r, i++),
            ErsHarvestMguk = D(r, i++),
            ErsHarvestMguh = D(r, i++),
            ErsDeployModeEnd = I(r, i++),
            TyreWearFlStart = D(r, i++),
            TyreWearFlEnd = D(r, i++),
            TyreWearFlDelta = D(r, i++),
            TyreWearFrStart = D(r, i++),
            TyreWearFrEnd = D(r, i++),
            TyreWearFrDelta = D(r, i++),
            TyreWearRlStart = D(r, i++),
            TyreWearRlEnd = D(r, i++),
            TyreWearRlDelta = D(r, i++),
            TyreWearRrStart = D(r, i++),
            TyreWearRrEnd = D(r, i++),
            TyreWearRrDelta = D(r, i++),
            TyreWearAvgEnd = D(r, i++),
            TyreWearAvgDelta = D(r, i++),
            TyreDamageFlEnd = I(r, i++),
            TyreDamageFrEnd = I(r, i++),
            TyreDamageRlEnd = I(r, i++),
            TyreDamageRrEnd = I(r, i++),
            FrontLeftWingDamageEnd = I(r, i++),
            FrontRightWingDamageEnd = I(r, i++),
            RearWingDamageEnd = I(r, i++),
            FloorDamageEnd = I(r, i++),
            DiffuserDamageEnd = I(r, i++),
            SidepodDamageEnd = I(r, i++),
            DamageDeltaMax = I(r, i++),
            MaxSpeed = D(r, i++),
            AvgSpeed = D(r, i++),
            FullThrottlePct = D(r, i++),
            BrakePct = D(r, i++),
            DrsPct = D(r, i++)
        };
    }

    private static string BuildNotes(RaceLapReportRow r)
    {
        var notes = new List<string>();
        if (r.PersonalBest) notes.Add("Personal best");
        if (!r.CleanLap) notes.Add(r.LapInvalid || r.InvalidCount > 0 ? "Invalid lap" : "Dirty lap");
        if (r.RewindCount > 0) notes.Add("Flashback used");
        if (r.PitThisLap) notes.Add("Pit stop");
        if (r.ActualCompoundStart != 0 && r.ActualCompoundEnd != 0 && r.ActualCompoundStart != r.ActualCompoundEnd) notes.Add("Compound changed");
        if (r.TyreAgeStart > 0 && r.TyreAgeEnd < r.TyreAgeStart) notes.Add("Tyre age reset");
        if (r.TyreWearAvgDelta >= 3.0) notes.Add("High tyre wear");
        var ersEndPct = ErsPct(r.ErsEnd);
        if (!double.IsNaN(ersEndPct) && ersEndPct <= 10.0 && r.ErsEnd > 0) notes.Add("Low ERS");
        if (r.DamageDeltaMax >= 10) notes.Add("New damage");
        if (r.Warnings > 0) notes.Add($"Warnings: {r.Warnings}");
        if (r.Penalties > 0) notes.Add($"Penalty: {r.Penalties}s");
        return notes.Count == 0 ? "-" : string.Join(", ", notes);
    }

    private static string CompoundName(int visual, int actual)
    {
        return visual switch
        {
            7 => "INT",
            8 => "WET",
            16 => "SOFT",
            17 => "MED",
            18 => "HARD",
            _ => actual switch
            {
                7 => "INT",
                8 => "WET",
                16 => "C5",
                17 => "C4",
                18 => "C3",
                19 => "C2",
                20 => "C1",
                21 => "C0",
                22 => "C6",
                _ => actual > 0 ? actual.ToString(CultureInfo.InvariantCulture) : "-"
            }
        };
    }

    private static string Pos(RaceLapReportRow r) => r.PositionEnd > 0 ? r.PositionEnd.ToString(CultureInfo.InvariantCulture) : "-";
    private static string CleanText(RaceLapReportRow r) => r.CleanLap ? "Yes" : "No";
    private static string PitText(RaceLapReportRow r) => r.PitThisLap ? "Pit stop" : "-";
    private static string TyreSet(double fl, double fr, double rl, double rr) => $"{fl,5:0.0}/{fr,5:0.0}/{rl,5:0.0}/{rr,5:0.0}";
    private static string TyreSetSigned(double fl, double fr, double rl, double rr) => $"{Signed(fl),5}/{Signed(fr),5}/{Signed(rl),5}/{Signed(rr),5}";
    private static string TyreDmgSet(RaceLapReportRow r) => $"{r.TyreDamageFlEnd,3}/{r.TyreDamageFrEnd,3}/{r.TyreDamageRlEnd,3}/{r.TyreDamageRrEnd,3}";
    private static string Signed(double value) => double.IsNaN(value) ? "-" : value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
    private static string OneDecimal(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "-" : value.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Num(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "-" : value.ToString("0.##", CultureInfo.InvariantCulture);
    private static double ToMj(double joules)
    {
        if (double.IsNaN(joules) || double.IsInfinity(joules)) return double.NaN;
        if (joules < -100_000 || joules > 10_000_000) return double.NaN;
        return joules / 1_000_000.0;
    }
    private static string MjText(double joules)
    {
        var mj = ToMj(joules);
        return double.IsNaN(mj) ? "-" : mj.ToString("0.00", CultureInfo.InvariantCulture);
    }
    private static double ErsPct(double joules)
    {
        if (double.IsNaN(joules) || double.IsInfinity(joules)) return double.NaN;
        if (joules < 0 || joules > 4_000_000) return double.NaN;
        return Math.Clamp(joules / 4_000_000.0 * 100.0, 0.0, 100.0);
    }
    private static string ErsPctText(double joules)
    {
        var pct = ErsPct(joules);
        return double.IsNaN(pct) ? "-" : pct.ToString("0", CultureInfo.InvariantCulture);
    }
    private static string ErsModeName(int mode) => mode switch
    {
        0 => "none",
        1 => "med",
        2 => "hotlap",
        3 => "boost",
        _ => mode.ToString(CultureInfo.InvariantCulture)
    };
    private static int MaxDamage(RaceLapReportRow r) => new[] { r.FrontLeftWingDamageEnd, r.FrontRightWingDamageEnd, r.RearWingDamageEnd, r.FloorDamageEnd, r.DiffuserDamageEnd, r.SidepodDamageEnd }.Max();
    private static string DeltaDamage(RaceLapReportRow r) => r.DamageDeltaMax > 0 ? "+" + r.DamageDeltaMax.ToString(CultureInfo.InvariantCulture) : "-";
    private static string Csv(string value) => '"' + (value ?? "").Replace("\"", "\"\"") + '"';
    private static int I(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
    private static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? double.NaN : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", table);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static bool ColumnExists(SqliteConnection con, string table, string column)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void RequireTable(SqliteConnection con, string table)
    {
        if (TableExists(con, table)) return;
        throw new InvalidOperationException($"Table '{table}' not found. Run Analyze selected session first so v{AppInfo.Version} can build the Race Report tables.");
    }
}
