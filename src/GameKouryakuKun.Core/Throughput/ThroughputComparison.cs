using System.Text;
using System.Text.Json;

namespace GameKouryakuKun.Core.Throughput;

/// <summary>1 区間（1 回の探索）の結果。Session が終わりに作る（数えるだけ。プレイの上手さは評価しない）</summary>
public sealed record SegmentResult
{
    public string Label { get; init; } = "";          // A/B/C/D
    public int Index { get; init; }                   // 実行順（1〜）
    public int Block { get; init; }                   // 1 = ABCD、2 = DCBA
    public string Flags { get; init; } = "";
    public double Minutes { get; init; }
    public int Actions { get; init; }
    public double ActionsPerMinute { get; init; }
    public double CioPerMinute { get; init; }
    public long Violations { get; init; }
    public long PendingUnresolved { get; init; }
    public long LateAmended { get; init; }
    public long Opened { get; init; }
    public long Missing { get; init; }
    public long SettleDropped { get; init; }
    public long NoChangeJudged { get; init; }
    public long LateChanges { get; init; }
    public string RawReplay { get; init; } = "n/a";   // ok / mismatch / n/a
    public long MemoryCommits { get; init; }
    public long ObservationsSaved { get; init; }
    public long DecisionsRecorded { get; init; }
    public long WorldChecked { get; init; }
    public long WorldHits { get; init; }
    public long TextPairs { get; init; }
    public double MedianSettleMs { get; init; }
    public string? AbortReason { get; init; }         // FAIL で打ち切ったとき
    public string EndReason { get; init; } = "";

    public double LateAttachRate => Opened == 0 ? 0 : (double)LateAmended / Opened;
    public double LateChangeRate => NoChangeJudged == 0 ? 0 : (double)LateChanges / NoChangeJudged;
    public double CommitRatio => Actions == 0 ? 0 : Math.Min(1, (double)MemoryCommits / Actions);
    public double ObsRatio => Actions == 0 ? 0 : Math.Min(1, (double)ObservationsSaved / Actions);
    public double DecisionRatio => Actions == 0 ? 0 : Math.Min(1, (double)DecisionsRecorded / Actions);
    public double PredictionCheckedRatio => ObservationsSaved == 0 ? 0 : Math.Min(1, (double)WorldChecked / ObservationsSaved);
    public double WorldHitRate => WorldChecked == 0 ? double.NaN : (double)WorldHits / WorldChecked;
    public double TextPairRatio => Actions == 0 ? 0 : Math.Min(1, (double)TextPairs / Actions);
    public bool Fail => Violations > 0 || SettleDropped > 0 || RawReplay == "mismatch" || AbortReason != null;
    public bool Warn => PendingUnresolved > 0 || LateChangeRate > LateChangeWarn;

    public const double LateChangeWarn = 0.05;   // 根拠: 診断の合格条件（HealthReport.LateChangeWarnRate と同じ）
}

/// <summary>
/// v0.40.0: 高速化比較の自動実行の計画と判定（docs/throughput-parallel-sensing-design.md §11）。
/// 順序は ABCD → DCBA（ABBA 型の釣り合い）。学習が進むと画面が既知になり手の速さ・新しさが時間とともに変わる（ドリフト）。
/// 各設定の平均の実行位置が同じ（1+8, 2+7, 3+6, 4+5 = 9）なので、時間に対して一次のドリフトは差から消える。ブロックごとに対で比べ、
/// 両方のブロックで同じ向きに改善したときだけ「採用」とする。判定は助言のみで、既定値は変えない。
/// </summary>
public static class ThroughputComparison
{
    public const int DefaultSegmentSeconds = 240;     // 根拠: 汎用（8 区間で約 32 分。#159 の 45 手/分で 1 区間 約 180 手＝率を言うのに足りる手数）
    public const double AdoptMinGain = 0.10;          // 根拠: 診断の合格条件（両ブロックで因果保持観測/分が 1 割以上増えたときだけ採用）
    public const double QualityDropTolerance = 0.05;  // 根拠: 診断の合格条件（記録・観測・判断・予測の付与・文字の対の割合が 5 ポイント以上落ちたら品質の後退）
    public const double HitDropSe = 2;                // 根拠: 汎用（予測の当たりの割合の差が標準誤差の 2 倍を超えて落ちたら後退）

