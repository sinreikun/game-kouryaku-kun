using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.31.5（P-6 の一部）: 見た目の表と数字の差の表</summary>
public class BlockV0315TablesTests
{
    private static EntityCandidate C(int track, string app, bool isNew = false) => new(0, track, 1, 1, 2, 2, 1.5, 1.5, 4, app, "A", 0, 0, false, !isNew, isNew, 1, 2);
    private static EntityLayer L(params EntityCandidate[] c) => new(LayerStatus.Observed, c, Array.Empty<int>(), Array.Empty<RelationObs>());

    [Fact]
    public void Action_x_gives_appearance_b_and_action_y_gives_c()
    {
        var t = new AppearanceTable(); var r = new Random(1); EntityLayer prev = L(C(1, "a"));
        for (int i = 0; i < 60; i++)
        {
            string act = r.Next(2) == 0 ? "x" : "y";
            var cur = L(C(1, act == "x" ? "b" : "c"));
            t.Observe(prev, cur, act);
            prev = L(C(1, "a"));   // 毎回 a に戻ってから手をする
        }
        Assert.Equal("b", t.Predict("a", "x")!.Value.Next);
        Assert.Equal("c", t.Predict("a", "y")!.Value.Next);
        Assert.True(t.Predict("a", "x")!.Value.P > 0.8);
        Assert.True(t.Hits >= t.Predicted - 2);
    }

    [Fact]
    public void New_candidates_are_not_counted_as_transitions()
    {
        var t = new AppearanceTable();
        t.Observe(L(C(1, "a")), L(C(2, "z", true)), "x");
        Assert.Null(t.Predict("a", "x"));
    }

    [Fact]
    public void Number_decreasing_by_4_after_action_x_is_predicted()
    {
        var t = new NumberDeltaTable(); double v = 100;
        for (int i = 0; i < 20; i++)
        {
            string act = i % 2 == 0 ? "x" : "y"; double pv = v; if (act == "x") v -= 4;
            var text = new TextLayer(LayerStatus.Observed, "full", "none", Array.Empty<TextLineObs>(), new[] { new NumberObs(7, 0, v, pv, Array.Empty<double>()) }, 1, 0, 0);
            t.Observe(text, act);
        }
        Assert.Equal(-4, t.Predict(7, 0, "x")!.Value.Delta);
        Assert.Equal(0, t.Predict(7, 0, "y")!.Value.Delta);
    }
}
