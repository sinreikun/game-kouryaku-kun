using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.31.3（P-5）: 動いた後に止まった四角が、止まっている間も同じ追跡 ID で存続する</summary>
public class BlockV0313StaticTests
{
    private const int W = 320, H = 240;
    private static long _id;
    private static Frame F(params (int X, int Y)[] sq) => G(2, false, sq);
    private static Frame G(int size, bool alt, params (int X, int Y)[] sq)
    {
        var b = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { b[i * 4] = 40; b[i * 4 + 1] = 40; b[i * 4 + 2] = 40; b[i * 4 + 3] = 255; }
        foreach (var s in sq)
            for (int y = s.Y * 10; y < (s.Y + size) * 10; y++) for (int x = s.X * 10; x < (s.X + size) * 10; x++) { int i = (y * W + x) * 4; bool a = alt && x >= s.X * 10 + size * 5; b[i] = (byte)(a ? 220 : 20); b[i + 1] = 20; b[i + 2] = (byte)(a ? 20 : 220); }
        return new Frame(++_id, W, H, b, 0);
    }

    [Fact]
    public void Stopped_square_persists_with_the_same_track()
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(F((2, 10))));
        int track = -1;
        for (int k = 1; k <= 3; k++) track = Assert.Single(t.Update(FineGrid.Of(F((2 + k, 10)))).Candidates).Track;
        for (int k = 1; k <= 5; k++)
        {
            var l = t.Update(FineGrid.Of(F((5, 10))));
            Assert.Empty(l.Candidates);   // 変わった所からの候補はない（出来事・学習の入力は変えない）
            var s = Assert.Single(l.Static);
            Assert.Equal(track, s.Track); Assert.Equal(k, s.StillSteps); Assert.Equal(5, s.X0);
        }
        var m = t.Update(FineGrid.Of(F((6, 10))));   // また動いたら同じ追跡の候補に戻る
        Assert.Equal(track, Assert.Single(m.Candidates).Track);
        Assert.Empty(m.Static);
    }

    [Fact]
    public void Removed_square_does_not_persist()
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(F((2, 10))));
        t.Update(FineGrid.Of(F((3, 10))));
        t.Update(FineGrid.Of(F()));   // 消えた（区画が変わったので存続しない）
        var l = t.Update(FineGrid.Of(F()));
        Assert.Empty(l.Static);
    }

    [Fact]
    public void One_cell_flicker_is_not_persisted()   // v0.31.4: 実機 #138 で草地に静止の枠
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(G(1, false)));
        t.Update(FineGrid.Of(G(1, false, (7, 7))));   // 1 区画のちらつき
        t.Update(FineGrid.Of(G(1, false, (8, 7))));
        for (int k = 0; k < 3; k++) Assert.Empty(t.Update(FineGrid.Of(G(1, false, (8, 7)))).Static);
    }

    [Fact]
    public void In_place_appearance_cycle_is_marked_periodic_on_the_same_track()   // v0.31.4: 足踏みのような変化
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(G(4, false)));
        t.Update(FineGrid.Of(G(4, false, (10, 10))));
        var cs = new List<EntityCandidate>();
        for (int k = 0; k < 6; k++) cs.AddRange(t.Update(FineGrid.Of(G(4, k % 2 == 0, (10, 10)))).Candidates);
        Assert.Single(cs.Select(c => c.Track).Distinct());
        Assert.True(cs.Count(c => c.PeriodicInPlace) >= 3, string.Join(",", cs.Select(c => $"{c.AppearanceState}{c.PeriodicInPlace}")));
    }
}
