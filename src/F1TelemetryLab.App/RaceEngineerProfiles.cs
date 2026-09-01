using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace F1TelemetryLab;

public sealed class RaceEngineerProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int SelectionPriority { get; set; }
    public int TrackId { get; set; } = -1;
    public string TrackName { get; set; } = "";
    public int TrackLengthM { get; set; }
    public List<int> SessionTypes { get; set; } = new();
    public double SafeTyreWearPct { get; set; } = 75;
    public double PitLossGreenSeconds { get; set; } = 23;
    public double PitLossVscSeconds { get; set; } = 15;
    public double PitLossSafetyCarSeconds { get; set; } = 12;
    public double PitLossUncertaintySeconds { get; set; } = 1.5;
    public double ErsBatteryCapacityJ { get; set; } = 4_000_000;
    public double ErsCriticalPct { get; set; } = 12;
    public double ErsAggressiveEnergyPerLapJ { get; set; } = 400_000;
    public List<TyreWearPrior> TyreWearPriors { get; set; } = new();
    public List<ErsEnergyBand> ErsEnergyBands { get; set; } = new();
    [JsonIgnore]
    public string SourcePath { get; set; } = "";
    public int LearnedPitSamples { get; set; }
    public Dictionary<int, int> LearnedTyreSamples { get; set; } = new();

    public double TyrePrior(int visualCompound) => TyreWearPriors
        .FirstOrDefault(x => x.VisualCompound == visualCompound)?.WearPctPerLap ?? 0;

    public ErsEnergyBand EnergyBand(double distanceM)
    {
        return ErsEnergyBands
                   .OrderByDescending(x => x.Priority)
                   .FirstOrDefault(x => Contains(x.StartM, x.EndM, distanceM, TrackLengthM))
               ?? new ErsEnergyBand { Segment = "Default", StartM = 0, EndM = TrackLengthM, TargetMinPct = 35, TargetMaxPct = 65 };
    }

    private static bool Contains(double start, double end, double value, double trackLength)
    {
        if (trackLength <= 0) return false;
        var normalized = ((value % trackLength) + trackLength) % trackLength;
        return start <= end ? normalized >= start && normalized <= end : normalized >= start || normalized <= end;
    }
}

public sealed class TyreWearPrior
{
    public int VisualCompound { get; set; }
    public double WearPctPerLap { get; set; }
}

public sealed class ErsEnergyBand
{
    public string Segment { get; set; } = "";
    public int Priority { get; set; }
    public double StartM { get; set; }
    public double EndM { get; set; }
    public double TargetMinPct { get; set; }
    public double TargetMaxPct { get; set; }
}

public sealed record RaceEngineerProfileLoadResult(
    string ProfileFolder,
    IReadOnlyList<RaceEngineerProfile> Profiles,
    IReadOnlyList<string> Warnings)
{
    public RaceEngineerProfile? Find(int trackId, int sessionType) => Profiles
        .Where(x => x.TrackId == trackId && x.SessionTypes.Contains(sessionType))
        .OrderByDescending(x => x.SelectionPriority)
        .ThenBy(x => x.ProfileId, StringComparer.Ordinal)
        .FirstOrDefault();
}

public sealed class LearnedRaceModel
{
    public int SchemaVersion { get; set; } = 1;
    public int TrackId { get; set; }
    public string TrackName { get; set; } = "";
    public int PitSamples { get; set; }
    public double PitLossMeanSeconds { get; set; }
    public Dictionary<int, LearnedTyreModel> Tyres { get; set; } = new();
    public HashSet<string> ProcessedSessionUids { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LearnedTyreModel
{
    public int Samples { get; set; }
    public double WearMeanPctPerLap { get; set; }
}

public static class RaceEngineerProfileStore
{
    private const string DefaultChinaFileName = "China_Race.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string ProfileFolder(string rootFolder) => Path.Combine(rootFolder, "race_profiles");

    public static RaceEngineerProfileLoadResult Load(string rootFolder)
    {
        var folder = EnsureDefaultProfiles(rootFolder);
        var profiles = new List<RaceEngineerProfile>();
        var warnings = new List<string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<RaceEngineerProfile>(File.ReadAllText(path), JsonOptions)
                              ?? throw new InvalidDataException("Profile JSON is empty.");
                Validate(profile);
                profile.SourcePath = path;
                ApplyLearnedModel(folder, profile, warnings);
                profiles.Add(profile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new RaceEngineerProfileLoadResult(folder, profiles, warnings);
    }

    public static string EnsureDefaultProfiles(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder)) throw new ArgumentException("Storage root is empty.", nameof(rootFolder));
        var folder = ProfileFolder(Path.GetFullPath(rootFolder.Trim()));
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "learned"));
        var installed = Path.Combine(AppContext.BaseDirectory, "data", "race_profiles");
        if (Directory.Exists(installed))
        {
            foreach (var source in Directory.EnumerateFiles(installed, "*.json", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(folder, Path.GetFileName(source));
                if (!File.Exists(target)) File.Copy(source, target);
            }
        }

        var china = Path.Combine(folder, DefaultChinaFileName);
        if (!File.Exists(china)) throw new FileNotFoundException("The built-in China race engineer profile is missing.", china);
        return folder;
    }

