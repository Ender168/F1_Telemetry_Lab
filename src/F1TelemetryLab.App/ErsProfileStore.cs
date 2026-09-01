using System.Text.Json;
using System.Text.Json.Serialization;

namespace F1TelemetryLab;

public sealed record ErsProfileLoadResult(
    string ProfileFolder,
    IReadOnlyList<ErsControlProfile> Profiles,
    IReadOnlyList<string> Warnings)
{
    public ErsControlProfile? Find(int trackId, int sessionType) => Profiles
        .Where(profile => profile.TrackId == trackId && profile.SessionTypes.Contains(sessionType))
        .OrderByDescending(profile => profile.SelectionPriority)
        .ThenBy(profile => profile.ProfileId, StringComparer.Ordinal)
        .FirstOrDefault();
}

public static class ErsProfileStore
{
    private const string DefaultChinaFileName = "China_Race.json";
    private const string BuiltInChinaProfileId = "china-race-advanced-v2";
    private const string LegacyBuiltInChinaProfileId = "china-race-r03-v1";
    private const string ProfileReadmeFileName = "README.md";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string ProfileFolder(string rootFolder) => Path.Combine(rootFolder, "ers_profiles");

    public static ErsProfileLoadResult Load(string rootFolder)
    {
        var folder = EnsureDefaultProfiles(rootFolder);
        return LoadFromDirectory(folder);
    }

