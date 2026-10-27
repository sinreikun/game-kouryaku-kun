using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Tests;

/// <summary>合成画像の作成補助</summary>
internal sealed class Canvas
{
    public int W { get; }
    public int H { get; }
    public byte[] Px { get; }
    public Canvas(int w, int h, byte gray = 60)
    {
        W = w; H = h; Px = new byte[w * h * 4];
        for (int i = 0; i < Px.Length; i += 4) { Px[i] = gray; Px[i + 1] = gray; Px[i + 2] = gray; Px[i + 3] = 255; }
    }
    public Canvas Clone() { var c = new Canvas(W, H); Buffer.BlockCopy(Px, 0, c.Px, 0, Px.Length); return c; }
    public Canvas Rect(int x, int y, int w, int h, byte b, byte g, byte r)
    {
        for (int yy = Math.Max(0, y); yy < Math.Min(H, y + h); yy++)
        for (int xx = Math.Max(0, x); xx < Math.Min(W, x + w); xx++)
        { int i = (yy * W + xx) * 4; Px[i] = b; Px[i + 1] = g; Px[i + 2] = r; }
        return this;
    }
    /// <summary>格子模様（スクロールのテスト用）。offset だけずらす</summary>
    public static Canvas Checker(int w, int h, int cell, int offX, int offY)
    {
        var c = new Canvas(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool on = (((x + offX) / cell) + ((y + offY) / cell)) % 2 == 0;
            int i = (y * w + x) * 4; byte v = on ? (byte)200 : (byte)40;
            c.Px[i] = v; c.Px[i + 1] = (byte)(v / 2); c.Px[i + 2] = (byte)(255 - v);
        }
        return c;
    }
    public Frame ToFrame(double ts) => new(Frame.NextId(), W, H, (byte[])Px.Clone(), ts, "test");
}
