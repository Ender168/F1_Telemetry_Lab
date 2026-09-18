using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    private static ErsTractionGate TractionGate(int stable = 250) => new()
    {
        MaximumFrontWheelsAngleRad = .08, MaximumYawRateRadS = .5,
        MaximumRearSlipAngleRad = .12, MaximumRearSlipRatio = .15,
        StableForMs = stable, MaximumSampleGapMs = 100, MaximumDataAgeMs = 150
    };

    private static ErsControlProfile TractionProfile()
    {
        var p = PitRuleProfile();
        p.TractionGates = new() { Hotlap = TractionGate(200), Boost = TractionGate() };
        p.Rules.Last().OncePerLap = true;
        p.Rules.Last().MaximumActiveMs = 500;
        return p;
    }

    private static ErsControlState TractionState(int ms, double angle = .02, double yaw = .1,
        double left = .02, double right = .02, double ratio = .02)
    {
        var at = DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
        return State(at, 4500, 70, 100) with
        {
            PitLapBurn = true,
            PlayerMotion = new(at, 1, 0, (uint)ms + 1, ms / 1000d, angle, yaw, left, right, ratio, ratio)
        };
    }

    [Fact]
    public void MotionExParserReadsPlayerFieldsAndRejectsInvalidEnvelope()
    {
        var packet = MotionPacket(1, .3f);
        var p = packet.AsSpan(F12026Parser.HeaderSize);
        BinaryPrimitives.WriteSingleLittleEndian(p[168..], -.07f);
        BinaryPrimitives.WriteSingleLittleEndian(p[148..], -.4f);
        BinaryPrimitives.WriteSingleLittleEndian(p[80..], -.11f);
        BinaryPrimitives.WriteSingleLittleEndian(p[84..], .03f);
        BinaryPrimitives.WriteSingleLittleEndian(p[64..], -.13f);
        BinaryPrimitives.WriteSingleLittleEndian(p[68..], .05f);
        var motion = F12026Parser.ParsePlayerMotionEx(packet, DateTimeOffset.UnixEpoch)!;
        Assert.Equal(-.07, motion.FrontWheelsAngle, 5);
        Assert.Equal(-.4, motion.YawRate, 5);
        Assert.Equal(.11, motion.RearSlipAngle, 5);
        Assert.Equal(.13, motion.RearSlipRatio, 5);
        Assert.Equal(0, motion.CarIndex);
        Assert.Null(F12026Parser.ParsePlayerMotionEx(packet[..^1], DateTimeOffset.UnixEpoch));
        packet[5] = 2;
        Assert.Null(F12026Parser.ParsePlayerMotionEx(packet, DateTimeOffset.UnixEpoch));
        packet[5] = 1; packet[27] = 255;
        Assert.Null(F12026Parser.ParsePlayerMotionEx(packet, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void WaitingDoesNotConsumeOncePerLapOrStartDeploymentTimer()
    {
        var engine = new ErsDecisionEngine(TractionProfile());
        for (int ms = 0; ms <= 1000; ms += 50)
            Assert.Equal(ErsDeployMode.Medium, engine.Evaluate(TractionState(ms, angle: .1)).TargetMode);
        for (int ms = 1050; ms < 1300; ms += 50)
            Assert.Equal(ErsDeployMode.Medium, engine.Evaluate(TractionState(ms)).TargetMode);
        Assert.Equal(ErsDeployMode.Boost, engine.Evaluate(TractionState(1300)).TargetMode);
        Assert.Equal(ErsDeployMode.Boost, engine.Evaluate(TractionState(1350)).TargetMode);
        engine.Evaluate(TractionState(1400) with { LapDistanceM = 4700 });
        Assert.Equal(ErsDeployMode.Medium, engine.Evaluate(TractionState(1450)).TargetMode);
    }

    [Theory]
    [InlineData(.08, .1, .02, .02, .02)]
    [InlineData(-.08, .1, .02, .02, .02)]
    [InlineData(.02, -.5, .02, .02, .02)]
    [InlineData(.02, .1, -.12, .02, .02)]
    [InlineData(.02, .1, .02, .12, .02)]
    [InlineData(.02, .1, .02, .02, -.15)]
    [InlineData(double.NaN, .1, .02, .02, .02)]
    public void EveryConfiguredMetricMustPass(double angle, double yaw, double left, double right, double ratio)
    {
        var engine = new ErsDecisionEngine(TractionProfile());
        for (int ms = 0; ms <= 300; ms += 50)
        {
            var decision = engine.Evaluate(TractionState(ms, angle, yaw, left, right, ratio));
            Assert.Equal(ErsDeployMode.Medium, decision.TargetMode);
            Assert.Contains("waiting for traction", decision.Reason);
        }
    }

    [Fact]
    public void DuplicateStaleMissingAndGappedSamplesCannotSatisfyDwell()
    {
        var engine = new ErsDecisionEngine(TractionProfile());
        var initial = TractionState(0);
        engine.Evaluate(initial);
        Assert.Equal(ErsDeployMode.Medium, engine.Evaluate(initial with
            { ReceivedAt = initial.ReceivedAt.AddMilliseconds(300) }).TargetMode);
        Assert.Equal(ErsDeployMode.Medium, engine.Evaluate(TractionState(300)).TargetMode);
        Assert.Equal(ErsDeployMode.Medium, engine.Evaluate(TractionState(350) with { PlayerMotion = null }).TargetMode);
        for (int ms = 400; ms <= 600; ms += 50)
            Assert.Equal(ErsDeployMode.Medium, engine.Evaluate(TractionState(ms)).TargetMode);
        Assert.Equal(ErsDeployMode.Boost, engine.Evaluate(TractionState(650)).TargetMode);
    }

    [Fact]
    public void HotlapToBoostUsesBoostGateAndReductionIsNeverHeld()
    {
        var profile = TractionProfile();
        var engine = new ErsDecisionEngine(profile);
        for (int ms = 0; ms <= 200; ms += 50)
            Assert.Equal(ErsDeployMode.Hotlap, engine.Evaluate(TractionState(ms) with { CurrentMode = ErsDeployMode.Hotlap }).TargetMode);
        Assert.Equal(ErsDeployMode.Boost, engine.Evaluate(TractionState(250) with { CurrentMode = ErsDeployMode.Hotlap }).TargetMode);
        var reduction = engine.Evaluate(TractionState(300, angle: .2) with
            { PitLapBurn = false, CurrentMode = ErsDeployMode.Boost });
        Assert.Equal(ErsDeployMode.Medium, reduction.TargetMode);
    }

    [Fact]
    public void RuleOverrideAndDefaultModeAreBothGated()
    {
        var profile = TractionProfile();
        profile.Rules.Last().TractionGate = TractionGate(0);
        Assert.Equal(ErsDeployMode.Boost, new ErsDecisionEngine(profile).Evaluate(TractionState(0)).TargetMode);
        profile.Rules.Clear(); profile.DefaultMode = ErsDeployMode.Boost;
        Assert.Equal(ErsDeployMode.Medium, new ErsDecisionEngine(profile).Evaluate(TractionState(0)).TargetMode);
    }

    [Fact]
    public void LiveServiceAcceptsOnlyPlayerMotionAndCancelsUnsafeRetries()
    {
        var clock = new FlashbackClock();
        var sink = new FlashbackSink();
        using var service = FlashbackService(ErsAutopilotOperatingMode.Live, clock, sink, new(), TractionProfile());
        FeedFlashbackState(service, clock, 1, 4500);
        SendFrame(service, clock, ButtonsPacket(6), 2);
        for (int ms = 0; ms <= 250; ms += 50)
        {
            clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
            var packet = MotionPacket((uint)ms + 3, ms / 1000f);
            var foreign = (byte[])packet.Clone(); foreign[27] = 1;
            service.ProcessPacket(foreign, clock.Now);
            if (ms == 250) Assert.Equal(0, sink.TapCount);
            service.ProcessPacket(packet, clock.Now);
        }
        Assert.Equal(1, sink.TapCount);
        clock.Now = clock.Now.AddMilliseconds(50);
        var unsafePacket = MotionPacket(303, .3f);
        BinaryPrimitives.WriteSingleLittleEndian(unsafePacket.AsSpan(F12026Parser.HeaderSize + 168), .2f);
        service.ProcessPacket(unsafePacket, clock.Now);
        Assert.Equal(ErsDeployMode.Medium, service.LastDecision!.TargetMode);
        Assert.Equal(1, sink.TapCount);
        SendFrame(service, clock, FlashbackPacket(), 400);
        FeedFlashbackState(service, clock, 401, 4500);
        SendFrame(service, clock, ButtonsPacket(6), 402);
        Assert.Equal(ErsDeployMode.Medium, service.LastDecision!.TargetMode);
    }

    [Fact]
    public void InvalidJsonGateIsRejectedAndValidGateLoads()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var profile = TractionProfile();
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            var path = Path.Combine(directory, "test.json");
            File.WriteAllText(path, JsonSerializer.Serialize(profile, options));
            Assert.Single(ErsProfileStore.LoadFromDirectory(directory).Profiles);
            profile.TractionGates!.Boost!.MaximumYawRateRadS = -1;
            File.WriteAllText(path, JsonSerializer.Serialize(profile, options));
            Assert.Empty(ErsProfileStore.LoadFromDirectory(directory).Profiles);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] MotionPacket(uint frame, float sessionTime)
    {
        var packet = Packet(13, 244, 91, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), frame);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(15), sessionTime);
        return packet;
    }
}
