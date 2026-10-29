using System.Text.Json;
using GameKouryakuKun.Core.Throughput;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ThroughputV0402Tests
{
    [Fact]
    public void Reproduces_v040_input_ignored_when_pressed_too_soon_and_holds_until_safe()
    {
        // 実機 v0.40.0: 同じ (画面, 手) で、静止から入力までが <300 ms だと変化なしが +13.8 pt。予測は同じ
        var g = new ReadyGap();
        var rng = new Random(1);
        for (int i = 0; i < 4000; i++)
            foreach (var key in new[] { "m1␟b1", "m2␟b2" })
            {
                double gap = new[] { 50, 200, 350, 500, 700 }[i % 5];
                double pNoChange = gap < 300 ? 0.6 : 0.3;   // 速すぎる入力は受け付けられないことがある
                g.Observe(key, gap, rng.NextDouble() < pNoChange);
            }
        var (ex0, se0, w0) = g.Excess(0);
        Assert.True(w0 >= 30 && ex0 - 2 * se0 > 0.05);   // 短い区分の増加を見つける
        Assert.Equal(300, g.SafeGapMs());                 // 増えない最短の区分
        double hold = g.HoldFor(100, out bool probe);
        Assert.False(probe);
        Assert.Equal(200, hold);
    }

    [Fact]
    public void Without_evidence_keeps_serial_like_gap_and_probes_shorter()
    {
        var g = new ReadyGap();
        Assert.Equal(600, g.SafeGapMs());
        int probes = 0;
        for (int i = 0; i < 8; i++) { g.HoldFor(0, out bool p); if (p) probes++; }
        Assert.Equal(2, probes);   // 4 手に 1 回だけ短い区分を試す
    }

    [Fact]
    public void Ready_gap_persists_across_runs()
    {
        var g = new ReadyGap();
        for (int i = 0; i < 80; i++) { g.Observe("k", 700, false); g.Observe("k", 50, false); }
        var js = JsonSerializer.Serialize(g.Export());
        var g2 = new ReadyGap(); g2.Import(JsonSerializer.Deserialize<ReadyGap.Persisted>(js));
        Assert.Equal(0, g2.SafeGapMs());
    }

    [Fact]
    public void Unshortened_late_change_no_longer_blocks_shortening_forever()
    {
        // 実機 v0.40.0: 全体の値 1200 ms での遅れた変化で下限が 2400 ms になり、適応が 0 回だった
        var a = new AdaptiveSettle();
        for (int i = 0; i < 40; i++) a.ObserveFirstChange("m1|Button", 80);
        a.LateChange("m1|Button", 1200, shortened: false);
        Assert.True(a.Recommend("m1|Button", 600, 250) < 600);   // 標本は足すが、下限は上げない（分位 0.99 が守る）
        // 自分が短くして早すぎたときは、従来どおり下限を上げて戻す
        a.LateChange("m1|Button", 250, shortened: true);
        for (int i = 0; i < a.CooldownTurns; i++) Assert.Equal(600, a.Recommend("m1|Button", 600, 250));
        Assert.True(a.Recommend("m1|Button", 600, 250) >= 500);
    }

    [Fact]
    public void Settle_stats_persist_across_segments()
    {
        var a = new AdaptiveSettle();
        for (int i = 0; i < 25; i++) a.ObserveFirstChange("m1|Button", 60);
        var js = JsonSerializer.Serialize(a.Export());
        var b = new AdaptiveSettle(); b.Import(JsonSerializer.Deserialize<AdaptiveSettle.Persisted>(js));
        Assert.Equal(250, b.Recommend("m1|Button", 1200, 250));   // 新しい区間の冷えた全体の値 1200 でも、持ち越した証拠で短くできる（下限 250）
    }

    private static SegmentResult Seg(string l, params (string K, long N, long Nc, double Settle)[] ks) => new()
    {
        Label = l, Block = 1, Actions = 200, CioPerMinute = 50, Opened = 200, MemoryCommits = 200, ObservationsSaved = 200, DecisionsRecorded = 200, TextPairs = 200,
        KeyTurns = ks.ToDictionary(k => k.K, k => (k.N, k.Nc, k.Settle * k.N)),
    };

    [Fact]
    public void Same_screen_action_no_change_increase_is_a_causality_regression()
    {
        var a = Seg("A", ("s␟b1", 100, 30, 500), ("s␟b2", 100, 30, 500));
        var b = Seg("B", ("s␟b1", 100, 55, 500), ("s␟b2", 100, 52, 500));
        Assert.Contains(ThroughputComparison.Regressions(b, a), r => r.Contains("変化なしが"));
        var (d, w) = ThroughputComparison.StratifiedSettleDiff(b, a);
        Assert.Equal(0, d, 6);
        Assert.Equal(100, w, 6);
    }

    [Fact]
    public void Screen_mix_alone_does_not_trigger_no_change_regression()
    {
        // B は変化しにくい画面に長く居ただけ（同じ (画面, 手) では同じ率）
        var a = Seg("A", ("easy␟b", 150, 15, 500), ("hard␟b", 50, 40, 1200));
        var b = Seg("B", ("easy␟b", 50, 5, 500), ("hard␟b", 150, 120, 1200));
        Assert.DoesNotContain(ThroughputComparison.Regressions(b, a), r => r.Contains("変化なしが"));
        Assert.Equal(0, ThroughputComparison.StratifiedSettleDiff(b, a).DiffMs, 6);
    }

    [Fact]
    public void Report_has_same_key_section()
    {
        var segs = new List<SegmentResult> { Seg("A", ("s␟b", 50, 10, 500)), Seg("B", ("s␟b", 50, 10, 400)) with { ReadyGapSummary = "安全な間 600 ms" } };
        var md = ThroughputComparison.Markdown(segs, ThroughputComparison.Judge(segs), "0.40.2", "x", 240);
        Assert.Contains("同じ (画面, 手) どうしの比較", md);
        Assert.Contains("-100", md);
        Assert.Contains("安全な間 600 ms", md);
    }
}
