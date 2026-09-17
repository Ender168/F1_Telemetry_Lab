using System.Buffers.Binary;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed partial class ErsAutopilotTests
{
    [Theory]
    [InlineData(2u, 6u)]
    [InlineData(4u, 6u)]
    [InlineData(0u, 14u)] // Other held buttons do not prevent the chord.
    public void PitChordTogglesOnceAndRequiresBothButtonsReleased(uint first, uint chord)
    {
        var clock = new FlashbackClock();
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new());
        FeedFlashbackState(service, clock, 1, 4_500);
        SendFrame(service, clock, ButtonsPacket(first), 2);
        Assert.False(service.PitLapStatus.Active);
        SendFrame(service, clock, ButtonsPacket(chord), 3);
        Assert.True(service.PitLapStatus.Active);
        Assert.Equal("pit-lap-burn", service.LastDecision!.RuleId);
        SendFrame(service, clock, ButtonsPacket(chord), 4);
        SendFrame(service, clock, ButtonsPacket(2), 5);
        SendFrame(service, clock, ButtonsPacket(6), 6);
        Assert.True(service.PitLapStatus.Active);
        SendFrame(service, clock, ButtonsPacket(0), 7);
        Assert.False(service.PitLapStatus.ButtonsPressed);
        Assert.True(service.PitLapStatus.Active);
        SendFrame(service, clock, ButtonsPacket(6), 8);
        Assert.False(service.PitLapStatus.Active);
        Assert.NotEqual("pit-lap-burn", service.LastDecision!.RuleId);
    }

    [Fact]
    public void PitLapLiveSendsIncreaseAndAuditsActivation()
    {
        var clock = new FlashbackClock();
        var sink = new FlashbackSink();
        var audit = new List<ErsAuditRecord>();
        using var service = FlashbackService(ErsAutopilotOperatingMode.Live, clock, sink, audit);
        FeedFlashbackState(service, clock, 1, 4_500);
        Assert.Equal(0, sink.TapCount);
        SendFrame(service, clock, ButtonsPacket(6), 2);
        Assert.Equal(1, sink.TapCount);
        Assert.Equal(ErsDeployMode.Boost, service.LastDecision!.TargetMode);
        Assert.Equal("Key sent", service.PitLapStatus.AutomationState);
        Assert.Single(audit, row => row.Action == "pit-lap-on");
        SendFrame(service, clock, ButtonsPacket(0), 3);
        SendFrame(service, clock, ButtonsPacket(6), 4);
        Assert.Single(audit, row => row.Action == "pit-lap-off");
        Assert.True(sink.ImmediateReleaseCount > 0);
    }

    [Theory]
    [InlineData(5, 100, 0, 220, ErsDeployMode.Boost)]
    [InlineData(70, 100, 0, 220, ErsDeployMode.Boost)]
    [InlineData(0, 100, 0, 220, ErsDeployMode.None)]
    [InlineData(70, 50, 0, 220, ErsDeployMode.None)]
    [InlineData(70, 100, 20, 220, ErsDeployMode.None)]
    [InlineData(70, 100, 0, 40, ErsDeployMode.None)]
    public void PitLapSpendsBelowNormalReserveOnlyUnderAcceleration(double battery, double throttle,
        double brake, int speed, ErsDeployMode expected)
    {
        var state = State(DateTimeOffset.UnixEpoch, distance: 4_500, battery: battery, throttle: throttle) with
        { PitLapBurn = true, BrakePct = brake, SpeedKph = speed };
        var decision = new ErsDecisionEngine(ChinaProfile()).Evaluate(state);
        Assert.Equal(expected, decision.TargetMode);
        Assert.Equal(0, decision.EnergyMinimumPct);
        Assert.Equal("pit-lap-burn", decision.RuleId);
    }

    [Fact]
    public void PitLapNeverOverridesBlockedAutomation()
    {
        var state = State(DateTimeOffset.UnixEpoch, distance: 4_500, battery: 70) with
        { PitLapBurn = true, AutomationAllowed = false, BlockReason = "Game is paused." };
        var decision = new ErsDecisionEngine(ChinaProfile()).Evaluate(state);
        Assert.True(decision.Blocked);
        Assert.Equal(decision.CurrentMode, decision.TargetMode);
    }

    [Theory]
    [InlineData(1, 1, 2)] // Pit entry
    [InlineData(2, 0, 2)] // Next lap, missed the pit entry
    [InlineData(1, 0, 3)] // Finished
    public void PitLapCancelsWhenInLapEnds(byte lap, byte pit, byte result)
    {
        var clock = new FlashbackClock();
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new());
        FeedFlashbackState(service, clock, 1, 4_500);
        SendFrame(service, clock, ButtonsPacket(6), 2);
        var packet = LapPacket(4_500);
        packet[F12026Parser.HeaderSize + 33] = lap;
        packet[F12026Parser.HeaderSize + 34] = pit;
        packet[F12026Parser.HeaderSize + 45] = result;
        SendFrame(service, clock, packet, 3);
        Assert.False(service.PitLapStatus.Active);
        Assert.NotEqual("pit-lap-burn", service.LastDecision!.RuleId);
    }

    [Theory]
    [InlineData("flashback")]
    [InlineData("session")]
    [InlineData("stop")]
    [InlineData("end")]
    public void PitLapCancelsOnResetAndStop(string reset)
    {
        var clock = new FlashbackClock();
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new());
        FeedFlashbackState(service, clock, 1, 4_500);
        SendFrame(service, clock, ButtonsPacket(6), 2);
        Assert.True(service.PitLapStatus.Active);
        if (reset == "stop") service.StopInput();
        else if (reset == "flashback") SendFrame(service, clock, FlashbackPacket(), 3);
        else if (reset == "end")
        {
            var packet = Packet(3, 4, 91, 0);
            "SEND"u8.CopyTo(packet.AsSpan(F12026Parser.HeaderSize));
            SendFrame(service, clock, packet, 3);
        }
        else
        {
            var packet = SessionPacket(true, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 92);
            SendFrame(service, clock, packet, 3);
        }
        Assert.False(service.PitLapStatus.Active);
    }

    [Fact]
    public void StaleForeignTruncatedAndOutOfOrderButtonEventsCannotTogglePitLap()
    {
        var clock = new FlashbackClock();
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new());
        FeedFlashbackState(service, clock, 1, 4_500);
        SendFrame(service, clock, ButtonsPacket(0), 10);
        SendFrame(service, clock, ButtonsPacket(6), 9);
        SendFrame(service, clock, ButtonsPacket(6), 10);
        var foreign = ButtonsPacket(6); foreign[27] = 1;
        SendFrame(service, clock, foreign, 11);
        SendFrame(service, clock, ButtonsPacket(6)[..^1], 12);
        Assert.False(service.PitLapStatus.Active);
        clock.Now = clock.Now.AddSeconds(5);
        SendFrame(service, clock, ButtonsPacket(6), 13); // Lap/session data now stale.
        Assert.False(service.PitLapStatus.Active);
    }

    [Fact]
    public void ButtonParserUsesF1ProtocolFlagsAndRecordsRawMask()
    {
        var packet = ButtonsPacket(6);
        Assert.True(F12026Parser.TryParseButtonStatus(packet, out var mask));
        Assert.Equal(6u, mask);
        Assert.False(F12026Parser.TryParseButtonStatus(packet[..^1], out _));
        var evt = F12026Parser.ParseEventPacket(packet, DateTimeOffset.UnixEpoch);
        Assert.Contains("\"button_status\":6", evt!.DetailsJson);
    }

    [Fact]
    public void PitOverlayDistinguishesLatchedModePhysicalChordDryRunAndStaleData()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new PitLapErsStatus(true, 15, false, ErsAutopilotOperatingMode.Live, "Key sent", now, "");
        var text = status.Format(true, now);
        Assert.Contains("ВКЛ · круг 15", text);
        Assert.Contains("не нажато", text);
        Assert.Contains("LIVE", text);
        Assert.Contains("DRY-RUN", (status with { Mode = ErsAutopilotOperatingMode.DryRun }).Format(false, now));
        Assert.Contains("Нет свежей телеметрии", status.Format(true, now.AddSeconds(5)));
        Assert.Contains("ERS: заблокирован", (status with { AutomationState = "Blocked" }).Format(true, now));
        Assert.Contains(OverlayLayoutService.Default(1920, 1080).Widgets, w => w.Id == "ers-pit-lap" && w.Visible);
    }

    private static byte[] ButtonsPacket(uint buttons)
    {
        var packet = Packet(3, 8, 91, 0);
        "BUTN"u8.CopyTo(packet.AsSpan(F12026Parser.HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(F12026Parser.HeaderSize + 4), buttons);
        return packet;
    }
}
