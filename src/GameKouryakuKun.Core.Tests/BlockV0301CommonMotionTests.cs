using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.1: 画面全体が同じだけずれる手（視点の移動のような動き）で、背景が新しい候補だらけにならない。関係は近い組に絞る</summary>
public class BlockV0301CommonMotionTests
{
    private const int W = 320, H = 240;
    private static byte Tex(int x, int y) { unchecked { uint h = (uint)(Math.Floor(x / 20.0) * 73856093) ^ (uint)(Math.Floor(y / 20.0) * 19349663); h ^= h >> 13; h *= 0x5bd1e995; return (byte)(40 + (h >> 8) % 160); } }

    /// <summary>背景（模様）を ox だけ左へずらした画面。obj があれば画面の上の固定の位置 (ax, ay) に 20×20 の赤い四角</summary>
    private static Frame F(long id, int ox, (int X, int Y)? obj = null, bool plain = false)
    {
        var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        { int i = (y * W + x) * 4; byte v = plain ? (byte)60 : Tex(x + ox, y); b[i] = b[i + 1] = b[i + 2] = v; b[i + 3] = 255; }
        if (obj is { } o) for (int y = o.Y; y < o.Y + 20; y++) for (int x = o.X; x < o.X + 20; x++) { int i = (y * W + x) * 4; b[i] = 0; b[i + 1] = 0; b[i + 2] = 255; }
        return new Frame(id, W, H, b, 0);
    }

    [Fact]
    public void Whole_screen_shift_is_common_motion_not_many_new_candidates()
    {
        var t = new EntityTracker(); EntityLayer? last = null;
        for (int s = 0; s < 6; s++) { var f = F(s, s * 20, (150, 110)); last = t.Update(FineGrid.Of(f, t.Stabilizer), f); }
        Assert.NotNull(last!.CommonShift);
        Assert.Equal(-2.0, last.CommonShift![0], 1);   // 20 画素 = 細かい区画 2 つ分（模様は左へずれる）
        Assert.Equal(0.0, last.CommonShift[1], 1);
        // 背景はずれを差し引くと変わらない。画面に固定の物だけが（背景に対して）動く候補
        Assert.True(last.Candidates.Count <= 2, $"候補 {last.Candidates.Count}");
        Assert.True(t.Created <= 4, $"新しい追跡 {t.Created}");
        Assert.True(t.CommonShiftSteps >= 4);
    }

    [Fact]
    public void Without_common_shift_tracking_is_unchanged()
    {
        var t = new EntityTracker(); EntityLayer? last = null;
        for (int s = 0; s < 5; s++) { var f = F(s, 0, (40 + s * 20, 110), plain: true); last = t.Update(FineGrid.Of(f, t.Stabilizer), f); }
        Assert.Null(last!.CommonShift);
        var c = Assert.Single(last.Candidates);
        Assert.True(c.Moved, $"{c}");
        Assert.True(c.Moved); Assert.Equal(2.0, c.Dx, 1);
        Assert.Equal(0, t.CommonShiftSteps);
    }

    [Fact]
    public void Relations_are_pruned_to_near_pairs_but_all_pair_count_is_kept()
    {
        var t = new EntityTracker();
        Frame Dots(long id, bool on)
        {
            var b = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++) { b[i * 4] = b[i * 4 + 1] = b[i * 4 + 2] = 30; b[i * 4 + 3] = 255; }
            if (on) for (int k = 0; k < 20; k++) { int x0 = 10 + (k % 5) * 60, y0 = 10 + (k / 5) * 55; for (int y = y0; y < y0 + 10; y++) for (int x = x0; x < x0 + 10; x++) { int i = (y * W + x) * 4; b[i + 1] = 230; } }
            return new Frame(id, W, H, b, 0);
        }
        t.Update(FineGrid.Of(Dots(0, false), t.Stabilizer));
        var e = t.Update(FineGrid.Of(Dots(1, true), t.Stabilizer));
        Assert.Equal(20, e.Candidates.Count);
        Assert.Equal(190, e.RelationPairs);
        Assert.InRange(e.Relations.Count, 20, 60);   // 候補ごとに近い 3 組（両側から重なる）
        foreach (var c in e.Candidates) Assert.Contains(e.Relations, r => r.TrackA == c.Track || r.TrackB == c.Track);
    }
}
