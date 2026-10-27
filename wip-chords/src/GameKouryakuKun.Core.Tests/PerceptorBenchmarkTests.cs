using GameKouryakuKun.Core.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// 知覚器の性能評価（U5）のうち合成データの部分。ゲームの画面を写したものではなく、乱数で作る一般的な図形（矩形の輪郭・塗りの面・雑音）だけを使う。
/// 合成データだけでは実機の性能を保証しない（実機の評価は src の外の開発用の道具で、一時的な正解データを使って行い、結果は数値だけを記録する）。
/// </summary>
public class PerceptorBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public PerceptorBenchmarkTests(ITestOutputHelper o) => _out = o;

    private static Canvas Background(Random r, int w, int h)
    {
        var c = new Canvas(w, h, (byte)r.Next(10, 90));
        for (int k = 0; k < 25; k++)   // 塗りの面（輪郭なし）
        {
            byte v = (byte)r.Next(0, 160);
            c.Rect(r.Next(w), r.Next(h), r.Next(4, w / 3), r.Next(4, h / 3), v, (byte)r.Next(0, 160), (byte)r.Next(0, 160));
        }
        return c;
    }

    private static (int X, int Y, int W, int H) Outline(Canvas c, Random r)
    {
        int w = c.W, h = c.H;
        int rw = r.Next(w / 5, w * 2 / 3), rh = r.Next(h / 6, h / 2), x = r.Next(2, w - rw - 2), y = r.Next(2, h - rh - 2), t = r.Next(1, 3);
        byte fill = (byte)r.Next(0, 40), line = (byte)r.Next(200, 256);
        c.Rect(x, y, rw, rh, fill, fill, fill);
        c.Rect(x, y, rw, t, line, line, line).Rect(x, y + rh - t, rw, t, line, line, line).Rect(x, y, t, rh, line, line, line).Rect(x + rw - t, y, t, rh, line, line, line);
        return (x, y, rw, rh);
    }

    [Fact]
    public void Rectangle_outline_detection_on_random_synthetic_scenes()
    {
        var r = new Random(20261018);
        var det = new PanelDetector();
        int tp = 0, fn = 0, fp = 0, tn = 0;
        for (int i = 0; i < 80; i++)
        {
            var c = Background(r, 480, 270);
            bool has = i % 2 == 0;
            (int X, int Y, int W, int H) truth = default;
            if (has) truth = Outline(c, r);
            var found = det.Detect(c.ToFrame(0));
            bool hit = has && found.Any(p => Math.Abs(p.X - truth.X) <= 6 && Math.Abs(p.Y - truth.Y) <= 6 && Math.Abs(p.Width - truth.W) <= 12);
            if (has) { if (hit) tp++; else fn++; }
            else { if (found.Count > 0) fp++; else tn++; }
        }
        double recall = tp / (double)(tp + fn), fpr = fp / (double)(fp + tn);
        _out.WriteLine($"矩形の輪郭（合成 80 枚）: 再現率 {recall:P0}（{tp}/{tp + fn}）・誤検出率 {fpr:P0}（{fp}/{fp + tn}）");
        Assert.True(recall >= 0.85, $"再現率 {recall:P0}");
        Assert.True(fpr <= 0.15, $"誤検出率 {fpr:P0}");
    }
}
