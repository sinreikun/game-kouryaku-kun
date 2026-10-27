using System.Runtime.CompilerServices;
using System.Text.Json;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// v0.32.8: 種から育てるずれの場の当てはめと、移動として無理な場（場所による違いが中央のずれを超える）を外す確かめ。
/// 実機 #153 の縮小画像（上下の移動）と #130 の画面の切り替わりを再生する
/// </summary>
public class BlockV0328WarpConsensusTests
{
    private static string Data(string name, [CallerFilePath] string f = "") => Path.Combine(Path.GetDirectoryName(f)!, "Data", name);
    private static JsonElement Sample(string file, int step) => JsonDocument.Parse(File.ReadAllText(Data(file))).RootElement.EnumerateArray().First(s => s.GetProperty("step").GetInt32() == step);
    private static Frame Up(byte[] g, int w, int h, long id, int k = 8)
    {
        int W = w * k, H = h * k; var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; var v = g[(y / k) * w + x / k]; b[i] = b[i + 1] = b[i + 2] = v; b[i + 3] = 255; }
        return new Frame(id, W, H, b, 0);
    }
    private static (double[]? Shift, CommonMotion M) Run(JsonElement s, bool warp)
    {
        int w = s.GetProperty("w").GetInt32(), h = s.GetProperty("h").GetInt32();
        var t = new EntityTracker { Motion = new CommonMotion { Warp = warp } };
        var a = Up(Convert.FromBase64String(s.GetProperty("prev").GetString()!), w, h, 0); var c = Up(Convert.FromBase64String(s.GetProperty("cur").GetString()!), w, h, 1);
        t.Update(FineGrid.Of(a, t.Stabilizer), a); var e = t.Update(FineGrid.Of(c, t.Stabilizer), c);
        return (e.CommonShift, t.Motion);
    }

    [Theory]
    [InlineData(2, 1)][InlineData(17, 1)][InlineData(30, 1)][InlineData(37, 1)][InlineData(38, 1)]
    [InlineData(19, -1)][InlineData(44, -1)]
    public void Vertical_moves_fit_a_field_of_about_the_scroll_in_the_right_direction(int step, int sign)
    {
        var (_, m) = Run(Sample("shift-samples-153.json", step), true);
        Assert.NotNull(m.LastWarpFit);
        double dy = m.LastWarpFit![4];   // 中央の縦のずれ（縮小の点）
        Assert.Equal(sign, Math.Sign(dy));
        Assert.InRange(Math.Abs(dy), 8, 20);   // 目で見た縦のずれ 約 15 点（上下の端で量が違う）
        Assert.InRange(Math.Abs(m.LastWarpFit[0]), 0, 2);   // 横には動いていない
    }

    [Theory]
    [InlineData("shift-samples-130.json", 5)][InlineData("shift-samples-130.json", 10)][InlineData("shift-samples-130.json", 23)]
    [InlineData("shift-samples-153.json", 5)][InlineData("shift-samples-153.json", 6)][InlineData("shift-samples-153.json", 34)]
    public void Screen_switches_and_texture_aliases_are_not_warps(string file, int step)
    {
        var (shift, m) = Run(Sample(file, step), true);
        Assert.NotEqual("warp", m.LastReason);
        Assert.Null(shift);
    }

    [Theory]
    [InlineData("shift-samples-130.json")][InlineData("shift-samples-149.json")][InlineData("shift-samples-150.json")][InlineData("shift-samples-153.json")]
    public void Translation_results_are_unchanged_by_the_warp(string file)
    {
        foreach (var s in JsonDocument.Parse(File.ReadAllText(Data(file))).RootElement.EnumerateArray())
        {
            var (off, mo) = Run(s, false);
            if (off == null) continue;
            var (on, _) = Run(s, true);
            Assert.Equal(off, on);
            Assert.NotEqual("warp", mo.LastReason);
        }
    }
}
