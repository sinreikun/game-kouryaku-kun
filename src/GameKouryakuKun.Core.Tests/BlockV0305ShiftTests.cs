using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.5: 細かい模様が縮小の 1 点に満たない量でずれても、動かない表示の帯があっても共通のずれとする。画面の 1/4 を超える塊は候補にしない</summary>
public class BlockV0305ShiftTests
{
    private const int W = 640, H = 480;
    private static byte Tex(int x, int y) { unchecked { uint h = (uint)(x / 8 * 73856093) ^ (uint)(y / 8 * 19349663); /* v0.30.7: 8 画素の模様（縮小の 1 点〔5 画素〕より大きい。4 画素の模様は 1 点の中で平均されてずれを測れない） */ h ^= h >> 13; h *= 0x5bd1e995; return (byte)(80 + (h >> 8) % 60); } }

    private static Frame F(long id, int ox, int oy)
    {
        var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4; byte v = y < H / 5 ? (byte)20 : Tex(x + ox, y + oy);   // 上の 1/5 は動かない表示の帯
            b[i] = v; b[i + 1] = (byte)(v + 30); b[i + 2] = v; b[i + 3] = 255;
        }
        return new Frame(id, W, H, b, 0);
    }

    [Theory]
    [InlineData(23, 0)]
    [InlineData(-23, 0)]
    [InlineData(0, 21)]
    public void Fine_texture_shift_with_static_band_is_found_in_the_right_direction(int sx, int sy)
    {
        var t = new EntityTracker(); EntityLayer? e = null; int found = 0;
        for (int s = 0; s < 5; s++) { var f = F(s, s * sx, s * sy); e = t.Update(FineGrid.Of(f, t.Stabilizer), f); if (e.CommonShift != null) found++; }
        Assert.True(found >= 3, $"見つけた {found}・調べた {string.Join(",", e!.ShiftProbe ?? Array.Empty<double>())}");
        double cell = W / 32.0;
        Assert.Equal(-sx / cell, e.CommonShift![0], 0);
        Assert.Equal(-sy / (H / 24.0), e.CommonShift[1], 0);
        Assert.True(e.Candidates.Count <= 3, $"候補 {e.Candidates.Count}");
        Assert.DoesNotContain(e.Candidates, c => c.Cells * EntityTracker.MaxCandidateShareDen > 32 * 24);
    }

    [Fact]
    public void Whole_screen_change_is_not_a_local_candidate()
    {
        var t = new EntityTracker();
        Frame Solid(long id, byte v) { var b = new byte[W * H * 4]; for (int i = 0; i < W * H; i++) { b[i * 4] = 30; b[i * 4 + 1] = v; b[i * 4 + 2] = 30; b[i * 4 + 3] = 255; } if (v > 100) for (int y = 60; y < 420; y++) for (int x = 60; x < 560; x++) { int i = (y * W + x) * 4; b[i] = 255; b[i + 1] = 40; } return new Frame(id, W, H, b, 0); }
        t.Update(FineGrid.Of(Solid(0, 40), t.Stabilizer), Solid(0, 40));
        var f = Solid(1, 200);   // 画面の 7 割を覆う四角が出る
        var e = t.Update(FineGrid.Of(f, t.Stabilizer), f);
        Assert.DoesNotContain(e.Candidates, c => c.Cells * EntityTracker.MaxCandidateShareDen > 32 * 24);
        Assert.True(t.LargeSuppressed >= 1);
    }
}
