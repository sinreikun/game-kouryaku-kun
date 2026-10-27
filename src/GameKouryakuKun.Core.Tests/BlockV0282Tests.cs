using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Ocr;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class BlockV0282Tests
{
    [Fact]
    public void Weak_prior_with_many_next_states_does_not_pin_uncertainty_at_one()
    {
        var m = new TransitionModel(); long seq = 0;
        for (int i = 0; i < 30; i++) m.Observe(seq++, "x", "k", "x" + i, true);   // 似た画面では行き先がばらばら
        m.SetPriors(new[] { ("y", new ExperiencePrior("x", "borrowed", 0.04)) });
        double prev = 1;
        for (int i = 0; i < 10; i++) { m.Observe(seq++, "y", "k", "y", false); double u = m.Uncertainty("y", "k"); Assert.True(u <= prev + 1e-9); prev = u; }
        Assert.True(prev < 0.3, $"直接 10 回同じ結果の後の不確かさ {prev}");
        Assert.Equal(1, new TransitionModel().Uncertainty("z", "k"));   // 観測なしは最大のまま
    }

    private static TextSnapshot Snap(params (string t, int x, int y)[] l) =>
        new(1, 800, 600, l.Select(z => new OcrLine(z.t, 1, new OcrBox(z.x, z.y, 40, 10))).ToList(), Enumerable.Range(1, 192).Select(i => (ulong)i).ToArray());

    [Fact]
    public void Reliably_predicted_action_caused_text_change_loses_curiosity_value()
    {
        var tb = new TextChangeBook(); var vals = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            tb.ObserveAction("s", "a", TextDiff.Of(Snap(($"c{i}", 700, 550)), Snap(($"c{i + 1}", 700, 550))));   // 手 a でいつも同じ区画の文字が変わる
            tb.ObserveAction("s", "b", TextDiff.Of(Snap(("x", 10, 10)), Snap(("x", 10, 10))));
            vals.Add(tb.ActionCausedRate("s", "a"));
        }
        Assert.True(vals[^1] < vals[9] * 0.5, $"{vals[9]} → {vals[^1]}");
        Assert.Equal(40, tb.Changed);   // 観測としての文字の変化は全部数える
        Assert.True(tb.ChangeRate("s", "a") > 0.95);
    }
}
