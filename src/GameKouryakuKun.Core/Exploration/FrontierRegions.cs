using System.Text.Json;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.25.0 ブロック E: 経験から作る「地域」（観測した遷移で互いに行き来できる状態の集まり＝強連結成分）と、
/// 地域ごとの「進み」（新しさの数ではなく、到達できる範囲の広がり・未知へ続く新しい道・先の選択肢の増え・予測の改善の続き）。
/// 地域に意味（設定・メニューなど）は付けない。固定の除外はしない。値打ちは進みが減れば自然に下がり、進みが戻れば上がる。
/// 画面の変化の回数・表示の計器は使わない（報酬ではない）。
/// </summary>
public sealed class FrontierRegions
{
    public const int Signals = 4;                 // 根拠: 汎用（量の種類の数: 範囲・選択肢・未知への道・予測の改善）
    public double Rate { get; init; } = 0.05;        // 根拠: 汎用（直近の約 20 手を重く見る移動平均。目的の値打ちの 0.2＝約 5 回の目的 × 1 目的数手）
    public double PriorWeight { get; init; } = 2;    // 根拠: 汎用（目的の値打ちと同じ 2 回分の事前）
    public double Prior { get; init; } = 1;          // 根拠: 汎用（探索の平均と同じ＝中立）
    public double SignalCap { get; init; } = 4;      // 根拠: 汎用（目的の値打ちの上限と同じ。1 つの量だけでは平均 1 を超えない）
    public double ValueFloor { get; init; } = 0.05;  // 根拠: 汎用（二度と行かないことを避ける下限。目的の値打ちと同じ）
    public int RecomputeEvery { get; init; } = 50;   // 根拠: 汎用（地域の計算し直しの間隔）

    // 4 つの量の全体の 1 手あたりの平均（基準）。0 割りを避ける弱い事前
    private readonly double[] _base = new double[Signals];
    private long _baseN;
    private readonly Dictionary<string, (double N, double Mean, double Peak, bool Decayed, int Recoveries)> _node = new(StringComparer.Ordinal);
    private Dictionary<string, string> _region = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenEdges = new(StringComparer.Ordinal);
    private long _steps;

    public long Steps => _steps;
    public int RegionCount => _region.Values.Distinct().Count();
    public string RegionOf(string node) => _region.TryGetValue(node, out var r) ? r : node;

