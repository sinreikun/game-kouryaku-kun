using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>画面1枚の細かい配置の特徴（Cols×Rows の区画ごとの平均の色）。色の分布（ScreenSignature）より位置に敏感。意味は付けない</summary>
public static class VisualLayout
{
    public const int Cols = 8;   // 根拠: 汎用（横の区画数。粗い配置）
    public const int Rows = 6;   // 根拠: 汎用（縦の区画数）
    public const int Length = Cols * Rows * 3;

    public static float[] Of(Frame f, int stride = 4)
    {
        var v = new float[Length]; var n = new int[Cols * Rows]; var px = f.Bgra;
        for (int y = 0; y < f.Height; y += stride)
        {
            int r = Math.Min(Rows - 1, y * Rows / f.Height);
            for (int x = 0; x < f.Width; x += stride)
            {
                int c = Math.Min(Cols - 1, x * Cols / f.Width), k = r * Cols + c, p = (y * f.Width + x) * 4;
                v[k * 3] += px[p]; v[k * 3 + 1] += px[p + 1]; v[k * 3 + 2] += px[p + 2]; n[k]++;
            }
        }
        for (int k = 0; k < n.Length; k++) for (int j = 0; j < 3; j++) v[k * 3 + j] /= Math.Max(1, n[k]) * 255f;
        return v;
    }

    /// <summary>0〜1 の距離（区画の色の差の平均）</summary>
    public static double Distance(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / Length;
    }
}

/// <summary>見た目の状態の1つ（細かい配置の教師なしの群）</summary>
public sealed class VisualState
{
    public int Id { get; init; }
    public float[] Prototype { get; init; } = Array.Empty<float>();
    public int Seen { get; set; }
    /// <summary>この見た目の状態が属した粗いノード（色の分布・矩形の配置）ごとの回数</summary>
    public Dictionary<string, int> Coarse { get; } = new(StringComparer.Ordinal);
    public string? DominantCoarse => Coarse.Count == 0 ? null : Coarse.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
}

/// <summary>見た目の状態ごとの、入力への遷移の特徴（入力ごとに 変わらない／変わったが同じ見た目のまま／別の見た目へ の回数）</summary>
public sealed class TransitionFingerprint
{
    public Dictionary<string, int[]> ByAction { get; } = new(StringComparer.Ordinal);
    public void Add(string action, int outcome, int n = 1)
    {
        if (!ByAction.TryGetValue(action, out var c)) ByAction[action] = c = new int[3];
        c[outcome] += n;
    }
}

/// <summary>
/// 見た目の似かよりと遷移の特徴の似かよりを、別々の表現として持つ（v0.9.0。2026-10-18 10:49 のユーザー条件）。
///  - 見た目の状態（V）: 細かい配置の特徴の先着順の群分け（距離の閾値は自己調整）
///  - 遷移の特徴: V ごと・入力ごとの結果の分布。似た特徴の V を「遷移の群（T）」として記録する（見た目が違っても統合はしない）
///  - 分割: 同じ粗いノードに属する V の遷移の特徴がはっきり違う（見た目は似ているのに遷移が違う）とき、証拠がそろってから
///    その V を別のノード（粗いノード名#v番号）に分ける。早い段階で1つにまとめたり、まとめ直したりはしない
/// 意味は付けない（番号だけ）。
/// </summary>
public sealed class VisualStateBook
{
    public double InitialNewStateDistance { get; init; } = 0.04;  // 根拠: 汎用（区画の色の平均の差 4%＝48 区画のうち約2区画が入れ替わる）。自己調整で上書き
    public double MinNewStateDistance { get; init; } = 0.02;      // 根拠: 汎用（自己調整の下限: 描画の揺らぎで分けすぎない）
    public double MaxNewStateDistance { get; init; } = 0.12;      // 根拠: 汎用（自己調整の上限）
    public int MinSamples { get; init; } = 30;                    // 根拠: 汎用（分布を推定する最小の標本数）
    public double SpreadFactor { get; init; } = 4;                // 根拠: 汎用（同じ群の中のばらつきの4標準偏差を超えたら別の群）
    public int MinTries { get; init; } = 3;                       // 根拠: 汎用（遷移の特徴を比べる、入力ごとの最小の回数）
    public int MinCommonActions { get; init; } = 2;               // 根拠: 汎用（比べる入力の最小の数。1つの入力の偶然で分けない）
    public double SameGroupDistance { get; init; } = 0.15;        // 根拠: 汎用（結果の分布の総変動距離 15% 以下＝遷移の特徴が似ている）
    public double SplitDistance { get; init; } = 0.5;             // 根拠: 汎用（結果の分布の総変動距離 50% 以上＝遷移の特徴がはっきり違う）
    /// <summary>分割に要る証拠: 「別々の分布」と「同じ分布」のベイズ因子の対数（共通の入力ごとの和）の下限（v0.11.0）。少ない観測の偶然の差で分けない</summary>
    public double SplitMinLogBayesFactor { get; init; } = 2.302585;   // 根拠: 汎用（ベイズ因子 10＝ジェフリーズの尺度で「強い」証拠）

