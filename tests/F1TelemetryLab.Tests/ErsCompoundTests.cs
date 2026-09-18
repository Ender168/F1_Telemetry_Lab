using System.Buffers.Binary;
using System.Text.Json;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    private static ErsControlProfile CompoundProfile(string id, int? actual = null, int? visual = null)
    {
        var p = PitRuleProfile();
        p.ProfileId = id;
        p.DisplayName = id;
        p.DryOnly = false;
        p.ActualTyreCompounds = actual is null ? null : new() { actual.Value };
        p.VisualTyreCompounds = visual is null ? null : new() { visual.Value };
        return p;
    }

    [Theory]
    [InlineData(16, 16, "soft")]
    [InlineData(18, 16, "soft")] // The same visual tyre can represent different physical compounds.
    [InlineData(18, 17, "medium")]
    [InlineData(20, 18, "hard")]
    [InlineData(7, 7, "inter")]
    [InlineData(8, 8, "wet")]
    public void SelectorSupportsAllFiveTyreTypes(int actual, int visual, string expected)
    {
        var generic = CompoundProfile("generic");
        generic.SelectionPriority = 99999;
        var profiles = new ErsProfileLoadResult("", new[] { generic,
            CompoundProfile("soft", visual: 16), CompoundProfile("medium", visual: 17),
            CompoundProfile("hard", visual: 18), CompoundProfile("inter", actual: 7),
            CompoundProfile("wet", actual: 8) }, Array.Empty<string>());
        Assert.Equal(expected, profiles.Find(2, 15, actual, visual, 0)!.ProfileId);
        Assert.Same(generic, profiles.Find(2, 15));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    public void SelectorCanTargetIndividualActualSlickCompounds(int actual)
    {
        var exact = CompoundProfile("actual", actual);
        var profiles = new ErsProfileLoadResult("", new[] { exact }, Array.Empty<string>());
        Assert.Same(exact, profiles.Find(2, 15, actual, 16));
        Assert.Null(profiles.Find(2, 15, 7, 7));
        Assert.Null(profiles.Find(2, 15));
    }

    [Fact]
    public void CompatibilityPrecedesPriorityAndBothFiltersMustMatch()
    {
        var dry = CompoundProfile("dry"); dry.DryOnly = true; dry.SelectionPriority = 99999;
        var inter = CompoundProfile("inter", 7);
        var both = CompoundProfile("both", 18, 16);
        var profiles = new ErsProfileLoadResult("", new[] { dry, inter, both }, Array.Empty<string>());
        Assert.Same(inter, profiles.Find(2, 15, 7, 7, 4));
        Assert.Same(inter, profiles.Find(2, 15, 7, 7, 0));
        Assert.Same(both, profiles.Find(2, 15, 18, 16, 0));
        Assert.Same(dry, profiles.Find(2, 15, 18, 17, 0));
        Assert.Null(profiles.Find(2, 15, 18, 17, 4));
    }

    [Fact]
    public void ServiceSwitchesInterWetAndSlickProfilesAndAuditsTransitions()
    {
        var clock = new FlashbackClock();
        var audit = new List<ErsAuditRecord>();
        using var service = CompoundService(clock, audit);
        FeedFlashbackState(service, clock, 1, 4500);
        foreach (var row in new[] { (7, 7, "inter"), (8, 8, "wet"), (7, 7, "inter"),
                     (18, 16, "soft"), (19, 17, "medium"), (20, 18, "hard") }.Select((v, i) => (v, i)))
        {
            SendFrame(service, clock, CompoundStatus(row.v.Item1, row.v.Item2), (uint)row.i + 2);
            Assert.Equal(row.v.Item3, service.Status.ProfileId);
        }
        Assert.Contains(audit, a => a.Action == "profile-transition" && a.Reason.Contains("inter -> wet") &&
            a.Reason.Contains("actual 7 -> 8"));
        SendFrame(service, clock, CompoundStatus(7, 7), 2); // Delayed old compound cannot switch back.
        Assert.Equal("hard", service.Status.ProfileId);
    }

    [Fact]
    public void PitExitWaitsForPostExitStatusAndFlashbackRequiresFreshCompound()
    {
        var clock = new FlashbackClock();
        using var service = CompoundService(clock, new());
        FeedFlashbackState(service, clock, 1, 4500);
        SendFrame(service, clock, CompoundStatus(7, 7), 2);
        var pit = LapPacket(4500); pit[F12026Parser.HeaderSize + 34] = 1;
        SendFrame(service, clock, pit, 3);
        SendFrame(service, clock, CompoundStatus(8, 8), 4);
        SendFrame(service, clock, LapPacket(4500), 5);
        Assert.Equal("Blocked", service.Status.State);
        SendFrame(service, clock, CompoundStatus(8, 8), 6);
        Assert.Equal("wet", service.Status.ProfileId);
        SendFrame(service, clock, FlashbackPacket(), 100);
        SendFrame(service, clock, SessionPacket(true, 0), 101);
        Assert.Equal("No profile", service.Status.State);
        SendFrame(service, clock, LapPacket(4500), 101);
        SendFrame(service, clock, TelemetryPacket(), 101);
        SendFrame(service, clock, CompoundStatus(7, 7), 102);
        Assert.Equal("inter", service.Status.ProfileId);
    }

    [Fact]
    public void PitFlagUsesNewCompoundRulesWithoutOldOncePerLapState()
    {
        var clock = new FlashbackClock();
        using var service = CompoundService(clock, new());
        FeedFlashbackState(service, clock, 1, 4500);
        SendFrame(service, clock, CompoundStatus(7, 7), 2);
        SendFrame(service, clock, ButtonsPacket(6), 3);
        Assert.Equal(ErsDeployMode.Boost, service.LastDecision!.TargetMode);
        SendFrame(service, clock, LapPacket(4700), 4);
        SendFrame(service, clock, LapPacket(4500), 5);
        Assert.Equal(ErsDeployMode.Medium, service.LastDecision!.TargetMode);
        SendFrame(service, clock, CompoundStatus(8, 8), 6);
        Assert.True(service.PitLapStatus.Active);
        Assert.Equal("wet", service.Status.ProfileId);
        Assert.Equal(ErsDeployMode.Boost, service.LastDecision!.TargetMode);
    }

    [Fact]
    public void StaleTelemetryDoesNotResetUsedRuleBudgets()
    {
        var clock = new FlashbackClock();
        using var service = CompoundService(clock, new());
        FeedFlashbackState(service, clock, 1, 4500);
        SendFrame(service, clock, CompoundStatus(7, 7), 2);
        SendFrame(service, clock, ButtonsPacket(6), 3);
        SendFrame(service, clock, LapPacket(4700), 4);
        SendFrame(service, clock, LapPacket(4500), 5);
        Assert.Equal(ErsDeployMode.Medium, service.LastDecision!.TargetMode);
        clock.Now = clock.Now.AddSeconds(2);
        SendFrame(service, clock, TelemetryPacket(), 6);
        Assert.True(service.LastDecision!.Blocked);
        SendFrame(service, clock, SessionPacket(true, 0), 7);
        SendFrame(service, clock, LapPacket(4500), 7);
        SendFrame(service, clock, CompoundStatus(7, 7), 8);
        Assert.False(service.LastDecision!.Blocked);
        Assert.Equal(ErsDeployMode.Medium, service.LastDecision.TargetMode);
    }

    [Fact]
    public void CompoundFiltersRoundTripAndRejectEmptyOrInvalidIds()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        try
        {
            var profile = CompoundProfile("test", 7);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var path = Path.Combine(folder, "test.json");
            File.WriteAllText(path, JsonSerializer.Serialize(profile, options));
            Assert.Equal(7, Assert.Single(ErsProfileStore.LoadFromDirectory(folder).Profiles).ActualTyreCompounds![0]);
            foreach (var ids in new[] { new List<int>(), new List<int> { 0 }, new List<int> { 256 }, new List<int> { 7, 7 } })
            {
                profile.ActualTyreCompounds = ids;
                File.WriteAllText(path, JsonSerializer.Serialize(profile, options));
                Assert.Empty(ErsProfileStore.LoadFromDirectory(folder).Profiles);
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    private static ErsAutopilotService CompoundService(FlashbackClock clock, List<ErsAuditRecord> audit)
    {
        var profiles = new[] { CompoundProfile("inter", 7), CompoundProfile("wet", 8),
            CompoundProfile("soft", visual: 16), CompoundProfile("medium", visual: 17), CompoundProfile("hard", visual: 18) };
        foreach (var profile in profiles) profile.Rules.Last().OncePerLap = true;
        return new(new ErsAutopilotOptions { OperatingMode = ErsAutopilotOperatingMode.DryRun },
            new ErsProfileLoadResult("", profiles, Array.Empty<string>()), new FlashbackSink(), audit.Add, timeProvider: clock);
    }

    private static byte[] CompoundStatus(int actual, int visual)
    {
        var packet = StatusPacket(ErsDeployMode.Medium);
        packet[F12026Parser.HeaderSize + 25] = (byte)actual;
        packet[F12026Parser.HeaderSize + 26] = (byte)visual;
        return packet;
    }
}
