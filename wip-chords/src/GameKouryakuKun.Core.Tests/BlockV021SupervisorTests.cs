using System.Text.RegularExpressions;
using GameKouryakuKun.Core.Supervisor;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.21.0 Game Supervisor の合成の試験（偽の OS で、終了・消失・取得の途切れ・再起動・回路遮断を起こす）</summary>
public class BlockV021SupervisorTests
{
    /// <summary>偽の OS。状態を書き換えて終了などを起こす。呼ばれた操作を記録する</summary>
    private sealed class FakeHost : ISupervisorHost
    {
        public double Now { get; set; }
        public bool StopRequested { get; set; }
        public bool Alive = true, Known = true, Window = true, Capture = true, Hung, HandleChanged, InputOkV = true, InputReconnectWorks = true;
        public int? ExitCode;
        public int W = 800, H = 600;
        public bool CanLaunchValue = true;
        /// <summary>起動の結果: true＝起動してウィンドウが出る</summary>
        public Func<int, bool> LaunchWorks = _ => true;
        public int Launches, Checkpoints, Flushes, Stops, ReObserves;
        public bool CheckpointThrows, ReObserveFails;
        /// <summary>ウィンドウが戻る時刻（一時的な消失）</summary>
        public double? WindowBackAt, CaptureBackAt;
        public List<string> Calls = new();

        public double? EnableLaunchAt, StopAt;
        public Action? AfterLaunch; public bool AliveWithWindow;
        public void Sleep(double ms) { Now += ms; Tick(); }
        private void Tick()
        {
            if (EnableLaunchAt is double el && Now >= el) { CanLaunchValue = true; EnableLaunchAt = null; }
            if (StopAt is double sa && Now >= sa) StopRequested = true;
            if (WindowBackAt is double w && Now >= w) { Window = true; WindowBackAt = null; if (AliveWithWindow) { Alive = true; ExitCode = null; AliveWithWindow = false; } }
            if (CaptureBackAt is double c && Now >= c) { Capture = true; CaptureBackAt = null; }
        }
        public SupervisorSignals Probe() { Tick(); return new(Known, Alive, ExitCode, Window, HandleChanged, Hung, Capture, Capture ? null : "err", InputOkV, W, H); }
        public void StopActions() { Stops++; Calls.Add("stop"); }
        public void FlushExperience() { Flushes++; Calls.Add("flush"); }
        public bool CheckpointDb(out string detail)
        {
            Calls.Add("checkpoint");
            if (CheckpointThrows) { detail = "disk full"; return false; }
            Checkpoints++; detail = "ok"; return true;
        }
        public bool FindWindow(out string d) { Calls.Add("find"); Tick(); d = Window ? "w" : "none"; return Window && Alive; }
        public bool ReconnectCapture(out string d) { Calls.Add("capture"); Tick(); d = ""; HandleChanged = false; return Capture; }
        public bool ReconnectInput(out string d) { Calls.Add("input"); d = ""; if (InputReconnectWorks) InputOkV = true; return InputReconnectWorks; }
        public ProcessCheck CheckProcess(out int? code) { code = ExitCode; return !Known ? ProcessCheck.Unknown : Alive ? ProcessCheck.Alive : ProcessCheck.Exited; }
        public bool CanLaunch => CanLaunchValue;
        public bool Launch(out string d)
        {
            Launches++; Calls.Add("launch"); d = "fake";
            if (!LaunchWorks(Launches)) return false;
            Alive = true; Known = true; Window = true; Capture = true; ExitCode = null; AfterLaunch?.Invoke(); return true;
        }
        public bool WaitCapture(double t, out string d) { d = ""; return Capture; }
        public bool ReObserve(out string d) { ReObserves++; Calls.Add("reobserve"); d = ""; return !ReObserveFails; }
    }

