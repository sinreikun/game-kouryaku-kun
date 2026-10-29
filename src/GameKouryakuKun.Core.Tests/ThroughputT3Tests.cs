using GameKouryakuKun.Core.Throughput;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ThroughputT3Tests
{
    private static bool[] B(params int[] on) { var b = new bool[8]; foreach (var i in on) b[i] = true; return b; }

    [Fact]
    public void Flickering_block_becomes_natural_candidate_and_input_block_stays_input()
    {
        var m = new NaturalNoiseModel();
        for (int i = 0; i < 10; i++) m.ObserveNatural("m1", B(0), i * 100);   // 区画 0 は入力なしでも毎回変わる
        var an = m.Annotate("m1", B(0, 3));
        Assert.Equal(2, an.Changed);
        Assert.Equal(1, an.Natural);
        Assert.Equal(1, an.Input);
        Assert.Equal(1, an.Periodic);   // 100 ms ごと
    }

    [Fact]
    public void No_baseline_is_reported_not_guessed()
    {
        var m = new NaturalNoiseModel();
        for (int i = 0; i < 4; i++) m.ObserveNatural("m1", B(0), i);
        var an = m.Annotate("m1", B(0));
        Assert.Equal(1, an.NoBaseline);
        Assert.Equal(0, an.Natural);
        Assert.Equal(0, m.ScreensWithBaseline);
    }

    [Fact]
    public void Irregular_change_is_not_periodic()
    {
        var m = new NaturalNoiseModel();
        double[] ts = { 0, 10, 400, 420, 1500, 1510 };
        foreach (var t in ts) m.ObserveNatural("m", B(1), t);
        Assert.Null(m.Period("m", 1));
    }

    [Fact]
    public void Annotation_does_not_modify_input_array()
    {
        var m = new NaturalNoiseModel();
        for (int i = 0; i < 6; i++) m.ObserveNatural("m", B(2), i);
        var c = B(2, 5); var copy = (bool[])c.Clone();
        m.Annotate("m", c);
        Assert.Equal(copy, c);   // 観測を消さない（注釈のみ）
    }
}