    public static ThroughputFlags.FlagSet Config(string label) => label switch
    {
        "A" => new("A", true, false, false, false, false, false),
        "B" => new("B", true, true, false, false, false, false),
        "C" => new("C", true, true, true, false, false, false),
        "D" => new("D", true, true, true, true, true, true),
        _ => throw new ArgumentException(label),
    };

    public static string Describe(string label) => label switch
    {
        "A" => "A 基準（すべて OFF・区間計測と台帳の記帳だけ）",
        "B" => "B ＋OCR パイプライン",
        "C" => "C ＋適応的静止判定",
        "D" => "D 6 フラグすべて",
        _ => label,
    };

    /// <summary>実行計画: ABCD → DCBA。(順番, ブロック, ラベル)</summary>
    public static IReadOnlyList<(int Index, int Block, string Label)> Plan()
    {
        var order = new[] { "A", "B", "C", "D", "D", "C", "B", "A" };
        return order.Select((l, i) => (i + 1, i < 4 ? 1 : 2, l)).ToList();
    }

    /// <summary>FAIL で打ち切った設定の残りの区間は基準 A に置き換える（ラベルは「X→A」）</summary>
    public static string NextLabel(string planned, ISet<string> failed) => failed.Contains(planned) ? "A" : planned;

    public enum Verdict { Adopt, Reject, Inconclusive }

    public sealed record StageVerdict(string Label, Verdict Verdict, string Reason, double GainBlock1, double GainBlock2, IReadOnlyList<string> Regressions);

    private static double Gain(double x, double a) => a <= 0 ? (x > 0 ? 1 : 0) : (x - a) / a;

    /// <summary>X と A の対の品質の後退（Prediction / Causality / Replay / Continual Learning / Outer）</summary>
    public static List<string> Regressions(SegmentResult x, SegmentResult a)
    {
        var r = new List<string>();
        if (x.CommitRatio < a.CommitRatio - QualityDropTolerance) r.Add($"継続学習: memory への記録の割合 {x.CommitRatio:P0} < 基準 {a.CommitRatio:P0}");
        if (x.ObsRatio < a.ObsRatio - QualityDropTolerance) r.Add($"Replay: 保存した観測の割合 {x.ObsRatio:P0} < 基準 {a.ObsRatio:P0}");
        if (x.DecisionRatio < a.DecisionRatio - QualityDropTolerance) r.Add($"Outer: 判断の記録の割合 {x.DecisionRatio:P0} < 基準 {a.DecisionRatio:P0}");
        if (x.PredictionCheckedRatio < a.PredictionCheckedRatio - QualityDropTolerance) r.Add($"Prediction: 予測と照合した観測の割合 {x.PredictionCheckedRatio:P0} < 基準 {a.PredictionCheckedRatio:P0}");
        if (!double.IsNaN(x.WorldHitRate) && !double.IsNaN(a.WorldHitRate) && x.WorldChecked > 0 && a.WorldChecked > 0)
        {
            double p = (x.WorldHits + a.WorldHits) / (double)(x.WorldChecked + a.WorldChecked);
            double se = Math.Sqrt(Math.Max(1e-9, p * (1 - p) * (1.0 / x.WorldChecked + 1.0 / a.WorldChecked)));
            if (a.WorldHitRate - x.WorldHitRate > HitDropSe * se) r.Add($"Prediction: 予測の当たり {x.WorldHitRate:P0} < 基準 {a.WorldHitRate:P0}（2SE 超）");
        }
        if (x.TextPairRatio < a.TextPairRatio - QualityDropTolerance) r.Add($"Causality: 前後の文字の対を作れた割合 {x.TextPairRatio:P0} < 基準 {a.TextPairRatio:P0}");
        if (x.Violations > 0) r.Add($"Causality: 対応違反 {x.Violations}");
        if (x.PendingUnresolved > 0) r.Add($"Causality: 未解決の pending {x.PendingUnresolved}");
        if (x.RawReplay == "mismatch") r.Add("Replay: Raw からの再集約が不一致");
        return r;
    }

