using GameKouryakuKun.Core.Activity;
using GameKouryakuKun.Core.Capture;
using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Input;
using GameKouryakuKun.Core.Probe;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class UserActivityTests
{
    [Fact]
    public void Classification_and_modes()
    {
        Assert.Equal(ActivitySource.Own, ActivityClassifier.Classify(ActivityClassifier.OwnSignature, true));
        Assert.Equal(ActivitySource.Physical, ActivityClassifier.Classify(0, true));
        Assert.Equal(ActivitySource.OtherInjected, ActivityClassifier.Classify(0, false));
        Assert.False(ActivityClassifier.CountsAsUser(ActivitySource.Own, UserActivityMode.AllButOwn));
        Assert.True(ActivityClassifier.CountsAsUser(ActivitySource.OtherInjected, UserActivityMode.AllButOwn));   // リモートデスクトップ越しもユーザー
        Assert.False(ActivityClassifier.CountsAsUser(ActivitySource.OtherInjected, UserActivityMode.PhysicalOnly));
        Assert.True(ActivityClassifier.CountsAsUser(ActivitySource.Physical, UserActivityMode.PhysicalOnly));
    }

    [Fact]
    public void Policy_pauses_isolated_only_when_game_is_foreground()
    {
        Assert.False(UserActivityPolicy.ShouldPause(IsolationLevel.Isolated, gameIsForeground: false));
        Assert.True(UserActivityPolicy.ShouldPause(IsolationLevel.Isolated, gameIsForeground: true));
        Assert.True(UserActivityPolicy.ShouldPause(IsolationLevel.ForegroundShared, gameIsForeground: false));
    }

    [Fact]
    public void Pause_controller_user_activity_auto_resumes_and_emergency_is_sticky()
    {
        var p = new PauseController { UserQuietMs = 1000 };
        var events = new List<(PauseReason, bool)>();
        p.Changed += (r, on, _) => events.Add((r, on));
        p.OnUserActivity(100);
        p.OnUserActivity(500);
        Assert.True(p.IsPaused(1400));
        Assert.False(p.IsPaused(1501));
        Assert.Single(events); // 連続した操作で通知は1回
        p.Set(PauseReason.Minimized, true);
        Assert.True(p.IsPaused(5000));
        p.Set(PauseReason.Minimized, false);
        Assert.False(p.IsPaused(5000));
        p.Set(PauseReason.EmergencyStop, true);
        p.Set(PauseReason.EmergencyStop, false);
        Assert.True(p.IsEmergencyStopped);
        Assert.Throws<ArgumentException>(() => p.Set(PauseReason.UserActivity, true));
    }
}

public class CaptureSelectorTests
{
    [Fact]
    public void Prefers_wgc_when_usable_and_falls_back()
    {
        var wgc = new CaptureProbeResult("wgc", true, 50, 8, 0, 1468, 857, "");
        var pw = new CaptureProbeResult("printwindow", true, 10, 30, 0, 1468, 857, "");
        Assert.Equal("wgc", CaptureBackendSelector.SelectAuto(new[] { pw, wgc }, new[] { "wgc", "printwindow" })!.BackendId);
        var wgcBlack = wgc with { BlackRatio = 1 };
        Assert.Equal("printwindow", CaptureBackendSelector.SelectAuto(new[] { wgcBlack, pw }, new[] { "wgc", "printwindow" })!.BackendId);
        Assert.Null(CaptureBackendSelector.SelectAuto(new[] { wgcBlack }, new[] { "wgc" }));
        Assert.True(new CaptureStatus(CaptureHealth.Minimized, "", 0).RequiresPause);
        Assert.False(new CaptureStatus(CaptureHealth.Stalled, "", 0).RequiresPause);
    }
}

