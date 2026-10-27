using GameKouryakuKun.Core.Exploration;

namespace GameKouryakuKun.Core.Observation;

/// <summary>
/// v0.31.0（P-4・仕様 §5）: 離れていても同じ手の後にいつも一緒に変わる区画を 1 つの群（共変動する Region）にまとめる。
/// 区画の組ごとに 2×2 の数（両方・片方ずつ・どちらも変わらない）を、手の後と入力なし（待つ手）で別々に数える。
/// 「片方が変わったかで、もう片方の変わる率が違う」のベイズ因子（RegionCausality と同じ周辺尤度）≥ ln10 で、一緒に変わる向きの組をつなぐ。
/// 意味は付けない。群は手で変わる群（action）と入力なしで変わる群（natural）に分ける
/// </summary>
public sealed class RegionCovariation
{
    public const int MinChanges = 3;   // 根拠: 2 値の数え上げで差が出る最小（RegionCausality.MinTries と同じ）
    private readonly int _cells;
    private readonly Mode[] _m = { new(), new() };
    private sealed class Mode { public int N; public readonly Dictionary<int, int> One = new(); public readonly Dictionary<(int, int), int> Co = new(); }
    public long Skipped { get; private set; }
    public RegionCovariation(int cells) { _cells = cells; }

    public void Observe(IReadOnlyCollection<int> changed, bool natural)
    {
        var m = _m[natural ? 1 : 0];
        var c = changed.Where(x => x >= 0 && x < _cells).Distinct().OrderBy(x => x).ToArray();
        // 根拠: 画面の過半が変わった手（画面が移った）は群の手がかりにならない。共通のずれと同じ「過半」。
        // v0.31.1: 1 区画ずつの数・手の数にも入れない（入れると組の数と食い違い、一緒に変わる組が「片方だけ変わる」に見えて群が 0 になった。実機 #134）
        if (c.Length > _cells / 2) { Skipped++; return; }
        m.N++;
        foreach (var i in c) m.One[i] = m.One.GetValueOrDefault(i) + 1;
        for (int a = 0; a < c.Length; a++) for (int b = a + 1; b < c.Length; b++) m.Co[(c[a], c[b])] = m.Co.GetValueOrDefault((c[a], c[b])) + 1;
    }

    public static double LogBf(int both, int onlyA, int onlyB, int none)
    {
        double sep = RegionCausality.LogBeta(both, onlyA) + RegionCausality.LogBeta(onlyB, none);
        double pooled = RegionCausality.LogBeta(both + onlyB, onlyA + none);
        return sep - pooled;
    }

    public IReadOnlyList<(string Kind, int[] Cells)> Groups()
    {
        var res = new List<(string, int[])>();
        for (int k = 0; k < 2; k++)
        {
            var m = _m[k];
            // v0.31.1: 調べる組が多いほど偶然に強く見える組が出る。事前の見込みを組の数で割る（しきい値 ln10 + ln 組の数）
            int tested = m.Co.Count(kv => kv.Value >= MinChanges && m.One[kv.Key.Item1] >= MinChanges && m.One[kv.Key.Item2] >= MinChanges);
            double th = Math.Log(10) + Math.Log(Math.Max(1, tested));
            var links = new List<(int I, int J, double Bf)>();
            foreach (var ((i, j), co) in m.Co)
            {
                int ni = m.One[i], nj = m.One[j]; if (ni < MinChanges || nj < MinChanges || co < MinChanges) continue;
                int onlyI = ni - co, onlyJ = nj - co, none = m.N - ni - nj + co;
                // j が変わる率: i が変わったとき co/ni、変わらないとき onlyJ/(N-ni)。一緒に変わる向きだけ
                if ((double)co / ni <= (double)onlyJ / Math.Max(1, m.N - ni)) continue;
                double bf = LogBf(co, onlyI, onlyJ, none);
                if (bf >= th) links.Add((i, j, bf));   // 根拠: ln10 は状態の分け直しの戻しと同じ（RevertMinLogBayesFactor）
            }
            // v0.31.2: 鎖のようにつながっただけの組は群にしない（実機 #135 で画面の過半 107 区画の群）。
            // 2 つの群をまとめるのは、まとめた後のどの 2 区画も互いに一緒に変わる組（すべての組がつながる）ときだけ。強い組から順に
            var linked = links.Select(x => (x.I, x.J)).ToHashSet();
            var grp = new Dictionary<int, List<int>>();
            foreach (var (i, j, _) in links.OrderByDescending(x => x.Bf).ThenBy(x => x.I).ThenBy(x => x.J))
            {
                if (!grp.TryGetValue(i, out var gi)) grp[i] = gi = new List<int> { i };
                if (!grp.TryGetValue(j, out var gj)) grp[j] = gj = new List<int> { j };
                if (ReferenceEquals(gi, gj)) continue;
                if (!gi.All(a => gj.All(b => linked.Contains((Math.Min(a, b), Math.Max(a, b)))))) continue;
                gi.AddRange(gj); foreach (var x in gj) grp[x] = gi;
            }
            foreach (var g in grp.Values.Distinct()) if (g.Count >= 2) res.Add((k == 0 ? "action" : "natural", g.OrderBy(x => x).ToArray()));
        }
        return res;
    }
}
