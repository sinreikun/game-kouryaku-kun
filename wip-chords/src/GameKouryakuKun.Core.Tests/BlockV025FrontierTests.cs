using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Goals;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.25.0 ブロック E: 地域の進みと、行き直しの値打ちの減衰と回復</summary>
public class BlockV025FrontierTests
{
    private static FrontierRegions TwoRegions()
    {
        var f = new FrontierRegions();
        // A: a1<->a2<->a3 の閉じた輪、B: b1<->b2。A→B の一方通行
        f.Recompute(new[] { ("a1", "a2"), ("a2", "a3"), ("a3", "a1"), ("a3", "b1"), ("b1", "b2"), ("b2", "b1") });
        return f;
    }

    [Fact]
    public void Regions_are_closures_of_observed_transitions_without_meaning()
    {
        var f = TwoRegions();
        Assert.Equal(2, f.RegionCount);
        Assert.Equal(f.RegionOf("a1"), f.RegionOf("a3"));
        Assert.NotEqual(f.RegionOf("a1"), f.RegionOf("b1"));
        Assert.Equal("a1", f.RegionOf("a2"));   // 名前は成分の中のいちばん小さい状態（意味の名前は付けない）
    }

    [Fact]
    public void Closed_region_with_only_screen_changes_decays_without_exclusion()
    {
        var f = TwoRegions();
        // 少しは学べる世界の基準
        for (int i = 0; i < 30; i++) f.Observe("b1", "x", "b2", false, 0, 0.1);
        // 閉じた地域で画面が変わり続けるが、新しい遷移も学びもない
        for (int i = 0; i < 200; i++) f.Observe(i % 2 == 0 ? "a1" : "a2", "x", i % 2 == 0 ? "a2" : "a1", false, 0, 0);
        double v = f.RegionValue("a1");
        Assert.True(v < 0.5, $"value {v}");
        Assert.True(v >= 0.05);              // 下限で止まり、除外はしない
        Assert.True(f.DecayedNodes > 0);
    }

    [Fact]
    public void Value_rises_again_when_new_paths_appear_from_the_region()
    {
        var f = TwoRegions();
        for (int i = 0; i < 30; i++) f.Observe("b1", "x", "b2", false, 0, 0.1);
        for (int i = 0; i < 200; i++) f.Observe("a1", "x", "a2", false, 0, 0);
        double low = f.RegionValue("a1");
        for (int i = 0; i < 20; i++) { f.Observe("a1", "k" + i, "n" + i, true, 3, 0.1); f.Observe("a1", "j" + i, i % 2 == 0 ? "b1" : "a3", false, 3, 0.1); }   // 未知の画面・別の地域や未知の操作が残る画面への新しい道・学び
        Assert.True(f.RegionValue("a1") > low * 4, $"{low} -> {f.RegionValue("a1")}");
        Assert.True(f.Recoveries > 0);
    }

    [Fact]
    public void One_signal_alone_cannot_exceed_the_exploration_average()
    {
        var f = new FrontierRegions(); f.Recompute(new[] { ("a", "b"), ("b", "a") });
        for (int i = 0; i < 100; i++) f.Observe("c", "x", "d", false, 0, 0.1);           // 基準: 学びだけ
        for (int i = 0; i < 100; i++) f.Observe("a", "x" + i, "a" + i, true, 0, 0);      // 新しい画面だけが大量に出る（新しさの数）
        Assert.True(f.NodeValue("a") <= 1.0 + 1e-9, $"{f.NodeValue("a")}");
    }

    [Fact]
    public void State_survives_export_import()
    {
        var f = TwoRegions();
        for (int i = 0; i < 40; i++) f.Observe("a1", "x", "a2", i < 5, 1, 0.05);
        var g = new FrontierRegions(); g.ImportState(f.ExportState());
        g.Recompute(new[] { ("a1", "a2"), ("a2", "a3"), ("a3", "a1"), ("a3", "b1"), ("b1", "b2"), ("b2", "b1") });
        Assert.Equal(f.RegionValue("a1"), g.RegionValue("a1"), 6);
    }

    [Fact]
    public void Goals_use_region_value_only_as_relative_factor()
    {
        var g = new SelfGoals();
        Assert.Null(g.RegionValue);   // 無ければ従来どおり
        g.RegionValue = n => n == "low" ? 0.1 : 1;
        Assert.NotNull(g.RegionValue);
    }
}
