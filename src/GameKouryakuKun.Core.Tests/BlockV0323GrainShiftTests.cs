using System.Runtime.CompilerServices;
using System.Text.Json;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// v0.32.3: 実機 #149（暗く、粒・細かい模様の多い画面）の縮小画像で共通のずれを確かめる。手の名前は正解の確認にだけ使う（判定には入れない）。
/// 縮小画像からは移動が見つかる（実機では 25 手中 17 手が説明できなかった）＝原因は細かい明るさ（512×384）の側。判定の理由を記録する
/// </summary>
public class BlockV0323GrainShiftTests
{
    private static string DataPath([CallerFilePath] string f = "") => Path.Combine(Path.GetDirectoryName(f)!, "Data", "shift-samples-149.json");
    private static Frame Up(byte[] g, int w, int h, long id, int k = 8)
    {
        int W = w * k, H = h * k; var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; var v = g[(y / k) * w + x / k]; b[i] = b[i + 1] = b[i + 2] = v; b[i + 3] = 255; }
        return new Frame(id, W, H, b, 0);
    }

    [Fact]
    public void Plain_direction_moves_in_the_dark_grainy_scene_are_common_shifts_with_a_reason()
    {
        int moves = 0, found = 0;
        foreach (var s in JsonDocument.Parse(File.ReadAllText(DataPath())).RootElement.EnumerateArray())
        {
            int w = s.GetProperty("w").GetInt32(), h = s.GetProperty("h").GetInt32(); string act = s.GetProperty("action").GetString()!;
            var t = new EntityTracker();
            var a = Up(Convert.FromBase64String(s.GetProperty("prev").GetString()!), w, h, 0); var c = Up(Convert.FromBase64String(s.GetProperty("cur").GetString()!), w, h, 1);
            t.Update(FineGrid.Of(a, t.Stabilizer), a); var e = t.Update(FineGrid.Of(c, t.Stabilizer), c);
            Assert.Contains(t.Motion.LastReason, new[] { "shifted", "floor", "improved", "small", "no_offset" });
            Assert.Equal(e.CommonShift != null, t.Motion.LastReason == "shifted");
            if (t.Motion.LastHiCrop is { } hc) Assert.Equal(CommonMotion.Sx * CommonMotion.Sy, hc.Cur.Length);
            if (!act.StartsWith("dpad_")) continue;
            moves++;
            if (e.CommonShift == null) continue;
            found++;
            double dx = e.CommonShift[0], dy = e.CommonShift[1];
            if (act.Contains("left")) Assert.True(dx > 1.5); if (act.Contains("right")) Assert.True(dx < -1.5);
            if (act.Contains("up")) Assert.True(dy > 1.5); if (act == "dpad_right" || act == "dpad_left") Assert.True(Math.Abs(dy) < 0.5);
        }
        Assert.True(moves >= 10 && found >= moves - 1, $"{found}/{moves}");
    }
}
