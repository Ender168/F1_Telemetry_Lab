using System.Buffers.Binary;
using System.Text.Json;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    private static LiftCoastPlan CoastPlan(double corner = 1000) => new()
    {
        Enabled = true, PreviewDistanceM = 300,
        Zones = new() { new() { Id = "t1", Corner = "T1", CornerDistanceM = corner,
            LiftBeforeCornerM = 250, BrakeBeforeCornerM = 100 } }
    };

    [Theory]
    [InlineData(400, 1, 0, LiftCoastPhase.Ready)]
    [InlineData(600, 1, 0, LiftCoastPhase.Approach)]
    [InlineData(750, 1, 0, LiftCoastPhase.Lift)]
    [InlineData(800, 0, 0, LiftCoastPhase.Coasting)]
    [InlineData(800, 1, .2, LiftCoastPhase.BrakeZone)]
    [InlineData(900, 1, 0, LiftCoastPhase.BrakeZone)]
    [InlineData(1001, 1, 0, LiftCoastPhase.Ready)]
    public void CoastCueUsesCalibratedDistancesAndStopsBeforeBraking(double distance, double throttle,
        double brake, LiftCoastPhase expected)
    {
        Assert.Equal(expected, LiftCoastAdvisor.Evaluate(CoastPlan(), 5441, distance, 250, throttle, brake).Phase);
    }

    [Fact]
    public void CoastZoneWorksAcrossStartFinishAndFormatsBothDistances()
    {
        var plan = CoastPlan(200);
        var beforeLine = LiftCoastAdvisor.Evaluate(plan, 5441, 5391, 250, 1, 0);
        Assert.Equal(LiftCoastPhase.Lift, beforeLine.Phase);
        Assert.Equal(250, beforeLine.DistanceToCornerM);
        Assert.Equal(LiftCoastPhase.Coasting, LiftCoastAdvisor.Evaluate(plan, 5441, 30, 250, 0, 0).Phase);
        var approach = LiftCoastAdvisor.Evaluate(CoastPlan(), 5441, 600, 250, 1, 0);
        Assert.Equal(150, approach.DistanceToLiftM);
        Assert.Contains("150", approach.Format(true));
        Assert.Contains("250", approach.Format(true));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5441)]
    [InlineData(double.NaN)]
    public void InvalidDistanceNeverProducesLiftCommand(double distance) =>
        Assert.Equal(LiftCoastPhase.Waiting, LiftCoastAdvisor.Evaluate(CoastPlan(), 5441, distance, 250, 1, 0).Phase);

    [Fact]
    public void CoastPlanValidationRejectsImpossibleAndOverlappingZones()
    {
        var plan = CoastPlan();
        LiftCoastAdvisor.Validate(plan, 5441);
        plan.Zones[0].BrakeBeforeCornerM = 260;
        Assert.Throws<InvalidDataException>(() => LiftCoastAdvisor.Validate(plan, 5441));
        plan = CoastPlan(100);
        plan.Zones.Add(new() { Id = "last", Corner = "Last", CornerDistanceM = 5300,
            LiftBeforeCornerM = 200, BrakeBeforeCornerM = 80 });
        Assert.Throws<InvalidDataException>(() => LiftCoastAdvisor.Validate(plan, 5441));
        plan = CoastPlan(); plan.Zones[0].CornerDistanceM = double.NaN;
        Assert.Throws<InvalidDataException>(() => LiftCoastAdvisor.Validate(plan, 5441));
    }

    private static LiftCoastService CoastService(bool configured = true)
    {
        var profile = ChinaProfile();
        if (configured) profile.LiftCoast = CoastPlan();
        return new(new("", new[] { profile }, Array.Empty<string>()));
    }

    private static void CoastPacket(LiftCoastService service, byte[] packet, uint frame = 1,
        DateTimeOffset? time = null)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), frame);
        service.ProcessPacket(packet, time ?? DateTimeOffset.UnixEpoch);
    }

    private static void CoastFeed(LiftCoastService service, uint frame = 1, float distance = 800,
        DateTimeOffset? time = null)
    {
        CoastPacket(service, SessionPacket(true, 0), frame, time);
        CoastPacket(service, LapPacket(distance), frame, time);
        CoastPacket(service, TelemetryPacket(), frame, time);
        CoastPacket(service, StatusPacket(ErsDeployMode.Medium), frame, time);
    }

    [Fact]
    public void MultiplayerCoastAdviceNeedsNoInputSinkAndExpiresWithoutPackets()
    {
        var service = CoastService(); CoastFeed(service);
        Assert.Equal(LiftCoastPhase.Lift, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
        Assert.Equal(LiftCoastPhase.Waiting, service.GetAdvice(DateTimeOffset.UnixEpoch.AddMilliseconds(251)).Phase);
        Assert.Equal(LiftCoastPhase.Suspended, service.GetAdvice(DateTimeOffset.UnixEpoch, pitLapBurn: true).Phase);
        Assert.Equal(LiftCoastPhase.Waiting, service.GetAdvice(DateTimeOffset.UnixEpoch.AddMilliseconds(-1)).Phase);
    }

    [Fact]
    public void ProfilesWithoutCoastPointsRemainCompatible()
    {
        var service = CoastService(false); CoastFeed(service);
        Assert.Equal(LiftCoastPhase.NoProfile, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
    }

    [Fact]
    public void CoastPausePitAndSafetyCarSuppressCue()
    {
        var service = CoastService(); CoastFeed(service);
        var paused = SessionPacket(true, 0); paused[F12026Parser.HeaderSize + 14] = 1;
        CoastPacket(service, paused, 2);
        Assert.Equal(LiftCoastPhase.Suspended, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
        var safety = SessionPacket(true, 0); safety[F12026Parser.HeaderSize + 124] = 1;
        CoastPacket(service, safety, 3);
        Assert.Equal(LiftCoastPhase.Suspended, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
        CoastFeed(service, 4);
        var pit = LapPacket(800); pit[F12026Parser.HeaderSize + 34] = 1;
        CoastPacket(service, pit, 5);
        Assert.Equal(LiftCoastPhase.Suspended, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
    }

    [Fact]
    public void CoastFlashbackClearsSamplesAndRejectsAbandonedFrames()
    {
        var service = CoastService(); CoastFeed(service);
        CoastPacket(service, FlashbackPacket(), 100);
        CoastFeed(service, 99);
        Assert.Equal(LiftCoastPhase.Waiting, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
        CoastFeed(service, 101, 600);
        Assert.Equal(LiftCoastPhase.Approach, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
        CoastPacket(service, LapPacket(800), 100);
        Assert.Equal(LiftCoastPhase.Approach, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
    }

    [Fact]
    public void CoastSessionChangeCannotReusePlayerSamplesOrRevertToOldSession()
    {
        var service = CoastService(); CoastFeed(service);
        var session = SessionPacket(true, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(session.AsSpan(7), 92);
        CoastPacket(service, session);
        Assert.Equal(LiftCoastPhase.Waiting, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
        CoastFeed(service, 10);
        Assert.Equal(LiftCoastPhase.Waiting, service.GetAdvice(DateTimeOffset.UnixEpoch).Phase);
    }

    [Fact]
    public void CoastPlanLoadsThroughExistingProfileStoreAndRejectsBadConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var p = ChinaProfile(); p.LiftCoast = CoastPlan();
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var path = Path.Combine(directory, "profile.json");
            File.WriteAllText(path, JsonSerializer.Serialize(p, options));
            Assert.True(Assert.Single(ErsProfileStore.LoadFromDirectory(directory).Profiles).LiftCoast!.Enabled);
            p.LiftCoast.Zones[0].BrakeBeforeCornerM = 300;
            File.WriteAllText(path, JsonSerializer.Serialize(p, options));
            Assert.Empty(ErsProfileStore.LoadFromDirectory(directory).Profiles);
        }
        finally { Directory.Delete(directory, true); }
    }
}
