using GameKouryakuKun.Core.Throughput;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class ThroughputComparisonTests : IDisposable
{
    public void Dispose() { ThroughputFlags.Override = null; Environment.SetEnvironmentVariable(ThroughputFlags.PipelineSensingEnv, null); ThroughputFlags.ApplyEffective(); }

    private static SegmentResult S(string l, int blk, double cio, int idx = 0, long viol = 0, double commit = 1) => new()
    {
        Label = l, Block = blk, Index = idx, Actions = 200, ActionsPerMinute = cio, CioPerMinute = cio, Violations = viol, Opened = 200,
        MemoryCommits = (long)(200 * commit), ObservationsSaved = 200, DecisionsRecorded = 200, WorldChecked = 100, WorldHits = 50, TextPairs = 190,
    };

    [Fact]
    public void Plan_is_abba_balanced()
    {
        var p = ThroughputComparison.Plan();
        Assert.Equal("ABCDDCBA", string.Concat(p.Select(x => x.Label)));
        foreach (var l in new[] { "A", "B", "C", "D" }) Assert.Equal(9, p.Where(x => x.Label == l).Sum(x => x.Index));   // 平均の実行位置が同じ
        Assert.Equal(new[] { 1, 1, 1, 1, 2, 2, 2, 2 }, p.Select(x => x.Block).ToArray());
    }

    [Fact]
    public void Configs_are_cumulative_and_a_is_measurement_only()
    {
        var a = ThroughputComparison.Config("A");
        Assert.True(a.StepTiming && a.MeasureLedger);
        Assert.False(a.PipelineSensing || a.AdaptiveSettle || a.NaturalNoise || a.RawAggSplit || a.ParallelObservers);
        Assert.True(ThroughputComparison.Config("B").PipelineSensing);
        Assert.True(ThroughputComparison.Config("C").AdaptiveSettle && ThroughputComparison.Config("C").PipelineSensing);
        var d = ThroughputComparison.Config("D");
        Assert.True(d.StepTiming && d.PipelineSensing && d.AdaptiveSettle && d.NaturalNoise && d.RawAggSplit && d.ParallelObservers);
    }

    [Fact]
    public void Override_wins_over_environment_so_no_env_vars_are_needed()
    {
        Environment.SetEnvironmentVariable(ThroughputFlags.PipelineSensingEnv, "1");
        ThroughputFlags.Override = ThroughputComparison.Config("A");
        ThroughputFlags.ApplyEffective();
        Assert.False(ThroughputFlags.PipelineSensing);
        Assert.True(ThroughputFlags.LedgerActive);   // 計測の台帳だけ
        ThroughputFlags.Override = ThroughputComparison.Config("D");
        Environment.SetEnvironmentVariable(ThroughputFlags.PipelineSensingEnv, null);
        ThroughputFlags.ApplyEffective();
        Assert.True(ThroughputFlags.ParallelObservers);
        Assert.Contains("自動比較 D", ThroughputFlags.StatusText());
        ThroughputFlags.Override = null; ThroughputFlags.ApplyEffective();
        Assert.False(ThroughputFlags.LedgerActive);   // 終われば既定（すべて OFF）
    }

    [Fact]
    public void Failed_config_falls_back_to_baseline()
    {
        var failed = new HashSet<string> { "C" };
        Assert.Equal("A", ThroughputComparison.NextLabel("C", failed));
        Assert.Equal("D", ThroughputComparison.NextLabel("D", failed));
    }

    [Fact]
    public void Verdicts_adopt_reject_inconclusive()
    {
        var segs = new List<SegmentResult>
        {
            S("A", 1, 45), S("B", 1, 60), S("C", 1, 70, viol: 1), S("D", 1, 48),
            S("D", 2, 47), S("C", 2, 72), S("B", 2, 58), S("A", 2, 44),
        };
        var v = ThroughputComparison.Judge(segs).ToDictionary(x => x.Label);
        Assert.Equal(ThroughputComparison.Verdict.Adopt, v["B"].Verdict);
        Assert.Equal(ThroughputComparison.Verdict.Reject, v["C"].Verdict);     // 対応違反＝FAIL
        Assert.Equal(ThroughputComparison.Verdict.Inconclusive, v["D"].Verdict);
    }

    [Fact]
    public void Quality_regression_rejects_even_if_faster()
    {
        var segs = new List<SegmentResult> { S("A", 1, 45), S("B", 1, 90, commit: 0.5), S("A", 2, 45), S("B", 2, 90, commit: 0.5) };
        var b = ThroughputComparison.Judge(segs).First(x => x.Label == "B");
        Assert.Equal(ThroughputComparison.Verdict.Reject, b.Verdict);
        Assert.Contains(b.Regressions, r => r.Contains("継続学習"));
    }

    [Fact]
    public void Missing_pair_is_inconclusive()
    {
        var b = ThroughputComparison.Judge(new List<SegmentResult> { S("A", 1, 45), S("B", 1, 80) }).First(x => x.Label == "B");
        Assert.Equal(ThroughputComparison.Verdict.Inconclusive, b.Verdict);
    }

    [Fact]
    public void Report_contains_all_metrics_in_japanese_and_json()
    {
        var segs = new List<SegmentResult> { S("A", 1, 45, 1), S("B", 1, 60, 2) };
        var vs = ThroughputComparison.Judge(segs);
        var md = ThroughputComparison.Markdown(segs, vs, "0.40.0", "2026-10-29 16:00:00", 240);
        foreach (var k in new[] { "手/分", "因果保持観測/分", "対応違反", "pending 未解決", "後から合流", "早すぎた変化なし", "Raw 再集約", "予測の当たり", "判定" }) Assert.Contains(k, md);
        var js = ThroughputComparison.Json(segs, vs, "0.40.0", "x", 240);
        Assert.Contains("\"defaults_changed\": false", js);
        Assert.Contains("cio_per_min", js);
    }

    private static Dictionary<string, (long, long)> W(params (string, long, long)[] xs) => xs.ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));

    [Fact]
    public void Screen_mix_difference_is_not_a_prediction_regression()
    {
        // 実機 2026-10-29: B は易しい画面と難しい画面を半々に訪れ、全体の当たりは 79%（A 88%）。同じ画面どうしでは同じ
        var a = S("A", 1, 45) with { WorldChecked = 100, WorldHits = 88, WorldPSum = 88, WorldByNode = W(("easy", 80, 76), ("hard", 20, 12)) };
        var b = S("B", 1, 90) with { WorldChecked = 200, WorldHits = 158, WorldPSum = 158, WorldByNode = W(("easy", 100, 95), ("hard", 100, 63)) };
        Assert.True(b.WorldHitRate < a.WorldHitRate - 0.05);
        Assert.DoesNotContain(ThroughputComparison.Regressions(b, a), r => r.StartsWith("Prediction"));
    }

    [Fact]
    public void Same_screen_prediction_drop_is_a_regression()
    {
        var a = S("A", 1, 45) with { WorldChecked = 200, WorldHits = 180, WorldPSum = 180, WorldByNode = W(("s", 200, 180)) };
        var b = S("B", 1, 90) with { WorldChecked = 200, WorldHits = 120, WorldPSum = 120, WorldByNode = W(("s", 200, 120)) };
        Assert.Contains(ThroughputComparison.Regressions(b, a), r => r.Contains("同じ画面どうし"));
    }

    [Fact]
    public void Calibration_gap_regression()
    {
        var a = S("A", 1, 45) with { WorldChecked = 100, WorldHits = 80, WorldPSum = 82 };
        var b = S("B", 1, 90) with { WorldChecked = 100, WorldHits = 50, WorldPSum = 90 };
        Assert.Contains(ThroughputComparison.Regressions(b, a), r => r.Contains("較正のずれ"));
    }

    [Fact]
    public void Causally_intact_counts_actions_not_sensor_results()
    {
        // 実機 2026-10-29: 観測器 4 つの D で因果保持観測/分が手/分の約 4 倍になっていた
        var l = new ObservationLedger();
        foreach (var step in new[] { 1, 2 })
        {
            l.BeginAction(step);
            foreach (var sn in new[] { "ocr", "frame_diff", "noise", "noise_gap" }) l.Open(step, step, sn, step, 0, FramePosition.Settle, 0);
            foreach (var sn in new[] { "ocr", "frame_diff", "noise", "noise_gap" }) l.Complete(step, sn, step, step, 0, 1);
        }
        l.BeginAction(3); l.Open(3, 3, "ocr", 3, 0, FramePosition.Settle, 0); l.Open(3, 3, "noise", 3, 0, FramePosition.Settle, 0);
        l.Complete(3, "ocr", 3, 3, 0, 1); l.Fail(3, "noise", 1, "x");   // missing を含む手は因果保持に数えない
        Assert.Equal(2, l.CausallyIntact);
        Assert.Equal(9, l.CausallyIntactResults);
    }
}
