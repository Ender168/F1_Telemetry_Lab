using System.Buffers.Binary;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    [Fact]
    public void FutureMotionCannotPoisonOpeningLapAndSafeDwellIsStillRequired()
    {
        var clock = new FlashbackClock();
        var profile = TractionProfile(); profile.Rules.Last().OncePerLap = false;
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new(), profile);
        FeedFlashbackState(service, clock, 1, 4500);
        SendFrame(service, clock, ButtonsPacket(6), 2);
        service.ProcessPacket(MotionPacket(365, 112.962753f), clock.Now);
        Assert.Contains("MotionEx rejected", service.LastDecision!.Reason);
        Assert.Contains("112.963", service.LastDecision.Reason);
        for (var ms = 50; ms <= 300; ms += 50)
        {
            clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
            service.ProcessPacket(MotionPacket((uint)(365 + ms), ms / 1000f), clock.Now);
            Assert.Equal(ms < 300 ? ErsDeployMode.Medium : ErsDeployMode.Boost, service.LastDecision!.TargetMode);
        }
    }

    [Fact]
    public void FrozenStartupClockDoesNotCreateStableDwellAndLateMotionCannotReplaceFreshSample()
    {
        var clock = new FlashbackClock();
        var profile = TractionProfile(); profile.Rules.Last().OncePerLap = false;
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new(), profile);
        FeedFlashbackState(service, clock, 1, 4500);
        SendFrame(service, clock, ButtonsPacket(6), 2);
        for (var ms = 0; ms <= 300; ms += 50)
        {
            clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
            service.ProcessPacket(MotionPacket((uint)(ms + 3), 0), clock.Now);
            Assert.Equal(ErsDeployMode.Medium, service.LastDecision!.TargetMode);
        }
        for (var ms = 350; ms <= 600; ms += 50)
        {
            clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
            service.ProcessPacket(MotionPacket((uint)(ms + 3), (ms - 300) / 1000f), clock.Now);
        }
        Assert.Equal(ErsDeployMode.Boost, service.LastDecision!.TargetMode);
        clock.Now = clock.Now.AddMilliseconds(200);
        service.ProcessPacket(MotionPacket(250, .1f), clock.Now);
        // A late arrival cannot refresh receipt time or bypass the stale gate.
        Assert.Contains("retained sample stale", service.LastDecision!.Reason);
        Assert.Contains("late/duplicate", service.LastDecision.Reason);
    }

    [Fact]
    public void InconsistentRetainedMotionNeedsCoherentRecoveryThenFullDwell()
    {
        var clock = new FlashbackClock();
        var profile = TractionProfile(); profile.Rules.Last().OncePerLap = false;
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new(), profile);
        FeedFlashbackState(service, clock, 1, 4500);
        SendFrame(service, clock, ButtonsPacket(6), 2);
        // Simulate a pre-existing bad watermark, originally consistent with its clocks.
        void ClockPacket(byte[] packet, uint frame, float time)
        {
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(15), time);
            SendFrame(service, clock, packet, frame);
        }
        ClockPacket(LapPacket(4500), 3, 112);
        ClockPacket(TelemetryPacket(), 3, 112);
        service.ProcessPacket(MotionPacket(4, 112), clock.Now);
        for (var ms = 50; ms <= 400; ms += 50)
        {
            clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
            ClockPacket(LapPacket(4500), (uint)ms, ms / 1000f);
            ClockPacket(TelemetryPacket(), (uint)ms, ms / 1000f);
            service.ProcessPacket(MotionPacket((uint)ms + 1, ms / 1000f), clock.Now);
            Assert.Equal(ms < 400 ? ErsDeployMode.Medium : ErsDeployMode.Boost, service.LastDecision!.TargetMode);
        }
    }
    [Theory]
    [InlineData("session")]
    [InlineData("player")]
    [InlineData("flashback")]
    public void MotionClockAndDwellResetAtLifecycleBoundaries(string change)
    {
        var clock = new FlashbackClock();
        var profile = TractionProfile(); profile.Rules.Clear(); profile.DefaultMode = ErsDeployMode.Boost;
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new(), profile);
        FeedFlashbackState(service, clock, 1, 4500);
        Assert.Contains("No MotionEx packets", service.LastDecision!.Reason);
        for (var ms = 0; ms <= 250; ms += 50)
        {
            clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(ms);
            service.ProcessPacket(MotionPacket((uint)ms + 10, ms / 1000f), clock.Now);
        }
        Assert.Equal(ErsDeployMode.Boost, service.LastDecision!.TargetMode);
        clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(300);
        if (change == "flashback") SendFrame(service, clock, FlashbackPacket(), 1000);
        void SendNew(byte[] packet, uint frame)
        {
            if (change == "session") BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 999);
            if (change == "player")
            {
                packet[27] = 1;
                var size = packet[6] == 2 ? 57 : packet[6] is 6 or 7 ? 59 : 0;
                if (size > 0) packet.AsSpan(29, size).CopyTo(packet.AsSpan(29 + size));
            }
            SendFrame(service, clock, packet, frame);
        }
        SendNew(SessionPacket(true, 0), 1001);
        SendNew(LapPacket(4500), 1001);
        SendNew(TelemetryPacket(), 1001);
        SendNew(StatusPacket(ErsDeployMode.Medium), 1001);
        Assert.NotEqual(ErsDeployMode.Boost, service.LastDecision!.TargetMode);
        for (var ms = 0; ms <= 250; ms += 50)
        {
            clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(350 + ms);
            SendNew(MotionPacket((uint)ms + 1010, ms / 1000f), (uint)ms + 1010);
            Assert.Equal(ms < 250 ? ErsDeployMode.Medium : ErsDeployMode.Boost, service.LastDecision!.TargetMode);
        }
    }

}
