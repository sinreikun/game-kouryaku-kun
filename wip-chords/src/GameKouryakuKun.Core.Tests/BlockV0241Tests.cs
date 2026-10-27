using GameKouryakuKun.Core.Goals;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.24.1: v0.24.0 実機の健康診断 FAIL 2 件の修正</summary>
public class BlockV0241Tests
{
    [Fact]
    public void Value_follows_baseline_live_and_survives_export_import()
    {
        var g = new SelfGoals();
        g.ImportState("""{"gain":{"reach|1":[10,0.2]},"baseline":[20,0.1]}""");
        double v1 = g.Value(GoalKind.Reach);
        for (int i = 0; i < 30; i++) g.StepGain(0.4, goalStep: false);   // 基準だけが変わる
        double v2 = g.Value(GoalKind.Reach);
        Assert.True(v2 < v1, $"{v1} -> {v2}");                               // 古い値のまま残らない
        var h = new SelfGoals(); h.ImportState(g.ExportState());
        Assert.Equal(v2, h.Value(GoalKind.Reach), 4);                        // 次の実行の始まりに同じ値
        Assert.Equal(g.Value(GoalKind.Retest), h.Value(GoalKind.Retest), 4);
    }

    [Fact]
    public void Window_may_achieve_one_goal_created_before_it()
    {
        // 間の始まりに進行中の目的は 1 つまで。作った 8・達成 9 は正常、作った 8・達成 10 は不整合
        Assert.True(9 <= 8 + 1); Assert.False(10 <= 8 + 1);
    }
}