    private readonly List<VisualState> _states = new();
    private readonly Dictionary<int, TransitionFingerprint> _fp = new();
    private readonly Dictionary<int, (string Coarse, string Node, double Distance, int Leader)> _splits = new();
    private readonly Dictionary<int, (string Coarse, string Node, double Distance, int Leader)> _reverted = new();
    // 見た目の状態ごと・入力ごとの「次の画面」の回数（予測に効く違い。連続履歴の流し直しで作り直すので保存しない）。自分の画面へ戻る手は "=" にまとめる
    private readonly Dictionary<int, Dictionary<string, Dictionary<string, int>>> _next = new();
    // 取り消した後の「次の画面」の回数（取り消しの後の観測だけで分け直しを判断し、分ける・戻すの往復を防ぐ）
    private readonly Dictionary<int, Dictionary<string, Dictionary<string, int>>> _nextSinceRevert = new();
    private int _n; private double _mean, _m2;
    public IReadOnlyList<VisualState> States => _states;
    public IReadOnlyDictionary<int, TransitionFingerprint> Fingerprints => _fp;
    public IReadOnlyDictionary<int, (string Coarse, string Node, double Distance, int Leader)> Splits => _splits;
    public IReadOnlyDictionary<int, (string Coarse, string Node, double Distance, int Leader)> Reverted => _reverted;
    /// <summary>直前の Assign の、いちばん近い見た目の状態までの距離 ÷ 新しい状態を作る距離（観測の確かさ。1 に近いほど境目）</summary>
    public double LastRatio { get; private set; } = double.NaN;
    /// <summary>境目の近くとみなす幅（距離の比が 1 ± この値）</summary>
    public double AmbiguousBand { get; init; } = 0.25;   // 根拠: 汎用（新しい状態を作る距離の ±25%＝どちらの状態ともいえる境目）
    public bool LastAmbiguous => double.IsFinite(LastRatio) && Math.Abs(LastRatio - 1) <= AmbiguousBand;

    public double NewStateDistance => _n < MinSamples ? InitialNewStateDistance
        : Math.Clamp(_mean + SpreadFactor * Math.Sqrt(_m2 / Math.Max(1, _n - 1)), MinNewStateDistance, MaxNewStateDistance);

    public void Load(IEnumerable<(int Id, float[] Proto, int Seen, string? Coarse)> states,
                     IEnumerable<(int Id, string Action, int None, int Stay, int Leave)> fingerprints,
                     IEnumerable<(string Coarse, int Id, string Node, double Distance, int Leader)> splits)
    {
        foreach (var (id, p, seen, coarse) in states)
            if (p.Length == VisualLayout.Length && _states.All(s => s.Id != id))
            {
                var v = new VisualState { Id = id, Prototype = (float[])p.Clone(), Seen = seen };
                if (coarse != null) v.Coarse[coarse] = Math.Max(1, seen);
                _states.Add(v);
            }
        foreach (var (id, a, none, stay, leave) in fingerprints) { var f = Fp(id); f.Add(a, 0, none); f.Add(a, 1, stay); f.Add(a, 2, leave); }
        foreach (var (c, id, node, d, leader) in splits) _splits[id] = (c, node, d, leader);
    }

