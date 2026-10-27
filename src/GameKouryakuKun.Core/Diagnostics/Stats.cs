namespace GameKouryakuKun.Core.Diagnostics;

public static class Percentile
{
    /// <summary>最近傍順位法のパーセンタイル（p は 0〜100）。空なら NaN。</summary>
    public static double Of(IEnumerable<double> values, double p)
    {
        var a = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        if (a.Length == 0) return double.NaN;
        int rank = (int)Math.Ceiling(p / 100.0 * a.Length);
        return a[Math.Clamp(rank - 1, 0, a.Length - 1)];
    }
}
