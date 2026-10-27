using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Core.Ocr;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.29.0 Observation Layer ブロック 1〜4 の合成の正解テスト（意味のラベルは攻略くんに渡さない）</summary>
public class BlockV0290ObservationTests
{
    private const int C = 16;
    private static int Cell(int x, int y) => y * C + x;

    [Fact]
    public void Small_pattern_moving_keeps_one_track_and_appearing_pattern_gets_new_track()
    {
        var b = new ObservationBuilder();
        var r1 = b.Regions(new[] { Cell(3, 3), Cell(4, 3) });
        var r2 = b.Regions(new[] { Cell(4, 3), Cell(5, 3) });            // 1 区画右へ（重なる）
        var r3 = b.Regions(new[] { Cell(5, 3), Cell(6, 3), Cell(12, 9) }); // さらに右へ・離れた所に出現
        Assert.Single(r1); Assert.Single(r2); Assert.Equal(2, r3.Count);
        Assert.Equal(r1[0].Track, r2[0].Track);
        Assert.Equal(r1[0].Track, r3.First(r => r.X0 == 5).Track);
        Assert.True(r3.First(r => r.X0 == 12).NewTrack);
        var r4 = b.Regions(Array.Empty<int>());                           // 消失
        Assert.Empty(r4);
        var r5 = b.Regions(new[] { Cell(6, 3) });
        Assert.True(r5[0].NewTrack);                                     // 消えた後は同じ ID にしない（推測で埋めない）
    }

    [Fact]
    public void Region_causality_label_comes_only_from_experience_classes()
    {
        var b = new ObservationBuilder();
        var r = b.Regions(new[] { Cell(0, 0), Cell(1, 0), Cell(8, 8) }, i => i == Cell(8, 8) ? ("independent", 0.9) : ("action", 0.05));
        Assert.Equal("action", r.First(x => x.X0 == 0).Causality);
        Assert.Equal("independent", r.First(x => x.X0 == 8).Causality);
        Assert.Equal(0.9, r.First(x => x.X0 == 8).NaturalRate, 6);
    }

    private static TextSnapshot Snap(params (string t, int x, int y)[] l) =>
        new(1, 800, 600, l.Select(z => new OcrLine(z.t, 0.9, new OcrBox(z.x, z.y, 40, 10))).ToList(), Enumerable.Range(1, 192).Select(i => (ulong)i).ToArray());

    [Fact]
    public void Numbers_are_kept_as_series_and_every_step_has_text_state()
    {
        var b = new ObservationBuilder(); TextSnapshot? prev = null; TextLayer? last = null;
        foreach (var v in new[] { 35, 31, 27 })
        {
            var s = Snap(($"x {v}", 10, 10));
            last = b.Text(prev, s, prev == null ? "full" : "region", new OcrBox(0, 0, 100, 40), prev == null ? null : TextDiff.Of(prev, s), 0);
            Assert.NotEqual(LayerStatus.NotObserved, last.Status);
            prev = s;
        }
        Assert.Equal(new double[] { 35, 31, 27 }, last!.Numbers.Single().Series);
        Assert.Equal(31, last.Numbers.Single().Previous);
        Assert.Equal("changed", last.Lines.Single().Change);
    }

    [Fact]
    public void Unchanged_pixels_mean_inherited_text_not_missing_text_and_flash_text_is_recorded()
    {
        var b = new ObservationBuilder();
        var a = Snap(("menu", 10, 10)); var flash = Snap(("menu", 10, 10), ("!!", 500, 400));
        b.Text(null, a, "full", null, null, 0);
        var t1 = b.Text(a, flash, "region", new OcrBox(480, 380, 100, 60), TextDiff.Of(a, flash), 0.7);
        Assert.Equal(1, t1.Observed); Assert.Equal(1, t1.Inherited);
        Assert.Equal("new", t1.Lines.Single(l => l.Text == "!!").Change);
        Assert.Equal(LayerStatus.Inherited, t1.Lines.Single(l => l.Text == "menu").Status);
        var t2 = b.Text(flash, flash, "reuse", null, TextDiff.Of(flash, flash), 0);
        Assert.Equal(LayerStatus.Inherited, t2.Status); Assert.Equal(2, t2.Inherited);
        var t3 = b.Text(flash, null, "failed", null, null, 0);
        Assert.Equal(LayerStatus.NotObserved, t3.Status);            // 失敗は継承と区別する
    }

    [Fact]
    public void Same_global_scene_with_different_local_state_stays_distinct_and_unbuilt_layers_are_explicit()
    {
        var b = new ObservationBuilder();
        var g = new GlobalScene("m1|0", "m1", "0", "v3", null, LayerStatus.Observed);
        var o1 = b.Build(1, "agent", "k", "m1|0", "m1|0", g, new RegionLayer(LayerStatus.Observed, 1, b.Regions(new[] { Cell(2, 2) })), b.Text(null, Snap(("a", 10, 10)), "full", null, null, 0), new TemporalLayer(100, true, null, LayerStatus.Observed));
        var o2 = b.Build(2, "human", "k", "m1|0", "m1|0", g, new RegionLayer(LayerStatus.Observed, 1, b.Regions(new[] { Cell(9, 9) })), b.Text(null, Snap(("b", 10, 10)), "full", null, null, 0), new TemporalLayer(100, true, null, LayerStatus.Observed));
        Assert.Equal(o1.Global, o2.Global);
        Assert.NotEqual(o1.ToJson(), o2.ToJson());
        Assert.Contains("\"entities\":\"not_implemented\"", o1.ToJson());
        Assert.Contains("\"schema\":1", o1.ToJson());
        Assert.Equal("human", o2.Provenance);
    }

    [Fact]
    public void Db_v17_stores_structured_observation_with_provenance_and_legacy_has_none()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.29.0");
        Assert.Equal(17, s.Db.SchemaVersion);
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        Assert.Equal(0, s.CountObservations(run));                    // 旧経験には行がない（推測で埋めない）
        s.AddObservation(run, 1, 1, StructuredObservation.CurrentSchema, "agent", "{}");
        Assert.Equal(1, s.CountObservations(run));
        Assert.ThrowsAny<Exception>(() => s.AddObservation(run, 2, 2, 1, "teacher", "{}"));   // 出どころは決まった値だけ
    }
}
