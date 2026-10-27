using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.6: 模様が毎回揺れて、ずらしても消えない差があっても、底を引いて共通のずれとする。物 1 つの動きは共通のずれにしない</summary>
public class BlockV0306NoisyShiftTests
{
    private const int W = 640, H = 480;
    private static byte Tex(int x, int y) { unchecked { uint h = (uint)(x / 8 * 73856093) ^ (uint)(y / 8 * 19349663); h ^= h >> 13; h *= 0x5bd1e995; return (byte)(70 + (h >> 8) % 90); } }

    private static Frame F(long id, int oy, Random r, int noise)
    {
        var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        { int i = (y * W + x) * 4; int v = Math.Clamp(Tex(x, y + oy) + r.Next(-noise, noise + 1), 0, 255); b[i] = (byte)v; b[i + 1] = (byte)Math.Min(255, v + 30); b[i + 2] = (byte)v; b[i + 3] = 255; }
        return new Frame(id, W, H, b, 0);
    }

    [Fact]
    public void Shift_under_strong_per_frame_noise_is_found()
    {
        var r = new Random(7); var t = new EntityTracker(); int found = 0; EntityLayer? e = null;
        for (int s = 0; s < 5; s++) { var f = F(s, s * 40, r, 40); e = t.Update(FineGrid.Of(f, t.Stabilizer), f); if (e.CommonShift != null) found++; }
        Assert.True(found >= 3, $"見つけた {found}・{string.Join(",", e!.ShiftProbe ?? Array.Empty<double>())}・底 {t.Motion.LastFloor}");
        Assert.Equal(-40 / 20.0, e.CommonShift![1], 0);
    }

    [Fact]
    public void Noise_alone_is_not_a_shift()
    {
        var r = new Random(8); var t = new EntityTracker(); int found = 0;
        for (int s = 0; s < 5; s++) { var f = F(s, 0, r, 40); if (t.Update(FineGrid.Of(f, t.Stabilizer), f).CommonShift != null) found++; }
        Assert.Equal(0, found);
    }
}
