using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.0（P-1）: 重ね表示は構造化した観測だけから作る。意味のラベルを出さない</summary>
public class BlockV0300OverlayTests
{
    private const int W = 320, H = 240;
    private static Frame F(long id, int x)
    {
        var b = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { b[i * 4] = b[i * 4 + 1] = b[i * 4 + 2] = 40; b[i * 4 + 3] = 255; }
        for (int y = 100; y < 120; y++) for (int xx = x * 10; xx < x * 10 + 20; xx++) { int i = (y * W + xx) * 4; b[i + 2] = 220; }
        return new Frame(id, W, H, b, 0);
    }

    private static StructuredObservation Obs(EntityLayer? e)
    {
        var text = new TextLayer(LayerStatus.Observed, "region", "numbers", new[]
        {
            new TextLineObs("35", 10, 10, 30, 12, 0.9, 0, LayerStatus.Observed, "changed"),
            new TextLineObs("abc", 200, 10, 40, 12, 0.8, 5, LayerStatus.Inherited, "same"),
        }, new[] { new NumberObs(0, 0, 31, 35, new double[] { 35, 31 }) }, 1, 1, 0.6);
        var o = new StructuredObservation(1, 7, "agent", "x", "a", "b", new GlobalScene("b", "m1", "1,1", "v2", null, LayerStatus.Observed),
            new RegionLayer(LayerStatus.Observed, 3, new[] { new ChangeRegion(0, 4, 1, 2, 2, 3, 3, "action", 0.1, false) }), text,
            new TemporalLayer(800, true, 0.2, LayerStatus.Observed));
        return o with { EntityLayer = e, Prediction = new PredictionObs(LayerStatus.Observed, new[] { 0.9, 0.1, 0.1, 0.1, 0.5, 0.3 }, new[] { 0 }, new[] { 0.1, 0.1, 0.1, 0.1, 0.7, 0.4 }, new[] { 1, 0 }) { WorldNext = "b", WorldP = 0.8, WorldHit = true } };
    }

    [Fact]
    public void Overlay_is_built_only_from_the_observation_with_geometry_in_frame_pixels()
    {
        var t = new EntityTracker(); t.Update(FineGrid.Of(F(1, 2), t.Stabilizer)); t.Update(FineGrid.Of(F(2, 3), t.Stabilizer));
        var e = t.Update(FineGrid.Of(F(3, 4), t.Stabilizer));
        var m = ObservationOverlay.Build(Obs(e), W, H, new[] { "目的: test" });
        var r = Assert.Single(m.Items.Where(i => i.Kind == "region"));
        Assert.Equal((20.0, 40.0, 40.0, 40.0), (r.X, r.Y, r.W, r.H));   // 区画 (1..2, 2..3) × 20×20 画素
        Assert.Equal("region_action", r.Style);
        var en = Assert.Single(m.Items.Where(i => i.Kind == "entity"));
        Assert.Contains("#", en.Label); Assert.Equal("entity_moved", en.Style);
        Assert.Single(m.Items.Where(i => i.Kind == "move"));
        Assert.Equal(new[] { "text_observed", "text_inherited" }, m.Items.Where(i => i.Kind == "text").Select(i => i.Style));
        Assert.Contains(m.Lines, l => l.Contains("35→31"));
        Assert.Contains(m.Lines, l => l.Contains("World Model") && l.Contains("当たり"));
        Assert.Contains("目的: test", m.Lines);
    }

    [Fact]
    public void Overlay_contains_no_meaning_labels_and_handles_missing_layers()
    {
        var m = ObservationOverlay.Build(Obs(null) with { Prediction = null }, W, H);
        Assert.Empty(m.Items.Where(i => i.Kind is "entity" or "relation" or "move"));
        Assert.Contains(m.Lines, l => l.Contains("not_implemented"));
        var all = string.Join(" ", m.Lines.Concat(m.Items.Select(i => i.Label + " " + i.Style)));
        foreach (var w in new[] { "player", "enemy", "hp", "プレイヤー", "敵", "階段", "アイテム", "HP" }) Assert.DoesNotContain(w, all, StringComparison.OrdinalIgnoreCase);
    }
}