/// <summary>v0.15.0: 入力の A（送達）と B（視覚的な反応）を分ける。特定の状態でキーが効くことを正常の条件にしない</summary>
public class InputDeliveryTests
{
    internal sealed class NullBackend : IInputBackend
    {
        public NullBackend(IsolationLevel l, string id = "x") { Isolation = new(l, false, false, false, null); Id = id; }
        public string Id { get; }
        public string DisplayName => Id;
        public IsolationInfo Isolation { get; }
        public InputProbeResult Probe(nint t) => new(true, "");
        public void Bind(nint t, LogicalKeyMap m, ForbiddenKeyGuard g, int h) { }
        public InputReceipt Send(InputAction a) => throw new NotSupportedException();
        public void ReleaseAll() { }
        public void Dispose() { }
    }

    [Fact]
    public void Selection_uses_only_delivery_and_prefers_isolated()
    {
        var iso = new NullBackend(IsolationLevel.Isolated, "window_message");
        var fg = new NullBackend(IsolationLevel.ForegroundShared, "sendinput");
        Assert.Equal("window_message", InputBackendSelector.SelectByDelivery(new (IInputBackend, bool)[] { (iso, true), (fg, true) }).BackendId);
        var onlyFg = InputBackendSelector.SelectByDelivery(new (IInputBackend, bool)[] { (iso, false), (fg, true) });
        Assert.Equal("sendinput", onlyFg.BackendId); Assert.True(onlyFg.NeedsConsent);
        Assert.Null(InputBackendSelector.SelectByDelivery(new (IInputBackend, bool)[] { (iso, false), (fg, false) }).BackendId);
    }

    [Fact]
    public void No_visual_reaction_never_becomes_a_system_failure()
    {
        var m = new InputDeliveryMonitor();
        for (int i = 0; i < 5000; i++) { Assert.False(m.Record(new InputReceipt(true, "w", "a", 0, 1, 2, ""))); m.RecordReaction(false); }
        Assert.False(m.SystemFailure); Assert.Equal(5000, m.LongestNoReactionStreak); Assert.Equal(0, m.Reacted);
    }

    [Fact]
    public void Consecutive_delivery_failures_are_a_system_failure_but_isolated_ones_are_not()
    {
        var m = new InputDeliveryMonitor();
        var bad = new InputReceipt(false, "w", "a", 0, 0, 0, "access denied"); var ok = new InputReceipt(true, "w", "a", 0, 1, 2, "");
        for (int i = 0; i < 20; i++) { m.Record(bad); m.Record(ok); }
        Assert.False(m.SystemFailure);
        bool failed = false; for (int i = 0; i < m.MaxConsecutiveFailures; i++) failed = m.Record(bad);
        Assert.True(failed); Assert.Equal("access denied", m.LastFailure);
    }

    [Fact]
    public void Session_has_no_known_operation_startup_test_and_does_not_stop_on_no_reaction()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), "GameKouryakuKun.App/Session.cs"));
        foreach (var gone in new[] { "Diagnostics.Toggle", "CompatTestRunner", "RunSeparationDiagnostic", "explorer.Unreachable", "diag_toggle" }) Assert.DoesNotContain(gone, src);
        Assert.Contains("SelectByDelivery", src);
        Assert.Contains("Delivery.Record(receipt)", src);
        var prof = File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), "profiles/torneko-remaster/profile.json"));
        Assert.DoesNotContain("toggle", prof);
    }
}

public class JsonlTests
{
    [Fact]
    public void Serializes_without_reflection()
    {
        var s = JsonlWriter.Serialize("turn", new Dictionary<string, object?> { ["a"] = 1, ["b"] = "日本語", ["c"] = null, ["d"] = double.NaN, ["e"] = new[] { 1, 2 }, ["f"] = TurnKindLike.X });
        Assert.Contains("\"type\":\"turn\"", s);
        Assert.Contains("\"b\":\"日本語\"", s);
        Assert.Contains("\"d\":null", s);
        Assert.Contains("\"e\":[1,2]", s);
        Assert.Contains("\"f\":\"X\"", s);
    }
    private enum TurnKindLike { X }
}