    private static SupervisorConfig Cfg(int max = 3) => new() { LaunchMethod = "exe", Executable = "game.exe", MaxRestarts = max, BackoffInitialSec = 1, BackoffMaxSec = 4, StartupTimeoutSec = 5, WindowTimeoutSec = 5 };

    private static string[] Kinds(SupervisorEngine s) => s.Events.Select(e => e.Name).ToArray();

    [Fact]
    public void Normal_exit_runs_full_pipeline_in_order_and_resumes()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        Assert.Equal(PollResult.Proceed, s.Poll());
        h.Alive = false; h.Window = false; h.ExitCode = 0;
        Assert.Equal(PollResult.Recovered, s.Poll());
        var k = Kinds(s);
        string[] order = { "OBSERVATION_LOST", "DB_CHECKPOINT", "PROCESS_EXITED", "SUPERVISOR_RESTART_BEGIN", "PROCESS_STARTED", "WINDOW_ACQUIRED", "CAPTURE_RESTORED", "OBSERVATION_RESTORED", "EXPLORATION_RESUMED" };
        int last = -1; foreach (var o in order) { int i = Array.IndexOf(k, o, last + 1); Assert.True(i > last, $"{o} の順序: {string.Join(",", k)}"); last = i; }
        // 手を止める → 経験の保存 → DB の保存 が起動より前
        Assert.True(h.Calls.IndexOf("stop") < h.Calls.IndexOf("flush") && h.Calls.IndexOf("flush") < h.Calls.IndexOf("checkpoint") && h.Calls.IndexOf("checkpoint") < h.Calls.IndexOf("launch"));
        Assert.Equal("normal", s.Events.First(e => e.Kind == SystemEventKind.ProcessExited).Data["class"]);
        Assert.Equal(SupervisorState.Normal, s.State);
        Assert.Equal(1, s.RecoveriesOk); Assert.Equal(1, s.Restarts); Assert.Equal(0, s.AbnormalExits);
        Assert.Equal("resumed", s.Transactions.Single().Outcome);
        Assert.Contains("Restart: 1", s.StatusText);
    }

    [Fact]
    public void Abnormal_and_crash_exit_codes_are_classified_only_as_os_categories()
    {
        Assert.Equal(ExitClass.Normal, ExitClassifier.Classify(0));
        Assert.Equal(ExitClass.Abnormal, ExitClassifier.Classify(1));
        Assert.Equal(ExitClass.Crash, ExitClassifier.Classify(unchecked((int)0xC0000005)));
        Assert.Equal(ExitClass.Unknown, ExitClassifier.Classify(null));
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.Alive = false; h.ExitCode = unchecked((int)0xC0000005);
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(1, s.AbnormalExits);
        Assert.Equal("crash", s.Events.First(e => e.Kind == SystemEventKind.ProcessExited).Data["class"]);
    }

    [Fact]
    public void Temporary_window_loss_within_grace_holds_without_recovery()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.Window = false;
        Assert.Equal(PollResult.Hold, s.Poll());
        h.Now += 2000; Assert.Equal(PollResult.Hold, s.Poll());
        h.Window = true; Assert.Equal(PollResult.Proceed, s.Poll());
        Assert.Empty(s.Transactions); Assert.Equal(0, h.Launches);
    }

    [Fact]
    public void Window_loss_with_live_process_reattaches_at_L1_without_restart()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.Window = false; s.Poll(); h.Now += 6000; h.WindowBackAt = h.Now + 3000;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(0, h.Launches);   // プロセスが生きている限り再起動しない
        Assert.Contains("L3_check_process", s.Transactions[0].Stages.Select(x => x.Stage));
        Assert.Contains("WINDOW_LOST", Kinds(s));
    }

    [Fact]
    public void Handle_change_reattaches_immediately()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.HandleChanged = true;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Contains("WINDOW_HANDLE_CHANGED", Kinds(s)); Assert.Equal(0, h.Launches);
    }

    [Fact]
    public void Temporary_capture_loss_reconnects_at_L2()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.Capture = false;
        Assert.Equal(PollResult.Hold, s.Poll());
        h.Now += 6000; h.CaptureBackAt = h.Now + 1000;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(0, h.Launches); Assert.True(s.CaptureReconnects >= 1);
        Assert.Contains("CAPTURE_LOST", Kinds(s)); Assert.Contains("CAPTURE_RESTORED", Kinds(s));
    }

    [Fact]
    public void Live_process_without_window_and_no_launch_method_waits_for_human()
    {
        var h = new FakeHost { CanLaunchValue = false }; var s = new SupervisorEngine(h, Cfg());
        h.Window = false; s.Poll(); h.Now += 6000;
        Assert.Equal(PollResult.Aborted, s.Poll());
        Assert.Equal(0, h.Launches);
        Assert.Equal("WINDOW_NOT_RESTORED", s.AbortReason);
    }

    [Fact]
    public void Restart_failure_then_success_counts_attempts()
    {
        var h = new FakeHost { LaunchWorks = n => n >= 2 }; var s = new SupervisorEngine(h, Cfg());
        h.Alive = false; h.ExitCode = 1;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(2, h.Launches); Assert.Equal(2, s.Transactions[0].Attempts);
        Assert.Contains("RECOVERY_FAILED", Kinds(s));
    }

    [Fact]
    public void Repeated_failure_trips_breaker_with_backoff_and_waits_for_human()
    {
        var h = new FakeHost { LaunchWorks = _ => false }; var s = new SupervisorEngine(h, Cfg(max: 3));
        h.Alive = false; h.ExitCode = 1;
        double t0 = h.Now;
        Assert.Equal(PollResult.Aborted, s.Poll());
        Assert.Equal(3, h.Launches);
        Assert.Equal("RESTART_LOOP", s.AbortReason);
        Assert.Equal(1, s.BreakerTrips);
        Assert.True(h.Now - t0 >= (1 + 2 + 4) * 1000, "待ちが倍になっていない");   // 1・2・4 秒（上限 4）
        Assert.Contains("RECOVERY_ABORTED", Kinds(s));
        Assert.Contains("RESTART_LOOP", s.StatusText);
        // 開いた後は何もしない（再起動しない）
        Assert.Equal(PollResult.Aborted, s.Poll()); Assert.Equal(3, h.Launches);
    }

    [Fact]
    public void Breaker_counts_restarts_across_recoveries_within_time_window()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg(max: 2));
        for (int i = 0; i < 2; i++) { h.Alive = false; h.ExitCode = 1; Assert.Equal(PollResult.Recovered, s.Poll()); }
        h.Alive = false; h.ExitCode = 1;
        Assert.Equal(PollResult.Aborted, s.Poll());
        Assert.Equal("RESTART_LOOP", s.AbortReason); Assert.Equal(2, h.Launches);
    }

    [Fact]
    public void Old_restarts_outside_window_do_not_trip()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg(max: 1));
        h.Alive = false; h.ExitCode = 1; Assert.Equal(PollResult.Recovered, s.Poll());
        h.Now += 2 * 3600_000;
        h.Alive = false; h.ExitCode = 1; Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(0, s.BreakerTrips);
    }

    [Fact]
    public void No_launch_method_waits_for_manual_restart_then_reattaches()
    {
        // 人が起動し直す: 10 秒後にプロセスとウィンドウが戻る
        var h = new ManualHost { CanLaunchValue = false, BackAt = 10_000 };
        var s = new SupervisorEngine(h, new SupervisorConfig { ManualRestartWaitSec = 30 });
        h.Alive = false; h.Window = false; h.ExitCode = 0;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(0, h.Launches);
        Assert.Contains(s.Transactions[0].Stages, x => x.Stage == "wait_manual_restart");
    }

    [Fact]
    public void No_launch_method_times_out_and_aborts_without_restart()
    {
        var h = new FakeHost { CanLaunchValue = false }; var s = new SupervisorEngine(h, new SupervisorConfig { ManualRestartWaitSec = 30 });
        h.Alive = false; h.Window = false; h.ExitCode = 0;
        Assert.Equal(PollResult.Aborted, s.Poll());
        Assert.Equal("MANUAL_RESTART_TIMEOUT", s.AbortReason); Assert.Equal(0, h.Launches);
    }

    // ---- v0.23.1: t44u の見直し ----
    [Fact]
    public void Window_lost_with_live_process_relaunches_after_timeout_without_kill()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.Window = false; s.Poll(); h.Now += 6000;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(1, h.Launches);
        Assert.Contains(s.Transactions[^1].Stages, x => x.Stage == "relaunch_without_exit");
        Assert.Contains(s.Transactions[^1].Stages, x => x.Stage == "process_tracked" && x.Ok);
    }

    [Fact]
    public void Window_lost_with_unknown_process_relaunches()
    {
        var h = new FakeHost { Known = false }; var s = new SupervisorEngine(h, Cfg());
        h.Window = false; s.Poll(); h.Now += 6000;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(1, h.Launches);
    }

    [Fact]
    public void Process_gone_relaunches_immediately()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.Alive = false; h.Window = false; h.ExitCode = 0;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(1, h.Launches); Assert.Contains("PROCESS_EXITED", Kinds(s));
    }

    [Fact]
    public void Input_backend_failure_is_a_supervisor_signal_and_reconnects()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.InputOkV = false;
        Assert.Equal(PollResult.Hold, s.Poll());
        h.Now += 6000;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(0, h.Launches); Assert.Equal(1, s.InputLosses); Assert.Contains("INPUT_RECONNECTED", Kinds(s));
    }

    [Fact]
    public void Repeated_input_failure_stops_and_waits_for_human()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg(max: 2));
        for (int i = 0; i < 2; i++) { h.InputOkV = false; s.Poll(); h.Now += 6000; Assert.Equal(PollResult.Recovered, s.Poll()); }
        h.InputOkV = false; s.Poll(); h.Now += 6000;
        Assert.Equal(PollResult.Aborted, s.Poll());
        Assert.Equal("INPUT_LOOP", s.AbortReason);
    }

    [Fact]
    public void Protected_file_baseline_persists_across_sessions_and_warns_once()
    {
        var d = Path.Combine(Path.GetTempPath(), "gkk-pf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        var f = Path.Combine(d, "settings.ini"); File.WriteAllText(f, "a");
        var bp = Path.Combine(d, ProtectedFiles.BaselineFileName);
        var p1 = new ProtectedFiles(); p1.LoadOrCreateBaseline(new[] { f }, bp);
        Assert.Equal("created", p1.BaselineOrigin); Assert.Empty(p1.NewChanges());
        File.WriteAllText(f, "bb");   // アプリを閉じている間の変化
        var p2 = new ProtectedFiles(); p2.LoadOrCreateBaseline(new[] { f }, bp);
        Assert.Equal("loaded", p2.BaselineOrigin);
        Assert.Single(p2.NewChanges());
        Assert.Empty(p2.NewChanges());            // 同じ変化は 1 回だけ
        Assert.Single(p2.Changes());              // 基準は動かない
        File.WriteAllText(f, "ccc");
        Assert.Single(p2.NewChanges());           // 別の変化はまた知らせる
    }

    [Fact]
    public void Enabling_launch_while_waiting_for_human_relaunches()
    {
        var h = new FakeHost { CanLaunchValue = false }; var s = new SupervisorEngine(h, Cfg());
        h.Alive = false; h.Window = false; h.ExitCode = 0;
        // 待っている間に人が ON にする（10 秒後）
        h.EnableLaunchAt = 10_000;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(1, h.Launches);
        Assert.Contains(s.Transactions[^1].Stages, x => x.Stage == "launch_enabled_while_waiting");
    }

    [Fact]
    public void Stop_during_manual_wait_is_a_stop_not_an_abort()
    {
        var h = new FakeHost { CanLaunchValue = false, StopAt = 10_000 }; var s = new SupervisorEngine(h, Cfg());
        h.Alive = false; h.Window = false; h.ExitCode = 0;
        Assert.Equal(PollResult.Aborted, s.Poll());
        Assert.NotEqual(SupervisorState.Aborted, s.State);
        Assert.Null(s.AbortReason);
        Assert.Equal("stopped", s.Transactions[^1].Outcome);
        Assert.DoesNotContain("RECOVERY_ABORTED", Kinds(s));
    }

    [Fact]
    public void Launcher_stub_exiting_with_zero_is_not_a_failed_attempt()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        // 起動したプロセスはすぐ 0 で終わり、本体のウィンドウは 3 秒後に出る
        h.LaunchWorks = _ => true; h.AfterLaunch = () => { h.Alive = false; h.ExitCode = 0; h.Window = false; h.WindowBackAt = h.Now + 3000; h.AliveWithWindow = true; };
        h.Alive = false; h.Window = false; h.ExitCode = 0;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.Equal(1, h.Launches);
        Assert.Contains(s.Transactions[^1].Stages, x => x.Stage == "launcher_exited_ok");
    }

    private sealed class ManualHost : ISupervisorHost
    {
        public double Now { get; set; }
        public bool StopRequested => false;
        public bool Alive = true, Window = true, CanLaunchValue; public int? ExitCode; public double BackAt; public int Launches;
        public void Sleep(double ms) { Now += ms; if (Now >= BackAt) { Alive = true; Window = true; } }
        public SupervisorSignals Probe() => new(true, Alive, ExitCode, Window, false, false, true, null, true);
        public void StopActions() { }
        public void FlushExperience() { }
        public bool CheckpointDb(out string d) { d = "ok"; return true; }
        public bool FindWindow(out string d) { d = ""; return Window && Alive; }
        public bool ReconnectCapture(out string d) { d = ""; return true; }
        public bool ReconnectInput(out string d) { d = ""; return true; }
        public ProcessCheck CheckProcess(out int? c) { c = ExitCode; return Alive ? ProcessCheck.Alive : ProcessCheck.Exited; }
        public bool CanLaunch => CanLaunchValue;
        public bool Launch(out string d) { Launches++; d = ""; return false; }
        public bool WaitCapture(double t, out string d) { d = ""; return true; }
        public bool ReObserve(out string d) { d = ""; return true; }
    }

    [Fact]
    public void Static_or_unresponsive_window_is_only_suspected_never_killed()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        h.Hung = true;
        Assert.Equal(PollResult.Proceed, s.Poll());
        h.Now += 11_000;
        Assert.Equal(PollResult.Hold, s.Poll());
        Assert.Equal(SupervisorState.SuspectedHang, s.State);
        h.Now += 600_000;
        Assert.Equal(PollResult.Hold, s.Poll());
        Assert.Equal(0, h.Launches); Assert.Empty(s.Transactions);
        Assert.Equal(1, s.Events.Count(e => e.Kind == SystemEventKind.SuspectedHang));
        h.Hung = false; Assert.Equal(PollResult.Proceed, s.Poll());
        Assert.Equal(SupervisorState.Normal, s.State); Assert.Contains("HANG_CLEARED", Kinds(s));
    }

    [Fact]
    public void Db_checkpoint_failure_is_recorded_and_recovery_continues()
    {
        var h = new FakeHost { CheckpointThrows = true }; var s = new SupervisorEngine(h, Cfg());
        h.Alive = false; h.ExitCode = 0;
        Assert.Equal(PollResult.Recovered, s.Poll());
        Assert.False((bool)s.Events.First(e => e.Kind == SystemEventKind.DbCheckpoint).Data["ok"]!);
        Assert.Contains(s.Transactions[0].Stages, x => x.Stage == "db_checkpoint" && !x.Ok);
    }

    [Fact]
    public void Reobservation_failure_does_not_resume()
    {
        var h = new FakeHost { ReObserveFails = true }; var s = new SupervisorEngine(h, Cfg(max: 1));
        h.Alive = false; h.ExitCode = 0;
        Assert.Equal(PollResult.Aborted, s.Poll());
        Assert.DoesNotContain("EXPLORATION_RESUMED", Kinds(s));
        Assert.True(h.ReObserves >= 1);
    }

    [Fact]
    public void Resize_is_followed_not_treated_as_breakage()
    {
        var h = new FakeHost(); var s = new SupervisorEngine(h, Cfg());
        s.Poll(); h.W = 1280; h.H = 720;
        Assert.Equal(PollResult.Proceed, s.Poll());
        Assert.Contains("WINDOW_RESIZED", Kinds(s)); Assert.Empty(s.Transactions);
    }

    [Fact]
    public void Config_parses_with_safe_defaults_and_rejects_unknown_launch_method()
    {
        var d = new SupervisorConfig();
        Assert.Equal("none", d.LaunchMethod); Assert.False(d.CanLaunch); Assert.True(d.MaxRestarts > 0 && d.BackoffMaxSec >= d.BackoffInitialSec);
        var c = SupervisorConfig.Parse("""{ "launch_method": "steam", "steam_app_id": "12345", "max_restarts": 5, "watch_files": ["C:/x.ini"] }""");
        Assert.True(c.CanLaunch); Assert.Equal(5, c.MaxRestarts); Assert.Single(c.WatchFiles);
        Assert.False(SupervisorConfig.Parse("""{ "launch_method": "steam", "steam_app_id": "1; calc" }""").CanLaunch);
        Assert.Throws<FormatException>(() => SupervisorConfig.Parse("""{ "launch_method": "shell" }"""));
        var ex = SupervisorConfig.Parse(SupervisorConfig.Example);
        Assert.Equal("none", ex.LaunchMethod);
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, SupervisorConfig.FileName), "{ bad json");
        Assert.NotNull(SupervisorConfig.Load(dir).LoadProblem);
    }

    [Fact]
    public void Protected_files_detect_change_without_restoring()
    {
        var dir = Directory.CreateTempSubdirectory().FullName; var f = Path.Combine(dir, "settings.ini");
        File.WriteAllText(f, "a=1");
        var w = new ProtectedFiles(); w.SetBaseline(new[] { f });
        Assert.Empty(w.Changes());
        File.WriteAllText(f, "a=2");
        var ch = w.Changes(); Assert.Single(ch);
        Assert.Equal("a=2", File.ReadAllText(f));   // 戻さない
        File.Delete(f); Assert.False(w.Changes().Single().After.Exists);
    }

    [Fact]
    public void System_event_names_match_db_check_constraint()
    {
        var schema = string.Join("\n", Schemas.Memory.Select(m => m.Sql));
        foreach (var k in Enum.GetValues<SystemEventKind>()) Assert.Contains($"'{SystemEvent.KindName(k)}'", schema);
        Assert.Equal("PROCESS_EXITED", SystemEvent.KindName(SystemEventKind.ProcessExited));
        Assert.Equal("SUPERVISOR_RESTART_BEGIN", SystemEvent.KindName(SystemEventKind.SupervisorRestartBegin));
    }

    [Fact]
    public void System_events_are_stored_separately_and_not_in_experience_tables()
    {
        using var d = new TempDir();
        using var mem = ExperienceStore.Open(d["p"], "t", "0.1.0", "0.2.0");
        var s = mem;
        mem.AppendSystemEvent(1, 10, "PROCESS_EXITED", 1, "x", "{}");
        Assert.Single(mem.LoadSystemEvents());
        Assert.Equal(0L, s.Db.Scalar("SELECT COUNT(*) FROM experience_stream"));
        Assert.Equal(0L, s.Db.Scalar("SELECT COUNT(*) FROM mode_transitions"));
        Assert.ThrowsAny<Exception>(() => mem.AppendSystemEvent(1, 10, "REWARD", null, "x", "{}"));
    }

    /// <summary>報酬・価値・予測・並び・目的のコードは、システムの出来事・再起動を知らない（静的検査）</summary>
    [Fact]
    public void Restart_and_system_events_never_reach_learning_code()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GameKouryakuKun.sln"))) root = root.Parent;
        var core = Path.Combine(root!.FullName, "GameKouryakuKun.Core");
        foreach (var d in new[] { "Exploration", "Goals", "Turn", "Perception" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(core, d), "*.cs", SearchOption.AllDirectories))
                Assert.False(Regex.IsMatch(File.ReadAllText(f), @"SystemEvent|SupervisorEngine|PROCESS_EXIT|RESTART"), f);
        // 学習 DB の経験の表に system の出どころを入れる経路がない（AppendStream の行動の出どころは agent／human だけ）
        var session = File.ReadAllText(Path.Combine(root.FullName, "GameKouryakuKun.App", "Session.cs"));
        Assert.DoesNotContain("AppendStream(RunId.Value, null", session);
        Assert.Contains("prevAfter = null; prevNoChange = false; pending.Clear(); composite = null; rechoose = 0; goalComposite = false;", session);
    }

    [Fact]
    public void Separator_lines_are_display_only()
    {
        var sep = Viewer.StepSummary.Separator(5, "PROCESS EXIT");
        Assert.Equal("──── PROCESS EXIT ────", sep.Line1);
        Assert.Equal("", sep.Line2);
        Assert.Null(sep.Detail);
    }

    [Fact]
    public void Human_queue_is_discarded_but_mode_kept()
    {
        var hc = new Control.HumanControl(new[] { "a" });
        hc.SetEnabled(true); hc.TakeSwitch(); hc.Enqueue("a"); hc.Enqueue("a");
        Assert.Equal(2, hc.DiscardQueued());
        Assert.True(hc.Enabled); Assert.False(hc.TryDequeue(out _)); Assert.False(hc.TakeSwitch());
    }

    [Fact]
    public void Health_report_rates_supervisor_items()
    {
        var checks = new List<(string, string, HealthReport.Level, string)>();
        var ok = System.Text.Json.JsonDocument.Parse("""{"t":"x","supervisor_state":"NORMAL","sup_exits":1,"sup_restarts":1,"sup_recoveries_ok":1,"sup_resumed_reobserved":1,"sup_db_checkpoints_ok":1,"sup_transactions":[{"id":1,"reason":"PROCESS_EXIT","outcome":"resumed","attempts":1,"stages":["a:ok"]}]}""").RootElement;
        HealthReport.AddSupervisor(ok, (a, n, l, d) => checks.Add((a, n, l, d)));
        Assert.DoesNotContain(checks, c => c.Item3 is HealthReport.Level.Fail or HealthReport.Level.Warn);
        checks.Clear();
        var bad = System.Text.Json.JsonDocument.Parse("""{"t":"x","supervisor_state":"RECOVERY_ABORTED","supervisor_abort_reason":"RESTART_LOOP","sup_restarts":3,"sup_breaker_trips":1,"sup_recoveries_failed":1,"sup_transactions":[{"id":1,"outcome":"aborted"}]}""").RootElement;
        HealthReport.AddSupervisor(bad, (a, n, l, d) => checks.Add((a, n, l, d)));
        Assert.Contains(checks, c => c.Item2.StartsWith("状態") && c.Item3 == HealthReport.Level.Fail);
        Assert.Contains(checks, c => c.Item2.StartsWith("回路") && c.Item3 == HealthReport.Level.Warn);
    }
}
