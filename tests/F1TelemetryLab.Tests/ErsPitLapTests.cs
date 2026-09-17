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
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new(), PitRuleProfile());
        FeedFlashbackState(service, clock, 1, 4_500);
        SendFrame(service, clock, ButtonsPacket(first), 2);
        Assert.False(service.PitLapStatus.Active);
        SendFrame(service, clock, ButtonsPacket(chord), 3);
        Assert.True(service.PitLapStatus.Active);
        Assert.Equal("json-pit-zone", service.LastDecision!.RuleId);
        SendFrame(service, clock, ButtonsPacket(chord), 4);
        SendFrame(service, clock, ButtonsPacket(2), 5);
        SendFrame(service, clock, ButtonsPacket(6), 6);
        Assert.True(service.PitLapStatus.Active);
        SendFrame(service, clock, ButtonsPacket(0), 7);
        Assert.False(service.PitLapStatus.ButtonsPressed);
        Assert.True(service.PitLapStatus.Active);
        SendFrame(service, clock, ButtonsPacket(6), 8);
        Assert.False(service.PitLapStatus.Active);
        Assert.NotEqual("json-pit-zone", service.LastDecision!.RuleId);
    }

    [Fact]
    public void PitLapLiveSendsIncreaseAndAuditsActivation()
    {
        var clock = new FlashbackClock();
        var sink = new FlashbackSink();
        var audit = new List<ErsAuditRecord>();
        using var service = FlashbackService(ErsAutopilotOperatingMode.Live, clock, sink, audit, PitRuleProfile());
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
    [InlineData(5, 85, 60, true)]
    [InlineData(70, 100, 220, true)]
    [InlineData(4, 100, 220, false)]
    [InlineData(70, 84, 220, false)]
    [InlineData(70, 100, 59, false)]
    public void PitLapUsesJsonThresholds(double battery, double throttle, int speed, bool matches)
    {
        var state = State(DateTimeOffset.UnixEpoch, 4_500, battery, throttle) with
        { PitLapBurn = true, SpeedKph = speed };
        var decision = new ErsDecisionEngine(PitRuleProfile()).Evaluate(state);
        Assert.Equal(matches, decision.RuleId == "json-pit-zone");
    }

    [Fact]
    public void FlagWithoutPitRulesDoesNotChangeStrategy()
    {
        var state = State(DateTimeOffset.UnixEpoch, 4_500, 70, 100);
        Assert.Equal(new ErsDecisionEngine(ChinaProfile()).Evaluate(state),
            new ErsDecisionEngine(ChinaProfile()).Evaluate(state with { PitLapBurn = true }));
    }

    private static ErsControlProfile JapanPitProfile()
    {
        var loaded = ErsProfileStore.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pit-ers"));
        Assert.Empty(loaded.Warnings);
        var profile = Assert.Single(loaded.Profiles);
        Assert.Same(profile, loaded.Find(13, 15));
        Assert.Equal(35, profile.Rules.Count);
        Assert.Equal(3, profile.Rules.Count(r => r.Condition == ErsRuleCondition.PitLapBurn));
        return profile;
    }

    [Theory]
    [InlineData(4_500, 8, "pit-lap-t14-t15-burn")]
    [InlineData(5_600, 5, "pit-lap-t18-t1-burn")]
    [InlineData(300, 5, "pit-lap-t18-t1-burn")]
    [InlineData(3_000, 10, "pit-lap-t11-burn")]
    public void SuppliedJapanRulesDeployBelowNormalReserve(double distance, double battery, string id)
    {
        var state = State(DateTimeOffset.UnixEpoch, distance, battery, 100) with { PitLapBurn = true };
        var profile = JapanPitProfile();
        var decision = new ErsDecisionEngine(profile).Evaluate(state);
        Assert.Equal(id, decision.RuleId);
        Assert.Equal(ErsDeployMode.Boost, decision.TargetMode);
        Assert.NotEqual(id, new ErsDecisionEngine(profile).Evaluate(state with { PitLapBurn = false }).RuleId);
        Assert.NotEqual(id, new ErsDecisionEngine(profile).Evaluate(state with { BatteryPct = battery - 1 }).RuleId);
    }

    [Fact]
    public void JapanPitRulesRespectZonesAndReleaseActiveRuleWhenFlagClears()
    {
        var engine = new ErsDecisionEngine(JapanPitProfile());
        var state = State(DateTimeOffset.UnixEpoch, 4_500, 50, 100) with { PitLapBurn = true };
        Assert.Equal("pit-lap-t14-t15-burn", engine.Evaluate(state).RuleId);
        Assert.NotEqual("pit-lap-t14-t15-burn", engine.Evaluate(state with
        { PitLapBurn = false, ReceivedAt = state.ReceivedAt.AddMilliseconds(100) }).RuleId);
        var outside = state with { LapDistanceM = 2_000 };
        Assert.Equal(new ErsDecisionEngine(JapanPitProfile()).Evaluate(outside with { PitLapBurn = false }),
            new ErsDecisionEngine(JapanPitProfile()).Evaluate(outside));
    }

    [Fact]
    public void PitRuleUsesJsonModePriorityBudgetAndOncePerLap()
    {
        var profile = PitRuleProfile();
        var rule = profile.Rules.Last();
        rule.TargetMode = ErsDeployMode.Hotlap;
        rule.OncePerLap = true;
        rule.MaximumDeployPct = 10;
        var engine = new ErsDecisionEngine(profile);
        var state = State(DateTimeOffset.UnixEpoch, 4_500, 70, 100) with { PitLapBurn = true };
        Assert.Equal(ErsDeployMode.Hotlap, engine.Evaluate(state).TargetMode);
        var spent = state with { BatteryPct = 59, ReceivedAt = state.ReceivedAt.AddSeconds(1) };
        Assert.NotEqual(rule.Id, engine.Evaluate(spent).RuleId);
        Assert.NotEqual(rule.Id, engine.Evaluate(state with { ReceivedAt = state.ReceivedAt.AddSeconds(2) }).RuleId);
        Assert.Equal(rule.Id, engine.Evaluate(state with
        { LapNumber = 6, ReceivedAt = state.ReceivedAt.AddSeconds(3) }).RuleId);
        rule.Priority = 1;
        Assert.Equal("critical", new ErsDecisionEngine(profile).Evaluate(state with { BatteryPct = 8 }).RuleId);
    }

    [Fact]
    public void PitRuleHonoursJsonTimerAndExplicitSurplusGate()
    {
        var profile = JapanPitProfile();
        var engine = new ErsDecisionEngine(profile);
        var state = State(DateTimeOffset.UnixEpoch, 4_500, 50, 100) with { PitLapBurn = true };
        Assert.Equal("pit-lap-t14-t15-burn", engine.Evaluate(state).RuleId);
        Assert.NotEqual("pit-lap-t14-t15-burn", engine.Evaluate(state with
        { ReceivedAt = state.ReceivedAt.AddSeconds(16) }).RuleId);
        profile.Rules[0].MinimumEnergySurplusPct = 100;
        Assert.NotEqual("pit-lap-t14-t15-burn", new ErsDecisionEngine(profile).Evaluate(state).RuleId);
    }

    private static ErsControlProfile PitRuleProfile()
    {
        var profile = ChinaProfile();
        profile.Rules.Add(new ErsControlRule
        {
            Id = "json-pit-zone", Segment = "JSON pit zone", Condition = ErsRuleCondition.PitLapBurn,
            StartM = 4_400, EndM = 4_600, Priority = 3_000, TargetMode = ErsDeployMode.Boost,
            MinimumBatteryPct = 5, MinimumThrottlePct = 85, MinimumSpeedKph = 60
        });
        return profile;
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
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new(), PitRuleProfile());
        FeedFlashbackState(service, clock, 1, 4_500);
        SendFrame(service, clock, ButtonsPacket(6), 2);
        var packet = LapPacket(4_500);
        packet[F12026Parser.HeaderSize + 33] = lap;
        packet[F12026Parser.HeaderSize + 34] = pit;
        packet[F12026Parser.HeaderSize + 45] = result;
        SendFrame(service, clock, packet, 3);
        Assert.False(service.PitLapStatus.Active);
        Assert.NotEqual("json-pit-zone", service.LastDecision!.RuleId);
    }

    [Theory]
    [InlineData("flashback")]
    [InlineData("session")]
    [InlineData("stop")]
    [InlineData("end")]
    public void PitLapCancelsOnResetAndStop(string reset)
    {
        var clock = new FlashbackClock();
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new(), PitRuleProfile());
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
        using var service = FlashbackService(ErsAutopilotOperatingMode.DryRun, clock, new(), new(), PitRuleProfile());
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
