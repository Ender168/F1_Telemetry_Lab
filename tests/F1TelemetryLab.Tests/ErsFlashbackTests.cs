using System.Buffers.Binary;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    [Theory]
    [InlineData(ErsAutopilotOperatingMode.DryRun)]
    [InlineData(ErsAutopilotOperatingMode.Live)]
    public void ConfirmedFlashbacksAllowSameLapRuleAgainAcrossRepeatedRewinds(ErsAutopilotOperatingMode mode)
    {
        var clock = new FlashbackClock();
        var sink = new FlashbackSink();
        var audit = new List<ErsAuditRecord>();
        using var service = FlashbackService(mode, clock, sink, audit);
        FeedFlashbackState(service, clock, 1, 3_500);
        Assert.Equal("t13-main-boost", service.LastDecision!.RuleId);
        for (uint rewind = 1; rewind <= 33; rewind++)
        {
            var frame = rewind * 10;
            SendFrame(service, clock, LapPacket(4_500), frame - 2);
            SendFrame(service, clock, LapPacket(3_500), frame - 1);
            Assert.Equal("default", service.LastDecision!.RuleId); // once_per_lap already consumed
            SendFrame(service, clock, FlashbackPacket(), frame);
            Assert.Null(service.LastDecision);
            Assert.Null(service.Status.Decision);
            FeedFlashbackState(service, clock, frame + 1, 3_500);
            Assert.Equal("t13-main-boost", service.LastDecision!.RuleId);
        }
        Assert.Equal(33, audit.Count(row => row.Action == "flashback-reset"));
        Assert.DoesNotContain(audit, row => row.Action == "feedback-timeout-blocked");
        Assert.Equal(mode == ErsAutopilotOperatingMode.Live ? 33 : 0, sink.ImmediateReleaseCount);
        Assert.Equal(mode == ErsAutopilotOperatingMode.Live ? 34 : 0, sink.TapCount);
    }

    [Fact]
    public void FlashbackWaitsForEveryFreshPacketAndIgnoresOldPacketsAndDuplicateEvent()
    {
        var clock = new FlashbackClock();
        var sink = new FlashbackSink();
        var audit = new List<ErsAuditRecord>();
        using var service = FlashbackService(ErsAutopilotOperatingMode.Live, clock, sink, audit);
        FeedFlashbackState(service, clock, 1, 3_500);
        SendFrame(service, clock, FlashbackPacket(), 100);
        FeedFlashbackState(service, clock, 99, 3_500);
        FeedFlashbackState(service, clock, 100, 3_500);
        Assert.Null(service.LastDecision);
        Assert.Equal(1, sink.TapCount);
        SendFrame(service, clock, SessionPacket(true, 0), 101);
        SendFrame(service, clock, LapPacket(3_500), 101);
        SendFrame(service, clock, TelemetryPacket(), 101);
        Assert.True(service.LastDecision!.Blocked);
        Assert.Equal(1, sink.TapCount);
        SendFrame(service, clock, StatusPacket(ErsDeployMode.Medium), 101);
        Assert.Equal(2, sink.TapCount);
        var decision = service.LastDecision;
        SendFrame(service, clock, FlashbackPacket(), 100);
        Assert.Same(decision, service.LastDecision);
        Assert.Single(audit, row => row.Action == "flashback-reset");
    }

    [Fact]
    public void FlashbackCancelsPendingFeedbackFromAbandonedCommand()
    {
        var clock = new FlashbackClock();
        var sink = new FlashbackSink();
        var audit = new List<ErsAuditRecord>();
        using var service = FlashbackService(ErsAutopilotOperatingMode.Live, clock, sink, audit);
        FeedFlashbackState(service, clock, 1, 3_500);
        Assert.Equal(1, sink.TapCount); // Awaiting Hotlap from the first command.
        SendFrame(service, clock, FlashbackPacket(), 100);
        clock.Now = clock.Now.AddSeconds(5);
        SendFrame(service, clock, SessionPacket(true, 0), 101);
        SendFrame(service, clock, LapPacket(3_500), 101);
        SendFrame(service, clock, TelemetryPacket(), 101);
        SendFrame(service, clock, StatusPacket(ErsDeployMode.Hotlap), 101);
        Assert.Equal(2, sink.TapCount);
        Assert.DoesNotContain(audit, row => row.Action == "telemetry-confirmed" || row.Action == "feedback-timeout-blocked");
        Assert.Equal("t13-main-boost", service.LastDecision!.RuleId);
    }

    [Theory]
    [InlineData("SSTA", 12)]
    [InlineData("FLBK", 4)]
    [InlineData("FLBK", 11)]
    public void OtherOrTruncatedEventsDoNotRearmRules(string code, int length)
    {
        var clock = new FlashbackClock();
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new FlashbackSink(), new());
        FeedFlashbackState(service, clock, 1, 3_500);
        SendFrame(service, clock, LapPacket(4_500), 2);
        var packet = Packet(3, length, 91, 0);
        System.Text.Encoding.ASCII.GetBytes(code).CopyTo(packet, F12026Parser.HeaderSize);
        SendFrame(service, clock, packet, 3);
        SendFrame(service, clock, LapPacket(3_500), 4);
        Assert.Equal("default", service.LastDecision!.RuleId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlashbackDoesNotClearEmergencyStopOrReleaseFailure(bool releaseFails)
    {
        var clock = new FlashbackClock();
        var sink = new FlashbackSink();
        using var service = FlashbackService(ErsAutopilotOperatingMode.Live, clock, sink, new());
        FeedFlashbackState(service, clock, 1, 3_500);
        if (releaseFails) sink.ReleaseFails = true;
        else
        {
            sink.EmergencyStop = true;
            SendFrame(service, clock, LapPacket(3_500), 2);
            sink.EmergencyStop = false;
        }
        SendFrame(service, clock, FlashbackPacket(), 100);
        FeedFlashbackState(service, clock, 101, 3_500);
        Assert.Equal(1, sink.TapCount);
        Assert.True(service.LastDecision!.Blocked);
        Assert.Contains(releaseFails ? "release failed" : "Emergency stop F12", service.Status.Detail);
    }

    [Fact]
    public void FlashbackClearsLearnedEnergyAndTacticalContextToMatchFreshController()
    {
        var profile = Assert.Single(ErsProfileStore.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ers")).Profiles);
        var clock = new FlashbackClock();
        using var rewound = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new FlashbackSink(), new(), profile);
        using var fresh = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new FlashbackSink(), new(), profile);
        FeedFlashbackState(rewound, clock, 1, 0);
        for (uint i = 1; i <= 20; i++)
        {
            clock.Now = clock.Now.AddMilliseconds(100);
            var lap = LapPacket(i * 250);
            // Changing gaps and battery train context that must not leak into the restored timeline.
            BinaryPrimitives.WriteUInt16LittleEndian(lap.AsSpan(F12026Parser.HeaderSize + 12), (ushort)(2500 - i * 80));
            SendFrame(rewound, clock, SessionPacket(true, 0), i + 1);
            SendFrame(rewound, clock, lap, i + 1);
            SendFrame(rewound, clock, TelemetryPacket(), i + 1);
            var status = StatusPacket(ErsDeployMode.Medium);
            BinaryPrimitives.WriteSingleLittleEndian(status.AsSpan(F12026Parser.HeaderSize + 37), 3_500_000 - i * 80_000);
            SendFrame(rewound, clock, status, i + 1);
        }
        SendFrame(rewound, clock, FlashbackPacket(), 100);
        FeedFlashbackState(rewound, clock, 101, 3_500);
        FeedFlashbackState(fresh, clock, 101, 3_500);
        Assert.Equal(fresh.LastDecision, rewound.LastDecision);
    }

    private static ErsAutopilotService FlashbackService(ErsAutopilotOperatingMode mode, FlashbackClock clock,
        FlashbackSink sink, List<ErsAuditRecord> audit, ErsControlProfile? profile = null) => new(
        new ErsAutopilotOptions { OperatingMode = mode, MinimumCommandIntervalMs = 0 },
        new ErsProfileLoadResult("", new[] { profile ?? ChinaProfile() }, Array.Empty<string>()),
        sink, audit.Add, timeProvider: clock);

    private static void FeedFlashbackState(ErsAutopilotService service, FlashbackClock clock, uint frame, float distance)
    {
        SendFrame(service, clock, SessionPacket(true, 0), frame);
        SendFrame(service, clock, LapPacket(distance), frame);
        SendFrame(service, clock, TelemetryPacket(), frame);
        SendFrame(service, clock, StatusPacket(ErsDeployMode.Medium), frame);
    }

    private static void SendFrame(ErsAutopilotService service, FlashbackClock clock, byte[] packet, uint overallFrame)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), overallFrame);
        service.ProcessPacket(packet, clock.Now);
    }

    private static byte[] FlashbackPacket()
    {
        var packet = Packet(3, 12, 91, 0);
        "FLBK"u8.CopyTo(packet.AsSpan(F12026Parser.HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(F12026Parser.HeaderSize + 4), 1);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(F12026Parser.HeaderSize + 8), 1);
        return packet;
    }

    private sealed class FlashbackClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FlashbackSink : IErsInputSink
    {
        public bool EmergencyStop { get; set; }
        public bool ReleaseFails { get; set; }
        public int TapCount { get; private set; }
        public int ImmediateReleaseCount { get; private set; }
        public ErsInputResult Tap(ErsInputDirection direction, ErsAutopilotOptions options, DateTimeOffset now)
        {
            TapCount++;
            return ErsInputResult.Ok("test-key");
        }
        public ErsInputResult? Poll(DateTimeOffset now)
        {
            if (now != DateTimeOffset.MaxValue) return null;
            ImmediateReleaseCount++;
            return ReleaseFails ? ErsInputResult.Error("release failed") : ErsInputResult.Ok("released");
        }
        public bool EmergencyStopRequested(ErsAutopilotOptions options) => EmergencyStop;
        public void Dispose() { }
    }
}
