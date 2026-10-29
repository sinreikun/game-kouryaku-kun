using GameKouryakuKun.Core.Throughput;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ThroughputV0410Tests
{
    [Fact]
    public void Blocked_randomization_gives_every_config_each_screen_action_kind()
    {
        var il = new InterleavedComparison(1);
        foreach (var key in new[] { "m48\u241fButton", "m1\u241fDpad" })
            for (int blk = 0; blk < 10; blk++)
                Assert.Equal(new[] { "A", "B", "C", "D" }, Enumerable.Range(0, 4).Select(_ => il.Assign(key)).OrderBy(x => x).ToArray());
        il.Fail("C", "対応違反 1"); il.Fail("A", "x");   // A は外さない
        var next = Enumerable.Range(0, 30).Select(_ => il.Assign("m48\u241fButton")).ToList();
        Assert.DoesNotContain("C", next);
        Assert.Contains("A", next);
    }

    // v0.40.2 実機の再現: 区間ごとに違う画面を訪れても、手ごとの交互なら同じ (画面, 手) の照合がすべての設定で貯まる
    private static InterleavedComparison Sim(Func<string, string, (double Ms, bool NoChange)> f, int perHalf = 400)
    {
        var il = new InterleavedComparison(7); int step = 0;
        for (int half = 1; half <= 2; half++)
            for (int i = 0; i < perHalf; i++)
            {
                string node = (half == 1 ? i % 2 == 0 ? "m44" : "m48" : i % 2 == 0 ? "m1" : "m42");
                string lab = il.Assign(node + "\u241fButton"); step++;
                il.Begin(step, lab, node + "\u241fbutton1", half);
                var (ms, nc) = f(lab, node);
                il.AddDuration(step, ms); il.Turn(step, node + "\u241fbutton1", nc, ms / 2);
                il.Predict(step, node + "\u241fbutton1", step % 3 != 0);
            }
        il.SetIntact(_ => true);
        return il;
    }

    [Fact]
    public void Same_screen_judgement_adopts_real_speedup_and_rejects_ignored_inputs()
    {
        var il = Sim((l, n) => (l == "B" ? 1000 : 1500, l == "C" && n != "m48" ? true : false));
        var v = il.Judge().ToDictionary(x => x.Label);
        Assert.Equal(InterleavedComparison.Verdict.Adopt, v["B"].Verdict);
        Assert.True(v["B"].Gain[0].Diff > 0.4 && v["B"].Gain[0].Weight >= 30 && v["B"].Gain[1].Weight >= 30);
        Assert.Equal(InterleavedComparison.Verdict.Reject, v["C"].Verdict);   // 同じ画面で変化なしが増えた＝入力が受け付けられていない
        Assert.Contains(v["C"].Regressions, r => r.Contains("変化なし"));
        Assert.Equal(InterleavedComparison.Verdict.Inconclusive, v["D"].Verdict);   // 速さが同じ
    }

    [Fact]
    public void Too_few_matched_pairs_is_inconclusive_and_fail_rejects()
    {
        var il = Sim((l, n) => (l == "B" ? 500 : 1500, false), perHalf: 40);
        Assert.Equal(InterleavedComparison.Verdict.Inconclusive, il.Judge().Single(x => x.Label == "B").Verdict);
        il.Fail("D", "静止確定の間引き 1");
        Assert.Equal(InterleavedComparison.Verdict.Reject, il.Judge().Single(x => x.Label == "D").Verdict);
    }

    [Fact]
    public void Residual_pending_wait_is_charged_to_the_step_that_left_it()
    {
        var il = new InterleavedComparison(3);
        il.Begin(1, "B", "k", 1); il.Begin(2, "A", "k", 1);
        il.AddDuration(1, 300); il.AddDuration(2, 900);
        il.AddDuration(1, 200); il.AddDuration(2, -200);   // 手 2 で手 1 の観測を待った 200 ms は手 1（B）の時間
        il.Turn(1, "k", false, 0); il.Turn(2, "k", false, 0);
        Assert.Equal("B", il.LabelOf(1));
        Assert.Equal(2, il.Count("A") + il.Count("B"));
    }

    [Fact]
    public void Ledger_reports_per_step_intact_after_late_amend()
    {
        var lg = new ObservationLedger();
        lg.BeginAction(1); lg.Open(1, 1, "ocr", 10, 0, FramePosition.Settle, 0);
        Assert.False(lg.StepIntact(1));
        lg.BeginAction(2);
        lg.Complete(1, "ocr", 10, 1, 0, 50);
        Assert.True(lg.StepIntact(1));
        Assert.Null(lg.StepIntact(2));
    }
}
