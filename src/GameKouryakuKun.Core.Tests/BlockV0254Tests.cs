using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Goals;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.25.4（t52u）: 環境の消失を直前の自分の手の結果（先のない状態への遷移）として学ぶ</summary>
public class BlockV0254Tests
{
    [Fact]
    public void Attribution_requires_recent_non_wait_action_outside_recovery()
    {
        Assert.True(EnvironmentLossBook.Attributable(true, false, false, 1000, 2300, 5000));
        Assert.False(EnvironmentLossBook.Attributable(true, false, false, 1000, 7000, 5000));   // 離れている
        Assert.False(EnvironmentLossBook.Attributable(true, true, false, 1000, 1500, 5000));    // 待つ手
        Assert.False(EnvironmentLossBook.Attributable(true, false, true, 1000, 1500, 5000));    // 復旧中
        Assert.False(EnvironmentLossBook.Attributable(false, false, false, 0, 1500, 5000));     // 送った手がない
    }

    [Fact]
    public void Human_loss_is_recorded_but_not_learned_and_repeats_are_counted()
    {
        var b = new EnvironmentLossBook();
        b.Record(true, "s", "a", "human", 1, 500, "t");
        b.Record(true, "s", "a", "agent", 2, 500, "t");
        b.Record(false, "", "", "", 3, double.NaN, "t");
        Assert.Single(b.Learnable);
        Assert.Equal(1, b.Unattributed);
        Assert.False(b.RepeatsAfterLearning);
        b.Record(true, "s", "a", "agent", 4, 500, "t"); b.Record(true, "s", "a", "agent", 5, 500, "t");
        Assert.True(b.RepeatsAfterLearning);
        var c = new EnvironmentLossBook(); c.ImportState(b.ExportState());
        Assert.Equal(b.Summary(), c.Summary());
    }

    [Fact]
    public void Terminal_outcome_counts_as_tried_but_not_as_change_or_discovery()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "Up", "Down" }).ToList();
        var ex = new Explorer(acts, 1);
        ex.Observe("s", acts.First(a => a.Name == "Down"), "s", false);
        int d = ex.Discoveries;
        ex.ObserveTerminal("s", "Up");
        var st = ex.StatsOf("s", "Up")!;
        Assert.Equal(1, st.Tries); Assert.Equal(0, st.Changes); Assert.Equal(0, st.Left); Assert.Equal(0, st.Discoveries);
        Assert.Equal(d, ex.Discoveries);
        Assert.False(ex.Nodes.ContainsKey(EnvironmentLossBook.Terminal));   // 先のない状態は画面にしない（到達の目的の行き先にならない）
        Assert.NotEqual(2.0, ex.Choose("s").Candidates.First(c => c.Action == "Up").Novelty);   // 未試行の新しさで同じ手を試し直さない
    }

    [Fact]
    public void Repeated_losses_strengthen_the_avoidance_through_prediction()
    {
        var m = new TransitionModel(); var f = new FrontierRegions();
        long q = 0;
        for (int i = 0; i < 8; i++) m.Observe(q++, "s", "a", "t", true);
        double r1 = 0, r0 = f.DestinationRatio("s", m.Predict("s", "a").Top);
        m.Observe(q++, "s", "a", EnvironmentLossBook.Terminal, true);
        r1 = f.DestinationRatio("s", m.Predict("s", "a").Top);
        for (int i = 0; i < 4; i++) m.Observe(q++, "s", "a", EnvironmentLossBook.Terminal, true);
        double r5 = f.DestinationRatio("s", m.Predict("s", "a").Top);
        Assert.True(r1 < r0, $"{r0} {r1}");    // 1 回でも下がるが、まだ不確か
        Assert.True(r5 < r1, $"{r1} {r5}");    // 確かめが重なるほど下がる
        Assert.Equal(f.ValueFloor, f.RegionValue(EnvironmentLossBook.Terminal));
    }

    [Fact]
    public void Plans_and_retests_never_go_through_the_terminal()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "Up" }).ToList();
        var ex = new Explorer(acts, 1); var m = new TransitionModel(); long q = 0;
        for (int i = 0; i < 3; i++) { ex.ObserveTerminal("s", "Up"); m.Observe(q++, "s", "Up", EnvironmentLossBook.Terminal, true); }
        ex.Observe("s", acts[0], "s", false); m.Observe(q++, "s", "Up", "s", false);
        Assert.Empty(new SelfGoals().PlanEdges(ex, m));
        var g = new SelfGoals();
        Assert.Null(g.Propose(ex, m, "s", 1, new Random(1)));
    }
}
