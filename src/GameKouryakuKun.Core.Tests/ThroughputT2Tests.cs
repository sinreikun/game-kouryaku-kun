using GameKouryakuKun.Core.Throughput;
using GameKouryakuKun.Core.Turn;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ThroughputT2Tests
{
    [Fact]
    public void Keeps_global_until_enough_samples()
    {
        var a = new AdaptiveSettle();
        for (int i = 0; i < 19; i++) a.ObserveFirstChange("m1", 50);
        Assert.Equal(600, a.Recommend("m1", 600, 250));
        a.ObserveFirstChange("m1", 50);
        Assert.Equal(250, a.Recommend("m1", 600, 250));   // 2×50=100 → 下限 StableMs 250
    }

    [Fact]
    public void Never_longer_than_global_never_below_stable()
    {
        var a = new AdaptiveSettle();
        for (int i = 0; i < 30; i++) a.ObserveFirstChange("slow", 900);
        Assert.Equal(600, a.Recommend("slow", 600, 250));
        for (int i = 0; i < 30; i++) a.ObserveFirstChange("fast", 10);
        Assert.Equal(300, a.Recommend("fast", 600, 300));
    }

    [Fact]
    public void Uses_upper_quantile_not_median()
    {
        var a = new AdaptiveSettle();
        for (int i = 0; i < 95; i++) a.ObserveFirstChange("m", 100);
        for (int i = 0; i < 5; i++) a.ObserveFirstChange("m", 280);
        Assert.Equal(560, a.Recommend("m", 600, 250), 0);   // 上位 1% の遅い応答も含めて待つ
    }

    [Fact]
    public void Late_change_reverts_screen_to_global_and_raises_estimate()
    {
        var a = new AdaptiveSettle();
        for (int i = 0; i < 20; i++) a.ObserveFirstChange("m", 60);
        double used = a.Recommend("m", 600, 250);
        Assert.Equal(250, used);
        a.ShortenedNoChange("m");
        a.LateChange("m", used, shortened: true);
        Assert.Equal(1, a.Reverted);
        Assert.Equal(1.0, a.LateRate);
        for (int i = 0; i < a.CooldownTurns; i++) Assert.Equal(600, a.Recommend("m", 600, 250));
        Assert.Equal(500, a.Recommend("m", 600, 250));   // 早すぎた待ち 250 の 2 倍より短くしない（打ち切られた遅れ）
    }

    [Fact]
    public void Screens_are_independent()
    {
        var a = new AdaptiveSettle();
        for (int i = 0; i < 20; i++) a.ObserveFirstChange("a", 50);
        Assert.Equal(600, a.Recommend("b", 600, 250));
        Assert.Contains("a:n20", a.ScreensSummary());
    }

    [Fact]
    public void Turn_detector_default_thresholds_unchanged()
    {
        // 回帰: 適応は no-change の待ちだけ。既定の StableMs / MaxMs / NoChange 初期値は v0.38.0 と同じ
        var o = new TurnDetectorOptions();
        Assert.Equal(250, o.StableMs);
        Assert.Equal(8000, o.MaxMs);
        Assert.Equal(1200, o.NoChangeTimeoutMs);
        Assert.Equal(600, new AdaptiveTurnTimeout().NoChangeMinMs);
    }
}
