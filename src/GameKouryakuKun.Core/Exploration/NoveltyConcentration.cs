namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// 「同じ画面で同じ操作をしたとき、まだ見たことのない結果がどれだけ出やすいか」を経験から推定する（v0.14.0）。
/// 次の画面の予測分布を、ディリクレ過程の予測分布 P(既に見た k) = w_k / (n + α)、P(まだ見ていない結果) = α / (n + α) にする。
/// α（集中度）は、関係ごとに n 回観測した後に新しい結果が出たか（出た: α/(α+n)、出ない: n/(α+n)）の尤度を最大にする値。
/// 従来のラプラス平滑化（すべての既知の画面に 1 回分ずつ足す）は、画面の数が増えるほど予測が薄まり、確率が当たる割合より大きく低く出ていた。
/// ゲームの知識は使わない（経験の数え方だけ）
/// </summary>
public sealed class NoveltyConcentration
{
    public const double DefaultAlpha = 1.0;   // 根拠: 汎用（材料がないうちは「1 回分」の新しい結果を見込む＝従来の 1 回分の平滑化と同じ強さ）
    public const double MinAlpha = 0.01;      // 根拠: 汎用（探す範囲の下限: 1 回分の 1/100）
    public const double MaxAlpha = 100;       // 根拠: 汎用（探す範囲の上限: 1 回分の 100 倍）
    public const int GridSteps = 60;          // 根拠: 汎用（対数で等間隔。隣どうしの比 約 1.17 倍）
    public const int MaxBucket = 200;         // 根拠: 汎用（観測の回数をまとめる上限。それより多い関係は同じ扱い）
    public int RefitEvery { get; init; } = 50;   // 根拠: 汎用（推定し直す間隔。最初の推定もこの回数がそろってから）

    private readonly long[] _novel = new long[MaxBucket + 1], _seen = new long[MaxBucket + 1];
    public double Alpha { get; private set; } = DefaultAlpha;
    public long Events { get; private set; }
    public long NovelEvents { get; private set; }

    /// <summary>予測に使った回数 n（重み）の関係で、結果が新しかったかを数える。n が 0（初めての関係）は数えない</summary>
    public void Observe(double priorTotal, bool novel)
    {
        if (!(priorTotal > 0)) return;
        int b = (int)Math.Clamp(Math.Round(priorTotal), 1, MaxBucket);
        if (novel) { _novel[b]++; NovelEvents++; } else _seen[b]++;
        Events++;
        if (Events % RefitEvery == 0) Alpha = Fit();
    }

    public double LogLikelihood(double a)
    {
        double l = 0;
        for (int n = 1; n <= MaxBucket; n++)
        {
            if (_novel[n] > 0) l += _novel[n] * Math.Log(a / (a + n));
            if (_seen[n] > 0) l += _seen[n] * Math.Log(n / (a + n));
        }
        return l;
    }

    private double Fit()
    {
        double best = Alpha, bl = double.NegativeInfinity;
        for (int i = 0; i <= GridSteps; i++)
        {
            double a = Math.Exp(Math.Log(MinAlpha) + i * (Math.Log(MaxAlpha) - Math.Log(MinAlpha)) / GridSteps);
            double l = LogLikelihood(a);
            if (l > bl) { bl = l; best = a; }
        }
        return best;
    }

    /// <summary>予測分布: 既に見た結果の確率</summary>
    public double PSeen(double w, double total) => w / (total + Alpha);
    /// <summary>予測分布: まだ見ていない結果の 1 つあたりの確率（まだ見ていない結果の数 unseen で等分）。何も見ていない関係では一様</summary>
    public double PUnseen(double total, int unseen) => Alpha / (total + Alpha) / Math.Max(1, unseen);

    /// <summary>予測分布のエントロピー（ビット）</summary>
    public double Entropy(IEnumerable<double> seen, double total, int unseen)
    {
        double h = 0;
        foreach (var w in seen) { if (w <= 0) continue; double p = PSeen(w, total); h -= p * Math.Log2(p); }
        if (unseen > 0) { double p = PUnseen(total, unseen); h -= unseen * p * Math.Log2(p); }
        return h;
    }
}
