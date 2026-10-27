using System.Globalization;
using System.Text;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// 判断の記録を人向けの文に直す（説明層。Viewer と診断用）。記録にある数値をそのまま並べるだけで、新しい判断や意味は加えない
/// </summary>
public static class DecisionText
{
    private static string F(double x, string f = "0.00") => x.ToString(f, CultureInfo.InvariantCulture);

    public static string RuleText(string rule) => rule switch
    {
        "scored" => "点数による抽選", "momentum" => "直前の操作を続けた", "chord_probe" => "2キー同時押しのためし",
        "sequence_step" => "操作の並びの途中の手", "fallback" => "候補なし（ランダム）", _ => rule,
    };

    /// <summary>予測の証拠の出どころ（直接・そのうち読み直し・弱い事前の推定）</summary>
    public static string Evidence(PredictedOutcome p)
    {
        var sb = new StringBuilder($"直接の経験 {F(p.DirectN, "0.#")} 回");
        if (p.ReinterpretedN > 0) sb.Append($"（うち分割で読み直した手 {F(p.ReinterpretedN, "0.#")}）");
        if (p.PriorN > 0) sb.Append($"・弱い事前の推定 {F(p.PriorN, "0.#")} 回分（{string.Join(", ", p.PriorSources)}）");
        return sb.ToString();
    }

    public static string Choice(DecisionRecord d)
    {
        var sb = new StringBuilder();
        sb.Append($"画面 {d.Node} で「{d.Chosen}」{(d.Composite != null ? $"（{d.Composite} の手）" : "")}");
        sb.Append($"\n規則: {RuleText(d.Rule)}{(double.IsFinite(d.Probability) ? $"・選ばれる確率 {F(d.Probability * 100, "0.#")}%" : "")}");
        if (d.Prediction is { } p)
        {
            sb.Append(p.TopNext == null ? "\n予測: 材料なし（初めての関係）" : $"\n予測: 次の画面 {p.TopNext}（{F(p.PTop * 100, "0")}%）・画面が変わる {F(p.PChange * 100, "0")}%・不確かさ {F(p.Uncertainty)}");
            sb.Append($"\n予測の証拠: {Evidence(p)}");
        }
        return sb.ToString();
    }

    /// <summary>候補の点数の内訳（確率の高い順に k 個）</summary>
    public static string Candidates(DecisionRecord d, int k = 6)
    {
        if (d.Candidates.Count == 0) return $"（{RuleText(d.Rule)}のため点数の比較なし）";
        var sb = new StringBuilder($"候補 {d.Candidates.Count} 個（点数 = 可逆性 × (0.5 + 未試行 + 割合 + 0.5×反応の情報 + 0.5×予測の不確かさ)）");
        foreach (var c in d.Candidates.OrderByDescending(c => c.Probability).Take(k))
        {
            sb.Append($"\n{c.Action}: {F(c.Probability * 100, "0.#")}%・点数 {F(c.Score)}（試行 {c.DirectTries}・未試行 {F(c.Novelty)}・割合 {F(c.Rates)}・反応 {F(c.Information)}・不確かさ {F(c.Uncertainty)}・可逆性 ×{F(c.Reversibility)}" +
                      $"{(c.Stagnant ? "・停滞 ×3" : "")}{(c.Deferred ? "・後回しの解禁" : "")}{(c.Priors.Count > 0 ? $"・推定 {string.Join(",", c.Priors)}" : "")}）");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 観測の確かさ: 結果の画面を、見た目の状態・画面の種類に割り当てたときの距離の比（1 未満＝既存のものに近い、1 以上＝新しく作った）、
    /// 判定に使った画面の古さ、境目の近くかどうか（境目の近くの手の外れは内部モデルの作り直しに使わない）
    /// </summary>
    public static string Observation(DecisionRecord d)
    {
        var parts = new List<string>();
        if (double.IsFinite(d.ObsVisRatio)) parts.Add($"見た目の状態の割り当て 距離の比 {F(d.ObsVisRatio)}{(d.ObsVisRatio >= 1 ? "（新しい状態）" : "")}");
        if (double.IsFinite(d.ObsModeRatio)) parts.Add($"画面の種類の割り当て 距離の比 {F(d.ObsModeRatio)}{(d.ObsModeRatio >= 1 ? "（新しい種類）" : "")}");
        if (double.IsFinite(d.FrameAgeMs)) parts.Add($"判定に使った画面の古さ {F(d.FrameAgeMs, "0")} ms");
        if (parts.Count == 0) return "観測: 記録なし";
        return "観測: " + string.Join("・", parts) + (d.Ambiguous ? "\n→ 割り当てが境目の近く（どちらともいえる）ため、この手の外れは予測の作り直しに使いません" : "・割り当ては境目から離れている");
    }

    public static string Result(DecisionRecord d)
    {
        if (d.Check is not { } c) return "—";
        var sb = new StringBuilder($"結果: 次の画面 {c.Actual}・{(c.Changed ? "画面変化あり" : "画面変化なし")}");
        sb.Append(c.Prediction.TopNext == null ? "（予測の材料なし）" : c.Hit ? "（予測どおり）" : $"（予測 {c.Prediction.TopNext} と違う）");
        sb.Append($"\n外れ: {F(c.Surprisal)} ビット（ふだんの不確かさとの差 {F(c.Excess)}）・起きた結果の予測の確率 {F(c.PActual * 100, "0.#")}%");
        if (c.Revision is { } r) sb.Append($"\n修正: {r.Reason}");
        sb.Append('\n').Append(Observation(d));
        return sb.ToString();
    }

    public static string Revisions(IEnumerable<ModelRevision> rs)
    {
        var l = rs.ToList();
        if (l.Count == 0) return "この回はまだありません（予測の外れ方が持続的に変わった関係だけ、古い証拠の重みを下げて予測を作り直します。記録は消しません）";
        return string.Join("\n", l.Select(r => $"#{r.Seq} 画面 {r.Node}「{r.Action}」: 証拠の重み {F(r.WeightBefore, "0.#")} → {F(r.WeightAfter, "0.#")}（最近の {r.RecentKept} 回を残した）"));
    }
}
