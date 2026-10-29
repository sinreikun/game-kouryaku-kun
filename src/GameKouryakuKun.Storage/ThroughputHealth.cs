using System.Text.Json;

namespace GameKouryakuKun.Storage;

/// <summary>
/// 高速経験収集（docs/throughput-parallel-sensing-design.md §9）の健全性。ログの step_timing / sensor_result / ledger_summary /
/// adaptive_settle / noise_annotation / raw_agg_summary / observer_bus を集計する。プレイの上手さは評価しない。
/// 対応違反（Action→Observation→Result の対応を壊した）は FAIL。pending 未解決は WARN。
/// </summary>
public static class ThroughputHealth
{
    public const string Area = "高速経験収集";
    public const double CoverageTolerance = 0.05;   // 根拠: 設計 §6 T0（区間合計が手の間隔を ±5% で説明）

    private static string T(JsonElement e) => e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
    private static double N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
    private static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
    private static double Med(IEnumerable<double> xs)
    {
        var a = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a[a.Length / 2];
    }

    public static void Add(IReadOnlyList<JsonElement> cur, Action<string, string, HealthReport.Level, string> add)
    {
        var timing = cur.Where(e => T(e) == "step_timing").ToList();
        if (timing.Count == 0) add(Area, "区間計測 (T0)", HealthReport.Level.NotTested, "今回のログに step_timing なし（GKK_STEP_TIMING OFF）");
        else
        {
            string M(string k) => Math.Round(Med(timing.Select(e => N(e, k))), 1).ToString();
            add(Area, "区間計測 (T0)", HealthReport.Level.Info,
                $"手 {timing.Count}・中央値 decide {M("decide_ms")}・send {M("send_ms")}・settle {M("settle_ms")}・commit {M("commit_ms")}・obs_wait {M("obs_wait_ms")}・post {M("post_ms")}・合計 {M("total_ms")} ms");
            var sum = cur.LastOrDefault(e => T(e) == "step_timing_summary");
            if (sum.ValueKind == JsonValueKind.Object)
            {
                double cov = N(sum, "coverage");
                var lvl = double.IsNaN(cov) ? HealthReport.Level.NotTested : Math.Abs(cov - 1) <= CoverageTolerance ? HealthReport.Level.Ok : HealthReport.Level.Warn;
                add(Area, "手の速さ", lvl, $"{N(sum, "actions_per_min"):0.0} 手/分・区間合計/手の間隔 {cov:0.000}（±{CoverageTolerance:P0} で PASS）");
            }
        }

        var ledger = cur.LastOrDefault(e => T(e) == "ledger_summary");
        var sr = cur.Where(e => T(e) == "sensor_result").ToList();
        if (ledger.ValueKind != JsonValueKind.Object && sr.Count == 0)
            add(Area, "非同期観測の契約 (T1/T5)", HealthReport.Level.NotTested, "今回のログに sensor_result / ledger_summary なし（GKK_PIPELINE_SENSING・GKK_PARALLEL_OBSERVERS OFF）");
        else
        {
            var byProv = sr.GroupBy(e => S(e, "provenance")).ToDictionary(g => g.Key, g => g.Count());
            string provs = string.Join(" ", byProv.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));
            if (ledger.ValueKind == JsonValueKind.Object)
            {
                double viol = N(ledger, "violations"), unres = N(ledger, "unresolved");
                add(Area, "Action→Observation→Result の対応", viol > 0 ? HealthReport.Level.Fail : HealthReport.Level.Ok,
                    $"対応違反 {viol}・開いた {N(ledger, "opened")}・合流 {provs}");
                add(Area, "pending の解決", unres > 0 ? HealthReport.Level.Warn : HealthReport.Level.Ok,
                    $"drain 後の未解決 {unres}・late-amend 成功率 {N(ledger, "late_amend_success"):P0}・missing {N(ledger, "missing")}・前の観測未完で決めた手 {N(ledger, "decided_with_pending")}");
                add(Area, "真の throughput", HealthReport.Level.Info, S(ledger, "metrics"));
            }
            else add(Area, "Action→Observation→Result の対応", HealthReport.Level.Warn, $"ledger_summary なし（実行が途中で終わった可能性）・sensor_result {sr.Count}（{provs}）");
        }

        var settle = cur.LastOrDefault(e => T(e) == "adaptive_settle_summary");
        if (settle.ValueKind != JsonValueKind.Object) add(Area, "適応的静止判定 (T2)", HealthReport.Level.NotTested, "adaptive_settle_summary なし（GKK_ADAPTIVE_SETTLE OFF）");
        else
        {
            double lr = N(settle, "late_rate");
            add(Area, "適応的静止判定 (T2)", lr > HealthReport.LateChangeWarnRate ? HealthReport.Level.Warn : HealthReport.Level.Ok,
                $"画面ごとに短くした手 {N(settle, "applied")}・戻した {N(settle, "reverted")}・早すぎた割合 {lr:P1}（{HealthReport.LateChangeWarnRate:P0} 超で WARN）・{S(settle, "screens")}");
        }

        var noise = cur.LastOrDefault(e => T(e) == "noise_summary");
        add(Area, "自然変化モデル (T3)", noise.ValueKind == JsonValueKind.Object ? HealthReport.Level.Info : HealthReport.Level.NotTested,
            noise.ValueKind == JsonValueKind.Object ? S(noise, "summary") + "（注釈のみ・学習経路と観測は不変）" : "noise_summary なし（GKK_NATURAL_NOISE_MODEL OFF）");

        var raw = cur.LastOrDefault(e => T(e) == "raw_agg_summary");
        if (raw.ValueKind != JsonValueKind.Object) add(Area, "Raw/Aggregate 分離 (T4)", HealthReport.Level.NotTested, "raw_agg_summary なし（GKK_RAW_AGG_SPLIT OFF）");
        else
        {
            double ret = N(raw, "retention");
            add(Area, "Raw/Aggregate 分離 (T4)", ret < 1 ? HealthReport.Level.Warn : HealthReport.Level.Ok,
                $"Raw {N(raw, "raw")}・集約 {N(raw, "agg")}・重複率 {N(raw, "dup_ratio"):P1}・Raw 保持率 {ret:P0}・再集約の一致 {S(raw, "replay_ok")}");
            if (S(raw, "replay_ok") == "False") add(Area, "Raw からの再集約", HealthReport.Level.Fail, "Raw をリプレイした集約が保存済みの集約と一致しない（再解釈可能性の破損）");
        }

        var bus = cur.LastOrDefault(e => T(e) == "observer_bus");
        if (bus.ValueKind != JsonValueKind.Object) add(Area, "並列観測器 (T5)", HealthReport.Level.NotTested, "observer_bus なし（GKK_PARALLEL_OBSERVERS OFF）");
        else
        {
            double dropped = N(bus, "settle_dropped");
            add(Area, "並列観測器 (T5)", dropped > 0 ? HealthReport.Level.Fail : HealthReport.Level.Ok,
                $"静止確定の結果の間引き {dropped}（0 以外は FAIL）・間引いた中間 {N(bus, "coalesced")}・late {N(bus, "late")}・並べ替え {N(bus, "reordered")}・{S(bus, "sensors")}");
        }
    }
}
