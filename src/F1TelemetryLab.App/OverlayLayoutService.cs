using System.Text.Json;

namespace F1TelemetryLab;

public sealed class OverlayWidgetLayout
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1;
    public bool Visible { get; set; } = true;
}

public sealed class OverlayLayout
{
    public int SchemaVersion { get; set; } = 2;
    public double ScreenWidth { get; set; } = 1920;
    public double ScreenHeight { get; set; } = 1080;
    public List<OverlayWidgetLayout> Widgets { get; set; } = new();
}

public static class OverlayLayoutService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static OverlayLayout Load(double screenWidth, double screenHeight, int slot = 1, string? directory = null)
    {
        try
        {
            var path = LayoutPath(slot, directory);
            if (!File.Exists(path) && slot == 1) path = Path.Combine(StorageDirectory(directory), "overlay-layout-v1.json");
            if (!File.Exists(path)) return Default(screenWidth, screenHeight);
            var loaded = JsonSerializer.Deserialize<OverlayLayout>(File.ReadAllText(path), JsonOptions);
            return Normalize(loaded, screenWidth, screenHeight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return Default(screenWidth, screenHeight); }
    }

    public static OverlayLayout Normalize(OverlayLayout? loaded, double width, double height)
    {
        var result = Default(width, height);
        if (loaded is null || loaded.SchemaVersion is not (1 or 2) || loaded.Widgets is null) return result;
        var xRatio = width / (double.IsFinite(loaded.ScreenWidth) && loaded.ScreenWidth > 0 ? loaded.ScreenWidth : width);
        var yRatio = height / (double.IsFinite(loaded.ScreenHeight) && loaded.ScreenHeight > 0 ? loaded.ScreenHeight : height);
        foreach (var target in result.Widgets)
        {
            var source = loaded.Widgets.FirstOrDefault(x => x is not null && string.Equals(x.Id, target.Id, StringComparison.OrdinalIgnoreCase));
            if (source is null && target.Id == "lap-comparison" && loaded.SchemaVersion == 1)
                source = loaded.Widgets.FirstOrDefault(x => x is not null && x.Id == "laps");
            if (source is null) continue;
            target.Scale = double.IsFinite(source.Scale) ? Math.Clamp(source.Scale, 0.75, 1.75) : 1;
            target.Visible = source.Visible;
            // Old factory positions would overlap the larger combined comparison.
            if (loaded.SchemaVersion == 1 && ((target.Id == "nearby-tyres" && source.X == 440 && source.Y == 90) ||
                (target.Id == "tyres" && source.X == 35 && source.Y == 205))) continue;
            target.X = Math.Clamp(double.IsFinite(source.X) ? source.X * xRatio : target.X, 0, Math.Max(0, width - 120));
            target.Y = Math.Clamp(double.IsFinite(source.Y) ? source.Y * yRatio : target.Y, 0, Math.Max(0, height - 60));

        }
        return result;
    }

    public static double Snap(double value, double maximum) => Math.Clamp(Math.Round(value / 10) * 10, 0, Math.Max(0, maximum));

    public static void Save(OverlayLayout layout, int slot = 1, string? directory = null)
    {
        var path = LayoutPath(slot, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(layout, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public static OverlayLayout Default(double width, double height)
    {
        var right = Math.Max(420, width - 480);
        return new OverlayLayout
        {
            ScreenWidth = width,
            ScreenHeight = height,
            Widgets = new List<OverlayWidgetLayout>
            {
                Widget("lap-comparison", 35, 90),
                Widget("tyres", 35, 330),
                Widget("nearby-tyres", 760, 90),
                Widget("pit", right, 90),
                Widget("ers-energy", right, 220),
                Widget("ers-tactical", right, 365),
                Widget("ers-action", right, 495),
                Widget("ers-pit-lap", right, 650),
                Widget("lift-coast", Math.Max(0, width / 2 - 155), Math.Max(0, height - 180)),
                Widget("track-guidance", Math.Max(0, width / 2 - 170), Math.Max(0, height - 300))
            }
        };
    }

    private static OverlayWidgetLayout Widget(string id, double x, double y) => new()
    {
        Id = id,
        X = x,
        Y = y,
        Scale = 1,
        Visible = true
    };

    private static string StorageDirectory(string? directory) => directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F1TelemetryLab");

    private static string LayoutPath(int slot, string? directory)
    {
        if (slot is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(slot));
        return Path.Combine(StorageDirectory(directory), $"overlay-layout-v2-slot{slot}.json");
    }
}
