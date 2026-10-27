using GameKouryakuKun.Core.Exploration;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.25.3: 1 手ごとの探索にも、行き先の地域の進みの比を掛ける（未試行・待つ手は 1）</summary>
public class BlockV0253Tests
{
    [Fact]
    public void Destination_ratio_uses_prediction_and_neutral_remainder()
    {
        var f = new FrontierRegions(); f.Recompute(new[] { ("a", "b"), ("b", "a") });
        for (int i = 0; i < 30; i++) f.Observe("c", "x", "d", false, 0, 0.1);
        for (int i = 0; i < 200; i++) f.Observe("a", "x", "b", false, 0, 0);   // a の地域は進みが減った
        Assert.True(f.DestinationRatio("c", new[] { ("a", 1.0) }) < 0.5);       // 進みの減った地域へ行く手は下がる
        Assert.True(f.DestinationRatio("a", new[] { ("c", 1.0) }) > 1.5);       // そこから出る手は上がる
        Assert.Equal(1.0, f.DestinationRatio("x", new (string, double)[0]), 6);   // 予測がなければ中立
    }

    [Fact]
    public void Explorer_applies_destination_only_to_tried_non_wait_actions()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "Up", "Down" }).Append(ExploreAction.Wait).ToList();
        var ex = new Explorer(acts, 1);
        var up = acts.First(a => a.Name == "Up");
        ex.Observe("n", up, "m", true);
        ex.DestinationValue = (_, _) => 0.1;
        var c = ex.Choose("n").Candidates.ToDictionary(x => x.Action);
        Assert.Equal(0.1, c["Up"].Destination, 6);
        Assert.Equal(1, c["Down"].Destination, 6);   // 未試行は行き先が分からないので 1
        Assert.Equal(1, c["wait"].Destination, 6);
    }
}
