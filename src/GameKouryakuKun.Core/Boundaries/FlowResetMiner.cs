using GameKouryakuKun.Core.Exploration;

namespace GameKouryakuKun.Core.Boundaries;

/// <summary>
/// エピソードの区切りの候補（v0.23.0 ブロック C。src/episode-boundary-design.md）。
/// ゲーム固有の区切り（失敗・達成など）の定義は持たない。連続経験履歴だけから、統計で次のような状態を候補にする:
///  ・大きな遷移（画面の変化率がこの履歴の上位の側）で入ってくることが多い
///  ・さまざまな前の状態から入ってくる（どこから来たかに依らない＝前の文脈がそこで切れている）
///  ・入った後の流れ（次の数手の状態の並び）が、どの前の状態から来ても同じようにくり返す
/// 再起動・アプリの起動し直し（実行の切れ目）・Supervisor の復旧はエピソードの終わりとしない: そこをまたいで流れをつながず、到着としても数えない。
/// 候補は記録と表示だけで、報酬・価値・予測・手の選択・目的には使わない（静的検査）。
/// </summary>
public sealed class FlowResetMiner
{
    public int FlowLength { get; init; } = 3;               // 根拠: 汎用（くり返す流れを見分けられる短い長さ）
    public int MinArrivals { get; init; } = 5;              // 根拠: 汎用（くり返しを言える最小の回数）
    public int MinPredecessors { get; init; } = 3;          // 根拠: 汎用（「どこから来ても」を言える最小の種類）
    public double LargeQuantile { get; init; } = 0.75;      // 根拠: 汎用（変化した手の中で上位 4 分の 1 を「大きな遷移」）
    public double MinLargeShare { get; init; } = 0.5;       // 根拠: 汎用（到着の過半が大きな遷移）
    public double MinFlowRecurrence { get; init; } = 0.6;   // 根拠: 汎用（到着の後の流れが過半より十分多く同じ）
    public double MaxContextDependence { get; init; } = 0.5; // 根拠: 汎用（前の状態を知っても後の流れの不確かさがほとんど減らない＝文脈が切れている）

    public sealed record Candidate(string State, int Arrivals, int Predecessors, double LargeShare, string Flow, double Recurrence,
        int PredecessorsWithFlow, double ContextDependence, long FirstSeq, long LastSeq, bool Accepted, string Why);

    public sealed record Result(IReadOnlyList<Candidate> Accepted, IReadOnlyList<Candidate> Examined, double LargeThreshold, int Steps, int Breaks);

    /// <summary>breaksBefore: その通し番号の手の前で履歴が切れている（実行の始まり・復旧の後）。そこをまたいで流れをつながない</summary>
    public Result Mine(IReadOnlyList<StreamStep> stream, ISet<long> breaksBefore)
    {
        var xs = stream.OrderBy(s => s.Seq).ToList();
        var changed = xs.Where(s => s.State != s.NextState && s.ChangeRatio > 0).Select(s => s.ChangeRatio).OrderBy(x => x).ToList();
        double large = changed.Count == 0 ? double.PositiveInfinity : changed[Math.Min(changed.Count - 1, (int)Math.Floor(LargeQuantile * (changed.Count - 1)))];
        // 連続: 次の手の前の状態が今の手の後の状態と同じで、切れ目でない
        bool Linked(int i) => i + 1 < xs.Count && !breaksBefore.Contains(xs[i + 1].Seq) && xs[i + 1].Seq == xs[i].Seq + 1 && xs[i + 1].State == xs[i].NextState;
        var arrivals = new Dictionary<string, List<(string Pred, bool Large, string Flow, long Seq)>>(StringComparer.Ordinal);
        for (int i = 0; i < xs.Count; i++)
        {
            var s = xs[i];
            if (s.State == s.NextState) continue;
            // 流れ: 到着の後の FlowLength 手の行き先（つながっていなければ数えない）
            var flow = new List<string>(); int j = i;
            while (flow.Count < FlowLength && Linked(j)) { j++; flow.Add(xs[j].Action + ">" + xs[j].NextState); }
            if (flow.Count < FlowLength) continue;
            // 流れは状態の並びで比べる（操作は自動の探索の選び方で変わるので、状態の流れを見る）
            string sig = string.Join(" → ", flow.Select(f => f[(f.IndexOf('>') + 1)..]));
            if (!arrivals.TryGetValue(s.NextState, out var l)) arrivals[s.NextState] = l = new();
            l.Add((s.State, s.ChangeRatio >= large, sig, s.Seq));
        }
        var examined = new List<Candidate>();
        foreach (var (st, l) in arrivals)
        {
            if (l.Count < MinArrivals) continue;
            int preds = l.Select(x => x.Pred).Distinct().Count();
            double largeShare = l.Count(x => x.Large) / (double)l.Count;
            var modal = l.GroupBy(x => x.Flow).OrderByDescending(g => g.Count()).First();
            double rec = modal.Count() / (double)l.Count;
            int predsWithFlow = modal.Select(x => x.Pred).Distinct().Count();
            // 文脈への依存: 後の流れのエントロピーが、前の状態を知るとどれだけ減るか（相互情報量 ÷ 流れのエントロピー）
            double hFlow = Entropy(l.GroupBy(x => x.Flow).Select(g => g.Count()));
            double hCond = l.GroupBy(x => x.Pred).Sum(g => g.Count() / (double)l.Count * Entropy(g.GroupBy(x => x.Flow).Select(h => h.Count())));
            double dep = hFlow <= 1e-9 ? 0 : (hFlow - hCond) / hFlow;
            var why = new List<string>();
            if (preds < MinPredecessors) why.Add($"前の状態 {preds} 種類");
            if (largeShare < MinLargeShare) why.Add($"大きな遷移 {largeShare:P0}");
            if (rec < MinFlowRecurrence) why.Add($"流れのくり返し {rec:P0}");
            if (predsWithFlow < MinPredecessors) why.Add($"同じ流れの前の状態 {predsWithFlow} 種類");
            if (dep > MaxContextDependence) why.Add($"前の状態への依存 {dep:0.00}");
            examined.Add(new Candidate(st, l.Count, preds, largeShare, modal.Key, rec, predsWithFlow, dep, l.Min(x => x.Seq), l.Max(x => x.Seq), why.Count == 0, why.Count == 0 ? "条件をすべて満たす" : string.Join("・", why)));
        }
        return new Result(examined.Where(c => c.Accepted).OrderByDescending(c => c.Arrivals).ToList(), examined.OrderByDescending(c => c.Arrivals).ToList(), large, xs.Count, breaksBefore.Count);
    }

    private static double Entropy(IEnumerable<int> counts)
    {
        var c = counts.Where(x => x > 0).ToList(); double n = c.Sum(); if (n <= 0) return 0;
        return Math.Max(0, -c.Sum(x => x / n * Math.Log2(x / n)));
    }
}
