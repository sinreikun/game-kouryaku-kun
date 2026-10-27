namespace GameKouryakuKun.Core.Perception;

public readonly record struct Vec2(double X, double Y)
{
    public double Length => Math.Sqrt(X * X + Y * Y);
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(double k, Vec2 a) => new(k * a.X, k * a.Y);
    public override string ToString() => FormattableString.Invariant($"({X:0.0}, {Y:0.0})");
}

/// <summary>格子の推定値（phase2-design §4.1。遠近の補正はまだ 0 固定）</summary>
public sealed record GridEstimate(Vec2 BasisU, Vec2 BasisV, double ExplainedRate, int MoveTurns, int Explained, double Confidence)
{
    /// <summary>画面上のずれ d を、格子の整数結合 (a, b) と残差で表す</summary>
    public (int A, int B, double Residual) Decompose(Vec2 d) => GridEstimator.Decompose(BasisU, BasisV, d);
}

/// <summary>
/// 画面全体が平行移動した手の移動量の候補から、少数の基底の整数結合で説明できる周期構造（格子）があるかを調べる（§4.2）。
/// 格子の存在は前提にしない（U4）: 説明率が低ければ採用しない。周期・反復パターンという一般の視覚特徴だけを使う。
/// 格子という汎用の仮定だけを使い、どのキーがどの向きかは事前に決めない（キー名は記録のためだけに持つ）。
/// </summary>
public sealed class GridEstimator
{
    private readonly List<(string Key, IReadOnlyList<MotionCandidate> Cands)> _turns = new();

    /// <summary>説明できたとみなす残差（基底の短い方の長さに対する比）</summary>
    public double Tolerance { get; init; } = 0.06;  // 根拠: 汎用（基底長の6%以内の残差＝説明できた）
    /// <summary>この手数に満たないうちは推定しない</summary>
    public int MinTurns { get; init; } = 12;  // 根拠: 汎用（推定に要る最小の標本数）
    /// <summary>採用に必要な説明率</summary>
    public double MinExplained { get; init; } = 0.9;  // 根拠: 汎用（9割を説明できなければ周期構造を採用しない）
    public int MaxCoef { get; init; } = 3;  // 根拠: 汎用（整数結合の係数の上限）

    public int Count => _turns.Count;

    /// <summary>画面全体が平行移動した手を1件足す（候補が空なら足さない）</summary>
    public void Add(string key, IReadOnlyList<MotionCandidate> candidates)
    {
        if (candidates.Count == 0) return;
        _turns.Add((key, candidates));
    }

    public static (int A, int B, double Residual) Decompose(Vec2 u, Vec2 v, Vec2 d)
    {
        double det = u.X * v.Y - u.Y * v.X;
        if (Math.Abs(det) < 1e-9) return (0, 0, double.PositiveInfinity);
        double a = (d.X * v.Y - d.Y * v.X) / det, b = (u.X * d.Y - u.Y * d.X) / det;
        int ia = (int)Math.Round(a), ib = (int)Math.Round(b);
        var r = d - (ia * u + ib * v);
        return (ia, ib, r.Length);
    }

