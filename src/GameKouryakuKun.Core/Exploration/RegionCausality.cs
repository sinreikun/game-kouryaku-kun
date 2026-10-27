using System.Text.Json;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.28.0: 区画ごとに「手で変わる所」と「手によらず（入力なしでも）変わる所」を経験から分ける（前景・メニューなどの定義は持たない）。
/// 証拠は 2 つ: (1) 待つ手（入力なし）の前後での区画の変化（入力なしの基準）、(2) 手との共変動: 同じ画面で、区画の変化の割合が手の種類によって違うか
///  （手ごとに別の割合 対 すべての手で同じ割合 の周辺尤度の比＝ベイズ因子。強さは状態の分割と同じ ±ln10）。
/// 手の種類によらず同じ割合で変わる区画は、その割合を「手によらない変化の基準」として使う。観測は消さない（分け方は評価のための見方）。
/// </summary>
public sealed class RegionCausality
{
    public int Cells { get; }
    public int MinTries { get; init; } = 3 * SplitReview.RevertTriesFactor;   // 根拠: 状態の分割の取り消しと同じ（入力ごとに 3 回 × 2）
    private readonly Dictionary<(string Node, string Action), (int N, int[] C)> _a = new();
    private readonly Dictionary<string, (int N, int[] C)> _w = new(StringComparer.Ordinal);
    public RegionCausality(int cells) => Cells = cells;

    public void ObserveAction(string node, string action, IEnumerable<int> changed)
    {
        var (n, c) = _a.TryGetValue((node, action), out var x) ? x : (0, new int[Cells]);
        foreach (var i in changed.Distinct()) if (i >= 0 && i < Cells) c[i]++;
        _a[(node, action)] = (n + 1, c);
    }

    public void ObserveNatural(string node, IEnumerable<int> changed)
    {
        var (n, c) = _w.TryGetValue(node, out var x) ? x : (0, new int[Cells]);
        foreach (var i in changed.Distinct()) if (i >= 0 && i < Cells) c[i]++;
        _w[node] = (n + 1, c);
    }

    public int NaturalObservations(string node) => _w.TryGetValue(node, out var x) ? x.N : 0;
    public (int N, int C) ActionCounts(string node, string action, int cell) => _a.TryGetValue((node, action), out var x) ? (x.N, x.C[cell]) : (0, 0);

    /// <summary>手の種類による違いのベイズ因子の対数（正＝手で違う・負＝手によらない）と、比べた手の数</summary>
    public (double LogBf, int Groups) ActionDependence(string node, int cell)
    {
        var g = _a.Where(kv => kv.Key.Node == node && kv.Value.N >= MinTries).Select(kv => (kv.Value.C[cell], kv.Value.N - kv.Value.C[cell])).ToList();
        if (g.Count < SplitReview.RevertMinCommonActions) return (0, g.Count);
        double sep = g.Sum(x => LogBeta(x.Item1, x.Item2)), pooled = LogBeta(g.Sum(x => x.Item1), g.Sum(x => x.Item2));
        return (sep - pooled, g.Count);
    }

    /// <summary>区画の分け方: action（手で違う）・independent（手によらない）・unknown（証拠が足りない）</summary>
    public string Class(string node, int cell)
    {
        var (lbf, k) = ActionDependence(node, cell);
        if (k < SplitReview.RevertMinCommonActions) return "unknown";
        if (lbf >= SplitReview.RevertMinLogBayesFactor) return "action";
        if (lbf <= -SplitReview.RevertMinLogBayesFactor) return "independent";
        return "unknown";
    }

    /// <summary>手によらない変化の割合（待つ手 → 手によらない区画の全部の手の割合 → いちばん変わりにくい手の割合（上限） の順。どれもなければ null）</summary>
    public (double P, string Source)? NaturalRate(string node, int cell)
    {
        if (_w.TryGetValue(node, out var w) && w.N > 0) return ((w.C[cell] + 0.5) / (w.N + 1.0), "wait");
        if (Class(node, cell) == "independent")
        {
            int n = 0, c = 0; foreach (var kv in _a) if (kv.Key.Node == node) { n += kv.Value.N; c += kv.Value.C[cell]; }
            return ((c + 0.5) / (n + 1.0), "independent");
        }
        // 手によらない変化はどの手の後にも起きるので、いちばん変わりにくい手の割合を超えない（上限として使う）
        var rs = _a.Where(kv => kv.Key.Node == node && kv.Value.N >= MinTries).Select(kv => (kv.Value.C[cell] + 0.5) / (kv.Value.N + 1.0)).ToList();
        if (rs.Count >= 2) return (Math.Min(0.5, rs.Min()), "min_action");   // 根拠: 2 つ以上の手を比べられるとき（1 つでは手の効果と区別できない）。0.5 は Jeffreys の事前値
        return null;
    }