    /// <summary>取り消した分割を読み込む（記録は残る。同じ番号で分け直すときに同じノード名を使う）</summary>
    public void LoadReverted(IEnumerable<(string Coarse, int Id, string Node, double Distance, int Leader)> reverted)
    {
        foreach (var (c, id, node, d, leader) in reverted) if (!_splits.ContainsKey(id)) _reverted[id] = (c, node, d, leader);
    }

    /// <summary>分割を取り消す（以後この見た目の状態は粗いノードに戻る）。取り消しの後の観測は別に数える</summary>
    public bool Revert(int id)
    {
        if (!_splits.Remove(id, out var sp)) return false;
        _reverted[id] = sp; _nextSinceRevert[id] = new();
        return true;
    }

    /// <summary>1手の「次の画面」を数える（from は手の始まりのノード名。自分へ戻る手は "=" にまとめる）</summary>
    public void ObserveNext(int vis, string from, string action, string next)
    {
        string key = next == from ? "=" : next;
        foreach (var d in _reverted.ContainsKey(vis) && _nextSinceRevert.TryGetValue(vis, out var since) ? new[] { Next(_next, vis), since } : new[] { Next(_next, vis) })
        {
            if (!d.TryGetValue(action, out var c)) d[action] = c = new Dictionary<string, int>(StringComparer.Ordinal);
            c[key] = c.TryGetValue(key, out var k) ? k + 1 : 1;
        }
    }
    private static Dictionary<string, Dictionary<string, int>> Next(Dictionary<int, Dictionary<string, Dictionary<string, int>>> d, int vis) =>
        d.TryGetValue(vis, out var x) ? x : d[vis] = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

    /// <summary>
    /// 「次の画面」の分布が別々か同じかのベイズ因子の対数（共通の入力ごとの和）と、比べた入力の数・総変動距離の平均。
    /// 次の画面の種類（両方に現れたものの和集合＋まだ見ていない分 1 つ）の多項分布に一様なディリクレ事前分布を置いた周辺尤度で比べる
    /// </summary>
    public static (double LogBf, int Common, double Distance) NextEvidence(IReadOnlyDictionary<string, Dictionary<string, int>> a, IReadOnlyDictionary<string, Dictionary<string, int>> b, int minTries)
    {
        double lbf = 0, tv = 0; int k = 0;
        foreach (var (act, ca) in a)
        {
            if (!b.TryGetValue(act, out var cb)) continue;
            int na = ca.Values.Sum(), nb = cb.Values.Sum();
            if (na < minTries || nb < minTries) continue;
            var cats = ca.Keys.Union(cb.Keys, StringComparer.Ordinal).ToList();
            int K = cats.Count + 1;
            int[] xa = cats.Select(c => ca.TryGetValue(c, out var v) ? v : 0).ToArray(), xb = cats.Select(c => cb.TryGetValue(c, out var v) ? v : 0).ToArray();
            lbf += LogMarginalK(xa, K) + LogMarginalK(xb, K) - LogMarginalK(xa.Zip(xb, (p, q) => p + q).ToArray(), K);
            double d = 0; for (int i = 0; i < cats.Count; i++) d += Math.Abs(xa[i] / (double)na - xb[i] / (double)nb);
            tv += d / 2; k++;
        }
        return (lbf, k, k == 0 ? double.NaN : tv / k);
    }

    /// <summary>K 通りの多項分布 × 一様なディリクレ事前分布の周辺尤度の対数: ln (K−1)! − ln (N+K−1)! + Σ ln c_i!</summary>
    private static double LogMarginalK(IReadOnlyList<int> c, int K)
    {
        int n = 0; double s = 0; foreach (var x in c) { n += x; s += LogFactorial(x); }
        return LogFactorial(K - 1) - LogFactorial(n + K - 1) + s;
    }

    /// <summary>2つの見た目の状態の「次の画面」の違いの証拠</summary>
    public (double LogBf, int Common, double Distance) NextEvidence(int a, int b, bool sinceRevert = false)
    {
        var da = _next.TryGetValue(a, out var x) ? x : new();
        var db = (sinceRevert ? _nextSinceRevert : _next).TryGetValue(b, out var y) ? y : new();
        return NextEvidence(da, db, MinTries);
    }

