using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Goals;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.20.0: Planning Horizon の伸び縮み・小目的と引き直し・目的での区間の再利用からの並びの候補・目的の結果の記録・起動をまたぐ引き継ぎ</summary>
public class BlockV020Tests
{
    private static List<ExploreAction> Acts() => Explorer.FromLogicalKeys(new[] { "button1", "button2", "button3" }).Append(ExploreAction.Wait).ToList();
    private static readonly string[] Chain = { "A", "B", "C", "D", "E" };

    private static void Obs(Explorer ex, TransitionModel m, ref long seq, string from, string a, string to)
    {
        var act = ex.Actions.First(x => x.Name == a);
        ex.Observe(from, act, to, from != to); m.Observe(seq++, from, a, to, from != to);
    }

    /// <summary>A→B→C→D→E を button1 でたどれる（確か）。A〜D はほかの操作も試してある。E だけ未試行の操作が残る</summary>
    private static (Explorer, TransitionModel) ChainWorld(Func<int, string>? noisyNext = null)
    {
        var ex = new Explorer(Acts(), 1); var m = new TransitionModel(); long seq = 1;
        for (int i = 0; i < 8; i++)
            for (int k = 0; k < 4; k++)
            {
                string n = Chain[k];
                Obs(ex, m, ref seq, n, "button1", noisyNext?.Invoke(i) is { } alt && alt != "" ? alt : Chain[k + 1]);
                Obs(ex, m, ref seq, n, "button2", n); Obs(ex, m, ref seq, n, "button3", n); Obs(ex, m, ref seq, n, "wait", n);
            }
        return (ex, m);
    }

    /// <summary>目的を最後まで追う（世界は決まった遷移。deviate が返す画面へ外れることもある）</summary>
    private static SelfGoal? Run(SelfGoals g, Explorer ex, TransitionModel m, string start, ref long step, Random rng, Func<string, string, string?>? deviate = null)
    {
        var goal = g.Propose(ex, m, start, step, rng); if (goal == null) return null;
        string cur = start;
        for (int i = 0; i < 20 && g.Active != null; i++)
        {
            var a = g.NextAction(ex, m, cur, step); if (a == null) break;
            int k = Array.IndexOf(Chain, cur);
            string next = deviate?.Invoke(cur, a) ?? (a == "button1" && k >= 0 && k < 4 ? Chain[k + 1] : cur);
            g.Observed(cur, a, next, next == m.Predict(cur, a).TopNext, step); cur = next; step++; g.Tick(true);
        }
        for (int i = 0; i < 4; i++) { step++; g.Tick(false); g.Review(ex, m, step); }
        return goal;
    }

    [Fact]
    public void Horizon_starts_short_and_grows_with_achievements_until_far_targets_are_planned()
    {
        var (ex, m) = ChainWorld();
        var g = new SelfGoals { MaxGoalShare = 1, BaseCooldown = 0 };
        Assert.Equal(2, g.Horizon);
        Assert.Null(g.PlanPath(ex, m, "A", "E", g.Horizon));   // 4 手先は今の Horizon では計画しない
        long step = 1; var rng = new Random(3); int farFromA = 0;
        for (int i = 0; i < 400; i++)
        {
            var start = Chain[rng.Next(4)];
            var goal = Run(g, ex, m, start, ref step, rng);
            if (goal != null && start == "A" && goal.Target == "E") farFromA++;
        }
        Assert.True(g.Horizon >= 4, $"Horizon {g.Horizon} created {g.Created} ach {g.Achieved} fail {g.Failed} ab {g.Abandoned} len {string.Join(",", g.LengthStats.Select(kv => $"{kv.Key}:{kv.Value.A}/{kv.Value.N}"))} ev {string.Join(";", g.Events.Take(6).Select(e => e.Kind + e.Target + e.Status + e.Reason + e.Plan))}");
        Assert.True(g.HorizonChanges >= 2);
        Assert.True(farFromA > 0, "Horizon が伸びたら A から 4 手先の E を目的にする");
        Assert.True(g.MaxAchievedLength >= 4);
        // 長い道筋には小目的がある
        Assert.True(g.SubgoalsTotal > 0 && g.SubgoalsReachedTotal > 0);
    }

