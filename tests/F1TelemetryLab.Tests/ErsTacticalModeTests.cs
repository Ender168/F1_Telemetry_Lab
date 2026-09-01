using F1TelemetryLab;

namespace F1TelemetryLab.Tests;

public sealed class ErsTacticalModeTests
{
    [Fact]
    public void EngineDistinguishesNeutralAttackAndDefend()
    {
        var neutral = new ErsDecisionEngine(Profile()).Evaluate(State());
        var attack = new ErsDecisionEngine(Profile()).Evaluate(State(gapAhead: 800));
        var defend = new ErsDecisionEngine(Profile()).Evaluate(State(gapBehind: 700));

        Assert.Equal("neutral", neutral.RuleId);
        Assert.Contains("[NEUTRAL]", neutral.Reason);
        Assert.Equal("attack", attack.RuleId);
        Assert.Contains("[ATTACK]", attack.Reason);
        Assert.Equal("defend", defend.RuleId);
        Assert.Contains("[DEFEND]", defend.Reason);
    }

    [Fact]
    public void DefenceWinsTwoSidedBattleOnlyWhenRearThreatIsMateriallyCloser()
    {
        var attackWins = new ErsDecisionEngine(Profile()).Evaluate(State(gapAhead: 900, gapBehind: 800));
        var defendWins = new ErsDecisionEngine(Profile()).Evaluate(State(gapAhead: 1_100, gapBehind: 800));

        Assert.Equal("attack", attackWins.RuleId);
        Assert.Equal("defend", defendWins.RuleId);
    }

    [Fact]
    public void TacticalExitMarginPreventsBoundaryFlapping()
    {
        var engine = new ErsDecisionEngine(Profile());
        var enter = engine.Evaluate(State(gapAhead: 1_100));
        var heldByHysteresis = engine.Evaluate(State(gapAhead: 1_350) with { ReceivedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) });
        var exit = engine.Evaluate(State(gapAhead: 1_500) with { ReceivedAt = DateTimeOffset.UnixEpoch.AddSeconds(2) });

        Assert.Equal("attack", enter.RuleId);
        Assert.Equal("attack", heldByHysteresis.RuleId);
        Assert.Equal("neutral", exit.RuleId);
    }

    [Fact]
    public void LegacyBattleConditionStillWorks()
    {
        var profile = Profile();
        profile.Rules.Insert(0, new ErsControlRule
        {
            Id = "legacy",
            Segment = "legacy",
            Priority = 500,
            StartM = 0,
            EndM = 5_441,
            TargetMode = ErsDeployMode.Boost,
            Condition = ErsRuleCondition.Battle
        });

        var decision = new ErsDecisionEngine(profile).Evaluate(State(gapAhead: 1_100));

        Assert.Equal("legacy", decision.RuleId);
        Assert.Contains("[ATTACK]", decision.Reason);
    }

    private static ErsControlProfile Profile() => new()
    {
        ProfileId = "tactical-test",
        TrackId = 2,
        TrackLengthM = 5_441,
        SessionTypes = new List<int> { 15 },
        DefaultMode = ErsDeployMode.Medium,
        AttackGapMs = 1_200,
        DefendGapMs = 1_000,
        TacticalExitMarginMs = 250,
        DefendPriorityMarginMs = 200,
        BattleGapMs = 1_200,
        Rules = new List<ErsControlRule>
        {
            new()
            {
                Id = "attack",
                Segment = "attack",
                Priority = 300,
                StartM = 0,
                EndM = 5_441,
                TargetMode = ErsDeployMode.Boost,
                Condition = ErsRuleCondition.Attack
            },
            new()
            {
                Id = "defend",
                Segment = "defend",
                Priority = 300,
                StartM = 0,
                EndM = 5_441,
                TargetMode = ErsDeployMode.Hotlap,
                Condition = ErsRuleCondition.Defend
            },
            new()
            {
                Id = "neutral",
                Segment = "neutral",
                Priority = 100,
                StartM = 0,
                EndM = 5_441,
                TargetMode = ErsDeployMode.Medium,
                Condition = ErsRuleCondition.Neutral
            }
        }
    };

    private static ErsControlState State(int? gapAhead = null, int? gapBehind = null) => new(
        DateTimeOffset.UnixEpoch,
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
        2_000,
        0,
        4,
        2,
        220,
        90,
        60,
        ErsDeployMode.Medium,
        false,
        gapAhead,
        gapBehind,
        true,
        "");
}
