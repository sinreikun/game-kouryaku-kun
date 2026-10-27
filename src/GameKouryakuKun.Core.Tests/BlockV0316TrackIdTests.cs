using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.31.6: 追跡番号の分断（見た目が変わっても、観測の手がかりで同じ物を続ける）</summary>
public class BlockV0316TrackIdTests
{
    private const int W = 320, H = 240;   // 細かい区画 = 10×10 画素
    private static long _id;
    private static readonly (byte, byte, byte) Red = (20, 20, 220), Blue = (220, 60, 20), Green = (30, 200, 30);
    /// <summary>四角（x,y,大きさは細かい区画）。alt なら右半分の色を入れ替える（見た目の別の状態）</summary>
    private static Frame F(params (int X, int Y, int S, (byte B, byte G, byte R) C, bool Alt)[] sq)
    {
        var b = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { b[i * 4] = 40; b[i * 4 + 1] = 40; b[i * 4 + 2] = 40; b[i * 4 + 3] = 255; }
        foreach (var s in sq)
            for (int y = s.Y * 10; y < (s.Y + s.S) * 10; y++) for (int x = s.X * 10; x < (s.X + s.S) * 10; x++)
            {
                int i = (y * W + x) * 4; bool a = s.Alt && x >= s.X * 10 + s.S * 5;
                b[i] = a ? s.C.R : s.C.B; b[i + 1] = s.C.G; b[i + 2] = a ? s.C.B : s.C.R;
            }
        return new Frame(++_id, W, H, b, 0);
    }
    private static EntityCandidate Near(EntityLayer l, double x, double y) => l.Candidates.OrderBy(c => Math.Abs(c.Cx - x) + Math.Abs(c.Cy - y)).First();

    [Fact]
    public void Appearance_cycling_moving_object_keeps_one_id()
    {
        var t = new EntityTracker(); t.Update(FineGrid.Of(F()));
        var ids = new List<int>();
        for (int k = 0; k < 12; k++)
        {
            // 2 区画ずつ右へ（前の枠と重ならない）。見た目は毎手入れ替わる（足踏みのような変化）
            var l = t.Update(FineGrid.Of(F((2 + 2 * k, 10, 3, Red, k % 2 == 1))));
            ids.Add(Near(l, 3 + 2 * k, 11).Track);
        }
        Assert.True(ids.Skip(1).Distinct().Count() == 1, string.Join(",", ids));
    }

    [Fact]
    public void Two_crossing_objects_do_not_swap()
    {
        var t = new EntityTracker(); t.Update(FineGrid.Of(F()));
        var a = new List<int>(); var b = new List<int>();
        for (int k = 0; k < 12; k++)
        {
            int ax = 4 + 2 * k, bx = 26 - 2 * k;   // 赤は右へ、青は左へ。縦に 3 区画ずらしてすれ違う
            var l = t.Update(FineGrid.Of(F((ax, 8, 2, Red, false), (bx, 11, 2, Blue, false))));
            a.Add(Near(l, ax + 0.5, 8.5).Track); b.Add(Near(l, bx + 0.5, 11.5).Track);
        }
        Assert.Single(a.Skip(1).Distinct()); Assert.Single(b.Skip(1).Distinct());
        Assert.NotEqual(a[^1], b[^1]);
    }

    [Fact]
    public void Disappear_and_reappear_nearby_is_reidentified_and_unrelated_object_is_not_merged()
    {
        var t = new EntityTracker(); t.Update(FineGrid.Of(F()));
        int id = -1;
        for (int k = 0; k < 8; k++) id = Assert.Single(t.Update(FineGrid.Of(F((4 + k, 10, 3, Red, false)))).Candidates).Track;
        t.Update(FineGrid.Of(F()));   // 消えた
        // 別の色・別の形の物が遠くに出る（無関係）
        var u = Assert.Single(t.Update(FineGrid.Of(F((25, 2, 2, Green, false)))).Candidates);
        Assert.NotEqual(id, u.Track);
        // 近く（進んでいた向きの先）に同じ物が戻る
        var l = t.Update(FineGrid.Of(F((25, 2, 2, Green, false), (13, 10, 3, Red, false))));
        Assert.Equal(id, Near(l, 14, 11).Track);
        Assert.True(t.Reidentified >= 1);
    }

    private static byte Tex(int x, int y) { unchecked { uint h = (uint)(Math.Floor(x / 20.0) * 73856093) ^ (uint)(Math.Floor(y / 20.0) * 19349663); h ^= h >> 13; h *= 0x5bd1e995; return (byte)(40 + (h >> 8) % 160); } }

    [Fact]
    public void Screen_fixed_object_over_scrolling_background_keeps_one_id_while_its_appearance_cycles()
    {
        // 背景が毎手 20 画素ずれる（視点が付いていく）。物は画面の真ん中に固定で、見た目が毎手入れ替わる
        var t = new EntityTracker(); var ids = new List<int>();
        for (int s = 0; s < 10; s++)
        {
            var b = new byte[W * H * 4];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; byte v = Tex(x + s * 20, y); b[i] = b[i + 1] = b[i + 2] = v; b[i + 3] = 255; }
            for (int y = 100; y < 140; y++) for (int x = 140; x < 170; x++) { int i = (y * W + x) * 4; bool a = s % 2 == 1 && x >= 155; b[i] = (byte)(a ? 255 : 0); b[i + 1] = 0; b[i + 2] = (byte)(a ? 0 : 255); }
            var f = new Frame(++_id, W, H, b, 0);
            var l = t.Update(FineGrid.Of(f, t.Stabilizer), f);
            if (s >= 2 && l.Candidates.Count > 0) ids.Add(Near(l, 15, 12).Track);
        }
        Assert.True(ids.Count >= 5, $"{ids.Count}");
        Assert.True(ids.Distinct().Count() == 1, string.Join(",", ids));
    }
}
