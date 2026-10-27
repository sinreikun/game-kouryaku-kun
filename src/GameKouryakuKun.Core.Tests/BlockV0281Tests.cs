using GameKouryakuKun.Core.Exploration;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class BlockV0281Tests
{
    [Fact]
    public void Wait_value_rises_with_post_action_change_and_falls_with_natural_samples()
    {
        double few = WaitInformation.Part(0, 0, 18, 20), quiet = WaitInformation.Part(0, 0, 1, 20), none = WaitInformation.Part(0, 0, 0, 0);
        Assert.True(few > quiet); Assert.Equal(0, none);
        double prev = few;
        for (int n = 1; n <= 30; n++) { double v = WaitInformation.Part(n / 3, n, 18, 20); Assert.True(v <= prev + 1e-12, $"n={n}"); prev = v; }
        Assert.True(prev < few * 0.6);
        var w = new WaitInformation(0.5, 0, 0, 0.5); Assert.Equal(0.75, w.Total, 9);
    }

    [Fact]
    public void Region_natural_information_needs_action_changes_and_shrinks_with_waits()
    {
        var r = new RegionCausality(4);
        Assert.Equal(0, r.NaturalInformation("s").Value);
        for (int i = 0; i < 20; i++) r.ObserveAction("s", "a", new[] { 2 });
        var (v0, c0) = r.NaturalInformation("s"); Assert.Equal(2, c0); Assert.True(v0 > 0.5);
        for (int i = 0; i < 20; i++) r.ObserveNatural("s", Array.Empty<int>());
        Assert.True(r.NaturalInformation("s").Value < v0 * 0.6);
    }

    [Fact]
    public void Wait_info_only_changes_wait_and_is_logged()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2" }).Append(ExploreAction.Wait).ToList();
        var a = new Explorer(acts, 5); var b = new Explorer(acts, 5) { WaitInfoValue = _ => new WaitInformation(0.4, 0.1, 0.2, 0.3) };
        var sa = a.ScoreCandidates("s"); var sb = b.ScoreCandidates("s");
        foreach (var x in sa.Where(c => c.Action != ExploreAction.Wait.Name)) Assert.Equal(x.Score, sb.First(c => c.Action == x.Action).Score, 9);
        var w = sb.First(c => c.Action == ExploreAction.Wait.Name);
        Assert.NotNull(w.Wait); Assert.Equal(w.Wait!.Total, w.Uncertainty, 9);
        Assert.Equal(ScoreBreakdown.Fields.Length, ScoreBreakdown.Row(w).Length);
        var log = new ScoreBreakdown(); log.Outcome(sb[0], true, true, false); log.Outcome(sb[1], false, false, false);
        Assert.Equal(2, log.Outcomes); Assert.Contains("新しい辺", log.ContributionSummary()); Assert.Contains("前半", log.Halves());
    }
}
