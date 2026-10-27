using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.29.1: 合成の正解つきの画面（意味のラベルは AI に渡さない。正解は試験の側だけが知る）</summary>
public class BlockV0291EntityTests
{
    private const int W = 320, H = 240;   // 細かい区画 = 10×10 画素
    private static long _id;
    /// <summary>背景の上に四角を置く（x,y は細かい区画、色は bgr）</summary>
    private static Frame F(params (int X, int Y, int S, (byte B, byte G, byte R) C, bool Split)[] sq)
    {
        var b = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { b[i * 4] = 40; b[i * 4 + 1] = 40; b[i * 4 + 2] = 40; b[i * 4 + 3] = 255; }
        foreach (var s in sq)
            for (int y = s.Y * 10; y < (s.Y + s.S) * 10; y++) for (int x = s.X * 10; x < (s.X + s.S) * 10; x++)
            {
                int i = (y * W + x) * 4; bool alt = s.Split && x >= (s.X * 10 + s.S * 5);   // 見た目の別の状態: 右半分の色を変える（向きの違いのような変化）
                b[i] = alt ? s.C.R : s.C.B; b[i + 1] = s.C.G; b[i + 2] = alt ? s.C.B : s.C.R;
            }
        return new Frame(++_id, W, H, b, 0);
    }
    private static readonly (byte, byte, byte) Red = (20, 20, 220), Blue = (220, 60, 20);

    [Fact]
    public void Moving_pattern_keeps_its_track_and_a_second_pattern_gets_another()
    {
        var t = new EntityTracker();
        Assert.Equal(LayerStatus.NotObserved, t.Update(FineGrid.Of(F((2, 2, 2, Red, false)))).Status);
        var l1 = t.Update(FineGrid.Of(F((5, 2, 2, Red, false), (20, 15, 2, Blue, false))));
        Assert.Equal(2, l1.Candidates.Count);   // 抜けた跡は候補にしない: 移った赤と現れた青
        int track = -1;
        for (int k = 0; k < 6; k++)
        {
            // 赤が右に 3 区画ずつ（前の位置と離れているので 2 つの塊。現れた側の追跡を確かめる）、青は動かない
            var l = t.Update(FineGrid.Of(F((8 + 3 * k, 2, 2, Red, false), (20, 15, 2, Blue, false))));
            var head = l.Candidates.OrderByDescending(c => c.Cx).First();
            if (k > 0) Assert.True(head.Track == track || l.Candidates.Any(c => c.Track == track));
            track = head.Track;
        }
    }

