using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.3: 明るさの差の小さい模様のずれも共通のずれとする。足踏みのような周期的な見た目の変化は同じ追跡の見た目の状態の変化</summary>
public class BlockV0303PeriodicTests
{
    private const int W = 320, H = 240;
    private static byte Tex(int x, int y, int amp) { unchecked { uint h = (uint)(Math.Floor(x / 10.0) * 73856093) ^ (uint)(Math.Floor(y / 10.0) * 19349663); h ^= h >> 13; h *= 0x5bd1e995; return (byte)(90 + (h >> 8) % (uint)amp); } }

    private static Frame Scene(long id, int ox, int amp, int phase = -1)
    {
        var b = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; byte v = amp == 0 ? (byte)90 : Tex(x + ox, y, amp); b[i] = v; b[i + 1] = (byte)Math.Min(255, v + 40); b[i + 2] = v; b[i + 3] = 255; }
        if (phase >= 0)   // 画面の中央の 30×40 の物。phase で下半分（足）の形が変わる
            for (int y = 100; y < 140; y++) for (int x = 145; x < 175; x++)
            {
                bool body = y < 120 || (phase == 0 ? (x < 155 || x >= 165) : (x >= 150 && x < 170));
                if (!body) continue; int i = (y * W + x) * 4; b[i] = 200; b[i + 1] = 40; b[i + 2] = 40;
            }
        return new Frame(id, W, H, b, 0);
    }

    [Fact]
    public void Low_contrast_texture_shift_is_common_motion()
    {
        var t = new EntityTracker(); EntityLayer? e = null; int shifted = 0;
        for (int s = 0; s < 6; s++) { var f = Scene(s, s * 20, 40); e = t.Update(FineGrid.Of(f, t.Stabilizer), f); if (e.CommonShift != null) shifted++; }
        Assert.True(shifted >= 4, $"ずれ {shifted}・調べた {t.Motion.Checked}・説明できない {t.Motion.Unexplained}");
        Assert.True(t.Created <= 3, $"新しい追跡 {t.Created}");
    }

    [Fact]
    public void Idle_animation_keeps_one_track_and_cycles_appearance_states()
    {
        var t = new EntityTracker(); var tracks = new HashSet<int>(); var states = new List<string>();
        t.Update(FineGrid.Of(Scene(0, 0, 0), t.Stabilizer));
        for (int s = 1; s <= 12; s++)
        {
            var e = t.Update(FineGrid.Of(Scene(s, 0, 0, s % 2), t.Stabilizer));
            foreach (var c in e.Candidates) { tracks.Add(c.Track); if (s > 1) states.Add(c.AppearanceState); }
        }
        Assert.True(tracks.Count == 1, $"追跡 {string.Join(",", tracks)}");
        // 2 つの見た目の状態を行き来する（新しい状態を作り続けない）
        Assert.Equal(2, states.Distinct().Count());
        Assert.True(t.PeriodicReturns + t.Continued >= 10);
    }
}
