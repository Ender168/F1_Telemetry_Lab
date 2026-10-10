using F1TelemetryLab;
using System.Buffers.Binary;

namespace F1TelemetryLab.Tests;

public sealed partial class RaceEngineerTests
{
    [Fact]
    public void HighWearIsRetainedAndPreviousSetIsExcluded()
    {
        var profile = new RaceEngineerProfile { ProfileId = "wear", TrackId = 2,
            TrackLengthM = 5441, SessionTypes = new() { 15 }, SafeTyreWearPct = 75,
            TyreWearPriors = new() { new() { VisualCompound = 17, WearPctPerLap = 2 } } };
        var service = new RaceEngineerService(new("", new[] { profile }, Array.Empty<string>()),
            new("", Array.Empty<ErsControlProfile>(), Array.Empty<string>()));
        var now = DateTimeOffset.UtcNow;
        service.ProcessPacket(SessionPacket(), now);
        service.ProcessPacket(StatusPacket(60, 1), now);
        service.ProcessPacket(DamagePacket(10), now);
        service.ProcessPacket(LapPacket(1, 0), now);
        service.ProcessPacket(DamagePacket(21), now);
        service.ProcessPacket(StatusPacket(50, 2), now);
        service.ProcessPacket(LapPacket(2, 90000), now);
        Assert.Equal(11, service.Snapshot.Tyres.WearRatePctPerLap!.Value, 2);
        // New tyres of the same compound: old 11% laps must not be reused.
        service.ProcessPacket(StatusPacket(50, 0), now);
        service.ProcessPacket(DamagePacket(0), now);
        Assert.Equal(2, service.Snapshot.Tyres.WearRatePctPerLap!.Value, 2);
    }

    [Fact]
    public void LegacyPitEstimateCannotOverrideConfiguredLossAndHighWearPriorLoads()
    {
        var root = Path.Combine(Path.GetTempPath(), "f1_learning_" + Guid.NewGuid().ToString("N"));
        try
        {
            var initial = RaceEngineerProfileStore.Load(root).Find(2, 15)!;
            var folder = RaceEngineerProfileStore.EnsureDefaultProfiles(root);
            RaceEngineerProfileStore.WriteLearnedModel(folder, new LearnedRaceModel
            {
                TrackId = 2, PitSamples = 20, PitLossMeanSeconds = 7,
                Tyres = new() { [17] = new() { Samples = 5, WearMeanPctPerLap = 11 } }
            });
            var loaded = RaceEngineerProfileStore.Load(root);
            var profile = loaded.Find(2, 15)!;
            Assert.Equal(initial.PitLossGreenSeconds, profile.PitLossGreenSeconds);
            Assert.Equal(0, profile.LearnedPitSamples);
            Assert.Equal(11, profile.TyrePrior(17));
            Assert.Contains(loaded.Warnings, x => x.Contains("Legacy learned pit loss ignored"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
