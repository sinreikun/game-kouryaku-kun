namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// v0.40.2: 入力を受け付けるまでの間（画面が落ち着いてから次の入力までの間）の学習。
/// 実機 v0.40.0 の比較で、同じ（画面, 手）どうしでも、前の手の静止から次の入力までが短い（&lt;300 ms）と「変化なし」が 13.8 ポイント多かった（6 SE）。
/// 予測の当たり自体は同じ（−0.3 pt）。つまり速く押すと、ゲームが入力を受け付けない手が増え、その「変化なし」が手の効果として経験に入る（時間≠因果）。
/// ここでは、間の長さの区分ごとに同じ (画面, 手) で「変化なし」の率を、十分長い間（基準の区分）と比べ、増えない最も短い区分を使う。
/// 標本が足りない間は基準の区分（安全側＝直列の頃と同じくらいの間）を守り、ProbeEvery 手に 1 回だけ短い区分を試して証拠を集める。
/// ゲームの知識は使わない。待ちを足すだけで、静止の判定・GT/Track には触れない。
/// </summary>
public sealed class ReadyGap
{
    public static readonly double[] Edges = { 0, 150, 300, 450, 600 };   // 根拠: 汎用（区分の下端 ms。最後が基準。#159 の直列の間は約 700 ms）
    public double ExcessTolerance { get; init; } = 0.05;  // 根拠: 診断の合格条件（変化なしの増加が 5 ポイント以下）
    public double Se { get; init; } = 2;                  // 根拠: 汎用（2 標準誤差）
    public double MinWeight { get; init; } = 30;          // 根拠: 汎用（同じ (画面, 手) どうしの実効の件数）
    public int ProbeEvery { get; init; } = 4;             // 根拠: 汎用（証拠を集めるために短い区分を試す頻度。4 手に 1 回）

    private readonly Dictionary<string, long[]> _n = new(StringComparer.Ordinal), _nc = new(StringComparer.Ordinal);
    private long _calls;
    public long Holds, Probes; public double HeldMsSum;

    public static int Bin(double gapMs) { int b = 0; for (int i = 0; i < Edges.Length; i++) if (gapMs >= Edges[i]) b = i; return b; }

    public void Observe(string key, double gapMs, bool noChange)
    {
        if (!_n.TryGetValue(key, out var n)) { _n[key] = n = new long[Edges.Length]; _nc[key] = new long[Edges.Length]; }
        int b = Bin(gapMs); n[b]++; if (noChange) _nc[key][b]++;
    }

    /// <summary>区分 b の「変化なし」の率の、基準の区分に対する増加（同じ key どうし）と標準誤差・実効件数</summary>
    public (double Excess, double SeV, double Weight) Excess(int b)
    {
        int r = Edges.Length - 1; double num = 0, w = 0, v = 0;
        foreach (var (k, n) in _n)
        {
            long nb = n[b], nr = n[r]; if (nb == 0 || nr == 0) continue;
            double pb = (double)_nc[k][b] / nb, pr = (double)_nc[k][r] / nr, wt = (double)nb * nr / (nb + nr);
            num += wt * (pb - pr); w += wt; v += wt * wt * (pb * (1 - pb) / nb + pr * (1 - pr) / nr);
        }
        return w == 0 ? (double.NaN, double.NaN, 0) : (num / w, Math.Sqrt(v) / w, w);
    }

    /// <summary>使ってよい最短の間（ms）。短い区分から見て、増加が許容内と言える（上限 2SE が許容以下）最初の区分の下端。言えなければ基準</summary>
    public double SafeGapMs()
    {
        for (int b = 0; b < Edges.Length - 1; b++)
        {
            var (ex, se, w) = Excess(b);
            if (w >= MinWeight && !double.IsNaN(ex) && ex + Se * se <= ExcessTolerance)
            {
                bool longerOk = true;   // それより長い区分で増加がはっきりしていれば使わない（単調でない場合の安全側）
                for (int c = b + 1; c < Edges.Length - 1; c++) { var (e2, s2, w2) = Excess(c); if (w2 >= MinWeight && e2 - Se * s2 > ExcessTolerance) longerOk = false; }
                if (longerOk) return Edges[b];
            }
        }
        return Edges[^1];
    }

    /// <summary>次の入力の前に置く間（ms）。sinceMs は前の手の静止からの経過。返り値は追加で待つ ms（0 以上）</summary>
    public double HoldFor(double sinceMs, out bool probe)
    {
        double safe = SafeGapMs();
        _calls++;
        probe = safe > 0 && _calls % ProbeEvery == 0;
        double target = probe ? Edges[(int)((_calls / ProbeEvery) % Math.Max(1, Array.IndexOf(Edges, safe)))] : safe;
        if (probe) Probes++;
        double hold = Math.Max(0, target - sinceMs);
        if (hold > 0) { Holds++; HeldMsSum += hold; }
        return hold;
    }

    public sealed record Persisted(Dictionary<string, long[]> N, Dictionary<string, long[]> NoChange);
    public Persisted Export() => new(_n.ToDictionary(k => k.Key, k => (long[])k.Value.Clone()), _nc.ToDictionary(k => k.Key, k => (long[])k.Value.Clone()));
    public void Import(Persisted? p)
    {
        if (p == null) return;
        foreach (var (k, n) in p.N)
            if (n.Length == Edges.Length && p.NoChange.TryGetValue(k, out var c) && c.Length == Edges.Length) { _n[k] = (long[])n.Clone(); _nc[k] = (long[])c.Clone(); }
    }

    public string Summary()
    {
        var parts = Enumerable.Range(0, Edges.Length - 1).Select(b => { var (e, s, w) = Excess(b); return $"≥{Edges[b]:0}:{(double.IsNaN(e) ? "-" : e.ToString("+0.000;-0.000"))}±{(double.IsNaN(s) ? "-" : s.ToString("0.000"))}(w{w:0})"; });
        return $"安全な間 {SafeGapMs():0} ms・待った {Holds} 回（計 {HeldMsSum / 1000:0.0} s）・試した {Probes} 回・変化なしの増加 " + string.Join(" ", parts);
    }
}
