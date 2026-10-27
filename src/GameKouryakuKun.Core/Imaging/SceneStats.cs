namespace GameKouryakuKun.Core.Imaging;

/// <summary>画面全体の単純な統計（ゲーム非依存）。意味は付けず、判定にも使わない（特徴量として記録・分類に渡すだけ）</summary>
public static class SceneStats
{
    /// <summary>平均の明るさ（0〜1。各画素の (R+G+B)/3 の平均。間引いて数える）</summary>
    public static double MeanLuminance(Frame f, int stride = 4)
    {
        if (f.Width <= 0 || f.Height <= 0) return 0;
        var px = f.Bgra; long sum = 0, n = 0;
        for (int y = 0; y < f.Height; y += stride)
            for (int x = 0; x < f.Width; x += stride)
            {
                int p = (y * f.Width + x) * 4;
                sum += px[p] + px[p + 1] + px[p + 2];
                n++;
            }
        return n == 0 ? 0 : sum / (3.0 * 255.0 * n);
    }
}
