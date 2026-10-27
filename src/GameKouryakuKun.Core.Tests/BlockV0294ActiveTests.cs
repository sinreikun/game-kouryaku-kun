using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.29.4（ブロック 13）: 局所の予測の不確かさを手の選び方の不確かさに入れる。正解は試験の側だけ</summary>
public class BlockV0294ActiveTests
{
    private static readonly EntityLayer Empty = new(LayerStatus.Observed, Array.Empty<EntityCandidate>(), Array.Empty<int>(), Array.Empty<RelationObs>());

    [Fact]
    public void Untried_has_no_local_value_and_value_decays_with_tries_for_a_deterministic_outcome()
    {
        var m = new LocalEventModel();
        Assert.Null(m.InformationGain("s", "x", 6));
        var g = new List<double>();
        for (int i = 0; i < 40; i++) { m.Observe("s", "x", false, new[] { 4 }, Empty); g.Add(m.InformationGain("s", "x", 6)!.Value); }   // 毎回文字が変わる（決まった結果）
        Assert.True(g[0] > g[9] && g[9] > g[39]);
        Assert.True(g[39] < 0.15);
    }

    [Fact]
    public void Unlearnable_random_outcome_is_excluded_once_errors_persist()
    {
        // 正解: 出来事 0 が画面に出ない条件で毎回ランダムに起きる（この画面の見分けでは学べない）
        var m = new LocalEventModel(); var r = new Random(5);
        for (int i = 0; i < 80; i++) m.Observe("s", "x", false, r.Next(2) == 0 ? new[] { 0 } : Array.Empty<int>(), Empty);
        var d = new LocalEventModel();
        for (int i = 0; i < 80; i++) d.Observe("s", "x", false, i % 2 == 0 ? new[] { 0 } : Array.Empty<int>(), Empty);   // 交互も画面×手だけでは予測できない
        Assert.Contains(m.PersistentErrors(6), e => e.Event == "entity_moved");
        Assert.True(m.InformationGain("s", "x", 6) < 0.1);
        Assert.True(m.ExcludedPersistent > 0);
    }
}

public class BlockV0295WorldModelTests
{
    [Fact]
    public void Coarse_state_candidate_only_when_world_model_hits_but_local_keeps_missing()
    {
        // 正解: "x" は次の画面がいつも同じ（当たる）が、局所の出来事 0 は隠れた条件で起きたり起きなかったり。"y" は次の画面も外れる
        var c = new CoarseStateEvidence(); var r = new Random(2);
        for (int i = 0; i < 30; i++)
        {
            c.Observe("s", "x", true, new[] { r.Next(2) == 0 ? 0.9 : 0.6, 0.05, 0.05, 0.05, 0.05, 0.05 });
            c.Observe("s", "y", false, new[] { 0.9, 0.05, 0.05, 0.05, 0.05, 0.05 });
            c.Observe("s", "z", true, new[] { 0.05, 0.05, 0.05, 0.05, 0.05, 0.05 });
        }
        var k = Assert.Single(c.Candidates(6));
        Assert.Equal(("s", "x", "entity_moved"), (k.Node, k.Action, k.Event));
        Assert.Equal(90, c.Joint);
    }

    [Fact]
    public void Prediction_layer_json_carries_world_and_local_predictions()
    {
        var p = new PredictionObs(LayerStatus.Observed, new[] { 0.9, 0.1, 0.1, 0.1, 0.5, 0.1 }, new[] { 0 }, new[] { 0.1, 0.1, 0.1, 0.1, 0.7, 0.1 }, new[] { 1, 0 }) { WorldNext = "m2", WorldP = 0.8, WorldHit = true };
        var j = System.Text.Json.JsonSerializer.Serialize(p, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        Assert.Contains("\"world_next\":\"m2\"", j); Assert.Contains("\"world_hit\":true", j); Assert.Contains("\"predicted_move\":[1,0]", j);
    }
}

public class BlockV0296SplitEstimateTests
{
    private static void Feed(CoarseStateEvidence c, Func<int, (string Ctx, bool Y)> f, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var (ctx, y) = f(i);
            c.Observe("s", "x", true, new[] { 0.9, 0.05, 0.05, 0.05, 0.05, 0.05 });   // 次の画面は当たる・局所は外れる（候補になる）
            c.ObserveContext("s", "x", ctx, y ? new[] { 0 } : Array.Empty<int>());
        }
    }

    [Fact]
    public void Splitting_by_position_helps_when_outcome_depends_on_position()
    {
        // 正解: 直前の位置 "1,2" なら必ず動き、"5,2" なら動かない（壁のような隠れた局所の条件）
        var c = new CoarseStateEvidence(); Feed(c, i => (i % 2 == 0 ? "1,2" : "5,2", i % 2 == 0), 40);
        var e = Assert.Single(c.SplitEstimates(6, 20).Where(x => x.Event == "entity_moved"));
        Assert.True(e.LogBf >= Math.Log(10)); Assert.True(e.LooSplit < e.LooPooled); Assert.Equal(2, e.Contexts);
    }

    [Fact]
    public void Splitting_does_not_help_when_outcome_is_independent_of_position_and_needs_20_observations()
    {
        var r = new Random(4);
        var c = new CoarseStateEvidence(); Feed(c, i => (i % 3 == 0 ? "1,2" : i % 3 == 1 ? "5,2" : "none", r.Next(2) == 0), 60);
        var e = Assert.Single(c.SplitEstimates(6, 20).Where(x => x.Event == "entity_moved"));
        Assert.True(e.LogBf < Math.Log(10));
        var few = new CoarseStateEvidence(); Feed(few, i => ("1,2", true), 19);
        Assert.Empty(few.SplitEstimates(6, 20));
    }

    [Fact]
    public void Skill_local_effect_is_the_event_that_reliably_follows_the_sequence()
    {
        var s = new SkillLocalEffects();
        for (int i = 0; i < 8; i++) { s.Observe("seq A", new[] { 5 }); s.Observe("seq B", i % 4 == 0 ? new[] { 5 } : Array.Empty<int>()); }
        var eff = s.Effects(6).ToDictionary(x => x.Sequence, x => x.Effects);
        Assert.Equal(new[] { "number_changed" }, eff["seq A"]); Assert.Empty(eff["seq B"]);
    }
}
