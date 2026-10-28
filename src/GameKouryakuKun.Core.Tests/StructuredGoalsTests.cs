using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Observation;
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
    public void Planner_expands_skill_macro_without_wm()
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
        Assert.Equal("unused", plan.WmLookaheadStatus);
        Assert.Contains("wm_lookahead=not_available", plan.Note!);
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
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.36.1.md")));
        Assert.Contains("S6 クローズ", File.ReadAllText(Path.Combine(root, "docs", "s6-goal-plan-skill-contract.md")));
        var fa = File.ReadAllText(Path.Combine(root, "future-architecture.md"));
        Assert.Contains("S6 Goal", fa);
        Assert.Contains("v0.36.1", fa);
        Assert.Contains("S6 クローズ可", fa);
    }


    [Fact]
    public void Track_relation_scored_from_bundle_relations()
    {
        var bundle = new ObservationBundle
        {
            Relations = new[]
            {
                new RelationSnapshot(1, 2, 1, 0, 1.0, "moved", true, false, true),
            },
            EntityExperience = new EntityExperienceSnapshot
            {
                Observed = 5,
                Tables = new[]
                {
                    new EntityExperienceTableStats("relation", 4, 3, 0, 0),
                    new EntityExperienceTableStats("appearance", 0, 0, 0, 0),
                    new EntityExperienceTableStats("movement", 0, 0, 0, 0),
                    new EntityExperienceTableStats("text_number", 0, 0, 0, 0),
                },
                Follows = Array.Empty<FollowPairSnapshot>(),
            },
        };
        var hit = ObservationPredicateEvidence.TrackRelation(bundle, null, null, "n0");
        Assert.Equal("scored", hit.Status);
        Assert.Equal(ObservationPredicateIds.TrackRelationReproduce, hit.PredicateId);
        var goal = ObservationPredicateEvidence.ToGoal(hit, 3);
        Assert.Equal("scored", goal.Predicate.Status);
        Assert.DoesNotContain("ボス", goal.SummaryLine());
    }

    [Fact]
    public void Number_series_scored_from_structured_text()
    {
        var so = new StructuredObservation(
            1, 1, "test", "button1", "a", "b",
            new GlobalScene("a", null, null, null, null, LayerStatus.Observed),
            new RegionLayer(LayerStatus.NotObserved, 0, Array.Empty<ChangeRegion>()),
            new TextLayer(LayerStatus.Observed, "ocr", "diff", Array.Empty<TextLineObs>(),
                new[] { new NumberObs(3, 0, 5.0, 4.0, new[] { 3.0, 4.0, 5.0 }) }, 1, 0, 0.5),
            new TemporalLayer(0, false, null, LayerStatus.Observed));
        var hit = ObservationPredicateEvidence.NumberSeries(null, so, "n0", "button1");
        Assert.Equal("scored", hit.Status);
        Assert.Contains("series", hit.Note!);
        Assert.True(hit.PeProxy > 0);
    }

    [Fact]
    public void Number_series_not_enough_without_numbers()
    {
        var hit = ObservationPredicateEvidence.NumberSeries(null, null, "n0");
        Assert.Equal("not_enough_evidence", hit.Status);
    }

    [Fact]
    public void Planner_uses_short_horizon_and_suppresses_on_miss()
    {
        var miner = new SkillMiner();
        miner.PromoteFromSequence(new ActionSequenceCandidate
        {
            Id = 3, Steps = new[] { "button1", "button2" }, Start = "here", End = "there",
            Reproducibility = 0.9, Origin = "goal", Status = SequenceStatus.Active,
        });
        var goal = ObservationGoalBridge.FromSelfGoal(new SelfGoal
        {
            Id = 9, Kind = GoalKind.Reach, Target = "there", Origin = "t",
            UncBefore = 0.7, PlanConfidence = 0.6, Budget = 4,
        });
        var ok = new ShortHorizonForecast
        {
            Status = LayerStatus.Observed,
            PredictionMiss = false,
            ContextHelpEvents = 1,
            Note = "ok",
        };
        var planOk = StructuredPlanner.Propose(goal, miner.Skills, "here", "dpad_left", ok);
        Assert.True(planOk.WmLookaheadUsed);
        Assert.Equal("observed", planOk.WmLookaheadStatus);
        Assert.Equal("soft_adopt_available", planOk.Status);
        Assert.Contains("wm_lookahead=observed", planOk.Note!);

        var miss = new ShortHorizonForecast { Status = LayerStatus.Observed, PredictionMiss = true };
        var planMiss = StructuredPlanner.Propose(goal, miner.Skills, "here", "dpad_left", miss);
        Assert.True(planMiss.WmLookaheadUsed);
        Assert.NotEqual("soft_adopt_available", planMiss.Status);
        Assert.Contains("skill_suppressed_wm_miss", planMiss.Note!);
    }

    [Fact]
    public void Soft_adopt_budget_blocks_human_gap_window()
    {
        Assert.Equal("human", SoftAdoptBudget.BlockReason(0, 20, 10, null, Array.Empty<long>(), null, true));
        Assert.Equal("budget", SoftAdoptBudget.BlockReason(SoftAdoptBudget.DefaultMaxSoftAdopts, SoftAdoptBudget.DefaultMaxSoftAdopts, 10, null, Array.Empty<long>(), null, false));
        Assert.Equal("recovery_gap", SoftAdoptBudget.BlockReason(0, 20, 12, null, Array.Empty<long>(), lastRecoveryStep: 10, humanControl: false));
        Assert.Equal("cooldown", SoftAdoptBudget.BlockReason(1, 20, 15, lastSoftAdoptStep: 10, Array.Empty<long>(), null, false));
        Assert.Equal("window", SoftAdoptBudget.BlockReason(2, 20, 40, lastSoftAdoptStep: 30,
            new long[] { 5, 20 }, null, false));
        Assert.Null(SoftAdoptBudget.BlockReason(0, 20, 100, null, Array.Empty<long>(), null, false));

        var blocked = StructuredPlanner.Propose(
            ObservationGoalBridge.StubFormal(ObservationPredicateIds.FrontierReach, "n"),
            Array.Empty<SkillRecord>(), "n", "a", softAdoptBlock: "recovery_gap");
        // no skill → parallel or not_applicable; SoftAdoptBlock carried when legacy present
        Assert.Equal("recovery_gap", blocked.SoftAdoptBlock);
    }

    [Fact]
    public void Soft_adopt_block_marks_status_when_skill_ready()
    {
        var miner = new SkillMiner();
        miner.PromoteFromSequence(new ActionSequenceCandidate
        {
            Id = 1, Steps = new[] { "x", "y" }, Start = "here", End = "there",
            Reproducibility = 0.95, Origin = "goal", Status = SequenceStatus.Active,
        });
        var goal = ObservationGoalBridge.FromSelfGoal(new SelfGoal
        {
            Id = 1, Kind = GoalKind.Reach, Target = "there", Origin = "t",
            UncBefore = 0.5, PlanConfidence = 0.8, Budget = 3,
        });
        var plan = StructuredPlanner.Propose(goal, miner.Skills, "here", "other", softAdoptBlock: "budget");
        Assert.Equal("blocked", plan.Status);
        Assert.Contains("soft_block=budget", plan.Note!);
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
        Assert.Contains("SoftAdoptBudget.BlockReason", ses);
        Assert.Contains("_lastSupervisorRecoveryStep", ses);
        Assert.Contains("ProposeFromEvidence", ses);
        Assert.Contains("LatestShortHorizon", ses);
        Assert.Contains("Human?.Enabled", ses);
    }
}