    private TransitionFingerprint Fp(int id) => _fp.TryGetValue(id, out var f) ? f : _fp[id] = new TransitionFingerprint();

    /// <summary>最も近い見た目の状態を返す。近いものがなければ新しく作る</summary>
    public (int Id, bool IsNew) Assign(float[] sig, string coarse)
    {
        VisualState? best = null; double bd = double.MaxValue;
        foreach (var s in _states) { double d = VisualLayout.Distance(sig, s.Prototype); if (d < bd) { bd = d; best = s; } }
        bool isNew = false;
        LastRatio = best == null ? double.NaN : bd / Math.Max(1e-9, NewStateDistance);
        if (best == null || bd > NewStateDistance)
        {
            best = new VisualState { Id = _states.Count == 0 ? 1 : _states.Max(x => x.Id) + 1, Prototype = (float[])sig.Clone() };
            _states.Add(best); isNew = true;
        }
        else
        {
            _n++; double dd = bd - _mean; _mean += dd / _n; _m2 += dd * (bd - _mean);
            float k = 1f / Math.Min(best.Seen + 1, 50);   // ゆっくり追従する平均
            for (int i = 0; i < VisualLayout.Length; i++) best.Prototype[i] += k * (sig[i] - best.Prototype[i]);
        }
        best.Seen++;
        best.Coarse[coarse] = best.Coarse.TryGetValue(coarse, out var c) ? c + 1 : 1;
        return (best.Id, isNew);
    }

    /// <summary>探索に使うノード名。分割の証拠がそろった見た目の状態だけ、粗いノードとは別のノードにする</summary>
    public string Node(string coarse, int visId) => _splits.TryGetValue(visId, out var s) && s.Coarse == coarse ? s.Node : coarse;

    /// <summary>1手の結果を、見た目の状態の遷移の特徴に足す</summary>
    public void ObserveTransition(int from, string action, bool changed, int to) => Fp(from).Add(action, !changed ? 0 : to == from ? 1 : 2);

    /// <summary>2つの遷移の特徴の距離（共通の入力ごとの結果の分布の総変動距離の平均）。比べられる入力が足りなければ null</summary>
    public double? Distance(int a, int b) { var (d, k) = Compare(a, b); return k < MinCommonActions ? null : d; }

    /// <summary>距離と、比べられた入力の数（似かよりの証拠の量）</summary>
    public (double Distance, int Common) Compare(int a, int b)
    {
        if (!_fp.TryGetValue(a, out var fa) || !_fp.TryGetValue(b, out var fb)) return (double.NaN, 0);
        double s = 0; int k = 0;
        foreach (var (act, ca) in fa.ByAction)
        {
            if (!fb.ByAction.TryGetValue(act, out var cb)) continue;
            int na = ca.Sum(), nb = cb.Sum();
            if (na < MinTries || nb < MinTries) continue;
            double tv = 0; for (int i = 0; i < 3; i++) tv += Math.Abs(ca[i] / (double)na - cb[i] / (double)nb);
            s += tv / 2; k++;
        }
        return (k == 0 ? double.NaN : s / k, k);
    }

    /// <summary>
    /// 2つの見た目の状態の遷移の特徴が「別々の分布」から来たか「同じ分布」から来たかのベイズ因子の対数（共通の入力ごとの和）。
    /// 結果の3通り（変化なし・同じ状態のまま変化・別の状態へ）の多項分布に一様なディリクレ事前分布を置いた周辺尤度で比べる。比べられる入力がなければ 0
    /// </summary>
    public double SplitEvidence(int a, int b)
    {
        if (!_fp.TryGetValue(a, out var fa) || !_fp.TryGetValue(b, out var fb)) return 0;
        double lbf = 0;
        foreach (var (act, ca) in fa.ByAction)
        {
            if (!fb.ByAction.TryGetValue(act, out var cb) || ca.Sum() < MinTries || cb.Sum() < MinTries) continue;
            var pooled = new int[3]; for (int i = 0; i < 3; i++) pooled[i] = ca[i] + cb[i];
            lbf += LogMarginal(ca) + LogMarginal(cb) - LogMarginal(pooled);
        }
        return lbf;
    }

