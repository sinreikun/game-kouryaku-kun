namespace GameKouryakuKun.Core.Imaging;

/// <summary>
/// 1枚の画面（BGRA 8bit、クライアント領域）。キャプチャ方式に依存しない共通表現。
/// TimestampMs は MonoClock 基準（フレーム到着時刻）。
/// </summary>
public sealed class Frame
{
    private static long _ids;
    /// <summary>全取得方式で共通の単調増加フレームID（方式を切り替えてもIDが戻らない）</summary>
    public static long NextId() => Interlocked.Increment(ref _ids);

    public long Id { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public double TimestampMs { get; }
    public string Source { get; }

    public Frame(long id, int width, int height, byte[] bgra, double timestampMs, string source = "")
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "サイズが0以下です");
        if (bgra.Length < width * height * 4) throw new ArgumentException("バッファがサイズに足りません", nameof(bgra));
        Id = id; Width = width; Height = height; Bgra = bgra; TimestampMs = timestampMs; Source = source;
    }

    public bool SameSize(Frame other) => other.Width == Width && other.Height == Height;

    public sealed record Stats(double MeanLuma, double StdLuma, double NearBlackRatio, bool LooksBlack);

    /// <summary>輝度統計（黒画面判定用）。大きな画像は間引いて計算する。</summary>
    public Stats ComputeStats()
    {
        long n = (long)Width * Height;
        int step = Math.Max(1, (int)Math.Sqrt(n / 250_000.0));
        double sum = 0, sum2 = 0; long nearBlack = 0, cnt = 0;
        for (int y = 0; y < Height; y += step)
        for (int x = 0; x < Width; x += step)
        {
            int i = (y * Width + x) * 4;
            double l = 0.114 * Bgra[i] + 0.587 * Bgra[i + 1] + 0.299 * Bgra[i + 2];
            sum += l; sum2 += l * l; cnt++;
            if (l < 10) nearBlack++;
        }
        double mean = sum / cnt, std = Math.Sqrt(Math.Max(0, sum2 / cnt - mean * mean));
        return new Stats(Math.Round(mean, 2), Math.Round(std, 2), Math.Round((double)nearBlack / cnt, 4), mean < 8 && std < 4);
    }
}