    /// <summary>観測した遷移（自己遷移を除く）から強連結成分を作る。地域の名前は成分の中のいちばん小さい状態の名前（安定）</summary>
    public void Recompute(IEnumerable<(string From, string To)> edges)
    {
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (f, t) in edges)
        {
            if (!adj.ContainsKey(f)) adj[f] = new(); if (!adj.ContainsKey(t)) adj[t] = new();
            if (f != t) adj[f].Add(t);
        }
        _region = Scc(adj);
    }

    private static Dictionary<string, string> Scc(Dictionary<string, List<string>> adj)
    {
        int idx = 0; var index = new Dictionary<string, int>(); var low = new Dictionary<string, int>();
        var on = new HashSet<string>(); var st = new Stack<string>(); var res = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in adj.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (index.ContainsKey(root)) continue;
            var work = new Stack<(string V, int I)>(); work.Push((root, 0));
            index[root] = low[root] = idx++; st.Push(root); on.Add(root);
            while (work.Count > 0)
            {
                var (v, i) = work.Pop(); var ns = adj[v];
                if (i < ns.Count)
                {
                    work.Push((v, i + 1)); var w = ns[i];
                    if (!index.ContainsKey(w)) { index[w] = low[w] = idx++; st.Push(w); on.Add(w); work.Push((w, 0)); }
                    else if (on.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                    continue;
                }
                if (low[v] == index[v])
                {
                    var comp = new List<string>(); string x;
                    do { x = st.Pop(); on.Remove(x); comp.Add(x); } while (x != v);
                    var name = comp.Min(StringComparer.Ordinal)!; foreach (var c in comp) res[c] = name;
                }
                if (work.Count > 0) { var p = work.Peek().V; low[p] = Math.Min(low[p], low[v]); }
            }
        }
        return res;
    }

    /// <summary>
    /// 1 手（待つ手・人間の手は呼ばない）。from の画面の進みとして数える:
    /// newNode = 到達できる範囲の広がり、newExit = 知っている別の地域へ初めてつながった遷移（先の選択肢の増え）、
    /// toFrontier = 初めての遷移で、未試行の操作が残る知っている画面へ着いた（未知の遷移へ続く新しい道）、gain = 予測の不確かさの減り（予測の改善の続き）
    /// </summary>
    public void Observe(string from, string action, string to, bool newNode, int untriedAtTo, double gain)
    {
        _steps++;
        bool firstEdge = from != to && _seenEdges.Add($"{from}\u0001{action}\u0001{to}");
        // 新しい画面に着いた手は「範囲の広がり」だけで数える（同じ出来事を 2 つの量で重ねて数えない）
        bool exit = firstEdge && !newNode && RegionOf(from) != RegionOf(to);
        bool toFrontier = firstEdge && !newNode && untriedAtTo > 0;
        var s = new[] { newNode ? 1.0 : 0, exit ? 1.0 : 0, toFrontier ? 1.0 : 0, Math.Max(0, gain) };
        _baseN++;
        for (int i = 0; i < Signals; i++) _base[i] += (s[i] - _base[i]) / _baseN;
        double p = 0;
        for (int i = 0; i < Signals; i++) p += Math.Min(SignalCap, s[i] / Math.Max(_base[i], 1e-6)) ;
        p /= Signals;
        var cur = _node.TryGetValue(from, out var c) ? c : (0, Prior, Prior, false, 0);
        double n = cur.N + 1, mean = cur.Mean + (p - cur.Mean) * Math.Max(1 / n, Rate);
        double v = Shrunk(n, mean);
        bool decayed = cur.Decayed || v < cur.Peak / 2;
        int rec = cur.Recoveries + (cur.Decayed && v >= cur.Peak * 0.75 ? 1 : 0);
        _node[from] = (n, mean, Math.Max(cur.Peak, v), decayed && !(cur.Decayed && v >= cur.Peak * 0.75), rec);
    }

    private double Shrunk(double n, double mean) { double ne = Math.Min(n, 1 / Rate); return Math.Clamp((ne * mean + PriorWeight * Prior) / (ne + PriorWeight), ValueFloor, SignalCap); }

    public double NodeValue(string node) => _node.TryGetValue(node, out var c) ? Shrunk(c.N, c.Mean) : Prior;

    /// <summary>地域の行き直しの値打ち（構成する画面の値打ちの手数の重みつき平均）。1 が探索の平均</summary>
    public double RegionValue(string node)
    {
        if (node == EnvironmentLossBook.Terminal) return ValueFloor;   // v0.25.4: 先のない状態は先の進みもない
        var r = RegionOf(node); double w = 0, sum = 0;
        foreach (var (k, c) in _node) if (RegionOf(k) == r) { w += c.N; sum += c.N * Shrunk(c.N, c.Mean); }
        return w == 0 ? Prior : Math.Clamp(sum / w, ValueFloor, SignalCap);
    }

    /// <summary>v0.25.3: 予測した行き先の分布での地域の値打ちの期待値 ÷ 今の地域の値打ち（予測に残る確率は中立 1 として数える）</summary>
    public double DestinationRatio(string node, IEnumerable<(string Next, double P)> top)
    {
        double sp = 0, ev = 0;
        foreach (var (n, p) in top) { sp += p; ev += p * RegionValue(n); }
        ev += Math.Max(0, 1 - sp) * Prior;
        return Math.Clamp(ev / Math.Max(RegionValue(node), ValueFloor), ValueFloor, SignalCap);
    }

    public IReadOnlyList<(string Region, int Nodes, double Steps, double Value)> Regions() =>
        _node.GroupBy(kv => RegionOf(kv.Key)).Select(g => (g.Key, _region.Values.Count(x => x == g.Key) is int nc && nc > 0 ? nc : 1, g.Sum(x => x.Value.N), RegionValue(g.First().Key)))
            .OrderByDescending(x => x.Item3).ToList();
    public int DecayedNodes => _node.Values.Count(x => x.Decayed);
    public int Recoveries => _node.Values.Sum(x => x.Recoveries);
    public IReadOnlyList<double> Baseline => _base;

    public string Summary() => $"地域 {RegionCount}・手 {_steps}・下がった画面 {DecayedNodes}・戻った {Recoveries}" +
        string.Concat(Regions().Take(3).Select(r => $"／{r.Region}（画面 {r.Nodes}・手 {r.Steps:0}・値打ち {r.Value:0.00}）"));

    public string ExportState() => JsonSerializer.Serialize(new
    {
        v = 1, n = _baseN, b = _base, steps = _steps, edges = _seenEdges.ToList(),
        node = _node.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.N, kv.Value.Mean, kv.Value.Peak, kv.Value.Decayed ? 1 : 0, kv.Value.Recoveries })
    });

    public void ImportState(string json)
    {
        using var d = JsonDocument.Parse(json); var r = d.RootElement;
        if (!r.TryGetProperty("v", out var v) || v.GetInt32() != 1) return;
        _baseN = r.GetProperty("n").GetInt64(); _steps = r.GetProperty("steps").GetInt64();
        int i = 0; foreach (var x in r.GetProperty("b").EnumerateArray()) if (i < Signals) _base[i++] = x.GetDouble();
        foreach (var e in r.GetProperty("edges").EnumerateArray()) _seenEdges.Add(e.GetString()!);
        foreach (var p in r.GetProperty("node").EnumerateObject())
        {
            var a = p.Value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            _node[p.Name] = (a[0], a[1], a[2], a[3] > 0, (int)a[4]);
        }
    }
}