    public static string EnsureDefaultProfiles(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
            throw new ArgumentException("Storage root is empty.", nameof(rootFolder));

        var folder = ProfileFolder(Path.GetFullPath(rootFolder.Trim()));
        Directory.CreateDirectory(folder);

        var installedFolder = Path.Combine(AppContext.BaseDirectory, "data", "ers_profiles");
        if (Directory.Exists(installedFolder))
        {
            foreach (var source in Directory.EnumerateFiles(installedFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(folder, Path.GetFileName(source));
                if (!File.Exists(target))
                {
                    File.Copy(source, target);
                }
                else
                {
                    UpgradeKnownBuiltInProfile(source, target);
                }
            }

            var readmeSource = Path.Combine(installedFolder, ProfileReadmeFileName);
            var readmeTarget = Path.Combine(folder, ProfileReadmeFileName);
            if (File.Exists(readmeSource) && !File.Exists(readmeTarget))
                File.Copy(readmeSource, readmeTarget);
        }

        var chinaTarget = Path.Combine(folder, DefaultChinaFileName);
        if (!File.Exists(chinaTarget))
            throw new FileNotFoundException("The built-in China ERS profile is missing from the application package.", chinaTarget);
        return folder;
    }

    public static ErsProfileLoadResult LoadFromDirectory(string folder)
    {
        var profiles = new List<ErsControlProfile>();
        var warnings = new List<string>();
        if (!Directory.Exists(folder))
            return new ErsProfileLoadResult(folder, profiles, new[] { $"ERS profile folder does not exist: {folder}" });

        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<ErsControlProfile>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Profile JSON is empty.");
                Validate(profile);
                profile.SourcePath = path;
                profiles.Add(profile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new ErsProfileLoadResult(folder, profiles, warnings);
    }

    public static string CreateSessionSnapshotJson(ErsControlProfile profile, ErsAutopilotOptions options)
    {
        var snapshot = new
        {
            captured_at = DateTimeOffset.Now,
            operating_mode = ErsAutopilotOptions.ToSettingValue(options.OperatingMode),
            decrease_key = VirtualKeyName(options.DecreaseVirtualKey),
            increase_key = VirtualKeyName(options.IncreaseVirtualKey),
            emergency_stop_key = VirtualKeyName(options.EmergencyStopVirtualKey),
            input_backend = "windows-scan-code",
            key_hold_ms = options.KeyHoldMilliseconds,
            profile
        };
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static string VirtualKeyName(int value) => value switch
    {
        0x76 => "F7",
        0x77 => "F8",
        0x7B => "F12",
        _ => $"VK_0x{value:X2}"
    };

    private static void UpgradeKnownBuiltInProfile(string source, string target)
    {
        if (!string.Equals(Path.GetFileName(target), DefaultChinaFileName, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var installed = JsonSerializer.Deserialize<ErsControlProfile>(File.ReadAllText(source), JsonOptions);
            var existing = JsonSerializer.Deserialize<ErsControlProfile>(File.ReadAllText(target), JsonOptions);
            if (installed is null || existing is null) return;
            if (!string.Equals(installed.ProfileId, BuiltInChinaProfileId, StringComparison.Ordinal)) return;
            if (!string.Equals(existing.ProfileId, BuiltInChinaProfileId, StringComparison.Ordinal) &&
                !string.Equals(existing.ProfileId, LegacyBuiltInChinaProfileId, StringComparison.Ordinal)) return;
            if (installed.ProfileRevision <= existing.ProfileRevision) return;

            var backup = target + ".pre-0.10.2.bak";
            if (!File.Exists(backup)) File.Copy(target, backup);
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Preserve an unreadable or user-customized target rather than replacing it blindly.
        }
    }

    private static void Validate(ErsControlProfile profile)
    {
        if (profile.SchemaVersion is < 1 or > 2) throw new InvalidDataException($"Unsupported schema_version {profile.SchemaVersion}.");
        if (profile.ProfileRevision <= 0) throw new InvalidDataException("profile_revision must be positive.");
        if (string.IsNullOrWhiteSpace(profile.ProfileId)) throw new InvalidDataException("profile_id is required.");
        if (profile.TrackId < 0) throw new InvalidDataException("track_id must be non-negative.");
        if (profile.TrackLengthM <= 0) throw new InvalidDataException("track_length_m must be positive.");
        if (profile.SessionTypes.Count == 0) throw new InvalidDataException("session_types must contain at least one value.");
        if (profile.BatteryCapacityJ <= 0) throw new InvalidDataException("battery_capacity_j must be positive.");
        if (profile.CriticalBatteryPct is < 0 or > 100 || profile.RecoveryEnterPct is < 0 or > 100 ||
            profile.RecoveryExitPct is < 0 or > 100 || profile.HighBatteryPct is < 0 or > 100)
            throw new InvalidDataException("Battery thresholds must be between 0 and 100.");
        if (profile.RecoveryEnterPct >= profile.RecoveryExitPct)
            throw new InvalidDataException("recovery_enter_pct must be below recovery_exit_pct.");
        if (profile.CriticalBatteryPct > profile.RecoveryEnterPct)
            throw new InvalidDataException("critical_battery_pct must not exceed recovery_enter_pct.");
        if (profile.HighBatteryPct < profile.RecoveryExitPct)
            throw new InvalidDataException("high_battery_pct must not be below recovery_exit_pct.");
        if (profile.BattleGapMs <= 0) throw new InvalidDataException("battle_gap_ms must be positive.");
        if (profile.AttackGapMs <= 0) throw new InvalidDataException("attack_gap_ms must be positive.");
        if (profile.DefendGapMs <= 0) throw new InvalidDataException("defend_gap_ms must be positive.");
        if (profile.TacticalExitMarginMs < 0) throw new InvalidDataException("tactical_exit_margin_ms must be non-negative.");
        if (profile.DefendPriorityMarginMs < 0) throw new InvalidDataException("defend_priority_margin_ms must be non-negative.");
        if (profile.MinimumControlSpeedKph < 0) throw new InvalidDataException("minimum_control_speed_kph must be non-negative.");
        if (profile.SessionTypes.Any(value => value is < 0 or > byte.MaxValue))
            throw new InvalidDataException("session_types values must fit in one byte.");

        if (profile.SchemaVersion == 2)
        {
            ValidateTacticalPlan(profile);
            ValidateEnergyPlan(profile);
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in profile.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id)) throw new InvalidDataException("Every rule needs an id.");
            if (!ids.Add(rule.Id)) throw new InvalidDataException($"Duplicate rule id: {rule.Id}.");
            if (rule.StartM < 0 || rule.StartM > profile.TrackLengthM || rule.EndM < 0 || rule.EndM > profile.TrackLengthM)
                throw new InvalidDataException($"Rule {rule.Id} is outside the configured track length.");
            if (rule.MinimumBatteryPct is < 0 or > 100 || rule.MinimumThrottlePct is < 0 or > 100)
                throw new InvalidDataException($"Rule {rule.Id} has a percentage outside 0-100.");
            if (rule.MinimumSpeedKph is < 0)
                throw new InvalidDataException($"Rule {rule.Id} minimum_speed_kph must be non-negative.");
            if (rule.MaximumActiveMs is <= 0)
                throw new InvalidDataException($"Rule {rule.Id} maximum_active_ms must be positive when supplied.");
            if (rule.DeploymentValue is < 0 or > 1)
                throw new InvalidDataException($"Rule {rule.Id} deployment_value must be between 0 and 1.");
            if (rule.MinimumEnergySurplusPct is < -100 or > 100)
                throw new InvalidDataException($"Rule {rule.Id} minimum_energy_surplus_pct must be between -100 and 100.");
            if (rule.FinalLapMinimumBatteryPct is < 0 or > 100)
                throw new InvalidDataException($"Rule {rule.Id} final_lap_minimum_battery_pct must be between 0 and 100.");
            if (rule.MinimumLapNumber is <= 0)
                throw new InvalidDataException($"Rule {rule.Id} minimum_lap_number must be positive when supplied.");
            if (rule.MaximumLapsRemaining is <= 0 || rule.MinimumLapsRemaining is <= 0)
                throw new InvalidDataException($"Rule {rule.Id} laps-remaining limits must be positive when supplied.");
            if (rule.MinimumLapsRemaining is not null && rule.MaximumLapsRemaining is not null &&
                rule.MinimumLapsRemaining > rule.MaximumLapsRemaining)
                throw new InvalidDataException($"Rule {rule.Id} minimum_laps_remaining must not exceed maximum_laps_remaining.");
        }
    }

    private static void ValidateTacticalPlan(ErsControlProfile profile)
    {
        var plan = profile.Tactical ?? throw new InvalidDataException("schema_version 2 requires tactical.");
        if (plan.AttackCriticalGapMs <= 0 || plan.AttackPressureGapMs <= 0 ||
            plan.DefendCriticalGapMs <= 0 || plan.DefendPressureGapMs <= 0)
            throw new InvalidDataException("Tactical gap thresholds must be positive.");
        if (plan.AttackCriticalGapMs >= plan.AttackPressureGapMs)
            throw new InvalidDataException("attack_critical_gap_ms must be below attack_pressure_gap_ms.");
        if (plan.DefendCriticalGapMs >= plan.DefendPressureGapMs)
            throw new InvalidDataException("defend_critical_gap_ms must be below defend_pressure_gap_ms.");
        if (plan.ExitMarginMs < 0 || plan.DefendPriorityMarginMs < 0 ||
            plan.ClosingRateGapExtensionMs < 0)
            throw new InvalidDataException("Tactical margins must be non-negative.");
        if (plan.ClosingRateWindowMs < 500)
            throw new InvalidDataException("closing_rate_window_ms must be at least 500.");
        if (plan.RapidClosingRateMsPerSecond >= 0)
            throw new InvalidDataException("rapid_closing_rate_ms_per_second must be negative.");
    }

    private static void ValidateEnergyPlan(ErsControlProfile profile)
    {
        var plan = profile.EnergyPlan ?? throw new InvalidDataException("schema_version 2 requires energy_plan.");
        if (plan.Checkpoints.Count < 2)
            throw new InvalidDataException("energy_plan.checkpoints must contain at least two points.");
        if (plan.TargetTolerancePct < 0 || plan.SurplusReleasePct < 0 || plan.LowValueReservePct < 0)
            throw new InvalidDataException("Energy-plan tolerances must be non-negative.");
        if (plan.ClosingLaps <= 0)
            throw new InvalidDataException("energy_plan.closing_laps must be positive.");
        if (plan.FinalLapFloorPct is < 0 or > 100)
            throw new InvalidDataException("energy_plan.final_lap_floor_pct must be between 0 and 100.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distances = new HashSet<double>();
        foreach (var point in plan.Checkpoints)
        {
            if (string.IsNullOrWhiteSpace(point.Id))
                throw new InvalidDataException("Every energy checkpoint needs an id.");
            if (!ids.Add(point.Id))
                throw new InvalidDataException($"Duplicate energy checkpoint id: {point.Id}.");
            if (!distances.Add(point.DistanceM))
                throw new InvalidDataException($"Duplicate energy checkpoint distance: {point.DistanceM}.");
            if (point.DistanceM < 0 || point.DistanceM >= profile.TrackLengthM)
                throw new InvalidDataException($"Energy checkpoint {point.Id} is outside the configured track length.");
            if (point.TargetPct is < 0 or > 100 || point.MinimumPct is < 0 or > 100)
                throw new InvalidDataException($"Energy checkpoint {point.Id} has a percentage outside 0-100.");
            if (point.MinimumPct > point.TargetPct)
                throw new InvalidDataException($"Energy checkpoint {point.Id} minimum_pct must not exceed target_pct.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

}
