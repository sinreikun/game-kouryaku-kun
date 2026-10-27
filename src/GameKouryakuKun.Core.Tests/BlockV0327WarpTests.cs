using System.Runtime.CompilerServices;
using System.Text.Json;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// v0.32.7: 場所によって違うずれ（双一次のずれの場）。実機 #150 の縮小画像を模様にして、斜めから見た画面（上ほど小さく見える）を合成し、
/// 上下・左右に視点を動かす。1 つの平行移動では説明できない移動を見つけ、画面の切り替わりはずれにしない
/// </summary>
public class BlockV0327WarpTests
{
    private const int W = 1024, H = 768;
    private static string DataPath([CallerFilePath] string f = "") => Path.Combine(Path.GetDirectoryName(f)!, "Data", "shift-samples-150.json");
    private static byte[] Tex(int step, string key = "prev") =>
        Convert.FromBase64String(JsonDocument.Parse(File.ReadAllText(DataPath())).RootElement.EnumerateArray().First(s => s.GetProperty("step").GetInt32() == step).GetProperty(key).GetString()!);

    /// <summary>世界の模様（128×96 をくり返し、1 点 = 8 画素）を、上ほど縮んで見える画面に写す。camX, camY は視点（世界の画素）</summary>
    private static Frame View(byte[] tex, double camX, double camY, long id, double keystone = 0.8)
    {
        var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            double t = (double)y / H, s = 1 - keystone / 2 + keystone * t;   // 行ごとの見かけの大きさ（上 0.6 倍 → 下 1.4 倍）
            double wy = camY + H * (Math.Log(s) - Math.Log(1 - keystone / 2)) / keystone;   // 見かけの大きさの逆数を積んだ世界の行（∫dy/s）
            for (int x = 0; x < W; x++)
            {
                double wx = camX + (x - W / 2.0) / s;
                int tx = (int)Math.Floor(wx / 8) % 128, ty = (int)Math.Floor(wy / 8) % 96; if (tx < 0) tx += 128; if (ty < 0) ty += 96;
                var v = tex[ty * 128 + tx]; int i = (y * W + x) * 4; b[i] = b[i + 1] = b[i + 2] = v; b[i + 3] = 255;
            }
        }
        return new Frame(id, W, H, b, 0);
    }

    private static (double[]? Shift, string Reason, double[]? Warp) Run(Frame a, Frame c, bool warp)
    {
        var t = new EntityTracker { Motion = new CommonMotion { Warp = warp } };
        t.Update(FineGrid.Of(a, t.Stabilizer), a); var e = t.Update(FineGrid.Of(c, t.Stabilizer), c);
        return (e.CommonShift, t.Motion.LastReason, e.ShiftWarp);
    }

    [Theory]
    [InlineData(0, -180)]   // 視点が上へ → 画面の中身は下へ（上ほど小さく動く）
    [InlineData(0, 100)]
    [InlineData(-90, 0)]    // 視点が左へ → 中身は右へ（行ごとに量が違う）
    [InlineData(90, 0)]
    public void Perspective_moves_are_explained_by_the_warp_field_in_the_right_direction(double mx, double my)
    {
        var tex = Tex(8);
        var a = View(tex, 3000, 3000, 1); var c = View(tex, 3000 + mx, 3000 + my, 2);
        var (s, why, w) = Run(a, c, true);
        Assert.True(s != null, why);
        if (my < 0) Assert.True(s![1] > 1, string.Join(",", s)); if (my > 0) Assert.True(s![1] < -1, string.Join(",", s));
        if (mx < 0) Assert.True(s![0] > 1, string.Join(",", s)); if (mx > 0) Assert.True(s![0] < -1, string.Join(",", s));
        if (my != 0) Assert.True(Math.Abs(s![0]) < 1, string.Join(",", s));
        if (mx != 0) Assert.True(Math.Abs(s![1]) < 1, string.Join(",", s));
        if (w != null && my != 0) Assert.True(w[6] * Math.Sign(-my) > 0, string.Join(",", w));   // dy の Y の係数: 下の行ほど大きく動く
    }

    [Fact]
    public void Large_vertical_perspective_move_fails_as_one_translation_and_is_explained_by_the_warp()
    {
        var tex = Tex(8);
        var o = Run(View(tex, 3000, 3000, 1), View(tex, 3000, 2820, 2), false);
        Assert.Null(o.Shift);   // v0.32.6 まで: 行ごとにずれが違いすぎて説明できない
        var n = Run(View(tex, 3000, 3000, 1), View(tex, 3000, 2820, 2), true);
        Assert.Equal("warp", n.Reason);
        Assert.True(n.Shift![1] > 3 && Math.Abs(n.Shift[0]) < 1, string.Join(",", n.Shift));
        Assert.True(n.Warp![6] > 0, string.Join(",", n.Warp));   // 下の行ほど大きく動く（上ほど小さく見える画面）
    }

    [Fact]
    public void Screen_switch_between_different_scenes_is_not_a_warp()
    {
        var a = View(Tex(8), 3000, 3000, 1); var c = View(Tex(3, "cur"), 3000, 3000, 2);
        var (s, why, _) = Run(a, c, true);
        Assert.True(s == null, why);
    }
}