    public GridEstimate? Estimate()
    {
        if (_turns.Count < MinTurns) return null;
        // 最良の候補を群にまとめる
        var best = _turns.Select(t => new Vec2(t.Cands[0].Dx, t.Cands[0].Dy)).Where(v => v.Length > 1).ToList();
        if (best.Count < MinTurns / 2) return null;
        var clusters = new List<(Vec2 C, int N)>();
        double minLen = best.Min(v => v.Length);
        double tol0 = Math.Max(3, minLen * 0.05);
        foreach (var v in best.OrderBy(v => v.Length))
        {
            int k = clusters.FindIndex(c => (c.C - v).Length <= tol0);
            if (k < 0) clusters.Add((v, 1));
            else { var c = clusters[k]; clusters[k] = ((1.0 / (c.N + 1)) * ((double)c.N * c.C + v), c.N + 1); }
        }
        var centers = clusters.OrderByDescending(c => c.N).Take(10).Select(c => c.C).ToList();
        var basisCands = new List<Vec2>(centers);
        for (int i = 0; i < centers.Count; i++)
            for (int j = i + 1; j < centers.Count; j++) { basisCands.Add(centers[i] - centers[j]); basisCands.Add(centers[i] + centers[j]); }
        basisCands = basisCands.Where(v => v.Length > 2).ToList();
        (Vec2 U, Vec2 V, int N, double Det)? top = null;
        for (int i = 0; i < basisCands.Count; i++)
            for (int j = i + 1; j < basisCands.Count; j++)
            {
                var u = basisCands[i]; var v = basisCands[j];
                double det = Math.Abs(u.X * v.Y - u.Y * v.X);
                double shortLen = Math.Min(u.Length, v.Length);
                if (det < 0.3 * u.Length * v.Length) continue;   // ほぼ平行な組は使わない
                int n = CountExplained(u, v, shortLen * Tolerance);
                // 説明率が高い順。同じなら面積（det）の大きい格子＝最も粗い格子
                if (top is null || n > top.Value.N || (n == top.Value.N && det > top.Value.Det * 1.02))
                    top = (u, v, n, det);
            }
        if (top is null) return null;
        var (bu, bv) = Reduce(top.Value.U, top.Value.V);
        // 基底を、説明できた手の最良候補から最小二乗で詰める
        (bu, bv) = Refine(bu, bv);
        int expl = CountExplained(bu, bv, Math.Min(bu.Length, bv.Length) * Tolerance);
        double rate = expl / (double)_turns.Count;
        double conf = Math.Clamp((rate - 0.5) / (1 - 0.5), 0, 1) * Math.Min(1, _turns.Count / 30.0);
        return new GridEstimate(bu, bv, rate, _turns.Count, expl, conf);
    }

    /// <summary>採用条件（§4.2 の 4）を満たすか</summary>
    public static bool Accepted(GridEstimate? g, double minRate = 0.9, int minTurns = 30) => g != null && g.MoveTurns >= minTurns && g.ExplainedRate >= minRate;

    private int CountExplained(Vec2 u, Vec2 v, double tol)
    {
        int n = 0;
        foreach (var (_, cands) in _turns)
            foreach (var c in cands)
            {
                var (a, b, r) = Decompose(u, v, new Vec2(c.Dx, c.Dy));
                if ((a != 0 || b != 0) && Math.Abs(a) <= MaxCoef && Math.Abs(b) <= MaxCoef && r <= tol) { n++; break; }
            }
        return n;
    }

    private (Vec2, Vec2) Refine(Vec2 u, Vec2 v)
    {
        // d ≈ a u + b v を、u・v について最小二乗で解く（x と y は別々に同じ式）
        double saa = 0, sab = 0, sbb = 0, sax = 0, sbx = 0, say = 0, sby = 0; int n = 0;
        double tol = Math.Min(u.Length, v.Length) * Tolerance;
        foreach (var (_, cands) in _turns)
            foreach (var c in cands)
            {
                var d = new Vec2(c.Dx, c.Dy);
                var (a, b, r) = Decompose(u, v, d);
                if ((a == 0 && b == 0) || r > tol) continue;
                saa += a * a; sab += a * b; sbb += b * b; sax += a * d.X; sbx += b * d.X; say += a * d.Y; sby += b * d.Y; n++;
                break;
            }
        double det = saa * sbb - sab * sab;
        if (n < 4 || Math.Abs(det) < 1e-9) return (u, v);
        var nu = new Vec2((sbb * sax - sab * sbx) / det, (sbb * say - sab * sby) / det);
        var nv = new Vec2((saa * sbx - sab * sax) / det, (saa * sby - sab * say) / det);
        return Reduce(nu, nv);
    }

    /// <summary>格子の基底を短い組に直し（Lagrange–Gauss の簡約）、向きをそろえる（U は x 成分が大きく正、V は y 成分が正）</summary>
    public static (Vec2 U, Vec2 V) Reduce(Vec2 u, Vec2 v)
    {
        for (int it = 0; it < 32; it++)
        {
            if (u.Length > v.Length) (u, v) = (v, u);
            double dot = u.X * v.X + u.Y * v.Y;
            double m = Math.Round(dot / (u.X * u.X + u.Y * u.Y));
            if (m == 0) break;
            v = v - m * u;
        }
        if (Math.Abs(u.X) * Math.Abs(v.Y) < Math.Abs(u.Y) * Math.Abs(v.X)) (u, v) = (v, u);
        if (u.X < 0) u = -1 * u;
        if (v.Y < 0) v = -1 * v;
        return (u, v);
    }
}