    /// <summary>多項分布（3通り）× 一様なディリクレ事前分布の周辺尤度の対数: ln 2! − ln (N+2)! + Σ ln c_i!</summary>
    private static double LogMarginal(IReadOnlyList<int> c)
    {
        int n = 0; double s = 0; foreach (var x in c) { n += x; s += LogFactorial(x); }
        return LogFactorial(2) - LogFactorial(n + 2) + s;
    }
    private static double LogFactorial(int n) { double s = 0; for (int i = 2; i <= n; i++) s += Math.Log(i); return s; }

    public int BorrowMinCommonActions { get; init; } = 3;   // 根拠: 汎用（経験を借りる前に、似かよりそのものを確かめる入力の数。群にする条件 2 より厳しく）
    public double BorrowStrength { get; init; } = 1.0;       // 根拠: 汎用（借りた経験は仮の観測 1 回分まで。直接の経験がすぐに支配する）

    /// <summary>
    /// 経験を弱い事前の推定として借りてよい組（遷移の特徴が似ていて、その似かより自体に十分な証拠がある、別のノードの見た目の状態どうし）。
    /// 強さは BorrowStrength ×（1 − 距離 / SameGroupDistance）。似かよりの証拠が足りない組は返さない
    /// </summary>
    public IReadOnlyList<(int A, int B, string NodeA, string NodeB, double Distance, int Common, double Strength)> BorrowPairs()
    {
        var list = new List<(int, int, string, string, double, int, double)>();
        var st = _states.Where(x => x.DominantCoarse != null).OrderBy(x => x.Id).ToList();
        for (int i = 0; i < st.Count; i++)
            for (int j = i + 1; j < st.Count; j++)
            {
                string na = Node(st[i].DominantCoarse!, st[i].Id), nb = Node(st[j].DominantCoarse!, st[j].Id);
                if (na == nb) continue;
                var (d, k) = Compare(st[i].Id, st[j].Id);
                if (k < BorrowMinCommonActions || !(d <= SameGroupDistance)) continue;
                list.Add((st[i].Id, st[j].Id, na, nb, d, k, BorrowStrength * (1 - d / SameGroupDistance)));
            }
        return list;
    }

    /// <summary>
    /// 遷移の群（T）: 遷移の特徴が似ている見た目の状態どうし（見た目の似かよりとは別の表現。統合はしない）。
    /// 比べられるだけの観測がある状態だけを、番号の順に先着順で群にする。結果は 見た目の状態の番号 → 群の番号
    /// </summary>
    public IReadOnlyDictionary<int, int> TransitionGroups()
    {
        var leaders = new List<int>(); var g = new Dictionary<int, int>();
        foreach (var s in _states.OrderBy(x => x.Id))
        {
            int found = -1;
            for (int i = 0; i < leaders.Count; i++) if (Distance(s.Id, leaders[i]) is double d && d <= SameGroupDistance) { found = i; break; }
            if (found < 0)
            {
                if (!_fp.TryGetValue(s.Id, out var f) || f.ByAction.Count(kv => kv.Value.Sum() >= MinTries) < MinCommonActions) continue;
                leaders.Add(s.Id); found = leaders.Count - 1;
            }
            g[s.Id] = found + 1;
        }
        return g;
    }

