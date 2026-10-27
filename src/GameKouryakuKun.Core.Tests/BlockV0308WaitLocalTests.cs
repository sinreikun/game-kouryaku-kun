using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.30.8（P-2）: 待つ手の値打ちに局所の出来事の成分。入力なしで点滅する候補の値打ちは、待つ手の観測とともに下がる</summary>
public class BlockV0308WaitLocalTests
{
    private static readonly EntityLayer Empty = new(LayerStatus.Observed, Array.Empty<EntityCandidate>(), Array.Empty<int>(), Array.Empty<RelationObs>());

    [Fact]
    public void Wait_value_of_blinking_candidate_decreases_with_wait_observations()
    {
        var m = new LocalEventModel(); int app = Array.IndexOf(LocalEventModel.Events, "appearance_only");
        Assert.Equal(0, m.NaturalInformation("s"));   // 手の観測がなければ 0
        for (int i = 0; i < 10; i++) m.Observe("s", "a", false, new[] { app }, Empty);   // 手の後にいつも見た目が変わる
        var vals = new List<double> { m.NaturalInformation("s") };
        for (int i = 0; i < 12; i++) { m.Observe("s", "wait", true, i % 2 == 0 ? new[] { app } : Array.Empty<int>(), Empty); vals.Add(m.NaturalInformation("s")); }
        Assert.True(vals[0] > 0.5, string.Join(",", vals));
        Assert.True(vals[^1] < vals[0] / 3, string.Join(",", vals));
        Assert.True(new WaitInformation(0, 0, 0, 0) { Local = vals[0] }.Total > new WaitInformation(0, 0, 0, 0) { Local = vals[^1] }.Total);
    }

    [Fact]
    public void No_local_value_when_actions_never_cause_events()
    {
        var m = new LocalEventModel();
        for (int i = 0; i < 20; i++) m.Observe("s", "a", false, Array.Empty<int>(), Empty);
        Assert.True(m.NaturalInformation("s") < 0.1);
    }
}
