using F1TelemetryLab;
using System.Text.Json;

namespace F1TelemetryLab.Tests;

public sealed class OverlayComparisonTests
{
    [Fact]
    public void ComparisonUsesSameLapNumberAndSuppressesDirtyOrUnknownDeltas()
    {
        var now = DateTimeOffset.UtcNow;
        var demo = LapComparison.Demo(now);
        var rows = LapComparison.Build(demo, true, now);
        Assert.Equal(-0.22, rows[0].Laps[0].DeltaSeconds!.Value, 3);
        Assert.Equal(0.34, rows[2].Laps[0].DeltaSeconds!.Value, 3);
        var ahead = demo.NearbyCars.Single(x => x.Position == 4);
        ahead = ahead with { LastLaps = new OpponentLap[]
        {
            new(13, 91820, true, false, false), // Matches own L13, not first displayed L12.
            new(14, 91640, true, true, false),
            new(15, 91000, null, false, false)
        } };
        var changed = demo with { NearbyCars = demo.NearbyCars.Select(x => x.Position == 4 ? ahead : x).ToArray() };
        rows = LapComparison.Build(changed, true, now);
        Assert.Equal(0, rows[0].Laps[0].DeltaSeconds);
        Assert.Null(rows[0].Laps[1].DeltaSeconds);
        Assert.Contains("PIT", rows[0].Laps[1].Text);
        Assert.Null(rows[0].Laps[2].DeltaSeconds);
        Assert.Contains("?", rows[0].Laps[2].Text);
    }

    [Fact]
    public void StaleNeighboursHaveNoHistoryOrDelta()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = LapComparison.Build(LapComparison.Demo(now), false, now.AddSeconds(6));
        Assert.All(rows[0].Laps.Concat(rows[2].Laps), x => Assert.Equal("·", x.Text));
        Assert.All(rows[1].Laps, x => Assert.StartsWith("L", x.Text));
    }

    [Fact]
    public void LegacyLayoutMigratesAndThreeSlotsStayIndependent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "f1_layout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "overlay-layout-v1.json"),
                """{"schema_version":1,"screen_width":1920,"screen_height":1080,"widgets":[{"id":"laps","x":120,"y":150,"scale":1.2}]}""");
            var first = OverlayLayoutService.Load(1920, 1080, 1, folder);
            Assert.Equal(2, first.SchemaVersion);
            Assert.Equal(120, first.Widgets.Single(x => x.Id == "lap-comparison").X);
            Assert.DoesNotContain(first.Widgets, x => x.Id is "laps-ahead" or "laps-behind" or "laps");
            OverlayLayoutService.Save(first, 1, folder);
            var second = OverlayLayoutService.Default(1920, 1080);
            second.Widgets[0].X = 400;
            OverlayLayoutService.Save(second, 2, folder);
            Assert.Equal(120, OverlayLayoutService.Load(1920, 1080, 1, folder).Widgets[0].X);
            Assert.Equal(400, OverlayLayoutService.Load(1920, 1080, 2, folder).Widgets[0].X);
            Assert.Equal(35, OverlayLayoutService.Load(1920, 1080, 3, folder).Widgets[0].X);
            Assert.True(File.Exists(Path.Combine(folder, "overlay-layout-v1.json")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void SnapClampsToScreenAndInvalidScaleFallsBack()
    {
        Assert.Equal(130, OverlayLayoutService.Snap(127, 500));
        Assert.Equal(0, OverlayLayoutService.Snap(-30, 500));
        var layout = OverlayLayoutService.Default(1920, 1080);
        layout.Widgets[0].Scale = double.NaN;
        Assert.Equal(1, OverlayLayoutService.Normalize(layout, 1920, 1080).Widgets[0].Scale);
    }
}
