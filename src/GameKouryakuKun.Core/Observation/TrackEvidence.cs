namespace GameKouryakuKun.Core.Observation;

/// <summary>追跡と候補の組の観測上の手がかり（意味なし）</summary>
public readonly record struct TrackFeatures(double Dist, double Size, double Aspect, double Hist, bool AppKnown, bool Overlap, bool Live, int Gap);

/// <summary>
/// v0.31.6（P-5 追跡の分断）: 「同じ物か」の手がかりごとの尤度比を、追跡自身の確かな続き（同じ物）と、確かに別の物と分かった組（違う物）から数えて学ぶ（単純ベイズ）。
/// 手がかり: 予測位置（速度・共通のずれ込み）からの距離、大きさの比、縦横比、色の分布の差、見た目の状態の再来、予測した枠との重なり、前の区画が変わったか、見えなかった手数。
/// 見た目は手がかりの 1 つで、同一の条件ではない。判定は事後確率 &gt; 1/2（対数尤度比 + 事前の対数オッズ &gt; 0）。事前のオッズも数えた比。
/// 区切り（ビン）は値の桁の目盛りで、判定のしきい値ではない
/// </summary>
public sealed class TrackEvidence
{
    private static readonly double[][] Edges =
    {
        new[] { 0.5, 1, 2, 3, 5, 8, 13 },      // 距離（細かい区画）
        new[] { 0.2, 0.5, 1, 2 },              // |log 大きさの比|
        new[] { 0.2, 0.5, 1, 2 },              // |log 縦横比の比|
        new[] { 0.1, 0.2, 0.4, 0.8 },          // 色の分布の差（0〜1）
        new[] { 0.5 }, new[] { 0.5 }, new[] { 0.5 },   // 見た目の再来・重なり・前の区画の変化（2 値）
        new[] { 0.5, 1.5, 3.5, 7.5 },          // 見えなかった手数
    };
    private readonly double[][] _same = Edges.Select(e => Enumerable.Repeat(1.0, e.Length + 1).ToArray()).ToArray();   // 根拠: Laplace（各ビン 1）
    private readonly double[][] _diff = Edges.Select(e => Enumerable.Repeat(1.0, e.Length + 1).ToArray()).ToArray();
    public double SameN { get; private set; } = 1;
    public double DiffN { get; private set; } = 1;

    private static int Bin(double v, double[] e) { int b = 0; while (b < e.Length && v >= e[b]) b++; return b; }
    private static double[] Vals(TrackFeatures f) => new[] { f.Dist, f.Size, f.Aspect, f.Hist, f.AppKnown ? 1 : 0, f.Overlap ? 1 : 0, f.Live ? 1 : 0, (double)f.Gap };

    public double LogLikelihoodRatio(TrackFeatures f)
    {
        var v = Vals(f); double s = 0;
        for (int i = 0; i < v.Length; i++)
        {
            int b = Bin(v[i], Edges[i]);
            s += Math.Log(_same[i][b] / _same[i].Sum()) - Math.Log(_diff[i][b] / _diff[i].Sum());
        }
        return s;
    }

    public double PriorLogOdds => Math.Log(SameN / DiffN);
    public double Posterior(TrackFeatures f) => LogLikelihoodRatio(f) + PriorLogOdds;

    public void Learn(TrackFeatures f, bool same)
    {
        var v = Vals(f); var t = same ? _same : _diff;
        for (int i = 0; i < v.Length; i++) t[i][Bin(v[i], Edges[i])]++;
        if (same) SameN++; else DiffN++;
    }
}