    [Fact]
    public void Adjacent_step_move_is_one_moving_candidate_with_displacement_and_same_track()
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(F((2, 10, 2, Red, false))));
        var tracks = new List<int>(); var dxs = new List<double>();
        for (int k = 1; k <= 5; k++)
        {
            var l = t.Update(FineGrid.Of(F((2 + k, 10, 2, Red, false))));   // 1 区画ずつ: 旧と新が隣り合い 1 つの塊
            var c = Assert.Single(l.Candidates); tracks.Add(c.Track); dxs.Add(c.Dx);
        }
        Assert.Single(tracks.Distinct());
        Assert.All(dxs.Skip(1), d => Assert.Equal(1.0, d, 3));
    }

    [Fact]
    public void Appearance_only_change_gets_distinct_states_and_returns_to_the_first()
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(F((10, 10, 4, Red, false))));
        var a = Assert.Single(t.Update(FineGrid.Of(F((10, 10, 4, Red, true)))).Candidates);    // 位置はそのまま、見た目だけ
        var b = Assert.Single(t.Update(FineGrid.Of(F((10, 10, 4, Red, false)))).Candidates);
        var c = Assert.Single(t.Update(FineGrid.Of(F((10, 10, 4, Red, true)))).Candidates);
        Assert.Equal(a.Track, b.Track); Assert.Equal(b.Track, c.Track);
        Assert.False(b.Moved); Assert.True(b.AppearanceChanged);
        Assert.Equal(a.AppearanceState, c.AppearanceState); Assert.NotEqual(a.AppearanceState, b.AppearanceState);
        Assert.Equal(2, t.AppearanceOnly);
    }

    [Fact]
    public void Disappearing_pattern_is_reported_and_reidentified_when_it_returns_unchanged()
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(F()));
        var l1 = t.Update(FineGrid.Of(F((5, 5, 2, Blue, false))));
        int id = Assert.Single(l1.Candidates).Track;
        var l2 = t.Update(FineGrid.Of(F()));   // 消える（変化した区画は同じ所。対応がつけば同じ追跡のまま見た目が変わる）
        var l3 = t.Update(FineGrid.Of(F((20, 5, 2, Red, false))));   // 別の場所に別のもの
        Assert.DoesNotContain(l3.Candidates, c => c.Track == id && !c.New && c.Cx < 10);
        Assert.Contains(id, l3.Disappeared.Concat(l2.Disappeared).Concat(l3.Candidates.Select(c => c.Track)));
    }

    [Fact]
    public void Relations_report_approach_and_same_move_without_labels()
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(F((2, 2, 2, Red, false), (2, 20, 2, Blue, false))));
        t.Update(FineGrid.Of(F((3, 2, 2, Red, false), (3, 20, 2, Blue, false))));
        var l = t.Update(FineGrid.Of(F((4, 3, 2, Red, false), (4, 19, 2, Blue, false))));   // 両方右に 1、縦に近づく
        var r = Assert.Single(l.Relations);
        Assert.Equal("approach", r.Change);
        Assert.False(r.SameMove);   // 縦の向きが逆
        var l2 = t.Update(FineGrid.Of(F((5, 3, 2, Red, false), (5, 19, 2, Blue, false))));
        var r2 = Assert.Single(l2.Relations);
        Assert.True(r2.SameMove); Assert.Equal("same", r2.Change);
    }

    private static readonly TextLayer NoText = new(LayerStatus.NotObserved, "none", "unknown", Array.Empty<TextLineObs>(), Array.Empty<NumberObs>(), 0, 0, 0);

    [Fact]
    public void Natural_motion_is_separated_from_action_caused_motion_and_predictions_learn_displacement()
    {
        // 正解: "x" の手で四角が右に 1 動く。入力なし（待つ）では別の場所が毎回点滅する（入力によらない）。"y"・"z" の手では何も動かない
        var m = new LocalEventModel(); var t = new EntityTracker();
        int pos = 2; bool blink = false;
        Frame Make() => blink ? F((pos % 28, 10, 2, Red, false), (25, 2, 2, Blue, false)) : F((pos % 28, 10, 2, Red, false));
        t.Update(FineGrid.Of(Make()));
        var rnd = new Random(7);
        for (int i = 0; i < 400; i++)
        {
            string a = (i % 4) switch { 0 => "x", 1 => "y", 2 => "z", _ => "wait" };
            if (a == "x") { if (pos % 28 == 26) pos += 2; pos++; }
            blink = !blink;   // 点滅は手によらず毎手
            var l = t.Update(FineGrid.Of(Make()));
            m.Observe("s", a, a == "wait", LocalEventModel.EventsOf(l, NoText), l);
        }
        // 移動は "x" だけ: 手に依存
        Assert.Equal("action", m.Causality.Class("s", 0));
        var px = m.Predict("s", "x")!.Value; var py = m.Predict("s", "y")!.Value;
        Assert.True(px.P[0] > 0.9 && py.P[0] < 0.1);
        Assert.Equal((1, 0), px.Move);
        Assert.True(m.MoveHits >= m.MoveChecks * 0.9);
        // 点滅（出現・消失）は手によらない
        Assert.NotEqual("action", m.Causality.Class("s", 2));
        Assert.DoesNotContain(m.PersistentErrors(6), e => e.Event == "entity_moved");
    }

    [Fact]
    public void Hidden_condition_shows_as_persistent_prediction_error()
    {
        // 正解: "x" の手で動くかどうかが画面に出ない隠れた条件（交互）で決まる → 画面×手では予測できない
        var m = new LocalEventModel(); var t = new EntityTracker(); int pos = 2;
        t.Update(FineGrid.Of(F((pos, 10, 2, Red, false))));
        var rnd = new Random(3);
        for (int i = 0; i < 60; i++)
        {
            if (rnd.Next(2) == 0) pos = pos >= 25 ? 2 : pos + 1;
            var l = t.Update(FineGrid.Of(F((pos, 10, 2, Red, false))));
            m.Observe("s", "x", false, LocalEventModel.EventsOf(l, NoText), l);
        }
        Assert.Contains(m.PersistentErrors(6), e => e.Event == "entity_moved" && e.Action == "x");
    }

    [Fact]
    public void Structured_observation_json_carries_entity_layer_and_stays_schema_1()
    {
        var b = new ObservationBuilder();
        var o = b.Build(1, "agent", "x", "a", "b", new GlobalScene("b", "m", null, null, null, LayerStatus.Observed), new RegionLayer(LayerStatus.Observed, 0, Array.Empty<ChangeRegion>()), NoText,
            new TemporalLayer(0, false, null, LayerStatus.Observed));
        Assert.Contains("\"entities\":\"not_implemented\"", o.ToJson());
        var t = new EntityTracker(); t.Update(FineGrid.Of(F())); var e = t.Update(FineGrid.Of(F((3, 3, 2, Red, false))));
        var o2 = o with { EntityLayer = e };
        var j = o2.ToJson();
        Assert.Contains("\"entities\":\"observed\"", j); Assert.Contains("\"appearance_state\":\"A\"", j); Assert.Equal(1, o2.Schema);
    }
}
