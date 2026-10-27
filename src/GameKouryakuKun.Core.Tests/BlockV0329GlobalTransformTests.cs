using System.Runtime.CompilerServices;
using System.Text.Json;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// v0.32.9 (t108u): 「画面全体の 1 つの変換か」を仮説の比べ（混合モデル・BIC）で決める。実機の縮小画像（#130/#149/#150/#153）を再生して確かめる
/// </summary>
public class BlockV0329GlobalTransformTests
{
    private static string Data(string name, [CallerFilePath] string f = "") => Path.Combine(Path.GetDirectoryName(f)!, "Data", name);
    private static IEnumerable<JsonElement> All(string file) => JsonDocument.Parse(File.ReadAllText(Data(file))).RootElement.EnumerateArray().ToList();
    private static JsonElement Sample(string file, int step) => All(file).First(s => s.GetProperty("step").GetInt32() == step);
    private static Frame Up(byte[] g, int w, int h, long id, int k = 8)
    {
        int W = w * k, H = h * k; var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; var v = g[(y / k) * w + x / k]; b[i] = b[i + 1] = b[i + 2] = v; b[i + 3] = 255; }
        return new Frame(id, W, H, b, 0);
    }
    private static (EntityLayer E, CommonMotion M) Run(JsonElement s, bool warp = true)
    {
        int w = s.GetProperty("w").GetInt32(), h = s.GetProperty("h").GetInt32();
        var t = new EntityTracker { Motion = new CommonMotion { Warp = warp } };
        var a = Up(Convert.FromBase64String(s.GetProperty("prev").GetString()!), w, h, 0); var c = Up(Convert.FromBase64String(s.GetProperty("cur").GetString()!), w, h, 1);
        t.Update(FineGrid.Of(a, t.Stabilizer), a); var e = t.Update(FineGrid.Of(c, t.Stabilizer), c);
        return (e, t.Motion);
    }

    [Theory]
    [InlineData(2, 1)][InlineData(3, 1)][InlineData(17, 1)][InlineData(30, 1)][InlineData(37, 1)][InlineData(38, 1)][InlineData(39, 1)][InlineData(40, 1)]
    [InlineData(19, -1)][InlineData(44, -1)]
    public void Real_vertical_moves_are_a_global_transform_in_the_right_direction(int step, int sign)
    {
        var (e, m) = Run(Sample("shift-samples-153.json", step));
        Assert.NotNull(e.CommonShift);
        Assert.Equal(sign, Math.Sign(e.CommonShift![1]));
        Assert.InRange(Math.Abs(e.CommonShift[1]) * 4, 12, 18);   // 縮小の点で約 15（目で見た縦のずれ）
        Assert.InRange(Math.Abs(e.CommonShift[0]) * 4, 0, 1);
        var g = Assert.IsType<GlobalTransformConfidence>(e.GlobalTransform);
        Assert.True(g.Accepted);
        Assert.Contains(g.Decision, new[] { "field", "translation" });
        Assert.True(g.Coverage >= 0.5 && g.LogOdds > 0 && g.ErrorReduction > 0, g.Note);
        Assert.NotNull(e.ResidualCells);   // 補正しても説明できない局所の変化は残す（消さない）
    }

    [Theory]
    [InlineData(2)][InlineData(17)][InlineData(30)]
    public void Field_is_row_dependent_for_the_perspective_view(int step)
    {
        var (e, m) = Run(Sample("shift-samples-153.json", step));
        Assert.Equal("field", e.GlobalTransform!.Decision);
        Assert.True(e.GlobalTransform.Field[6] > 0, e.GlobalTransform.Note);   // 下の行ほど大きく下へ動く
    }

    [Theory]
    [InlineData("shift-samples-130.json", 5)][InlineData("shift-samples-130.json", 10)][InlineData("shift-samples-130.json", 23)]
    [InlineData("shift-samples-150.json", 3)][InlineData("shift-samples-150.json", 4)]
    [InlineData("shift-samples-153.json", 5)][InlineData("shift-samples-153.json", 6)][InlineData("shift-samples-153.json", 34)]
    [InlineData("shift-samples-149.json", 138)][InlineData("shift-samples-149.json", 258)]
    public void Switches_and_aliases_are_not_global_transforms(string file, int step)
    {
        var (e, m) = Run(Sample(file, step));
        Assert.Null(e.CommonShift);
        Assert.False(e.GlobalTransform?.Accepted ?? false);
    }

    [Theory]
    [InlineData("shift-samples-130.json")][InlineData("shift-samples-149.json")][InlineData("shift-samples-150.json")][InlineData("shift-samples-153.json")]
    public void Existing_translation_decisions_are_unchanged(string file)
    {
        foreach (var s in All(file))
        {
            var (off, mo) = Run(s, warp: false);
            if (off.CommonShift == null) continue;
            var (on, mn) = Run(s);
            Assert.Equal(off.CommonShift, on.CommonShift);
            Assert.Equal("translation_check", on.GlobalTransform!.Method);
        }
    }

    [Fact]
    public void Global_transform_is_written_to_the_structured_observation()
    {
        var (e, _) = Run(Sample("shift-samples-153.json", 2));
        var json = JsonSerializer.Serialize(e, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.Contains("\"global_transform\":{", json);
        Assert.Contains("\"log_odds\"", json);
        Assert.Contains("\"coverage\"", json);
    }
}
