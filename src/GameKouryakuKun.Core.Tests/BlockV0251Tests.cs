using GameKouryakuKun.Core.Growth;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.25.1: 起動をまたいだ価値の引き継ぎは、同じ版で記録した組だけで比べる（版が変わると価値の定義が変わり得る）</summary>
public class BlockV0251Tests
{
    private static GrowthSnapshot S(long run, long seq, string why, double vr, string ver) =>
        new(run, seq, (int)seq, 3, 0, 1, 2, 1, 0, 0, 0, 0, 5, 10, 1.0, vr, 0.5, 1, 0, 0, 0, 0, 0, new Dictionary<int, int>(), why) { AppVersion = ver };

    private static GrowthCheck C(IEnumerable<GrowthSnapshot> xs) => GrowthEvaluator.Evaluate(xs.ToList()).Single(c => c.Name.Contains("引き継ぎ"));

    [Fact]
    public void Same_version_value_mismatch_still_fails()
    {
        var c = C(new[] { S(1, 1, "start", 0.5, "0.26.2"), S(1, 2, "end", 0.3, "0.26.2"), S(2, 3, "start", 0.8, "0.26.2"), S(2, 4, "end", 0.8, "0.26.2") });   // v0.26.3: 修正の版以降で検査
        Assert.Equal(GrowthLevel.Fail, c.Level);
    }

    [Fact]
    public void Cross_or_unrecorded_version_value_change_is_not_compared_but_noted()
    {
        var c = C(new[] { S(1, 1, "start", 0.5, ""), S(1, 2, "end", 0.3, ""), S(2, 3, "start", 0.8, "0.25.1"), S(2, 4, "end", 0.8, "0.25.1") });
        Assert.Equal(GrowthLevel.Pass, c.Level);
        Assert.Contains("版をまたぐ", c.Detail);
    }

    [Fact]
    public void Version_round_trips_through_json()
    {
        var s = S(1, 1, "start", 0.5, "0.25.1");
        Assert.Equal("0.25.1", GameKouryakuKun.Storage.GrowthJson.Read(GameKouryakuKun.Storage.GrowthJson.Write(s)).AppVersion);
    }
}
