using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.9（P-3）: 同じ画面・同じ手でも候補の位置で移動するかが決まるとき、文脈で分けた予測の驚きが分けない予測より下がる</summary>
public class BlockV0309ContextTests
{
    private static readonly EntityLayer Empty = new(LayerStatus.Observed, Array.Empty<EntityCandidate>(), Array.Empty<int>(), Array.Empty<RelationObs>());
    private static readonly int Mv = Array.IndexOf(LocalEventModel.Events, "entity_moved");

    private static (double Ctx, double Pooled, long Used) Run(Func<int, bool> moves)
    {
        var m = new LocalEventModel(); var pooled = new LocalEventModel(); double sc = 0, sp = 0; int n = 0;
        for (int i = 0; i < 120; i++)
        {
            string ctx = i % 2 == 0 ? "1,2" : "5,2";
            var ev = moves(i) ? new[] { Mv } : Array.Empty<int>();
            var a = m.Observe("s", "a", false, ev, Empty, ctx); var b = pooled.Observe("s", "a", false, ev, Empty);
            if (i >= 60) { sc += a[Mv]; sp += b[Mv]; n++; }
        }
        return (sc / n, sp / n, m.ContextUsed);
    }

    [Fact]
    public void Context_dependent_movement_is_predicted_better_with_context()
    {
        var (c, p, used) = Run(i => i % 2 == 0);   // 位置 1,2 のときだけ動く
        Assert.True(used > 0);
        Assert.True(c < p / 3, $"文脈 {c:0.000}・分けない {p:0.000}");
    }

    [Fact]
    public void Context_is_not_used_when_movement_is_independent_of_position()
    {
        var r = new Random(3); var seq = Enumerable.Range(0, 120).Select(_ => r.NextDouble() < 0.5).ToArray();
        var (c, p, used) = Run(i => seq[i]);
        Assert.True(c <= p + 0.05, $"文脈 {c:0.000}・分けない {p:0.000}");
        Assert.True(used < 20, $"使った {used}");
    }
}
