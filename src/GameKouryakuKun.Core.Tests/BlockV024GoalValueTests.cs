using GameKouryakuKun.Core.Goals;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.24.0 ブロック D: 目的の結果から、目的の種類の値打ちを学ぶ</summary>
public class BlockV024GoalValueTests
{
    [Fact]
    public void Prior_is_neutral_half()
    {
        var g = new SelfGoals();
        Assert.Equal(0.5, g.Ratio(GoalKind.Reach, 3), 3);
    }

    [Fact]
    public void Baseline_learns_from_non_goal_steps_only()
    {
        var g = new SelfGoals();
        for (int i = 0; i < 10; i++) g.StepGain(0.2, goalStep: false);
        Assert.Equal(10, g.Baseline.N); Assert.Equal(0.2, g.Baseline.Mean, 3);
        g.StepGain(-1, false);   // 負の量は 0 として数える（不確かさが増えた手を罰にしない）
        Assert.True(g.Baseline.Mean < 0.2);
    }

    [Fact]
    public void Ratio_compares_goal_gain_to_exploration_gain_and_shrinks_to_prior()
    {
        var g = new SelfGoals();
        for (int i = 0; i < 20; i++) g.StepGain(0.1, false);
        // 内部の集計に直接入れる代わりに、状態の読み込みで与える
        g.ImportState("""{"gain":{"reach|3-4":[8,0.2],"retest|1":[8,0.02]},"baseline":[20,0.1]}""");
        double reach = g.Ratio(GoalKind.Reach, 3), retest = g.Ratio(GoalKind.Retest, 1);
        Assert.True(reach > 1, $"reach {reach}");    // 探索より学べた種類は 1 を超える
        Assert.True(retest < 0.5, $"retest {retest}");
        Assert.True(g.Value(GoalKind.Reach) > g.Value(GoalKind.Retest));
        // 試した回数が少なければ事前値に近い
        Assert.Equal(0.5, g.Ratio(GoalKind.Reach, 1), 2);
    }

    [Fact]
    public void Old_state_without_gain_resets_stuck_value_to_prior()
    {
        var g = new SelfGoals();
        g.ImportState("""{"value":{"Reach":0.05,"Retest":0.4}}""");
        Assert.Equal(0.5, g.Value(GoalKind.Reach), 3);
    }

    [Fact]
    public void State_round_trips()
    {
        var g = new SelfGoals();
        g.ImportState("""{"gain":{"reach|2":[3,0.3]},"baseline":[5,0.1]}""");
        var g2 = new SelfGoals(); g2.ImportState(g.ExportState());
        Assert.Equal(g.Ratio(GoalKind.Reach, 2), g2.Ratio(GoalKind.Reach, 2), 6);
        Assert.Equal(5, g2.Baseline.N);
    }

    /// <summary>値打ちの単位は内部モデルの不確かさの減りだけ。表示の計器・成長の計測・人間の手は入らない（静的検査）</summary>
    [Fact]
    public void Goal_value_uses_only_internal_learning_gain()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GameKouryakuKun.sln"))) root = root.Parent;
        var t = File.ReadAllText(Path.Combine(root!.FullName, "GameKouryakuKun.Core", "Goals", "SelfGoals.cs"));
        foreach (var w in new[] { "GrowthSnapshot", "Metrics", "Score", "Human" }) Assert.DoesNotContain(w, t);
        var session = File.ReadAllText(Path.Combine(root.FullName, "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("if (!ca.IsWait && !isHuman) goals.StepGain(", session);   // 待つ手・人間の手は数えない
    }
}
