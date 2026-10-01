using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    private static ErsAutopilotService RecoveryService(FlashbackClock clock, FlashbackSink sink, List<ErsAuditRecord> audit)
    {
        var profile = ChinaProfile();
        profile.Rules.Clear(); profile.DefaultMode = ErsDeployMode.Hotlap;
        return new(new ErsAutopilotOptions { OperatingMode = ErsAutopilotOperatingMode.Live,
            MinimumCommandIntervalMs = 0, MaximumRetries = 0, ConfirmationTimeoutMs = 100 },
            new("", new[] { profile }, Array.Empty<string>()), sink, audit.Add, timeProvider: clock);
    }

    [Fact]
    public void TimeoutRecoversWithoutNewRecordingAndBacksOffWhenGameStillIgnoresInput()
    {
        var clock = new FlashbackClock(); var sink = new FlashbackSink(); var audit = new List<ErsAuditRecord>();
        using var service = RecoveryService(clock, sink, audit);
        FeedFlashbackState(service, clock, 1, 1000);
        Assert.Equal(1, sink.TapCount);
        clock.Now = clock.Now.AddMilliseconds(110); FeedFlashbackState(service, clock, 2, 1005);
        Assert.Equal("Recovering", service.Status.State);
        Assert.Contains("Medium -> Hotlap", service.Status.Detail);
        Assert.Equal(1, sink.ImmediateReleaseCount);
        clock.Now = clock.Now.AddSeconds(2); FeedFlashbackState(service, clock, 3, 1100);
        Assert.Equal(1, sink.TapCount);
        clock.Now = clock.Now.AddSeconds(1.1); FeedFlashbackState(service, clock, 4, 1200);
        Assert.Equal(2, sink.TapCount);
        Assert.Single(audit, x => x.Action == "input-recovery-resumed");
        clock.Now = clock.Now.AddMilliseconds(110); FeedFlashbackState(service, clock, 5, 1205);
        Assert.Contains("6 s", service.Status.Detail);
        clock.Now = clock.Now.AddSeconds(6.1); FeedFlashbackState(service, clock, 6, 1500);
        Assert.Equal(3, sink.TapCount);
        SendFrame(service, clock, StatusPacket(ErsDeployMode.Hotlap), 7);
        Assert.Equal("Holding", service.Status.State);
        Assert.Single(audit, x => x.Action == "telemetry-confirmed");
    }

    [Fact]
    public void RecoveryWaitsForFreshStatusAndUsesActualModeRatherThanReplayingOldCommand()
    {
        var clock = new FlashbackClock(); var sink = new FlashbackSink(); var audit = new List<ErsAuditRecord>();
        using var service = RecoveryService(clock, sink, audit);
        FeedFlashbackState(service, clock, 1, 1000);
        clock.Now = clock.Now.AddMilliseconds(110); FeedFlashbackState(service, clock, 2, 1005);
        clock.Now = clock.Now.AddSeconds(4);
        SendFrame(service, clock, SessionPacket(true, 0), 3);
        SendFrame(service, clock, LapPacket(1200), 3);
        SendFrame(service, clock, TelemetryPacket(), 3);
        Assert.Equal(1, sink.TapCount);
        SendFrame(service, clock, StatusPacket(ErsDeployMode.Hotlap), 3);
        Assert.Equal(1, sink.TapCount);
        Assert.Equal("Holding", service.Status.State);
    }

    [Fact]
    public void RecoveryRetainsConsumedOncePerLapRules()
    {
        var clock = new FlashbackClock(); var sink = new FlashbackSink();
        var profile = ChinaProfile(); profile.Rules.Clear(); profile.DefaultMode = ErsDeployMode.Medium;
        profile.Rules.Add(new() { Id = "once", StartM = 1000, EndM = 1300,
            TargetMode = ErsDeployMode.Hotlap, OncePerLap = true });
        using var service = new ErsAutopilotService(new ErsAutopilotOptions {
            OperatingMode = ErsAutopilotOperatingMode.Live, MaximumRetries = 0,
            MinimumCommandIntervalMs = 0, ConfirmationTimeoutMs = 100 },
            new("", new[] { profile }, Array.Empty<string>()), sink, timeProvider: clock);
        FeedFlashbackState(service, clock, 1, 1100);
        clock.Now = clock.Now.AddMilliseconds(110); FeedFlashbackState(service, clock, 2, 1110);
        Assert.Equal("Recovering", service.Status.State);
        clock.Now = clock.Now.AddSeconds(4); FeedFlashbackState(service, clock, 3, 1500);
        FeedFlashbackState(service, clock, 4, 1100);
        Assert.Equal("default", service.LastDecision!.RuleId);
        Assert.Equal(1, sink.TapCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmergencyStopOrReleaseFailureCannotAutomaticallyRecover(bool releaseFails)
    {
        var clock = new FlashbackClock(); var sink = new FlashbackSink(); var audit = new List<ErsAuditRecord>();
        using var service = RecoveryService(clock, sink, audit);
        FeedFlashbackState(service, clock, 1, 1000);
        sink.ReleaseFails = releaseFails; sink.EmergencyStop = !releaseFails;
        clock.Now = clock.Now.AddMilliseconds(110); FeedFlashbackState(service, clock, 2, 1005);
        sink.ReleaseFails = false; sink.EmergencyStop = false;
        clock.Now = clock.Now.AddSeconds(40); FeedFlashbackState(service, clock, 3, 1500);
        Assert.Equal(1, sink.TapCount);
        Assert.Equal("Blocked", service.Status.State);
        Assert.DoesNotContain(audit, x => x.Action == "input-recovery-resumed");
    }
}
