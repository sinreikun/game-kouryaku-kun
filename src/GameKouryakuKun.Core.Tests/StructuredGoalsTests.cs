using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Goals;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class StructuredGoalsTests : IDisposable
{
    public StructuredGoalsTests()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.StructuredGoalsEnvName, null);
        RuntimeFlags.UseStructuredGoals = false;
        RuntimeFlags.ApplyStructuredGoalsEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.StructuredGoalsEnvName, null);
        RuntimeFlags.UseStructuredGoals = false;
        RuntimeFlags.ApplyStructuredGoalsEffective();
    }

    [Fact]
    public void Bridge_maps_reach_and_retest_to_predicates()
    {
        var reach = new SelfGoal
        {
            Id = 1, Kind = GoalKind.Reach, Target = "n1", Origin = "test",
            UncBefore = 0.8, PlanConfidence = 0.5, Budget = 3,
            Subgoals = new List<string> { "a", "b" },
        };
        var og = ObservationGoalBridge.FromSelfGoal(reach);
        Assert.Equal(ObservationPredicateIds.FrontierReach, og.Predicate.Id);
        Assert.Equal("scored", og.Predicate.Status);
        Assert.Equal("reach", og.LegacyKind);
        Assert.True(og.ExpectedValue.Combined > 0);
        Assert.DoesNotContain("ボス", og.SummaryLine());
        Assert.DoesNotContain("HP", og.SummaryLine());

        var retest = new SelfGoal
        {
            Id = 2, Kind = GoalKind.Retest, Target = "n1", TargetAction = "button1",
            Origin = "test", UncBefore = 0.9, Budget = 1,
        };
        var og2 = ObservationGoalBridge.FromSelfGoal(retest);
        Assert.Equal(ObservationPredicateIds.HighUncertaintyRetest, og2.Predicate.Id);
        Assert.Equal("button1", og2.Predicate.Action);
    }

    [Fact]
    public void Formal_stubs_not_enough_evidence()
    {
        var stub = ObservationGoalBridge.StubFormal(ObservationPredicateIds.TrackRelationReproduce);
        Assert.Equal("not_enough_evidence", stub.Predicate.Status);
        Assert.Contains("track_relation", stub.Predicate.Id);
        var stub2 = ObservationGoalBridge.StubFormal(ObservationPredicateIds.NumberSeriesChangeTest);
        Assert.Equal(ObservationPredicateIds.NumberSeriesChangeTest, stub2.Predicate.Id);
    }

    [Fact]
    public void SkillMiner_promotes_sequence_with_pre_post()
    {
        var miner = new SkillMiner();
        var seq = new ActionSequenceCandidate
        {
            Id = 7,
            Steps = new[] { "button1", "dpad_up" },
            Start = "s0",
            End = "s1",
            Reproducibility = 0.8,
            Origin = "stream",
            Status = SequenceStatus.Active,
        };
        var skill = miner.PromoteFromSequence(seq);
        Assert.NotNull(skill);
        Assert.Equal(2, skill!.Body.Count);
        Assert.Equal("s0", skill.PreCondition!.CoarseNode);
        Assert.Equal("s1", skill.PostCondition!.CoarseNode);
        Assert.True(skill.EffectConfidence >= SkillMiner.MinEffectConfidence);
        Assert.Equal("sequence", skill.Origin);
        Assert.Equal(7, skill.SequenceId);
        Assert.DoesNotContain("模倣", skill.Note ?? "");
    }

    [Fact]
    public void SkillMiner_rejects_low_confidence_stream()
    {
        var miner = new SkillMiner();
        var seq = new ActionSequenceCandidate
        {
            Id = 1, Steps = new[] { "a", "b" }, Start = "x", End = "y",
            Reproducibility = 0.2, Origin = "stream", Status = SequenceStatus.Active,
        };
        Assert.Null(miner.PromoteFromSequence(seq));
    }

    [Fact]
    public void Planner_expands_skill_macro_and_stubs_wm()
    {
        var miner = new SkillMiner();
        var skill = miner.PromoteFromSequence(new ActionSequenceCandidate
        {
            Id = 3, Steps = new[] { "button1", "button2" }, Start = "here", End = "there",
            Reproducibility = 0.9, Origin = "goal", Status = SequenceStatus.Active,
        })!;
        var goal = ObservationGoalBridge.FromSelfGoal(new SelfGoal
        {
            Id = 9, Kind = GoalKind.Reach, Target = "there", Origin = "t",
            UncBefore = 0.7, PlanConfidence = 0.6, Budget = 4,
        });
        var plan = StructuredPlanner.Propose(goal, miner.Skills, "here", legacyNextAction: "dpad_left");
        Assert.Equal("soft_adopt_available", plan.Status);
        Assert.Equal("button1", plan.FirstAction);
        Assert.Equal(skill.Id, plan.ExpandedSkillId);
        Assert.False(plan.WmLookaheadUsed);
        Assert.Contains("wm_lookahead=not_implemented", plan.Note!);
        Assert.Contains("no_supervisor_glue", plan.Note!);

        var mirror = StructuredPlanner.Propose(goal, Array.Empty<SkillRecord>(), "here", "wait");
        Assert.Equal("parallel_log", mirror.Status);
        Assert.Equal("wait", mirror.FirstAction);
    }

    [Fact]
    public void Flag_default_off()
    {
        RuntimeFlags.ApplyStructuredGoalsEffective();
        Assert.False(RuntimeFlags.UseStructuredGoals);
        Environment.SetEnvironmentVariable(RuntimeFlags.StructuredGoalsEnvName, "1");
        RuntimeFlags.ApplyStructuredGoalsEffective();
        Assert.True(RuntimeFlags.UseStructuredGoals);
        Assert.Contains("ON", RuntimeFlags.StructuredGoalsStatusText());
    }

    [Fact]
    public void Health_parses_structured_goal_logs()
    {
        using var d = new TempDir();
        var logs = d["logs"];
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "app-1.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"structured_goal","summary":"obs_goal id=1 pred=frontier_reach:scored value=0.50 legacy=reach:n1 status=parallel_log","pred":"frontier_reach"}""",
            """{"type":"skill_record","summary":"skill #1 Skill #1 body=2 conf=0.80","status":"active","origin":"sequence"}""",
            """{"type":"plan_propose","summary":"plan status=soft_adopt_available first=button1","status":"soft_adopt_available","soft_adopt":true}""",
        });
        using var mem = ExperienceStore.Open(d["mem"], "p", "1", "0.36.0");
        using var app = AppStore.Open(d["app"], "0.36.0");
        var r = HealthReport.Build("0.36.0", mem.Db, app.Db, null, new[] { path }, 2, null);
        var area = r.Checks.Where(c => c.Area == "Goal/Skill").ToList();
        Assert.NotEmpty(area);
        Assert.Contains(area, c => c.Name.Contains("ObservationGoal"));
        Assert.Contains(area, c => c.Name.Contains("Skill") && c.Detail.Contains("skill_record"));
        Assert.Contains(area, c => c.Name.Contains("Plan") && c.Detail.Contains("soft_adopt"));
        Assert.DoesNotContain(r.Text, "ボス");
    }

    [Fact]
    public void Contract_and_audit_exist()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s6-goal-plan-skill-contract.md"));
        Assert.Contains("ObservationGoal", text);
        Assert.Contains("SkillRecord", text);
        Assert.Contains("GKK_USE_STRUCTURED_GOALS", text);
        Assert.Contains("クリア", text); // forbidden mentioned
        Assert.Contains("wm_lookahead", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.36.0.md")));
        var fa = File.ReadAllText(Path.Combine(root, "future-architecture.md"));
        Assert.Contains("S6 Goal", fa);
        Assert.Contains("v0.36.0", fa);
    }

    [Fact]
    public void Session_wires_structured_goals_under_flag()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var ses = File.ReadAllText(Path.Combine(d!.Parent!.FullName, "src", "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("ObservationGoalBridge.FromSelfGoal", ses);
        Assert.Contains("StructuredPlanner.Propose", ses);
        Assert.Contains("SkillLibrary", ses);
        Assert.Contains("soft_adopt=skill_macro", ses);
        Assert.Contains("ApplyStructuredGoalsEffective", ses);
    }
}
