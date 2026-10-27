using System.Runtime.CompilerServices;
using System.Text.Json;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.7: 実機 #130 の縮小画像（明るさ 128×96、shift_sample）で共通のずれの判定を確かめる。手の名前は正解の確認にだけ使う（判定には入れない）</summary>
public class BlockV0307RealShiftTests
{
    private static string DataPath([CallerFilePath] string f = "") => Path.Combine(Path.GetDirectoryName(f)!, "Data", "shift-samples-130.json");

    private static Frame Up(byte[] g, int w, int h, long id, int k = 8)
    {
        int W = w * k, H = h * k; var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; var v = g[(y / k) * w + x / k]; b[i] = b[i + 1] = b[i + 2] = v; b[i + 3] = 255; }
        return new Frame(id, W, H, b, 0);
    }

    [Fact]
    public void Real_direction_moves_are_common_shifts_in_the_right_direction_and_transitions_are_not()
    {
        foreach (var s in JsonDocument.Parse(File.ReadAllText(DataPath())).RootElement.EnumerateArray())
        {
            int w = s.GetProperty("w").GetInt32(), h = s.GetProperty("h").GetInt32(); string act = s.GetProperty("action").GetString()!;
            var t = new EntityTracker();
            var a = Up(Convert.FromBase64String(s.GetProperty("prev").GetString()!), w, h, 0); var c = Up(Convert.FromBase64String(s.GetProperty("cur").GetString()!), w, h, 1);
            t.Update(FineGrid.Of(a, t.Stabilizer), a); var e = t.Update(FineGrid.Of(c, t.Stabilizer), c);
            string tag = $"{s.GetProperty("step")} {act} {string.Join(",", e.ShiftProbe ?? Array.Empty<double>())}";
            if (act is "dpad_left" or "dpad_right" or "dpad_up" or "dpad_down" && s.GetProperty("step").GetInt32() != 23)
            {
                Assert.True(e.CommonShift != null, tag);
                var (dx, dy) = (e.CommonShift![0], e.CommonShift[1]);
                switch (act)
                {
                    case "dpad_left": Assert.True(dx > 1.5 && Math.Abs(dy) < 0.5, tag); break;
                    case "dpad_right": Assert.True(dx < -1.5 && Math.Abs(dy) < 0.5, tag); break;
                    case "dpad_up": Assert.True(dy > 1.5 && Math.Abs(dx) < 0.5, tag); break;
                    case "dpad_down": Assert.True(dy < -1.5 && Math.Abs(dx) < 0.5, tag); break;
                }
            }
            else Assert.True(e.CommonShift == null, tag);   // 画面の切り替わり・暗転（手 23 は画面が切り替わった手）
        }
    }
}
