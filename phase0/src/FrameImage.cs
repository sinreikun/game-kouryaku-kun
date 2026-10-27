using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GameKouryakuKun.Phase0;

/// <summary>BGRA 8bit の画像バッファ。キャプチャ結果の統計・差分・PNG保存を担う。</summary>
public sealed class FrameImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public string Source { get; init; } = "";
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public FrameImage(int w, int h, byte[] bgra) { Width = w; Height = h; Bgra = bgra; }

    public sealed record Stats(double MeanLuma, double StdLuma, double NearBlackRatio, bool LooksBlack, bool LooksUniform);

    public Stats ComputeStats()
    {
        long n = (long)Width * Height; if (n == 0) return new Stats(0, 0, 1, true, true);
        double sum = 0, sum2 = 0; long nearBlack = 0;
        // 大きい画像は間引いて計算
        int step = Math.Max(1, (int)Math.Sqrt(n / 250_000.0));
        long cnt = 0;
        for (int y = 0; y < Height; y += step)
        for (int x = 0; x < Width; x += step)
        {
            int i = (y * Width + x) * 4;
            double l = 0.114 * Bgra[i] + 0.587 * Bgra[i + 1] + 0.299 * Bgra[i + 2];
            sum += l; sum2 += l * l; cnt++;
            if (l < 10) nearBlack++;
        }
        double mean = sum / cnt, std = Math.Sqrt(Math.Max(0, sum2 / cnt - mean * mean));
        double nb = (double)nearBlack / cnt;
        return new Stats(Math.Round(mean, 2), Math.Round(std, 2), Math.Round(nb, 4), mean < 8 && std < 4, std < 1.5);
    }

    public sealed record Diff(bool Comparable, double ChangedRatio, double MeanAbsDiff, string Note);

    /// <summary>ピクセル差分。チャンネル最大差が threshold を超えた画素を「変化」とみなす。</summary>
    public static Diff Compare(FrameImage? a, FrameImage? b, int threshold = 24, string? maskPath = null)
    {
        if (a == null || b == null) return new Diff(false, 0, 0, "画像なし");
        if (a.Width != b.Width || a.Height != b.Height)
            return new Diff(false, 1, 0, $"サイズ不一致 {a.Width}x{a.Height} vs {b.Width}x{b.Height}");
        long changed = 0; double absSum = 0; long n = (long)a.Width * a.Height;
        byte[]? mask = maskPath != null ? new byte[n * 4] : null;
        for (long p = 0; p < n; p++)
        {
            long i = p * 4;
            int db = Math.Abs(a.Bgra[i] - b.Bgra[i]), dg = Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]), dr = Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]);
            int m = Math.Max(db, Math.Max(dg, dr));
            absSum += (db + dg + dr) / 3.0;
            bool ch = m > threshold;
            if (ch) changed++;
            if (mask != null)
            {
                // 変化画素は赤、それ以外は元画像を暗くしたグレー
                byte g = (byte)((a.Bgra[i] + a.Bgra[i + 1] + a.Bgra[i + 2]) / 9);
                mask[i] = ch ? (byte)0 : g; mask[i + 1] = ch ? (byte)0 : g; mask[i + 2] = ch ? (byte)255 : g; mask[i + 3] = 255;
            }
        }
        if (mask != null) new FrameImage(a.Width, a.Height, mask).SavePng(maskPath!);
        return new Diff(true, Math.Round((double)changed / n, 5), Math.Round(absSum / n, 3), "");
    }

    public void SavePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                IntPtr row = data.Scan0 + y * data.Stride;
                Marshal.Copy(Bgra, y * Width * 4, row, Width * 4);
            }
            // アルファは不透明化（WGCのプリマルチプライドαやPrintWindowの0αで透明PNGにならないように）
            unsafe
            {
                for (int y = 0; y < Height; y++)
                {
                    byte* r = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < Width; x++) r[x * 4 + 3] = 255;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }

    public static FrameImage FromBitmap(Bitmap bmp, string source)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * bmp.Width * 4, bmp.Width * 4);
            return new FrameImage(bmp.Width, bmp.Height, buf) { Source = source };
        }
        finally { bmp.UnlockBits(data); }
    }
}
