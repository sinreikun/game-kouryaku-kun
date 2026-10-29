using System.Text.Json;
using GameKouryakuKun.Core.Throughput;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class ThroughputT0Tests : IDisposable
{
    private static readonly string[] Envs = { ThroughputFlags.StepTimingEnv, ThroughputFlags.PipelineSensingEnv, ThroughputFlags.AdaptiveSettleEnv,
        ThroughputFlags.NaturalNoiseEnv, ThroughputFlags.RawAggSplitEnv, ThroughputFlags.ParallelObserversEnv };
    public ThroughputT0Tests() { foreach (var e in Envs) Environment.SetEnvironmentVariable(e, null); ThroughputFlags.ApplyEffective(); }
    public void Dispose() { foreach (var e in Envs) Environment.SetEnvironmentVariable(e, null); ThroughputFlags.ApplyEffective(); }

    [Fact]
    public void All_flags_default_off()
    {
        ThroughputFlags.ApplyEffective();
        Assert.False(ThroughputFlags.StepTiming || ThroughputFlags.PipelineSensing || ThroughputFlags.AdaptiveSettle
            || ThroughputFlags.NaturalNoise || ThroughputFlags.RawAggSplit || ThroughputFlags.ParallelObservers);
        Assert.False(ThroughputFlags.LedgerActive);
        Assert.Contains("OFF（既定", ThroughputFlags.StatusText());
        foreach (var e in Envs)
        {
            Environment.SetEnvironmentVariable(e, "1"); ThroughputFlags.ApplyEffective();
            Assert.DoesNotContain("OFF（既定", ThroughputFlags.StatusText());
            Environment.SetEnvironmentVariable(e, null); ThroughputFlags.ApplyEffective();
        }
    }

    [Fact]
    public void Phases_sum_to_interval()
    {
        var t = new StepTiming();
        double now = 0;
        t.Mark("post", now);   // 印を置くだけ
        for (int i = 0; i < 10; i++)
        {
            now += 40; t.Mark("decide", now); t.SendStarted(now);
            now += 100; t.Mark("send", now);
            now += 600; t.Mark("settle", now);
            now += 70; t.Mark("commit", now);
            now += 470; t.Mark("obs_wait", now);
            now += 2; t.Mark("post", now);
            t.EndStep();
        }
        Assert.Equal(10, t.Steps);
        Assert.Equal(600, t.MedianByPhase()["settle"]);
        Assert.InRange(t.CoverageRatio(), 0.95, 1.05);
        Assert.InRange(t.ActionsPerMinute(), 46, 48);   // 1282 ms/手 ≒ 46.8 手/分（#159 相当）
    }

    [Fact]
    public void Unknown_phase_throws() => Assert.Throws<ArgumentException>(() => new StepTiming().Mark("ocr", 0));

    private static List<JsonElement> Parse(params string[] lines) => lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [Fact]
    public void Health_not_tested_when_off()
    {
        var cs = new List<(string, HealthReport.Level)>();
        ThroughputHealth.Add(Parse("{\"type\":\"turn\"}"), (a, n, l, d) => cs.Add((n, l)));
        Assert.All(cs, c => Assert.Equal(HealthReport.Level.NotTested, c.Item2));
        Assert.Equal(6, cs.Count);
    }

    [Fact]
    public void Health_timing_pass_within_tolerance()
    {
        var cs = new List<(string, HealthReport.Level)>();
        ThroughputHealth.Add(Parse("{\"type\":\"step_timing\",\"decide_ms\":40,\"settle_ms\":600,\"total_ms\":1282}",
            "{\"type\":\"step_timing_summary\",\"actions_per_min\":46.8,\"coverage\":1.01}"), (a, n, l, d) => cs.Add((n, l)));
        Assert.Contains(cs, c => c.Item1 == "手の速さ" && c.Item2 == HealthReport.Level.Ok);
    }
}