    public static string SerializeSnapshot(RaceEngineerProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    public static string LearnedModelPath(string profileFolder, int trackId) =>
        Path.Combine(profileFolder, "learned", $"Track_{trackId.ToString(CultureInfo.InvariantCulture)}.json");

    public static LearnedRaceModel? ReadLearnedModel(string profileFolder, int trackId)
    {
        var path = LearnedModelPath(profileFolder, trackId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LearnedRaceModel>(File.ReadAllText(path), JsonOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void WriteLearnedModel(string profileFolder, LearnedRaceModel model)
    {
        var path = LearnedModelPath(profileFolder, model.TrackId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(model, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void ApplyLearnedModel(string folder, RaceEngineerProfile profile, List<string> warnings)
    {
        var model = ReadLearnedModel(folder, profile.TrackId);
        if (model is null) return;
        if (model.PitSamples > 0 && model.PitLossMeanSeconds is > 5 and < 60)
        {
            profile.PitLossGreenSeconds = model.PitLossMeanSeconds;
            profile.LearnedPitSamples = model.PitSamples;
        }
        foreach (var pair in model.Tyres)
        {
            if (pair.Value.Samples <= 0 || pair.Value.WearMeanPctPerLap is <= 0 or > 10) continue;
            var prior = profile.TyreWearPriors.FirstOrDefault(x => x.VisualCompound == pair.Key);
            if (prior is null)
            {
                prior = new TyreWearPrior { VisualCompound = pair.Key };
                profile.TyreWearPriors.Add(prior);
            }
            prior.WearPctPerLap = pair.Value.WearMeanPctPerLap;
            profile.LearnedTyreSamples[pair.Key] = pair.Value.Samples;
        }
    }

    private static void Validate(RaceEngineerProfile profile)
    {
        if (profile.SchemaVersion != 1) throw new InvalidDataException($"Unsupported schema_version {profile.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(profile.ProfileId)) throw new InvalidDataException("profile_id is required.");
        if (profile.TrackId < 0 || profile.TrackLengthM <= 0) throw new InvalidDataException("Track id and length are required.");
        if (profile.SessionTypes.Count == 0) throw new InvalidDataException("session_types cannot be empty.");
        if (profile.SafeTyreWearPct is <= 0 or > 100) throw new InvalidDataException("safe_tyre_wear_pct must be within 0-100.");
        if (profile.PitLossGreenSeconds is <= 0 or > 120 || profile.PitLossUncertaintySeconds is <= 0 or > 30)
            throw new InvalidDataException("Pit loss settings are outside a sensible range.");
        if (profile.ErsBatteryCapacityJ <= 0 || profile.ErsAggressiveEnergyPerLapJ <= 0)
            throw new InvalidDataException("ERS energy settings must be positive.");
        foreach (var band in profile.ErsEnergyBands)
        {
            if (band.StartM < 0 || band.EndM < 0 || band.StartM > profile.TrackLengthM || band.EndM > profile.TrackLengthM)
                throw new InvalidDataException($"ERS band {band.Segment} is outside the track length.");
            if (band.TargetMinPct < 0 || band.TargetMaxPct > 100 || band.TargetMinPct > band.TargetMaxPct)
                throw new InvalidDataException($"ERS band {band.Segment} has an invalid target range.");
        }
    }
}
