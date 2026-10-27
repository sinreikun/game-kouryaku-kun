using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Ocr;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class BlockV0280Tests
{
    private static Explorer Build(int seed, out IReadOnlyList<ExploreAction> acts)
    {
        acts = Explorer.FromLogicalKeys(new[] { "button1", "button2", "button3" });
        var ex = new Explorer(acts, seed) { TextChangeValue = (n, a) => a.Name == "button2" ? 0.3 : 0 };
        for (int i = 0; i < 5; i++) { ex.Observe("s", acts[0], "s", false); ex.Observe("s", acts[1], "t" + i, true); }
        return ex;
    }

    [Fact]
    public void A_score_breakdown_does_not_change_choices()
    {
        var a = Build(7, out _); var b = Build(7, out _);
        for (int i = 0; i < 200; i++)
        {
            _ = b.ScoreCandidates("s");   // 内訳を見ても乱数を使わない
            var ca = a.Choose("s"); var cb = b.Choose("s");
            Assert.Equal(ca.Action.Name, cb.Action.Name); Assert.Equal(ca.Reason, cb.Reason);
        }
    }

    [Fact]
    public void A_breakdown_reconstructs_score_and_counts_flips()
    {
        var ex = Build(3, out _);
        var cs = ex.ScoreCandidates("s");
        Assert.NotEmpty(cs);
        foreach (var c in cs) Assert.True(Math.Abs(ScoreBreakdown.ScoreWithout(c, null) - c.Score) < 1e-6 * Math.Max(1, c.Score), $"{c.Action}: {ScoreBreakdown.ScoreWithout(c, null)} vs {c.Score}");
        Assert.True(Math.Abs(cs.Sum(c => c.Probability) - 1) < 1e-6);
        var log = new ScoreBreakdown(); log.Add("scored", cs, cs[0].Action);
        Assert.Equal(1, log.Steps); Assert.Contains("text", log.Summary());
    }

    // ---- STEP B ----
    [Fact]
    public void B_cell_changing_equally_for_every_action_is_independent_and_not_action_caused()
    {
        var r = new RegionCausality(4); var rnd = new Random(1);
        foreach (var a in new[] { "a", "b", "c" })
            for (int i = 0; i < 150; i++)   // 手によらないと「強く」言うには回数が要る（しきい値は緩めない）
            {
                var ch = new List<int>(); if (rnd.NextDouble() < 0.5) ch.Add(0);   // 区画 0: 手によらず半分の確率で変わる
                if (a == "a") ch.Add(1);                                           // 区画 1: 手 a でだけ変わる
                r.ObserveAction("s", a, ch);
            }
        Assert.Equal("independent", r.Class("s", 0));
        Assert.Equal("action", r.Class("s", 1));
        Assert.Equal("independent", r.NaturalRate("s", 0)!.Value.Source);
        Assert.True(r.CausedShare("s", "a", 0) < 0.25);
        Assert.True(r.CausedShare("s", "a", 1) > 0.9);
        Assert.True(r.Confidence("s", "a", 1) > 0.95);
        Assert.True(r.DiffCaused("s", "b", new[] { 0 }) < 0.25);
        var r2 = new RegionCausality(4); r2.ImportState(r.ExportState()); Assert.Equal(r.Class("s", 1), r2.Class("s", 1));
    }

    [Fact]
    public void B_wait_observations_give_cell_baseline()
    {
        var r = new RegionCausality(2);
        for (int i = 0; i < 10; i++) { r.ObserveNatural("s", new[] { 0 }); r.ObserveAction("s", "a", new[] { 0 }); }
        Assert.Equal("wait", r.NaturalRate("s", 0)!.Value.Source);
        Assert.True(r.CausedShare("s", "a", 0) < 0.05);
    }

    private static TextSnapshot Snap(params (string t, int x, int y)[] l) =>
        new(1, 800, 600, l.Select(z => new OcrLine(z.t, 1, new OcrBox(z.x, z.y, 40, 10))).ToList(), Enumerable.Range(1, 192).Select(i => (ulong)i).ToArray());

    [Fact]
    public void B_natural_text_is_still_observed_but_not_scored_as_action_caused()
    {
        var tb = new TextChangeBook(); int n = 0;
        // 時計のような手によらない数字（左上）と、手 a の後だけ出る文字（右下）
        foreach (var a in new[] { "a", "b", "c" })
            for (int i = 0; i < 30; i++)
            {
                var p = Snap(($"{n++}", 10, 10)); var q = a == "a" ? Snap(($"{n++}", 10, 10), ("ok", 700, 550)) : Snap(($"{n++}", 10, 10));
                tb.ObserveAction("s", a, TextDiff.Of(p, q));
            }
        Assert.Equal(90, tb.Changed);                                  // 文字の変化そのものは全部数える（消さない）
        Assert.True(tb.ChangeRate("s", "b") > 0.9);                    // 観測としての文字の変化
        Assert.True(tb.ActionCausedRate("s", "b") < 0.1);              // 手が起こした変化ではない
        Assert.True(tb.ActionCausedRate("s", "a") > 2 * tb.ActionCausedRate("s", "b"));   // v0.28.2: 予測できるほど値打ちは下がる（別のテスト）が、手の効果のある手のほうが大きい
        Assert.True(tb.CausedSum < tb.Changed);
        var top = tb.TopCause("s", "a"); Assert.True(top.Confidence > 0.9);
        var tb2 = new TextChangeBook(); tb2.ImportState(tb.ExportState()); Assert.Equal(tb.ActionCausedRate("s", "a"), tb2.ActionCausedRate("s", "a"), 9);
    }

    [Fact]
    public void B_small_text_change_counts_even_without_pixel_change()
    {
        var d = TextDiff.Of(Snap(("HP 10", 10, 10)), Snap(("HP 11", 10, 10)));
        Assert.Equal("numbers", d.Kind); Assert.Equal(1, d.PixelUnconfirmed);
    }

    [Fact]
    public void B_text_change_that_precedes_different_transitions_is_detected()
    {
        var l = new TextTransitionLink();
        for (int i = 0; i < 10; i++)
            foreach (var a in new[] { "a", "b", "c" })
            {
                l.Observe("s", a, false, "s");
                l.Observe("s", a, true, "t", new[] { "x" });
            }
        Assert.True(l.Evidence("s")!.Value.LogBf >= SplitReview.RevertMinLogBayesFactor);
        Assert.Equal(1, l.Counts().Predictive);
        Assert.Contains(l.TopTexts(5), t => t.Next == "t");
        var u = new TextTransitionLink();
        for (int i = 0; i < 10; i++) foreach (var a in new[] { "a", "b", "c" }) { u.Observe("s", a, false, "s"); u.Observe("s", a, true, "s"); }
        Assert.Equal(0, u.Counts().Predictive);
    }
}
