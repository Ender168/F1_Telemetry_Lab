using System.Buffers.Binary;
using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed class ErsAutopilotTests
{
    [Fact]
    public void SessionControlParserReadsOfflinePauseAndSafetyFlagsFrom2026Layout()
    {
        var packet = Packet(1, 126, sessionUid: 77, playerCarIndex: 4);
        var payload = packet.AsSpan(F12026Parser.HeaderSize);
        payload[0] = 2;
        payload[3] = 28;
        BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], 5_441);
        payload[6] = 15;
        payload[7] = 2;
        payload[8] = 13;
        payload[14] = 1;
        payload[15] = 0;
        payload[124] = 2;
        payload[125] = 0;

        var session = F12026Parser.TryParseSessionControl(packet, DateTimeOffset.UnixEpoch);

        Assert.NotNull(session);
        Assert.Equal((77UL, 2, 15, 5_441, 28),
            (session!.SessionUid, session.TrackId, session.SessionType, session.TrackLengthM, session.TotalLaps));
        Assert.Equal((2, 13, true, false, 2, false),
            (session.Weather, session.Formula, session.GamePaused, session.IsSpectating, session.SafetyCarStatus, session.IsNetworkGame));
        Assert.Equal(-1, session.ErsAssist);
    }

    [Fact]
    public void SessionControlParserReads2026ErsAssistFlag()
    {
        var packet = Packet(1, 662, sessionUid: 78, playerCarIndex: 0);
        var payload = packet.AsSpan(F12026Parser.HeaderSize);
        payload[6] = 15;
        payload[7] = 2;
        payload[661] = 1;

        var session = F12026Parser.TryParseSessionControl(packet, DateTimeOffset.UnixEpoch);

        Assert.NotNull(session);
        Assert.Equal(1, session!.ErsAssist);
    }

    [Fact]
    public void CarStatusParserReadsNetworkPausedFlag()
    {
        const int rowSize = 59;
        var packet = Packet(7, rowSize * F12026Parser.MaxCars2026, sessionUid: 9, playerCarIndex: 0);
        packet[F12026Parser.HeaderSize + 58] = 1;

        var player = F12026Parser.ParseCarStatusPacket(packet, DateTimeOffset.UnixEpoch).Single(row => row.IsPlayer);

        Assert.True(player.NetworkPaused);
    }

    [Fact]
    public void ChinaMainBoostRunsOnceAndStopsAfterConfiguredDuration()
    {
        var profile = ChinaProfile();
        var engine = new ErsDecisionEngine(profile);
        var start = DateTimeOffset.UnixEpoch;

        var first = engine.Evaluate(State(start, distance: 3_500, battery: 55, throttle: 100));
        var during = engine.Evaluate(State(start.AddSeconds(5), distance: 4_000, battery: 48, throttle: 100));
        var expired = engine.Evaluate(State(start.AddSeconds(6.1), distance: 4_100, battery: 46, throttle: 100));

        Assert.Equal(("t13-main-boost", ErsDeployMode.Boost), (first.RuleId, first.TargetMode));
        Assert.Equal(ErsDeployMode.Boost, during.TargetMode);
        Assert.Equal(("default", ErsDeployMode.Medium), (expired.RuleId, expired.TargetMode));
    }

    [Fact]
    public void LowBatteryUsesRecoveryZonesUntilExitThreshold()
    {
        var engine = new ErsDecisionEngine(ChinaProfile());
        var start = DateTimeOffset.UnixEpoch;

        var enter = engine.Evaluate(State(start, distance: 2_000, battery: 34));
        var hysteresis = engine.Evaluate(State(start.AddSeconds(1), distance: 2_100, battery: 42));
        var exit = engine.Evaluate(State(start.AddSeconds(2), distance: 2_200, battery: 46));

        Assert.Equal(("technical-recovery", ErsDeployMode.None), (enter.RuleId, enter.TargetMode));
        Assert.Equal(ErsDeployMode.None, hysteresis.TargetMode);
        Assert.Equal(ErsDeployMode.Medium, exit.TargetMode);
    }

    [Fact]
    public void CriticalBatteryRulePreemptsAnActiveLowerPriorityRule()
    {
        var engine = new ErsDecisionEngine(ChinaProfile());
        var start = DateTimeOffset.UnixEpoch;

        var recovery = engine.Evaluate(State(start, distance: 2_000, battery: 34));
        var critical = engine.Evaluate(State(start.AddSeconds(1), distance: 2_050, battery: 10));

        Assert.Equal("technical-recovery", recovery.RuleId);
        Assert.Equal("critical", critical.RuleId);
    }

    [Fact]
    public void OptionalStartFinishBoostRequiresBattleOrHighBattery()
    {
        var start = DateTimeOffset.UnixEpoch;
        var normal = new ErsDecisionEngine(ChinaProfile()).Evaluate(State(start, distance: 5_350, battery: 55, throttle: 100));
        var high = new ErsDecisionEngine(ChinaProfile()).Evaluate(State(start, distance: 5_350, battery: 70, throttle: 100));
        var battle = new ErsDecisionEngine(ChinaProfile()).Evaluate(State(start, distance: 5_350, battery: 50, throttle: 100, gapAhead: 800));

        Assert.Equal(ErsDeployMode.Medium, normal.TargetMode);
        Assert.Equal(("start-finish-optional", ErsDeployMode.Boost), (high.RuleId, high.TargetMode));
        Assert.Equal(ErsDeployMode.Boost, battle.TargetMode);
    }

    [Fact]
    public void StartFinishBoostIsOneActivationAcrossLapCounterChange()
    {
        var profile = ChinaProfile();
        var engine = new ErsDecisionEngine(profile);
        var start = DateTimeOffset.UnixEpoch;

        var beforeLine = engine.Evaluate(State(start, distance: 5_350, battery: 70, throttle: 100));
        var afterLine = engine.Evaluate(State(start.AddSeconds(1), distance: 100, battery: 69, throttle: 100) with { LapNumber = 6 });
        var expired = engine.Evaluate(State(start.AddSeconds(2.6), distance: 250, battery: 68, throttle: 100) with { LapNumber = 6 });

        Assert.Equal("start-finish-optional", beforeLine.RuleId);
        Assert.Equal("start-finish-optional", afterLine.RuleId);
        Assert.Equal(("default", ErsDeployMode.Medium), (expired.RuleId, expired.TargetMode));
    }

    [Fact]
    public void BlockedStateNeverRequestsAKeyModeChange()
    {
        var state = State(DateTimeOffset.UnixEpoch, distance: 3_500, battery: 70, throttle: 100) with
        {
            AutomationAllowed = false,
            BlockReason = "Online session detected."
        };

        var decision = new ErsDecisionEngine(ChinaProfile()).Evaluate(state);

        Assert.True(decision.Blocked);
        Assert.Equal(decision.CurrentMode, decision.TargetMode);
        Assert.Contains("Online", decision.Reason);
    }

    [Fact]
    public void LiveServiceHardBlocksOnlineSessionsBeforeInput()
    {
        WithService(new FakeInputSink(), (service, sink) =>
        {
            service.ProcessPacket(SessionPacket(isNetworkGame: true, ersAssist: 0), DateTimeOffset.UtcNow);

            Assert.Equal("Blocked", service.Status.State);
            Assert.Contains("Online", service.Status.Detail);
            Assert.Equal(0, sink.TapCount);
        });
    }

    [Fact]
    public void LiveServiceRequiresInGameErsAssistOff()
    {
        WithService(new FakeInputSink(), (service, sink) =>
        {
            service.ProcessPacket(SessionPacket(isNetworkGame: false, ersAssist: 1), DateTimeOffset.UtcNow);

            Assert.Equal("Blocked", service.Status.State);
            Assert.Contains("ERS Assist off", service.Status.Detail);
            Assert.Equal(0, sink.TapCount);
        });
    }

    [Fact]
    public void EmergencyStopRemainsLatchedForTheRecording()
    {
        var input = new FakeInputSink { EmergencyStop = true };
        WithService(input, (service, sink) =>
        {
            service.ProcessPacket(SessionPacket(isNetworkGame: false, ersAssist: 0), DateTimeOffset.UtcNow);
            Assert.Equal("Emergency stop", service.Status.State);

            sink.EmergencyStop = false;
            service.ProcessPacket(SessionPacket(isNetworkGame: false, ersAssist: 0), DateTimeOffset.UtcNow);

            Assert.Equal("Blocked", service.Status.State);
            Assert.Contains("Emergency stop F12", service.Status.Detail);
            Assert.Equal(0, sink.TapCount);
        });
    }

    [Fact]
    public void ModeTransitionMovesOneObservedStepAtATime()
    {
        Assert.Equal(ErsInputDirection.Increase, ErsModeTransition.Next(ErsDeployMode.Medium, ErsDeployMode.Boost));
        Assert.Equal(ErsDeployMode.Hotlap, ErsModeTransition.ExpectedAfter(ErsDeployMode.Medium, ErsInputDirection.Increase));
        Assert.Equal(ErsInputDirection.Decrease, ErsModeTransition.Next(ErsDeployMode.Boost, ErsDeployMode.None));
        Assert.Null(ErsModeTransition.Next(ErsDeployMode.Medium, ErsDeployMode.Medium));
    }

    [Fact]
    public void ExpiredRuleDoesNotFalselyConfirmAnUnobservedModeChange()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"f1tlab-ers-feedback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var input = new FakeInputSink();
            var audit = new List<ErsAuditRecord>();
            var profiles = new ErsProfileLoadResult(folder, new[] { ChinaProfile() }, Array.Empty<string>());
            var start = DateTimeOffset.UtcNow;
            using (var service = new ErsAutopilotService(
                       new ErsAutopilotOptions { OperatingMode = ErsAutopilotOperatingMode.Live },
                       profiles,
                       input,
                       audit.Add))
            {
                service.ProcessPacket(SessionPacket(isNetworkGame: false, ersAssist: 0), start);
                service.ProcessPacket(LapPacket(distance: 3_500), start.AddMilliseconds(10));
                service.ProcessPacket(TelemetryPacket(), start.AddMilliseconds(20));
                service.ProcessPacket(StatusPacket(ErsDeployMode.Medium), start.AddMilliseconds(30));

                Assert.Equal(1, input.TapCount);
                Assert.Equal("Key sent", service.Status.State);

                service.ProcessPacket(LapPacket(distance: 4_500), start.AddMilliseconds(100));

                Assert.Equal("Holding", service.Status.State);
                Assert.Equal(ErsDeployMode.Medium, service.Status.CurrentMode);
            }

            Assert.Contains(audit, row => row.Action.Contains("feedback-superseded:expected-Hotlap", StringComparison.Ordinal));
            Assert.DoesNotContain(audit, row => row.Action.Equals("telemetry-confirmed", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ProfileStoreLoadsStringEnumsAndSelectsTrackSpecificProfile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"f1tlab-ers-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "China.json"), """
            {
              "schema_version": 1,
              "profile_id": "china-test",
              "display_name": "China test",
              "track_id": 2,
              "track_name": "China",
              "track_length_m": 5441,
              "session_types": [15],
              "default_mode": "medium",
              "battery_capacity_j": 4000000,
              "critical_battery_pct": 12,
              "recovery_enter_pct": 35,
              "recovery_exit_pct": 45,
              "high_battery_pct": 65,
              "battle_gap_ms": 1200,
              "rules": [
                {
                  "id": "main",
                  "segment": "T13 -> T14",
                  "priority": 10,
                  "start_m": 3420,
                  "end_m": 4300,
                  "target_mode": "boost",
                  "condition": "always"
                }
              ]
            }
            """);

            var loaded = ErsProfileStore.LoadFromDirectory(folder);

            Assert.Empty(loaded.Warnings);
            var profile = Assert.Single(loaded.Profiles);
            Assert.Same(profile, loaded.Find(2, 15));
            Assert.Equal(ErsDeployMode.Boost, Assert.Single(profile.Rules).TargetMode);
            Assert.Null(loaded.Find(0, 15));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ShippedChinaProfileIsValidAndRaceSpecific()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ers");

        var loaded = ErsProfileStore.LoadFromDirectory(folder);

        Assert.Empty(loaded.Warnings);
        var profile = Assert.Single(loaded.Profiles);
        Assert.Equal("china-race-advanced-v2", profile.ProfileId);
        Assert.Equal(2, profile.TrackId);
        Assert.Equal(new[] { 15, 16, 17 }, profile.SessionTypes);
        Assert.Contains(profile.Rules, rule => rule.Id == "t13-attack-critical-no-drs" && rule.TargetMode == ErsDeployMode.Boost);
    }

    private static ErsControlProfile ChinaProfile() => new()
    {
        ProfileId = "china-test",
        DisplayName = "China test",
        TrackId = 2,
        TrackName = "China",
        TrackLengthM = 5_441,
        SessionTypes = new List<int> { 15 },
        DefaultMode = ErsDeployMode.Medium,
        CriticalBatteryPct = 12,
        RecoveryEnterPct = 35,
        RecoveryExitPct = 45,
        HighBatteryPct = 65,
        BattleGapMs = 1_200,
        Rules = new List<ErsControlRule>
        {
            new()
            {
                Id = "critical",
                Segment = "Any segment",
                Priority = 1_000,
                StartM = 0,
                EndM = 5_441,
                TargetMode = ErsDeployMode.None,
                Condition = ErsRuleCondition.CriticalBattery
            },
            new()
            {
                Id = "t13-main-boost",
                Segment = "T13 -> T14",
                Priority = 900,
                StartM = 3_420,
                EndM = 4_350,
                TargetMode = ErsDeployMode.Boost,
                Condition = ErsRuleCondition.Always,
                MinimumBatteryPct = 45,
                MinimumThrottlePct = 85,
                MaximumActiveMs = 6_000,
                OncePerLap = true
            },
            new()
            {
                Id = "start-finish-optional",
                Segment = "T16 -> T1",
                Priority = 700,
                StartM = 5_260,
                EndM = 520,
                TargetMode = ErsDeployMode.Boost,
                Condition = ErsRuleCondition.BattleOrHighBattery,
                MinimumBatteryPct = 45,
                MinimumThrottlePct = 90,
                MaximumActiveMs = 2_500,
                OncePerLap = true
            },
            new()
            {
                Id = "technical-recovery",
                Segment = "T6 -> T10",
                Priority = 500,
                StartM = 1_570,
                EndM = 3_000,
                TargetMode = ErsDeployMode.None,
                Condition = ErsRuleCondition.LowBattery
            }
        }
    };

    private static ErsControlState State(
        DateTimeOffset at,
        double distance,
        double battery,
        double throttle = 70,
        int? gapAhead = null,
        int? gapBehind = null) => new(
        at,
        1,
        2,
        15,
        5_441,
        0,
        false,
        false,
        0,
        false,
        5,
        distance,
        0,
        4,
        2,
        220,
        throttle,
        battery,
        ErsDeployMode.Medium,
        false,
        gapAhead,
        gapBehind,
        true,
        "");

    private static byte[] SessionPacket(bool isNetworkGame, byte ersAssist)
    {
        var packet = Packet(1, 662, sessionUid: 91, playerCarIndex: 0);
        var payload = packet.AsSpan(F12026Parser.HeaderSize);
        payload[3] = 28;
        BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], 5_441);
        payload[6] = 15;
        payload[7] = 2;
        payload[124] = 0;
        payload[125] = isNetworkGame ? (byte)1 : (byte)0;
        payload[661] = ersAssist;
        return packet;
    }

    private static byte[] LapPacket(float distance)
    {
        const int rowSize = 57;
        var packet = Packet(2, rowSize * F12026Parser.MaxCars2026 + 2, sessionUid: 91, playerCarIndex: 0);
        var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
        BinaryPrimitives.WriteSingleLittleEndian(row[20..], distance);
        BinaryPrimitives.WriteSingleLittleEndian(row[24..], distance);
        row[32] = 1;
        row[33] = 1;
        row[44] = 4;
        row[45] = 2;
        return packet;
    }

    private static byte[] TelemetryPacket()
    {
        const int rowSize = 59;
        var packet = Packet(6, rowSize * F12026Parser.MaxCars2026 + 3, sessionUid: 91, playerCarIndex: 0);
        var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
        BinaryPrimitives.WriteUInt16LittleEndian(row, 220);
        BinaryPrimitives.WriteSingleLittleEndian(row[2..], 1f);
        row[15] = 6;
        BinaryPrimitives.WriteUInt16LittleEndian(row[16..], 11_000);
        return packet;
    }

    private static byte[] StatusPacket(ErsDeployMode mode)
    {
        const int rowSize = 59;
        var packet = Packet(7, rowSize * F12026Parser.MaxCars2026, sessionUid: 91, playerCarIndex: 0);
        var row = packet.AsSpan(F12026Parser.HeaderSize, rowSize);
        BinaryPrimitives.WriteSingleLittleEndian(row[37..], 3_500_000f);
        row[41] = (byte)mode;
        return packet;
    }

    private static void WithService(FakeInputSink input, Action<ErsAutopilotService, FakeInputSink> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"f1tlab-ers-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var profiles = new ErsProfileLoadResult(folder, new[] { ChinaProfile() }, Array.Empty<string>());
            using var service = new ErsAutopilotService(
                new ErsAutopilotOptions { OperatingMode = ErsAutopilotOperatingMode.Live },
                profiles,
                input);
            test(service, input);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class FakeInputSink : IErsInputSink
    {
        public bool EmergencyStop { get; set; }
        public int TapCount { get; private set; }

        public ErsInputResult Tap(ErsInputDirection direction, ErsAutopilotOptions options, DateTimeOffset now)
        {
            TapCount++;
            return ErsInputResult.Ok(direction.ToString());
        }

        public ErsInputResult? Poll(DateTimeOffset now) => null;

        public bool EmergencyStopRequested(ErsAutopilotOptions options) => EmergencyStop;

        public void Dispose() { }
    }

    private static byte[] Packet(byte packetId, int payloadSize, ulong sessionUid, byte playerCarIndex)
    {
        var packet = new byte[F12026Parser.HeaderSize + payloadSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 2026);
        packet[2] = 26;
        packet[5] = 1;
        packet[6] = packetId;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), sessionUid);
        packet[27] = playerCarIndex;
        packet[28] = 255;
        return packet;
    }
}
