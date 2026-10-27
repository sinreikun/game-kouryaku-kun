using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.14.0: 次の画面の予測の確率が、当たる割合を表すこと（画面の数が増えても薄まらない）</summary>
public class NoveltyTests
{
    [Fact]
    public void Prediction_is_not_diluted_by_the_number_of_known_screens()
    {
        var m = new TransitionModel(); long q = 0;
        // 300 個の画面を知っている（それぞれ別の関係で見た）
        for (int i = 0; i < 300; i++) m.Observe(q++, $"n{i}", "button1", $"n{i + 1}", true);
        // 決まった結果を返す関係を 5 回見た後の予測は、ほぼ確か（従来の平滑化では 6/305 ≒ 0.02 だった）
        for (int i = 0; i < 5; i++) m.Observe(q++, "a", "button2", "b", true);
        var p = m.Predict("a", "button2");
        Assert.Equal("b", p.TopNext);
        Assert.True(p.PTop > 0.8, $"PTop {p.PTop}");
        // 何も見ていない関係は一様（予測の材料なし）
        var u = m.Predict("zz", "button3");
        Assert.Null(u.TopNext);
        Assert.Equal(Math.Log2(303 + 1), u.Entropy, 6);   // 既知の画面 n0〜n300・a・b の 303 個＋まだ見ていない 1 つ
    }

    [Theory]
    [InlineData(0.3, 11)]
    [InlineData(2.0, 12)]
    public void Estimated_concentration_and_calibration_follow_the_generating_process(double trueAlpha, int seed)
    {
        // 関係ごとの結果をディリクレ過程（集中度 trueAlpha、基底は 500 個の画面から一様）で作る。ゲームの知識は使わない合成データ
        var rnd = new Random(seed); var m = new TransitionModel(); long q = 0;
        var checks = new List<(double P, bool Hit)>();
        for (int rel = 0; rel < 400; rel++)
        {
            var seen = new List<string>();
            for (int t = 0; t < 15; t++)
            {
                string next = rnd.NextDouble() < trueAlpha / (trueAlpha + t) ? $"s{rnd.Next(500)}" : seen[rnd.Next(seen.Count)];
                seen.Add(next);
                var c = m.Observe(q++, $"r{rel}", "button1", next, true);
                if (c.Prediction.TopNext != null && rel >= 100) checks.Add((c.Prediction.PTop, c.Hit));
            }
        }
        Assert.InRange(m.Novelty.Alpha, trueAlpha / 1.6, trueAlpha * 1.6);
        var (gap, _, _) = HealthReport.Calibration(checks);
        Assert.True(gap < 0.05, $"較正のずれ {gap}");
    }

    [Fact]
    public void Concentration_starts_at_default_and_refits_only_after_enough_events()
    {
        var n = new NoveltyConcentration();
        for (int i = 0; i < n.RefitEvery - 1; i++) n.Observe(3, false);
        Assert.Equal(NoveltyConcentration.DefaultAlpha, n.Alpha);
        n.Observe(3, false);
        Assert.Equal(NoveltyConcentration.MinAlpha, n.Alpha, 6);   // 新しい結果が一度もなければ下限
        n.Observe(0, true);                                          // 初めての関係は数えない
        Assert.Equal(n.RefitEvery, n.Events);
    }

    [Fact]
    public void Health_calibration_gap_flags_underconfident_predictions()
    {
        // 確率 0.1 と言って 8 割当たる（v0.13.1 までの平滑化で起きていたずれの形）
        var bad = Enumerable.Range(0, 100).Select(i => (0.1, i % 10 < 8)).ToList();
        Assert.True(HealthReport.Calibration(bad).Gap > HealthReport.CalibrationWarnGap);
        var good = Enumerable.Range(0, 100).Select(i => (0.8, i % 10 < 8)).ToList();
        Assert.True(HealthReport.Calibration(good).Gap < 0.01);
    }
}
