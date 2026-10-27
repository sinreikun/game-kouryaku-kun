using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.4: 大きなずれも共通のずれとして見つける。いっしょに変わり続ける隣り合った候補を 1 つのまとまりにする</summary>
public class BlockV0304CommonFateTests
{
    private const int W = 320, H = 240;
    private static byte Tex(int x, int y) { unchecked { uint h = (uint)(Math.Floor(x / 10.0) * 73856093) ^ (uint)(Math.Floor(y / 10.0) * 19349663); h ^= h >> 13; h *= 0x5bd1e995; return (byte)(90 + (h >> 8) % 40); } }

    [Fact]
    public void Large_shift_of_a_quarter_screen_is_found()
    {
        var t = new EntityTracker(); int found = 0; double dx = 0;
        for (int s = 0; s < 5; s++)
        {
            var b = new byte[W * H * 4];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; byte v = Tex(x + s * 90, y); b[i] = b[i + 2] = v; b[i + 1] = (byte)(v + 40); b[i + 3] = 255; }
            var f = new Frame(s, W, H, b, 0);
            var e = t.Update(FineGrid.Of(f, t.Stabilizer), f);
            if (e.CommonShift is { } cs) { found++; dx = cs[0]; Assert.NotNull(e.ResidualCells); }
            if (s > 0) Assert.NotNull(e.ShiftProbe);
        }
        Assert.Equal(4, found);
        Assert.Equal(-9.0, dx, 1);   // 90 画素 = 細かい区画 9 つ分
    }

    [Fact]
    public void Fragments_that_change_together_become_one_group_but_keep_their_tracks()
    {
        var t = new EntityTracker(); EntityLayer? e = null;
        for (int s = 0; s < 10; s++)
        {
            var b = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++) { b[i * 4] = b[i * 4 + 1] = b[i * 4 + 2] = 30; b[i * 4 + 3] = 255; }
            if (s > 0)
                foreach (var x0 in new[] { 100, 130 })   // 間が 1 区画（10 画素）の 2 つの部分。1 手ごとにいっしょに色が変わる
                    for (int y = 100; y < 120; y++) for (int x = x0; x < x0 + 20; x++) { int i = (y * W + x) * 4; b[i + (s % 2 == 0 ? 1 : 2)] = 230; }
            var f = new Frame(s, W, H, b, 0);
            e = t.Update(FineGrid.Of(f, t.Stabilizer), f);
        }
        Assert.Equal(2, e!.Candidates.Count);
        var g = Assert.Single(e.Groups);
        Assert.Equal(e.Candidates.Select(c => c.Track).OrderBy(x => x), g.Tracks);
        Assert.True(g.CoSteps >= EntityTracker.GroupMinCo);
    }

    [Fact]
    public void Far_apart_candidates_are_not_grouped()
    {
        var t = new EntityTracker(); EntityLayer? e = null;
        for (int s = 0; s < 10; s++)
        {
            var b = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++) { b[i * 4] = b[i * 4 + 1] = b[i * 4 + 2] = 30; b[i * 4 + 3] = 255; }
            if (s > 0) foreach (var x0 in new[] { 20, 260 }) for (int y = 100; y < 120; y++) for (int x = x0; x < x0 + 20; x++) { int i = (y * W + x) * 4; b[i + (s % 2 == 0 ? 1 : 2)] = 230; }
            var f = new Frame(s, W, H, b, 0);
            e = t.Update(FineGrid.Of(f, t.Stabilizer), f);
        }
        Assert.Empty(e!.Groups);
    }
}
