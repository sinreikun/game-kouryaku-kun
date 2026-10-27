using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Perception;

/// <summary>縮小した輝度画像（float）。Scale は元の画素に対する縮小率（2 なら 1/2）</summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public int Scale { get; }
    public float[] Px { get; }

    public GrayImage(int w, int h, int scale, float[]? px = null)
    {
        Width = w; Height = h; Scale = scale; Px = px ?? new float[w * h];
    }

    public float this[int x, int y] => Px[y * Width + x];

    /// <summary>scale×scale の箱で平均して縮小した輝度</summary>
    public static GrayImage FromFrame(Frame f, int scale)
    {
        int w = f.Width / scale, h = f.Height / scale;
        var g = new GrayImage(w, h, scale);
        var src = f.Bgra; float inv = 1f / (scale * scale);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int yy = y * scale; yy < y * scale + scale; yy++)
                {
                    int p = (yy * f.Width + x * scale) * 4;
                    for (int xx = 0; xx < scale; xx++, p += 4) s += 0.114f * src[p] + 0.587f * src[p + 1] + 0.299f * src[p + 2];
                }
                g.Px[y * w + x] = s * inv;
            }
        return g;
    }

    /// <summary>局所平均（半径 r の箱）を引いた高域成分。画面に固定の明暗（周辺減光など）の影響を減らす</summary>
    public GrayImage HighPass(int r)
    {
        int w = Width, h = Height;
        var ii = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double row = 0;
            for (int x = 0; x < w; x++) { row += Px[y * w + x]; ii[(y + 1) * (w + 1) + x + 1] = ii[y * (w + 1) + x + 1] + row; }
        }
        var o = new GrayImage(w, h, Scale);
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                double s = ii[y1 * (w + 1) + x1] - ii[y0 * (w + 1) + x1] - ii[y1 * (w + 1) + x0] + ii[y0 * (w + 1) + x0];
                o.Px[y * w + x] = Px[y * w + x] - (float)(s / ((x1 - x0) * (y1 - y0)));
            }
        }
        return o;
    }
}
