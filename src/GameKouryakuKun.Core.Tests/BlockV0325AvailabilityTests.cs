using GameKouryakuKun.Core.Exploration;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.32.5（t106u）: 人が止めた入力を自律探索の候補から外す・保存と復元・すべて止めたとき</summary>
public class BlockV0325AvailabilityTests
{
    private static Explorer Ex(ActionAvailability av) =>
        new(Explorer.FromProfile(new[] { "dpad_up", "dpad_down", "button1", "button2" }, new[] { (IReadOnlyList<string>)new[] { "dpad_up", "button1" } }), 3) { Availability = av };

    [Fact]
    public void Disabled_actions_are_never_chosen_and_are_listed_as_excluded_by_user()
    {
        var av = new ActionAvailability(); var ex = Ex(av);
        av.Set("button1", false); av.Set("wait", false);
        for (int i = 0; i < 300; i++)
        {
            var c = ex.Choose("n" + (i % 3));
            Assert.NotEqual("button1", c.Action.Name); Assert.NotEqual("wait", c.Action.Name);
            ex.Observe("n" + (i % 3), c.Action, "n" + ((i + 1) % 3), true);
        }
        var sc = ex.ScoreCandidates("n0");
        var b1 = Assert.Single(sc, x => x.Action == "button1");
        Assert.True(b1.ExcludedByUser); Assert.Equal(0, b1.Score); Assert.Equal(0, b1.Probability);
        Assert.All(sc.Where(x => !x.ExcludedByUser), x => Assert.True(x.Score > 0));
        Assert.Equal(true, ScoreBreakdown.Row(b1)[^1]);
        av.Set("button1", true);
        Assert.False(ex.ScoreCandidates("n0").Single(x => x.Action == "button1").ExcludedByUser);
    }

    [Fact]
    public void Toggles_are_saved_and_restored_and_unknown_names_are_dropped()
    {
        var av = new ActionAvailability(); av.Set("button2", false); av.Set("dpad_down", false);
        var s = av.Serialize();
        var b = new ActionAvailability(); b.Load(s + "\nno_such_key", new[] { "button2", "dpad_down", "dpad_up" });
        Assert.Equal(new[] { "button2", "dpad_down" }, b.Disabled.OrderBy(x => x).ToArray());
        b.EnableAll(); Assert.False(b.Any); Assert.Equal("", b.Serialize());
    }

    [Fact]
    public void When_everything_is_disabled_exploration_reports_no_allowed_input_instead_of_choosing()
    {
        var av = new ActionAvailability(); var ex = Ex(av);
        foreach (var a in ex.Actions) av.Set(a.Name, false);
        Assert.False(ex.AnyUserAllowed);
        Assert.Throws<InvalidOperationException>(() => ex.Choose("n0"));   // Session は AnyUserAllowed を見て一時停止する
        Assert.All(ex.ScoreCandidates("n0"), x => Assert.True(x.ExcludedByUser));
        av.Set("dpad_up", true);
        Assert.True(ex.AnyUserAllowed); Assert.Equal("dpad_up", ex.Choose("n0").Action.Name);
    }
}
