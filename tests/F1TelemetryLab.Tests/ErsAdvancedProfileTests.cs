using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed class ErsAdvancedProfileTests
{
    [Fact]
    public void ShippedChinaProfileLoadsAsSchemaTwoReference()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ers");

        var loaded = ErsProfileStore.LoadFromDirectory(folder);

        Assert.Empty(loaded.Warnings);
        var profile = Assert.Single(loaded.Profiles);
        Assert.Equal(2, profile.SchemaVersion);
        Assert.Equal("china-race-advanced-v2", profile.ProfileId);
        Assert.NotNull(profile.Tactical);
        Assert.NotNull(profile.EnergyPlan);
        Assert.True(profile.EnergyPlan!.Checkpoints.Count >= 8);
        Assert.Contains(profile.Rules, rule => rule.Condition == ErsRuleCondition.FinalLap);
        Assert.Contains(profile.Rules, rule => rule.DrsRequirement == ErsDrsRequirement.Active);
    }

    [Fact]
    public void LowValueZoneRequiresExtraReserveAboveLocalMinimum()
    {
        var profile = AdvancedProfile();
        profile.Rules.Add(new ErsControlRule
        {
            Id = "low-value",
            Segment = "low-value",
            Priority = 100,
            StartM = 1_000,
            EndM = 2_000,
            TargetMode = ErsDeployMode.Boost,
            Condition = ErsRuleCondition.Always,
            DeploymentValue = 0.5
        });
        var blockedByBudget = new ErsDecisionEngine(profile).Evaluate(State(distance: 1_500, battery: 34));
        var allowed = new ErsDecisionEngine(profile).Evaluate(State(distance: 1_500, battery: 36));

        Assert.Equal("default", blockedByBudget.RuleId);
        Assert.Equal("low-value", allowed.RuleId);
    }

    [Fact]
    public void RapidClosingRateCreatesAttackPressureOutsideStaticGap()
    {
        var profile = AdvancedProfile();
        profile.Rules.Add(new ErsControlRule
        {
            Id = "attack-pressure",
            Segment = "attack-pressure",
            Priority = 100,
            StartM = 0,
            EndM = 5_441,
            TargetMode = ErsDeployMode.Hotlap,
            Condition = ErsRuleCondition.AttackPressure
        });
        var engine = new ErsDecisionEngine(profile);
        var start = DateTimeOffset.UnixEpoch;

        engine.Evaluate(State(at: start, gapAhead: 2_100));
        var decision = engine.Evaluate(State(at: start.AddSeconds(2), gapAhead: 1_850));

        Assert.Equal("attack-pressure", decision.RuleId);
        Assert.Contains("[ATTACK][PRESSURE]", decision.Reason);
        Assert.Contains("-125 ms/s", decision.Reason);
    }

    [Fact]
    public void CriticalGapEscalatesBeyondPressureRule()
    {
        var profile = AdvancedProfile();
        profile.Rules.AddRange(new[]
        {
            new ErsControlRule
            {
                Id = "critical",
                Segment = "critical",
                Priority = 200,
                StartM = 0,
                EndM = 5_441,
                TargetMode = ErsDeployMode.Boost,
                Condition = ErsRuleCondition.AttackCritical
            },
            new ErsControlRule
            {
                Id = "pressure",
                Segment = "pressure",
                Priority = 100,
                StartM = 0,
                EndM = 5_441,
                TargetMode = ErsDeployMode.Hotlap,
                Condition = ErsRuleCondition.AttackPressure
            }
        });

        var decision = new ErsDecisionEngine(profile).Evaluate(State(gapAhead: 800));

        Assert.Equal("critical", decision.RuleId);
        Assert.Contains("[ATTACK][CRITICAL]", decision.Reason);
    }

    [Fact]
    public void DrsRequirementSelectsTheMatchingTacticalRule()
    {
        var profile = AdvancedProfile();
        profile.Rules.AddRange(new[]
        {
            new ErsControlRule
            {
                Id = "no-drs",
                Segment = "no-drs",
                Priority = 100,
                StartM = 0,
                EndM = 5_441,
                TargetMode = ErsDeployMode.Boost,
                Condition = ErsRuleCondition.AttackCritical,
                DrsRequirement = ErsDrsRequirement.Inactive
            },
            new ErsControlRule
            {
                Id = "drs-late",
                Segment = "drs-late",
                Priority = 100,
                StartM = 0,
                EndM = 5_441,
                TargetMode = ErsDeployMode.Hotlap,
                Condition = ErsRuleCondition.AttackCritical,
                DrsRequirement = ErsDrsRequirement.Active
            }
        });

        var decision = new ErsDecisionEngine(profile).Evaluate(State(gapAhead: 700) with { DrsActive = true });

        Assert.Equal("drs-late", decision.RuleId);
        Assert.Contains("DRS active", decision.Reason);
    }

    [Fact]
    public void FinalLapRuleCanUseTheConfiguredReleaseFloor()
    {
        var profile = AdvancedProfile();
        profile.Rules.Add(new ErsControlRule
        {
            Id = "final-release",
            Segment = "final-release",
            Priority = 100,
            StartM = 0,
            EndM = 5_441,
            TargetMode = ErsDeployMode.Boost,
            Condition = ErsRuleCondition.FinalLap,
            MinimumBatteryPct = 40,
            FinalLapMinimumBatteryPct = 12,
            DeploymentValue = 1
        });
        var finalLap = State(battery: 14) with { TotalLaps = 14, LapNumber = 14 };
        var normalLap = State(battery: 14) with { TotalLaps = 14, LapNumber = 10 };

        var release = new ErsDecisionEngine(profile).Evaluate(finalLap);
        var conserve = new ErsDecisionEngine(profile).Evaluate(normalLap);

        Assert.Equal("final-release", release.RuleId);
        Assert.Equal("default", conserve.RuleId);
    }

    private static ErsControlProfile AdvancedProfile() => new()
    {
        SchemaVersion = 2,
        ProfileRevision = 1,
        ProfileId = "advanced-test",
        TrackId = 2,
        TrackLengthM = 5_441,
        SessionTypes = new List<int> { 15 },
        DefaultMode = ErsDeployMode.Medium,
        CriticalBatteryPct = 10,
        RecoveryEnterPct = 25,
        RecoveryExitPct = 35,
        HighBatteryPct = 65,
        Tactical = new ErsTacticalPlan
        {
            AttackPressureGapMs = 1_800,
            AttackCriticalGapMs = 900,
            DefendPressureGapMs = 1_500,
            DefendCriticalGapMs = 700,
            ClosingRateWindowMs = 3_000,
            RapidClosingRateMsPerSecond = -80,
            ClosingRateGapExtensionMs = 400
        },
        EnergyPlan = new ErsEnergyPlan
        {
            SurplusReleasePct = 8,
            LowValueReservePct = 10,
            ClosingLaps = 3,
            FinalLapRelease = true,
            FinalLapFloorPct = 10,
            Checkpoints = new List<ErsEnergyCheckpoint>
            {
                new() { Id = "start", DistanceM = 0, TargetPct = 40, MinimumPct = 30 },
                new() { Id = "half", DistanceM = 2_720, TargetPct = 40, MinimumPct = 30 }
            }
        }
    };

    private static ErsControlState State(
        DateTimeOffset? at = null,
        double distance = 1_500,
        double battery = 60,
        int? gapAhead = null,
        int? gapBehind = null) => new(
        at ?? DateTimeOffset.UnixEpoch,
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
        100,
        battery,
        ErsDeployMode.Medium,
        false,
        gapAhead,
        gapBehind,
        true,
        "");
}
