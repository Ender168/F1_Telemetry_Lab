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
                if (!File.Exists(target)) File.Copy(source, target);
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

    public static void WriteSessionSnapshot(ErsControlProfile profile, string sessionFolder, ErsAutopilotOptions options)
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
        File.WriteAllText(
            Path.Combine(sessionFolder, "ers_profile_used.json"),
            JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    public static string VirtualKeyName(int value) => value switch
    {
        0x76 => "F7",
        0x77 => "F8",
        0x7B => "F12",
        _ => $"VK_0x{value:X2}"
    };

    private static void Validate(ErsControlProfile profile)
    {
        if (profile.SchemaVersion != 1) throw new InvalidDataException($"Unsupported schema_version {profile.SchemaVersion}.");
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
        if (profile.MinimumControlSpeedKph < 0) throw new InvalidDataException("minimum_control_speed_kph must be non-negative.");
        if (profile.SessionTypes.Any(value => value is < 0 or > byte.MaxValue))
            throw new InvalidDataException("session_types values must fit in one byte.");

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
