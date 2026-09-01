using System.Text.Json;

namespace F1TelemetryLab;

public sealed class AppSettings
{
    public int Port { get; set; } = 20777;
    public string RootFolder { get; set; } = AppSettingsService.DefaultRootFolder();
    public bool AutoZip { get; set; } = true;
    public int RetentionDays { get; set; }
    public string Language { get; set; } = "en";
    public int UiScalePercent { get; set; } = 100;
    public string ErsAutopilotMode { get; set; } = "dry-run";
    public int ErsDecreaseVirtualKey { get; set; } = 0x76;
    public int ErsIncreaseVirtualKey { get; set; } = 0x77;
    public string WinRarPath { get; set; } = "";
    public bool OpenRaceEngineerOverlayOnStart { get; set; }
}

public static class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return Normalize(new AppSettings());
            return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings());
        }
        catch (IOException)
        {
            return Normalize(new AppSettings());
        }
        catch (JsonException)
        {
            return Normalize(new AppSettings());
        }
        catch (UnauthorizedAccessException)
        {
            return Normalize(new AppSettings());
        }
    }

    public static void Save(AppSettings settings)
    {
        Normalize(settings);
        var path = SettingsPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public static string DefaultRootFolder() =>
        Directory.Exists(@"D:\")
            ? @"D:\F1TelemetryLab"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "F1TelemetryLab");

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Port = Math.Clamp(settings.Port, 1, 65_535);
        settings.RootFolder = string.IsNullOrWhiteSpace(settings.RootFolder) ? DefaultRootFolder() : settings.RootFolder.Trim();
        settings.RetentionDays = Math.Clamp(settings.RetentionDays, 0, 3_650);
        settings.Language = string.Equals(settings.Language, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        settings.UiScalePercent = Math.Clamp(settings.UiScalePercent, 80, 175);
        settings.ErsAutopilotMode = ErsAutopilotOptions.ToSettingValue(ErsAutopilotOptions.ParseOperatingMode(settings.ErsAutopilotMode));
        settings.ErsDecreaseVirtualKey = Math.Clamp(settings.ErsDecreaseVirtualKey, 1, ushort.MaxValue);
        settings.ErsIncreaseVirtualKey = Math.Clamp(settings.ErsIncreaseVirtualKey, 1, ushort.MaxValue);
        settings.WinRarPath = settings.WinRarPath?.Trim() ?? "";
        return settings;
    }

    private static string SettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "F1TelemetryLab",
        "settings.json");
}

public sealed record SessionRetentionCandidate(string FolderPath, DateTime LastWriteUtc, long SizeBytes);

public static class SessionRetentionService
{
    public static IReadOnlyList<SessionRetentionCandidate> Preview(string rootFolder, int retentionDays, string? excludedFolder = null)
    {
        if (retentionDays <= 0) return Array.Empty<SessionRetentionCandidate>();
        var packs = ResolvePacksFolder(rootFolder);
        if (!Directory.Exists(packs)) return Array.Empty<SessionRetentionCandidate>();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var excluded = string.IsNullOrWhiteSpace(excludedFolder) ? null : Path.GetFullPath(excludedFolder);
        return Directory.GetDirectories(packs)
            .Select(path => new DirectoryInfo(path))
            .Where(info => info.LastWriteTimeUtc < cutoff)
            .Where(info => excluded is null || !string.Equals(info.FullName, excluded, StringComparison.OrdinalIgnoreCase))
            .Select(info => new SessionRetentionCandidate(info.FullName, info.LastWriteTimeUtc, DirectorySize(info.FullName)))
            .OrderBy(x => x.LastWriteUtc)
            .ToList();
    }

    public static int Delete(string rootFolder, IReadOnlyList<SessionRetentionCandidate> candidates)
    {
        var packs = ResolvePacksFolder(rootFolder);
        var prefix = packs.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var removed = 0;
        foreach (var candidate in candidates)
        {
            var target = Path.GetFullPath(candidate.FolderPath);
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target)) continue;
            Directory.Delete(target, recursive: true);
            removed++;
        }
        return removed;
    }

    private static string ResolvePacksFolder(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder)) throw new ArgumentException("Storage root is empty.", nameof(rootFolder));
        return Path.GetFullPath(Path.Combine(rootFolder.Trim(), "telemetry_packs"));
    }

    private static long DirectorySize(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch (IOException) { return 0L; }
                    catch (UnauthorizedAccessException) { return 0L; }
                });
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