    [Fact]
    public void Repeatedly_used_segments_of_achieved_goals_become_skill_candidates_without_lowering_thresholds()
    {
        var (ex, m) = ChainWorld();
        var g = new SelfGoals { MaxGoalShare = 1, BaseCooldown = 0 };
        long step = 1; var rng = new Random(5); var all = new List<(string Start, string End, IReadOnlyList<string> Steps, int Achieved, int Attempts)>();
        for (int i = 0; i < 300; i++) { Run(g, ex, m, Chain[rng.Next(4)], ref step, rng); all.AddRange(g.SkillCandidates); g.SkillCandidates.Clear(); }
        Assert.NotEmpty(all);
        Assert.All(all, c => { Assert.InRange(c.Steps.Count, 2, 3); Assert.True(c.Achieved >= 2 && c.Achieved * 2 > c.Attempts); Assert.DoesNotContain(c.Steps, s => s.StartsWith("ActionSequence")); });
        Assert.Contains(all, c => c.Steps.SequenceEqual(new[] { "button1", "button1" }));
    }

    [Fact]
    public void Low_confidence_paths_are_not_planned_even_with_a_long_horizon()
    {
        // button1 の結果が 4 割で横に外れる世界（1 手 0.6・4 手 0.13 の見込み）
        var (ex, m) = ChainWorld(i => i % 8 < 3 ? "X" + (i % 3) : "");
        foreach (var x in new[] { "X0", "X1", "X2" }) { long s = 9999; foreach (var a in new[] { "button1", "button2", "button3" }) for (int j = 0; j < 3; j++) Obs(ex, m, ref s, x, a, x); }
        var g = new SelfGoals { MaxGoalShare = 1, HorizonInitial = 6 };
        var p = g.PlanPath(ex, m, "A", "E", 6);
        Assert.NotNull(p);
        Assert.True(SelfGoals.Confidence(m, p!) < g.MinPlanConfidence);
        for (int i = 0; i < 100; i++) { var goal = g.Propose(ex, m, "A", i, new Random(i)); if (goal != null) { Assert.NotEqual("E", goal.Target); g.Abandon("t", i); } }
    }

    [Fact]
    public void Off_path_outcome_replans_from_the_current_state()
    {
        var (ex, m) = ChainWorld();
        var g = new SelfGoals { MaxGoalShare = 1, BaseCooldown = 0, HorizonInitial = 4 };
        long step = 1; var rng = new Random(1); bool once = true; SelfGoal? goal = null;
        for (int t = 0; t < 200 && (goal == null || goal.Target != "E" || goal.Kind != GoalKind.Reach); t++)
        {
            goal = g.Propose(ex, m, "A", step, rng);
            if (goal != null && goal.Target != "E") { g.Abandon("t", step); goal = null; }
        }
        Assert.NotNull(goal);
        // 1 手目で予測と違って C へ飛んだ（B を飛ばした）。今の画面 C から引き直して E へ行く
        string cur = "A";
        for (int i = 0; i < 20 && g.Active != null; i++)
        {
            var a = g.NextAction(ex, m, cur, step); if (a == null) break;
            int k = Array.IndexOf(Chain, cur);
            string next = once ? "C" : (a == "button1" ? Chain[k + 1] : cur); once = false;
            g.Observed(cur, a, next, false, step); cur = next; step++;
        }
        Assert.Equal(GoalStatus.Achieved, goal!.Status);
        Assert.True(goal.Replans >= 1);
        Assert.Equal(1, g.ReplanAchieved);
    }

    [Fact]
    public void Goal_outcomes_are_recorded_and_state_persists_across_restarts()
    {
        var (ex, m) = ChainWorld();
        var g = new SelfGoals { MaxGoalShare = 1, BaseCooldown = 0 };
        long step = 1; var rng = new Random(7);
        for (int i = 0; i < 200; i++) Run(g, ex, m, Chain[rng.Next(4)], ref step, rng);
        var reviewed = g.Events.Where(e => e.Status == "reviewed" && e.Kind == "reach").ToList();
        Assert.NotEmpty(reviewed);
        Assert.All(reviewed, e => { Assert.NotNull(e.Outcome); Assert.Contains("options_gain", e.Outcome); Assert.True(e.Horizon >= 1); });
        Assert.NotEmpty(g.OutcomeModel);
        var json = g.ExportState();
        var g2 = new SelfGoals(); g2.ImportState(json);
        Assert.Equal(g.Horizon, g2.Horizon);
        Assert.Equal(g.MaxAchievedLength, g2.MaxAchievedLength);
        Assert.Equal(g.OutcomeModel.Count, g2.OutcomeModel.Count);
        Assert.Equal(g.Value(GoalKind.Reach), g2.Value(GoalKind.Reach), 6);
    }
}