    /// <summary>
    /// 分割の見直し。同じ粗いノード（いちばん多く属したノード）の見た目の状態のうち、遷移の特徴がはっきり違うものを別のノードにする。
    /// いちばん観測の多い状態を基準にし、基準と比べられて、かつはっきり違う状態だけを分ける（証拠のない状態は粗いノードのまま）。
    /// 違いの証拠は2通り（v0.12.0）: (a) 結果の3通り（変化なし・同じ状態のまま変化・別の状態へ）、(b) 次の画面そのもの（予測に効く違い）。
    /// どちらも、差の大きさ（総変動距離 SplitDistance 以上）と証拠（ベイズ因子 10 以上）の両方を求める。
    /// 取り消した状態は、取り消しの後の観測だけで (b) を満たしたときに分け直す（分ける・戻すの往復を防ぐ）
    /// </summary>
    public IReadOnlyList<SplitDecision> RefreshSplits()
    {
        var made = new List<SplitDecision>();
        foreach (var grp in _states.Where(s => s.DominantCoarse != null).GroupBy(s => s.DominantCoarse!))
        {
            var members = grp.OrderByDescending(s => _fp.TryGetValue(s.Id, out var f) ? f.ByAction.Values.Sum(c => c.Sum()) : 0).ThenBy(s => s.Id).ToList();
            if (members.Count < 2) continue;
            var leader = members[0];
            foreach (var s in members.Skip(1))
            {
                if (_splits.ContainsKey(s.Id)) continue;
                bool resplit = _reverted.TryGetValue(s.Id, out var old);
                // 差がはっきりしていて（距離）、その差に十分な証拠がある（ベイズ因子）ときだけ分ける
                string? kind = null; double dist = double.NaN, lbf = double.NaN;
                if (!resplit && Distance(leader.Id, s.Id) is double d && d >= SplitDistance && SplitEvidence(leader.Id, s.Id) is var e && e >= SplitMinLogBayesFactor)
                    (kind, dist, lbf) = ("outcome", d, e);
                else if (NextEvidence(leader.Id, s.Id, resplit) is var ne && ne.Common >= MinCommonActions && ne.Distance >= SplitDistance && ne.LogBf >= SplitMinLogBayesFactor)
                    (kind, dist, lbf) = ("next", ne.Distance, ne.LogBf);
                if (kind == null) continue;
                var node = resplit ? old.Node : $"{grp.Key}#v{s.Id}";
                _reverted.Remove(s.Id); _nextSinceRevert.Remove(s.Id);
                _splits[s.Id] = (grp.Key, node, dist, leader.Id);
                made.Add(new SplitDecision(grp.Key, s.Id, node, dist, lbf, kind, resplit, leader.Id));
            }
        }
        return made;
    }
}

/// <summary>分割の決定。EvidenceKind は "outcome"（結果の3通り）か "next"（次の画面）。Resplit は取り消した後の分け直し</summary>
public sealed record SplitDecision(string Coarse, int Id, string Node, double Distance, double LogBayesFactor, string EvidenceKind, bool Resplit, int Leader);

/// <summary>
/// 分割を取り消すかの判断（v0.12.0）。分けたノードと粗いノードの、分けた後の直接の経験（入力ごとの次の画面の回数）を比べ、
/// 「同じ分布」の証拠が強い（ベイズ因子の対数が −RevertMinLogBayesFactor 以下）ときだけ取り消す。早くまとめ直さない（証拠が十分になってから）
/// </summary>
public static class SplitReview
{
    public const double RevertMinLogBayesFactor = 2.302585;   // 根拠: 汎用（ベイズ因子 10＝ジェフリーズの尺度で「強い」証拠。分けるときと同じ強さ）
    public const int RevertMinCommonActions = 3;              // 根拠: 汎用（取り消しは分けるとき（2）より多くの入力で確かめる）
    public const int RevertTriesFactor = 2;                   // 根拠: 汎用（入力ごとの回数も分けるときの 2 倍を求める。早くまとめ直さない）

    public static (bool Revert, double LogBf, int Common) Review(IReadOnlyDictionary<string, Dictionary<string, int>> splitNode, IReadOnlyDictionary<string, Dictionary<string, int>> coarseNode, int minTries)
    {
        var (lbf, k, _) = VisualStateBook.NextEvidence(splitNode, coarseNode, minTries * RevertTriesFactor);
        return (k >= RevertMinCommonActions && lbf <= -RevertMinLogBayesFactor, lbf, k);
    }

    /// <summary>探索の集計（ノード → 入力 → 次のノードの回数）を、自分へ戻る手を "=" にまとめた形にする</summary>
    public static Dictionary<string, Dictionary<string, int>> NextCounts(string node, NodeStats? ns)
    {
        var r = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        if (ns == null) return r;
        foreach (var (a, st) in ns.Actions)
        {
            if (st.Tries == 0) continue;
            var c = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (to, n) in st.To) { var k = to == node ? "=" : to; c[k] = c.TryGetValue(k, out var x) ? x + n : n; }
            r[a] = c;
        }
        return r;
    }
}