    /// <summary>この手がこの区画を変える割合のうち、手によらない変化を引いた残りの割合（0〜1。基準がなければ事前値 0.5 を基準にする）</summary>
    public double CausedShare(string node, string action, int cell)
    {
        var (n, c) = ActionCounts(node, action, cell);
        double pa = (c + 0.5) / (n + 1.0), pn = NaturalRate(node, cell)?.P ?? 0.5;   // 根拠: Jeffreys の事前値（観測 0 回の割合）
        return pa <= 0 ? 0 : Math.Max(0, pa - pn) / pa;
    }

    /// <summary>手の割合が基準より大きい確からしさ（ベータ分布の正規近似。0〜1）。判断のしきい値には使わない（記録と表示）</summary>
    public double Confidence(string node, string action, int cell)
    {
        var (n, c) = ActionCounts(node, action, cell);
        double a1 = c + 0.5, b1 = n - c + 0.5, m1 = a1 / (a1 + b1), v1 = a1 * b1 / ((a1 + b1) * (a1 + b1) * (a1 + b1 + 1));
        double m2 = 0.5, v2 = 1.0 / 8;   // 基準が分からないとき: Jeffreys の事前分布の平均と分散
        if (_w.TryGetValue(node, out var w) && w.N > 0) { double a2 = w.C[cell] + 0.5, b2 = w.N - w.C[cell] + 0.5; m2 = a2 / (a2 + b2); v2 = a2 * b2 / ((a2 + b2) * (a2 + b2) * (a2 + b2 + 1)); }
        else if (NaturalRate(node, cell) is { } nr) { m2 = nr.P; v2 = 0; }
        double z = (m1 - m2) / Math.Sqrt(Math.Max(1e-12, v1 + v2));
        return 0.5 * (1 + Erf(z / Math.Sqrt(2)));
    }

    /// <summary>ある変化（変わった区画の集まり）のうち、手が起こしたと見込める割合（変わった区画の CausedShare の最大）。変わった区画がなければ 0</summary>
    public double DiffCaused(string node, string action, IReadOnlyCollection<int> changed) =>
        changed.Count == 0 ? 0 : changed.Where(i => i >= 0 && i < Cells).Select(i => CausedShare(node, action, i)).DefaultIfEmpty(0).Max();

    /// <summary>v0.28.1: 区画ごとの「入力なしの割合の不確かさ × 手の後に変わる割合」の最大（待つ手の情報の値打ちの成分）と、その区画</summary>
    public (double Value, int Cell) NaturalInformation(string node)
    {
        int n = 0; var c = new int[Cells];
        foreach (var kv in _a) if (kv.Key.Node == node) { n += kv.Value.N; for (int i = 0; i < Cells; i++) c[i] += kv.Value.C[i]; }
        if (n == 0) return (0, -1);
        var (wn, wc) = _w.TryGetValue(node, out var w) ? w : (0, new int[Cells]);
        (double, int) best = (0, -1);
        for (int i = 0; i < Cells; i++) { if (c[i] == 0) continue; double v = WaitInformation.Part(wc[i], wn, c[i], n); if (v > best.Item1) best = (v, i); }
        return best;
    }

    public (int Action, int Independent, int Unknown) ClassCounts()
    {
        int a = 0, ind = 0, u = 0;
        foreach (var node in _a.Keys.Select(k => k.Node).Distinct())
            for (int i = 0; i < Cells; i++)
            {
                if (!_a.Any(kv => kv.Key.Node == node && kv.Value.C[i] > 0) && !(_w.TryGetValue(node, out var w) && w.C[i] > 0)) continue;   // 一度も変わっていない区画は数えない
                switch (Class(node, i)) { case "action": a++; break; case "independent": ind++; break; default: u++; break; }
            }
        return (a, ind, u);
    }

    public static double LogBeta(int c, int d) => LogFactorial(c) + LogFactorial(d) - LogFactorial(c + d + 1);   // 一様な事前分布の 2 値の周辺尤度
    private static double LogFactorial(int n) { double s = 0; for (int i = 2; i <= n; i++) s += Math.Log(i); return s; }
    private static double Erf(double x)
    {
        double t = 1 / (1 + 0.3275911 * Math.Abs(x));
        double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return x >= 0 ? y : -y;
    }

    public string ExportState()
    {
        return JsonSerializer.Serialize(new
        {
            cells = Cells,
            a = _a.Select(kv => new object[] { kv.Key.Node, kv.Key.Action, kv.Value.N, kv.Value.C }).ToList(),
            w = _w.Select(kv => new object[] { kv.Key, kv.Value.N, kv.Value.C }).ToList(),
        });
    }

    public void ImportState(string json)
    {
        using var d = JsonDocument.Parse(json); var r = d.RootElement;
        if (r.GetProperty("cells").GetInt32() != Cells) return;
        foreach (var x in r.GetProperty("a").EnumerateArray()) _a[(x[0].GetString()!, x[1].GetString()!)] = (x[2].GetInt32(), x[3].EnumerateArray().Select(v => v.GetInt32()).ToArray());
        foreach (var x in r.GetProperty("w").EnumerateArray()) _w[x[0].GetString()!] = (x[1].GetInt32(), x[2].EnumerateArray().Select(v => v.GetInt32()).ToArray());
    }
}
