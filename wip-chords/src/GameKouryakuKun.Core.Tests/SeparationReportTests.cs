using GameKouryakuKun.Core.Activity;
using GameKouryakuKun.Core.Input;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class SeparationReportTests
{
    private static SeparationStats Good() => new()
    {
        DurationSec = 640, TurnsGameForeground = 40, TurnsOtherForeground = 500, UserEventsIgnored = 3000, NoChangeWhileBackground = 30,
    };

    [Fact]
    public void Ten_minutes_mostly_background_with_user_work_passes()
    {
        var (app, ok, notes) = SeparationReport.Evaluate(Good(), IsolationLevel.Isolated);
        Assert.True(app); Assert.True(ok); Assert.Contains(notes, n => n.StartsWith("合格"));
    }

    [Fact]
    public void Short_session_like_run10_is_not_applicable()
    {
        // 実機 #10: 約57秒・67ターン・ほぼ前面・背面での操作なし
        var s = new SeparationStats { DurationSec = 57, TurnsGameForeground = 67, TurnsOtherForeground = 0 };
        var (app, ok, notes) = SeparationReport.Evaluate(s, IsolationLevel.Isolated);
        Assert.False(app); Assert.False(ok); Assert.Equal(3, notes.Count);
    }

    [Fact]
    public void Pausing_on_user_activity_while_game_is_background_fails()
    {
        var s = Good(); s.UserActivityPausesWhileBackground = 1;
        Assert.False(SeparationReport.Evaluate(s, IsolationLevel.Isolated).Passed);
    }

    [Fact]
    public void No_visual_reaction_does_not_fail_the_check()
    {
        // 画面が反応しない手（B）は合否に使わない（参考値）
        var s = Good(); s.NoChangeWhileBackground = 200;
        var (app, ok, notes) = SeparationReport.Evaluate(s, IsolationLevel.Isolated);
        Assert.True(app); Assert.True(ok); Assert.Contains(notes, n => n.StartsWith("参考"));
    }

    [Fact]
    public void Emergency_stop_or_foreground_shared_fail()
    {
        var s = Good(); s.EmergencyStopped = true;
        Assert.False(SeparationReport.Evaluate(s, IsolationLevel.Isolated).Passed);
        Assert.False(SeparationReport.Evaluate(Good(), IsolationLevel.ForegroundShared).Passed);
    }

    [Fact]
    public void Pause_counts_accumulate()
    {
        var s = new SeparationStats(); s.CountPause("UserActivity"); s.CountPause("UserActivity"); s.CountPause("Minimized");
        Assert.Equal(2, s.Pauses["UserActivity"]); Assert.Equal(1, s.Pauses["Minimized"]);
    }
}