    public static IReadOnlyList<StageVerdict> Judge(IReadOnlyList<SegmentResult> segs)
    {
        var res = new List<StageVerdict>();
        foreach (var lab in new[] { "B", "C", "D" })
        {
            var xs = segs.Where(s => s.Label == lab).ToList();
            if (xs.Any(s => s.Fail))
            {
                var f = xs.First(s => s.Fail);
                res.Add(new(lab, Verdict.Reject, "FAIL: " + (f.AbortReason ?? (f.Violations > 0 ? "対応違反" : f.SettleDropped > 0 ? "静止確定の間引き" : "Raw の再集約不一致")), double.NaN, double.NaN, Regressions(f, segs.FirstOrDefault(s => s.Label == "A") ?? f)));
                continue;
            }
            var gains = new double[2]; var regs = new List<string>(); bool paired = true;
            for (int blk = 1; blk <= 2; blk++)
            {
                var x = xs.FirstOrDefault(s => s.Block == blk);
                var a = segs.FirstOrDefault(s => s.Label == "A" && s.Block == blk && !s.Fail);
                if (x == null || a == null || x.Actions == 0 || a.Actions == 0) { paired = false; gains[blk - 1] = double.NaN; continue; }
                gains[blk - 1] = Gain(x.CioPerMinute, a.CioPerMinute);
                regs.AddRange(Regressions(x, a).Select(t => $"ブロック{blk}: {t}"));
            }
            if (regs.Count > 0) res.Add(new(lab, Verdict.Reject, "品質の後退", gains[0], gains[1], regs));
            else if (!paired) res.Add(new(lab, Verdict.Inconclusive, "対になる区間が足りない（途中で止まった）", gains[0], gains[1], regs));
            else if (gains.All(g => g >= AdoptMinGain)) res.Add(new(lab, Verdict.Adopt, $"両ブロックで因果保持観測/分が {AdoptMinGain:P0} 以上増加・品質の後退なし", gains[0], gains[1], regs));
            else res.Add(new(lab, Verdict.Inconclusive, $"増加が両ブロックで {AdoptMinGain:P0} に届かない／向きがそろわない", gains[0], gains[1], regs));
        }
        return res;
    }

    private static string V(Verdict v) => v switch { Verdict.Adopt => "採用（推奨）", Verdict.Reject => "不採用", _ => "判定保留" };
    private static string F(double d, string f = "0.0") => double.IsNaN(d) ? "-" : d.ToString(f);

