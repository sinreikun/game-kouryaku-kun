using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.32.4: ふだんの変化は比べられる区画（ずらした後の底より大きく変わった区画）で測る。模様の薄い広い所があっても画面全体のずれを見つけ、切り替わりはずれにしない</summary>
public class BlockV0324ComparableMedianTests
{
    private const int W = 1280, H = 720;
    private static Frame Scene(int offX, int seed, long id, bool other = false)
    {
        var tx = new Random(other ? 9 : 1); var tex = new byte[64 * 40]; for (int i = 0; i < tex.Length; i++) tex[i] = (byte)tx.Next(30, 120);
        var r = new Random(seed); var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int wx = x + offX, v;
            if (y < H / 3) v = tex[(y / 23 % 40) * 64 + (wx / 23 % 64)];          // 模様のはっきりした所（画面の 1/3）
            else v = 70 + (tex[(y / 23 % 40) * 64 + (wx / 23 % 64)] - 75) / 12;    // 暗く模様の薄い広い所（画面の 2/3）
            v += r.Next(-3, 4);
            int i = (y * W + x) * 4; b[i] = b[i + 1] = b[i + 2] = (byte)Math.Clamp(v, 0, 255); b[i + 3] = 255;
        }
        return new Frame(id, W, H, b, 0);
    }

    private static (double[]? Shift, string Reason) Run(Frame a, Frame c, bool comparable)
    {
        var t = new EntityTracker { Motion = new CommonMotion { ComparableMedian = comparable } };
        t.Update(FineGrid.Of(a, t.Stabilizer), a); var e = t.Update(FineGrid.Of(c, t.Stabilizer), c);
        return (e.CommonShift, t.Motion.LastReason);
    }

    [Fact]
    public void Scroll_over_a_mostly_faint_screen_is_a_common_shift_and_a_scene_change_is_not()
    {
        var (s, why) = Run(Scene(200, 1, 1), Scene(300, 2, 2), true);
        Assert.True(s != null && s[0] < -1.5 && Math.Abs(s[1]) < 0.5, why);
        var (s2, _) = Run(Scene(200, 1, 3), Scene(200, 2, 4, other: true), true);
        Assert.Null(s2);
    }
}
