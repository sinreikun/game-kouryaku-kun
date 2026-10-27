using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.31.0（P-4）: 離れた区画の共変動で Region の群を作る</summary>
public class BlockV0310CovariationTests
{
    [Fact]
    public void Two_distant_cells_changing_together_after_actions_are_one_region()
    {
        var c = new RegionCovariation(192); var r = new Random(1);
        for (int i = 0; i < 80; i++)
        {
            var ch = new List<int>(); if (r.NextDouble() < 0.4) { ch.Add(5); ch.Add(150); }
            if (r.NextDouble() < 0.4) ch.Add(70);   // 独立に変わる区画
            c.Observe(ch, false);
        }
        var g = c.Groups();
        Assert.Contains(g, x => x.Kind == "action" && x.Cells.SequenceEqual(new[] { 5, 150 }));
        Assert.DoesNotContain(g, x => x.Cells.Contains(70));
    }

    [Fact]
    public void Periodic_background_without_input_is_a_natural_group()
    {
        var c = new RegionCovariation(192);
        for (int i = 0; i < 60; i++) c.Observe(i % 3 == 0 ? new[] { 0, 1, 2, 30 } : Array.Empty<int>(), true);
        for (int i = 0; i < 60; i++) c.Observe(i % 2 == 0 ? new[] { 100 } : Array.Empty<int>(), false);
        var g = c.Groups();
        Assert.Contains(g, x => x.Kind == "natural" && x.Cells.SequenceEqual(new[] { 0, 1, 2, 30 }));
        Assert.DoesNotContain(g, x => x.Kind == "action");
    }

    [Fact]
    public void Screen_change_steps_do_not_hide_the_group()   // v0.31.1: 実機 #134 で群が 0
    {
        var c = new RegionCovariation(192); var r = new Random(2); var all = Enumerable.Range(0, 192).ToArray();
        for (int i = 0; i < 300; i++)
        {
            if (r.NextDouble() < 0.3) { c.Observe(all, false); continue; }
            var ch = Enumerable.Range(0, 192).Where(_ => r.NextDouble() < 0.2).Where(x => x != 5 && x != 150).ToList();
            if (r.NextDouble() < 0.3) { ch.Add(5); ch.Add(150); }
            c.Observe(ch, false);
        }
        Assert.Contains(c.Groups(), x => x.Kind == "action" && x.Cells.Contains(5) && x.Cells.Contains(150));
        Assert.True(c.Skipped > 50);
        Assert.True(c.Groups().Sum(x => x.Cells.Length) <= 10, string.Join(" ", c.Groups().Select(x => x.Cells.Length)));   // 独立に変わる区画は偶然でつながらない
    }

    [Fact]
    public void A_chain_of_overlapping_pairs_is_not_one_big_group()   // v0.31.2: 実機 #135 で 107 区画の群
    {
        var c = new RegionCovariation(192); var r = new Random(4);
        for (int i = 0; i < 400; i++)
        {
            int k = r.Next(40);   // 隣り合う 2 区画（k, k+1）だけが一緒に変わる。鎖でつなぐと 41 区画になる
            c.Observe(new[] { k, k + 1 }, false);
        }
        Assert.True(c.Groups().Max(x => x.Cells.Length) <= 3, string.Join(" ", c.Groups().Select(x => x.Cells.Length)));
    }
}
