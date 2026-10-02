using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    private static TrackGuidancePlan GuidePlan(TrackCueKind kind = TrackCueKind.Speed) => new()
    {
        Enabled = true, Cues = new() { new() { Id = "t1", Text = "T1: 115-125 km/h", Kind = kind,
            StartM = 700, EndM = 1000, TargetM = 900, ToleranceM = 15, MinimumSpeedKph = 115, MaximumSpeedKph = 125 } }
    };

    [Theory]
    [InlineData(120, TrackCuePhase.Success)]
    [InlineData(160, TrackCuePhase.Missed)]
    public void SpeedIsJudgedAtCheckpointAndResultStaysVisible(int speed, TrackCuePhase expected)
    {
        var advisor = new TrackGuidanceAdvisor(); var plan = GuidePlan(); var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(TrackCuePhase.Approach, advisor.Observe(plan, 5441, 800, 120, 0, now).Phase);
        advisor.Observe(plan, 5441, 890, speed, 0, now.AddMilliseconds(100));
        Assert.Equal(expected, advisor.Observe(plan, 5441, 910, speed, 0, now.AddMilliseconds(200)).Phase);
        Assert.Equal(expected, advisor.Observe(plan, 5441, 950, 120, 0, now.AddMilliseconds(300)).Phase);
        Assert.Equal(TrackCuePhase.Waiting, advisor.Observe(plan, 5441, 1001, 120, 0, now.AddMilliseconds(400)).Phase);
    }

    [Fact]
    public void BrakeRequiresOnsetInWindowAndEarlyBrakeCannotTurnGreenLater()
    {
        var advisor = new TrackGuidanceAdvisor(); var plan = GuidePlan(TrackCueKind.Brake); var now = DateTimeOffset.UnixEpoch;
        advisor.Observe(plan, 5441, 800, 200, 0, now);
        Assert.Equal(TrackCuePhase.Missed, advisor.Observe(plan, 5441, 820, 200, .5, now.AddMilliseconds(100)).Phase);
        Assert.Equal(TrackCuePhase.Missed, advisor.Observe(plan, 5441, 900, 180, .5, now.AddMilliseconds(200)).Phase);
        advisor.Reset(); advisor.Observe(plan, 5441, 890, 200, 0, now);
        Assert.Equal(TrackCuePhase.Success, advisor.Observe(plan, 5441, 900, 200, .5, now.AddMilliseconds(100)).Phase);
    }

    [Fact]
    public void GapsAndResetCannotInventSuccessfulSpeedOrBrakeMeasurements()
    {
        var advisor = new TrackGuidanceAdvisor(); var plan = GuidePlan(); var now = DateTimeOffset.UnixEpoch;
        advisor.Observe(plan, 5441, 890, 120, 0, now);
        Assert.Equal(TrackCuePhase.Unavailable, advisor.Observe(plan, 5441, 910, 120, 0, now.AddSeconds(1)).Phase);
        advisor.Reset();
        Assert.Equal(TrackCuePhase.Unavailable, advisor.Observe(plan, 5441, 910, 120, 0, now).Phase);
    }

    [Fact]
    public void GuidanceWrapsAcrossLineAndActionTakesPriorityOverInformation()
    {
        var advisor = new TrackGuidanceAdvisor(); var plan = GuidePlan();
        plan.Cues[0].StartM = 5300; plan.Cues[0].TargetM = 10; plan.Cues[0].EndM = 100;
        plan.Cues.Add(new() { Id = "note", Text = "Kerb", Kind = TrackCueKind.Info, Priority = 100, StartM = 5300, EndM = 100 });
        TrackGuidanceAdvisor.Validate(plan, 5441);
        advisor.Observe(plan, 5441, 5435, 120, 0, DateTimeOffset.UnixEpoch);
        Assert.Equal(TrackCuePhase.Success, advisor.Observe(plan, 5441, 20, 120, 0, DateTimeOffset.UnixEpoch.AddMilliseconds(200)).Phase);
    }

    [Fact]
    public void GuidanceValidationRejectsInvalidTargetsAndDuplicateIds()
    {
        var plan = GuidePlan(); TrackGuidanceAdvisor.Validate(plan, 5441);
        plan.Cues[0].TargetM = 1001;
        Assert.Throws<InvalidDataException>(() => TrackGuidanceAdvisor.Validate(plan, 5441));
        plan = GuidePlan(); plan.Cues.Add(plan.Cues[0]);
        Assert.Throws<InvalidDataException>(() => TrackGuidanceAdvisor.Validate(plan, 5441));
    }

    [Fact]
    public void GuidanceRoundTripsThroughSnakeCaseProfileJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var profile = ChinaProfile(); profile.TrackGuidance = GuidePlan();
            var options = new System.Text.Json.JsonSerializerOptions {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
            var path = Path.Combine(directory, "profile.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(profile, options));
            var loaded = Assert.Single(ErsProfileStore.LoadFromDirectory(directory).Profiles);
            Assert.Equal(TrackCueKind.Speed, Assert.Single(loaded.TrackGuidance!.Cues).Kind);
            profile.TrackGuidance.Cues[0].TargetM = 1001;
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(profile, options));
            Assert.Empty(ErsProfileStore.LoadFromDirectory(directory).Profiles);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PacketServiceShowsInfoInMultiplayerAndClearsOnFlashbackPauseAndStaleData()
    {
        var profile = ChinaProfile(); profile.TrackGuidance = GuidePlan(TrackCueKind.Info);
        var service = new LiftCoastService(new("", new[] { profile }, Array.Empty<string>()));
        CoastFeed(service);
        Assert.Equal(TrackCuePhase.Info, service.GetGuidance(DateTimeOffset.UnixEpoch).Phase);
        Assert.Equal(TrackCuePhase.Waiting, service.GetGuidance(DateTimeOffset.UnixEpoch.AddMilliseconds(251)).Phase);
        CoastFeed(service, 2);
        var pause = SessionPacket(true, 0); pause[F12026Parser.HeaderSize + 14] = 1;
        CoastPacket(service, pause, 3);
        Assert.Equal(TrackCuePhase.Waiting, service.GetGuidance(DateTimeOffset.UnixEpoch).Phase);
        CoastFeed(service, 4); CoastPacket(service, FlashbackPacket(), 100);
        CoastFeed(service, 99);
        Assert.Equal(TrackCuePhase.Waiting, service.GetGuidance(DateTimeOffset.UnixEpoch).Phase);
        CoastFeed(service, 101);
        Assert.Equal(TrackCuePhase.Info, service.GetGuidance(DateTimeOffset.UnixEpoch).Phase);
    }
    private static byte[] GuidanceControls(float brake, ushort speed = 200)
    {
        var packet = TelemetryPacket();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(F12026Parser.HeaderSize), speed);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(F12026Parser.HeaderSize + 10), brake);
        return packet;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuidanceJudgesBrakeOnlyWhenSameFramePairCompletes(bool telemetryFirst)
    {
        var profile = ChinaProfile(); profile.TrackGuidance = GuidePlan(TrackCueKind.Brake);
        var service = new LiftCoastService(new("", new[] { profile }, Array.Empty<string>()));
        CoastFeed(service, 1, 820);
        var now = DateTimeOffset.UnixEpoch.AddMilliseconds(100);
        if (telemetryFirst) CoastPacket(service, GuidanceControls(.5f), 2, now);
        else CoastPacket(service, LapPacket(900), 2, now);
        Assert.Equal(TrackCuePhase.Approach, service.GetGuidance(now).Phase);
        if (telemetryFirst) CoastPacket(service, LapPacket(900), 2, now.AddMilliseconds(10));
        else CoastPacket(service, GuidanceControls(.5f), 2, now.AddMilliseconds(10));
        Assert.Equal(TrackCuePhase.Success, service.GetGuidance(now.AddMilliseconds(10)).Phase);
    }

    [Fact]
    public void GuidanceDoesNotJudgeMismatchedFramesAndUsesBufferedMatchingControls()
    {
        var profile = ChinaProfile(); profile.TrackGuidance = GuidePlan(TrackCueKind.Brake);
        var service = new LiftCoastService(new("", new[] { profile }, Array.Empty<string>()));
        CoastFeed(service, 1, 820);
        var now = DateTimeOffset.UnixEpoch.AddMilliseconds(100);
        CoastPacket(service, GuidanceControls(.5f), 2, now);
        CoastPacket(service, GuidanceControls(0), 3, now.AddMilliseconds(10));
        Assert.Equal(TrackCuePhase.Approach, service.GetGuidance(now.AddMilliseconds(10)).Phase);
        CoastPacket(service, LapPacket(900), 2, now.AddMilliseconds(20));
        Assert.Equal(TrackCuePhase.Success, service.GetGuidance(now.AddMilliseconds(20)).Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuidanceSpeedUsesMatchingFrameRegardlessOfArrivalOrder(bool telemetryFirst)
    {
        var profile = ChinaProfile(); profile.TrackGuidance = GuidePlan();
        var service = new LiftCoastService(new("", new[] { profile }, Array.Empty<string>()));
        CoastFeed(service, 1, 890);
        var now = DateTimeOffset.UnixEpoch.AddMilliseconds(100);
        CoastPacket(service, GuidanceControls(0, 120), 2, now);
        CoastPacket(service, LapPacket(895), 2, now);
        if (telemetryFirst) CoastPacket(service, GuidanceControls(0, 120), 3, now.AddMilliseconds(100));
        else CoastPacket(service, LapPacket(910), 3, now.AddMilliseconds(100));
        if (telemetryFirst) CoastPacket(service, LapPacket(910), 3, now.AddMilliseconds(110));
        else CoastPacket(service, GuidanceControls(0, 120), 3, now.AddMilliseconds(110));
        Assert.Equal(TrackCuePhase.Success, service.GetGuidance(now.AddMilliseconds(110)).Phase);
    }

}

