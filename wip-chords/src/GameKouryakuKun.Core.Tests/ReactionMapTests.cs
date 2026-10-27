using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Perception;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>区画とキーの反応の対応表（v0.9.0）。物体の前提は置かず、区画ごとの統計だけで反応する所を見つける</summary>
public class ReactionMapTests
{
    private const int W = 320, H = 180;

    private static byte[] Texture(int seed)
    {
        var rnd = new Random(seed); var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                // 周期のない模様（2×2 画素の無作為な明暗。周期があると移動の向きが一意に決まらない）
                int p = (y * W + x) * 4; byte v = (byte)(Hash(x / 2, y / 2) % 2 == 0 ? 60 : 190);
                px[p] = px[p + 1] = px[p + 2] = v; px[p + 3] = 255;
            }
        return px;
    }

    private static int Hash(int x, int y) { unchecked { int h = x * 73856093 ^ y * 19349663; h ^= h >> 13; h *= 0x5bd1e995; return (h ^ (h >> 15)) & 0x7fffffff; } }

    private static void Rect(byte[] px, int x0, int y0, int w, int h, Func<int, int, byte> f)
    {
        for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) { int p = (y * W + x) * 4; px[p] = px[p + 1] = px[p + 2] = f(x, y); }
    }

    private static (int X, int Y) CellOrigin(int c, int r) => (c * W / 16, r * H / 9);

    [Fact]
    public void Cells_reacting_to_a_key_are_found_and_common_flicker_is_not()
    {
        var map = new ReactionMap(); var rnd = new Random(1);
        var acts = new[] { "button1", "button2", "dpad_left", "dpad_up" };
        for (int i = 0; i < 120; i++)
        {
            var a = acts[i % acts.Length];
            var before = Texture(i); var after = (byte[])before.Clone();
            // どの入力でも起きる揺れ（左上の区画）
            if (rnd.NextDouble() < 0.5) { var (x, y) = CellOrigin(0, 0); byte v = (byte)rnd.Next(256); Rect(after, x, y, 20, 20, (_, _) => v); }
            if (a == "button1") { var (x, y) = CellOrigin(5, 3); Rect(after, x, y, 20, 20, (_, _) => (byte)rnd.Next(256)); }
            if (a == "dpad_left")
            {
                // 区画 (8,4) の中身が左へ 4 画素ずれる（区画の中の模様はそのまま）
                var (x, y) = CellOrigin(8, 4);
                Rect(after, x, y, 20, 20, (xx, yy) => before[(yy * W + Math.Min(W - 1, xx + 4)) * 4]);
            }
            map.Observe("m1", a, new Frame(i * 2, W, H, before, 0), new Frame(i * 2 + 1, W, H, after, 0));
        }
        var r1 = map.Reactive("m1", "button1");
        Assert.Contains(r1, c => c.Col == 5 && c.Row == 3 && c.TranslatedShare < 0.5);
        var r2 = map.Reactive("m1", "dpad_left");
        var moved = Assert.Single(r2, c => c.Col == 8 && c.Row == 4);
        Assert.True(moved.TranslatedShare > 0.8, $"平行移動の割合 {moved.TranslatedShare}");
        Assert.True(moved.MeanDx < 0, $"移動の向き {moved.MeanDx}");
        foreach (var a in acts) Assert.DoesNotContain(map.Reactive("m1", a), c => c.Col == 0 && c.Row == 0);   // 入力に依らない揺れは反応ではない
        Assert.Empty(map.Reactive("m1", "button2"));
        // 保存と読み込みで同じ結果
        var m2 = new ReactionMap(); m2.Load(map.Export());
        Assert.Equal(map.Summary(), m2.Summary());
    }

    [Fact]
    public void Map_is_resolution_independent()
    {
        var map = new ReactionMap();
        foreach (var (w, h) in new[] { (320, 180), (640, 360) })
        {
            var a = new byte[w * h * 4]; var b = (byte[])a.Clone();
            for (int y = h / 2; y < h / 2 + h / 9; y++) for (int x = w / 2; x < w / 2 + w / 16; x++) { int p = (y * w + x) * 4; b[p] = b[p + 1] = b[p + 2] = 200; }
            var k = map.Classify(new Frame(1, w, h, a, 0), new Frame(2, w, h, b, 0)).Kind;
            Assert.Equal(1, k[4 * 16 + 8]);   // 画面の中央の区画
            Assert.Equal(0, k[0]);
        }
    }
}
