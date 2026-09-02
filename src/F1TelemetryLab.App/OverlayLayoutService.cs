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
    public int SchemaVersion { get; set; } = 1;
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

    public static OverlayLayout Load(double screenWidth, double screenHeight)
    {
        var fallback = Default(screenWidth, screenHeight);
        try
        {
            var path = LayoutPath();
            if (!File.Exists(path)) return fallback;
            var loaded = JsonSerializer.Deserialize<OverlayLayout>(File.ReadAllText(path), JsonOptions);
            if (loaded is null || loaded.SchemaVersion != 1) return fallback;
            var xRatio = screenWidth / Math.Max(1, loaded.ScreenWidth);
            var yRatio = screenHeight / Math.Max(1, loaded.ScreenHeight);
            var defaults = fallback.Widgets.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var widget in loaded.Widgets)
            {
                if (!defaults.ContainsKey(widget.Id)) continue;
                defaults[widget.Id] = new OverlayWidgetLayout
                {
                    Id = widget.Id,
                    X = Math.Clamp(widget.X * xRatio, 0, Math.Max(0, screenWidth - 120)),
                    Y = Math.Clamp(widget.Y * yRatio, 0, Math.Max(0, screenHeight - 60)),
                    Scale = Math.Clamp(widget.Scale, 0.75, 1.75),
                    Visible = widget.Visible
                };
            }
            fallback.Widgets = defaults.Values.ToList();
            return fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return fallback;
        }
    }

    public static void Save(OverlayLayout layout)
    {
        var path = LayoutPath();
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
                Widget("laps", 35, 90),
                Widget("tyres", 35, 205),
                Widget("pit", right, 90),
                Widget("ers-energy", right, 220),
                Widget("ers-tactical", right, 365),
                Widget("ers-action", right, 495)
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

    private static string LayoutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "F1TelemetryLab",
        "overlay-layout-v1.json");
}