    public static string Markdown(IReadOnlyList<SegmentResult> segs, IReadOnlyList<StageVerdict> vs, string appVersion, string startedLocal, int segmentSeconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 高速化比較（自動実行） v{appVersion}");
        sb.AppendLine();
        sb.AppendLine($"- 開始: {startedLocal}（PC の現地時刻）・区間 {segmentSeconds / 60.0:0.#} 分 × {segs.Count}・順序 ABCD → DCBA（時間のドリフトを打ち消す釣り合いの順）");
        sb.AppendLine("- 主指標: 因果保持観測/分（Action→Observation→Result の対応が壊れていない、完成または後から合流した観測の数/分）");
        sb.AppendLine("- 判定は助言のみ。既定値（すべて OFF）は変えていない。経験はすべての区間で記録（区間の設定はログの throughput_segment に記録）");
        sb.AppendLine();
        sb.AppendLine("| # | 設定 | 手 | 手/分 | 因果保持観測/分 | 対応違反 | pending 未解決 | 後から合流 | 静止待ち中央値 ms | 早すぎた変化なし | Raw 再集約 | 状態 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in segs)
            sb.AppendLine($"| {s.Index} | {Describe(s.Label)} | {s.Actions} | {F(s.ActionsPerMinute)} | {F(s.CioPerMinute)} | {s.Violations} | {s.PendingUnresolved} | {s.LateAttachRate:P0} | {F(s.MedianSettleMs, "0")} | {s.LateChangeRate:P1} | {s.RawReplay} | {(s.Fail ? "FAIL" + (s.AbortReason != null ? $"（{s.AbortReason}）" : "") : s.Warn ? "WARN" : "PASS")} |");
        sb.AppendLine();
        sb.AppendLine("## 品質の指標（区間ごと）");
        sb.AppendLine("| # | 設定 | memory 記録 | 観測の保存 | 判断の記録 | 予測と照合 | 予測の当たり | 文字の対 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in segs)
            sb.AppendLine($"| {s.Index} | {s.Label} | {s.CommitRatio:P0} | {s.ObsRatio:P0} | {s.DecisionRatio:P0} | {s.PredictionCheckedRatio:P0} | {(double.IsNaN(s.WorldHitRate) ? "-" : s.WorldHitRate.ToString("P0"))} | {s.TextPairRatio:P0} |");
        sb.AppendLine();
        sb.AppendLine("## 判定（基準 A と同じブロックで対にして比較）");
        foreach (var v in vs)
        {
            sb.AppendLine($"- **{Describe(v.Label)}: {V(v.Verdict)}** — {v.Reason}（因果保持観測/分の増加 ブロック1 {(double.IsNaN(v.GainBlock1) ? "-" : v.GainBlock1.ToString("+0%;-0%"))}・ブロック2 {(double.IsNaN(v.GainBlock2) ? "-" : v.GainBlock2.ToString("+0%;-0%"))}）");
            foreach (var r in v.Regressions) sb.AppendLine($"  - {r}");
        }
        return sb.ToString();
    }

    public static string Json(IReadOnlyList<SegmentResult> segs, IReadOnlyList<StageVerdict> vs, string appVersion, string startedLocal, int segmentSeconds)
    {
        var o = new
        {
            app_version = appVersion, started_local = startedLocal, segment_seconds = segmentSeconds, order = "ABCD-DCBA",
            segments = segs.Select(s => new
            {
                s.Index, s.Block, s.Label, s.Flags, s.Minutes, s.Actions, actions_per_min = s.ActionsPerMinute, cio_per_min = s.CioPerMinute,
                s.Violations, pending_unresolved = s.PendingUnresolved, late_attach_rate = s.LateAttachRate, late_change_rate = s.LateChangeRate,
                settle_dropped = s.SettleDropped, raw_replay = s.RawReplay, median_settle_ms = s.MedianSettleMs,
                commit_ratio = s.CommitRatio, obs_ratio = s.ObsRatio, decision_ratio = s.DecisionRatio, prediction_checked_ratio = s.PredictionCheckedRatio,
                world_hit_rate = double.IsNaN(s.WorldHitRate) ? (double?)null : s.WorldHitRate, text_pair_ratio = s.TextPairRatio,
                fail = s.Fail, warn = s.Warn, abort_reason = s.AbortReason, end_reason = s.EndReason,
            }),
            verdicts = vs.Select(v => new
            {
                v.Label, verdict = v.Verdict.ToString().ToLowerInvariant(), v.Reason,
                gain_block1 = double.IsNaN(v.GainBlock1) ? (double?)null : v.GainBlock1, gain_block2 = double.IsNaN(v.GainBlock2) ? (double?)null : v.GainBlock2, regressions = v.Regressions,
            }),
            defaults_changed = false,
        };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
