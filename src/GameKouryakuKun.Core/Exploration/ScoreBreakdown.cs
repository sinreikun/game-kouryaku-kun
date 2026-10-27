namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.27.5: 手の選択の点数の内訳の記録と集計（診断だけ。選択には使わない）。
/// 点数 = Reversibility × (0.5 + Sum × Destination × Stagnation)、Sum = Novelty + Discovery + Leave + Change + Text + InfoTerm + UncTerm。
/// 「その項を除くと、いちばん点数の高い候補が変わるか」で、項が選択に効いた手を数える（記録から同じ計算で再現できる）。
/// </summary>
public sealed class ScoreBreakdown
{
    public static readonly string[] Fields = { "action", "score", "p", "tries", "rev", "novelty", "discovery", "leave", "change", "text", "info", "unc", "dest", "stag", "nat_change_cut", "nat_leave_cut", "wait_visual", "wait_leave", "wait_pixel", "wait_text", "wait_local", "excluded_by_user" };
    public static readonly string[] Terms = { "novelty", "discovery", "leave", "change", "text", "info", "unc" };

    public static object?[] Row(CandidateScore c)
    {
        static double R(double x) => Math.Round(x, 4);
        return new object?[] { c.Action, R(c.Score), R(c.Probability), c.DirectTries, R(c.Reversibility), R(c.Novelty), R(c.Discovery), R(c.Leave), R(c.Change), R(c.Text), R(c.InfoTerm), R(c.UncTerm), R(c.Destination), c.StagnationFactor, R(c.NaturalChangeCut), R(c.NaturalLeaveCut),
            c.Wait is { } w ? R(w.Visual) : null, c.Wait is { } w2 ? R(w2.Leave) : null, c.Wait is { } w3 ? R(w3.Pixel) : null, c.Wait is { } w4 ? R(w4.Text) : null, c.Wait is { } w5 ? R(w5.Local) : null, c.ExcludedByUser };
    }

    public static double Term(CandidateScore c, string t) => t switch
    {
        "novelty" => c.Novelty, "discovery" => c.Discovery, "leave" => c.Leave, "change" => c.Change, "text" => c.Text, "info" => c.InfoTerm, "unc" => c.UncTerm, _ => 0,
    };

    /// <summary>項 t を除いた（または add を足した）ときの点数（同じ式）</summary>
    public static double ScoreWithout(CandidateScore c, string? t, double add = 0)
    {
        double sum = Terms.Sum(x => Term(c, x)) - (t == null ? 0 : Term(c, t)) + add;
        return c.Reversibility * (0.5 + sum * c.Destination * c.StagnationFactor);
    }

    public long Steps { get; private set; }
    public long Scored { get; private set; }
    public long TextFlips { get; private set; }
    public long NaturalFlips { get; private set; }
    private readonly Dictionary<string, long> _flips = Terms.ToDictionary(t => t, _ => 0L);
    private readonly Dictionary<string, double> _chosenSum = Terms.ToDictionary(t => t, _ => 0.0);
    private readonly Dictionary<string, long> _rules = new();

    public void Add(string rule, IReadOnlyList<CandidateScore> cands, string chosen)
    {
        Steps++; _rules[rule] = _rules.GetValueOrDefault(rule) + 1;
        if (rule != "scored" || cands.Count == 0) return;
        Scored++;
        var top = cands.OrderByDescending(c => c.Score).First().Action;
        foreach (var t in Terms)
            if (cands.OrderByDescending(c => ScoreWithout(c, t)).First().Action != top) _flips[t]++;
        TextFlips = _flips["text"];
        // 入力なしの差し引きを戻したら（差し引かなかったら）いちばんが変わるか
        if (cands.OrderByDescending(c => ScoreWithout(c, null, c.NaturalChangeCut + c.NaturalLeaveCut)).First().Action != top) NaturalFlips++;
        if (cands.FirstOrDefault(c => c.Action == chosen) is { } ch) foreach (var t in Terms) _chosenSum[t] += Term(ch, t);
    }

    // v0.28.1: 項ごとの短期の変化と長期の寄与（診断だけ。重みは変えない）。選んだ手の項の割合で、その手の結果を項に配る
    private readonly Dictionary<string, double> _w = Terms.ToDictionary(t => t, _ => 0.0), _short = Terms.ToDictionary(t => t, _ => 0.0),
        _edge = Terms.ToDictionary(t => t, _ => 0.0), _node = Terms.ToDictionary(t => t, _ => 0.0);
    private readonly List<double[]> _chosenTerms = new();
    public long Outcomes { get; private set; }

    /// <summary>選んだ手の結果: changed＝画面が変わった（短期）・newEdge＝遷移の図に新しい辺・newNode＝新しい画面（長期の広がり）</summary>
    public void Outcome(CandidateScore? chosen, bool changed, bool newEdge, bool newNode)
    {
        if (chosen == null) return;
        var v = Terms.Select(t => Math.Max(0, Term(chosen, t))).ToArray(); double sum = v.Sum(); if (sum <= 0) return;
        Outcomes++; _chosenTerms.Add(v);
        for (int i = 0; i < Terms.Length; i++)
        {
            double w = v[i] / sum; var t = Terms[i];
            _w[t] += w; if (changed) _short[t] += w; if (newEdge) _edge[t] += w; if (newNode) _node[t] += w;
        }
    }

    public (double Weight, double Short, double Edge, double Node) Contribution(string t) => (_w[t], _short[t], _edge[t], _node[t]);

    /// <summary>前半と後半で、選んだ手の項の割合の平均（項の支配の移り変わり）</summary>
    public string Halves()
    {
        if (_chosenTerms.Count < 2) return "手が足りない";
        string H(IEnumerable<double[]> x) { var l = x.ToList(); return string.Join("・", Terms.Select((t, i) => $"{t} {l.Average(a => a.Sum() > 0 ? a[i] / a.Sum() : 0):P0}")); }
        int h = _chosenTerms.Count / 2;
        return $"前半 {H(_chosenTerms.Take(h))}／後半 {H(_chosenTerms.Skip(h))}";
    }

    public string ContributionSummary() => string.Join("・", Terms.Where(t => _w[t] > 0).Select(t =>
        $"{t} 重み {_w[t]:0.#}: 画面が変わった {_short[t] / _w[t]:P0}・新しい辺 {_edge[t] / _w[t]:P0}・新しい画面 {_node[t] / _w[t]:P0}"));

    public string Summary() =>
        $"記録した手 {Steps}（{string.Join("・", _rules.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))}）／点数で選んだ手 {Scored}: 選んだ手の項の平均 " +
        string.Join("・", Terms.Select(t => $"{t} {(Scored > 0 ? _chosenSum[t] / Scored : 0):0.00}")) +
        "／除くといちばんが変わる手 " + string.Join("・", Terms.Select(t => $"{t} {_flips[t]}")) + $"・入力なしの差し引き {NaturalFlips}";
}
