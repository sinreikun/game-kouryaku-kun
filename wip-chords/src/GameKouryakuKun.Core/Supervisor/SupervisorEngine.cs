namespace GameKouryakuKun.Core.Supervisor;

/// <summary>Supervisor が 1 回に見る OS の信号（ゲームの中身は見ない）</summary>
public sealed record SupervisorSignals(
    bool ProcessKnown, bool ProcessAlive, int? ExitCode,
    bool WindowValid, bool HandleChanged, bool WindowHung,
    bool CaptureOk, string? CaptureError, bool InputOk,
    int Width = 0, int Height = 0);

public enum ProcessCheck { Alive, Exited, Unknown }

/// <summary>
/// Supervisor が OS とアプリに対して行うこと（実装は Windows 側。テストでは偽物）。どれもゲームの中身に依らない。
/// 攻略 AI の操作（中立のキー入力）とは別の権限で、AI からは呼べない。
/// </summary>
public interface ISupervisorHost
{
    double Now { get; }
    bool StopRequested { get; }
    void Sleep(double ms);
    SupervisorSignals Probe();
    /// <summary>手を止める（押しているキーを離す。以後の送信を止める）</summary>
    void StopActions();
    /// <summary>送る前の手・並び・目的・計画・小目的・予測の前提を捨てる（古い状態を前提にしない）。経験の記録は残す</summary>
    void FlushExperience();
    bool CheckpointDb(out string detail);
    /// <summary>L1: ウィンドウを探し直す（ハンドルが変わっていてもよい）</summary>
    bool FindWindow(out string detail);
    /// <summary>L2: 取得と入力をつなぎ直す</summary>
    bool ReconnectCapture(out string detail);
    bool ReconnectInput(out string detail);
    /// <summary>L3: プロセスを確かめる（終了コードが分かれば返す）</summary>
    ProcessCheck CheckProcess(out int? exitCode);
    bool CanLaunch { get; }
    /// <summary>L4: 設定の方法で起動する（はっきり終了したときだけ呼ばれる）</summary>
    bool Launch(out string detail);
    bool WaitCapture(double timeoutMs, out string detail);
    /// <summary>今の画面を通常の状態の認識で観測し直す（前と同じ状態だとは仮定しない）</summary>
    bool ReObserve(out string detail);
}

public enum SupervisorState { Normal, SuspectedHang, Recovering, Aborted }

/// <summary>1 回の復旧（終了 → 再起動 → 取得 → 観測し直し → 再開）の記録。診断 zip だけで成否が分かるように段ごとに残す</summary>
public sealed class RecoveryTransaction
{
    public long Id { get; init; }
    public string Reason { get; init; } = "";
    public double StartedMs { get; init; }
    public double? EndedMs { get; set; }
    public List<(string Stage, double AtMs, bool Ok, string Detail)> Stages { get; } = new();
    public int Attempts { get; set; }
    public string Outcome { get; set; } = "running";   // running／resumed／failed／aborted
}

public enum PollResult { Proceed, Hold, Recovered, Aborted }

/// <summary>
/// Game Supervisor（v0.21.0。src/supervisor-design.md）。プロセスとウィンドウを観測でき操作できる状態に保つことだけをする。
/// ゲームの中身（タイトル画面・設定画面・死亡・クリアなど）は知らず、判断にも使わない。再起動は AI の行動ではない。
/// 段階的な復旧: L1 ウィンドウを探し直す → L2 取得と入力をつなぎ直す → L3 プロセスを確かめる → L4 はっきり終了したときだけ再起動。
/// 画面が止まっていることだけでは終了させない（未知の静止の状態かもしれない）。OS が応答なしと言い続けても記録と警告だけで、終了はさせない。
/// </summary>
public sealed class SupervisorEngine
{
    private readonly ISupervisorHost _h;
    private readonly SupervisorConfig _c;
    private readonly List<double> _restartTimes = new();
    private double? _windowBadSince, _captureBadSince, _hungSince;
    private bool _hangWarned;
    private long _nextTx = 1;
    private int _lastW, _lastH;

    public SupervisorState State { get; private set; } = SupervisorState.Normal;
    public List<SystemEvent> Events { get; } = new();
    public List<RecoveryTransaction> Transactions { get; } = new();
    public RecoveryTransaction? Current { get; private set; }
    public string? CurrentStage { get; private set; }
    public string? AbortReason { get; private set; }
    public string? LastInterruption { get; private set; }
    public int Restarts { get; private set; }
    public int ProcessExits { get; private set; }
    public int AbnormalExits { get; private set; }
    public int RecoveriesOk { get; private set; }
    public int RecoveriesFailed { get; private set; }
    public int BreakerTrips { get; private set; }
    public int CaptureReconnects { get; private set; }
    public int InputReconnects { get; private set; }
    public int SuspectedHangs { get; private set; }
    public double ObservationLostMs { get; private set; }
    public event Action<SystemEvent>? EventRaised;

    public SupervisorEngine(ISupervisorHost host, SupervisorConfig config) { _h = host; _c = config; }

    public string StatusText => State switch
    {
        SupervisorState.Recovering => $"Supervisor: RECOVERING（{Current?.Reason}・試行 {Current?.Attempts}/{_c.MaxRestarts}・{CurrentStage}）",
        SupervisorState.Aborted => $"Supervisor: RECOVERY_ABORTED（{AbortReason}）",
        SupervisorState.SuspectedHang => $"Supervisor: SUSPECTED_HANG（終了させずに見守り中）／Restart: {Restarts}",
        _ => $"Supervisor: NORMAL／Restart: {Restarts}／Recovery: {(Transactions.Count == 0 ? "-" : Transactions[^1].Outcome)}／Last interruption: {LastInterruption ?? "-"}",
    };

    private void Raise(SystemEventKind k, string detail, params (string, object?)[] data)
    {
        var e = new SystemEvent(k, _h.Now, Current?.Id, detail, data.ToDictionary(x => x.Item1, x => x.Item2));
        Events.Add(e); EventRaised?.Invoke(e);
    }

    private void Stage(string name, bool ok, string detail) { CurrentStage = name; Current?.Stages.Add((name, _h.Now, ok, detail)); }

    /// <summary>手の前に呼ぶ。Proceed＝そのまま続ける・Hold＝この手は送らない（応答なしの疑い）・Recovered＝復旧した（観測し直し済み）・Aborted＝止めて人を待つ</summary>
    public PollResult Poll()
    {
        if (State == SupervisorState.Aborted) return PollResult.Aborted;
        var s = _h.Probe(); double now = _h.Now;
        if (s.WindowValid && s.Width > 0 && (_lastW, _lastH) != (0, 0) && (s.Width, s.Height) != (_lastW, _lastH))
            Raise(SystemEventKind.WindowResized, $"{_lastW}x{_lastH} → {s.Width}x{s.Height}（故障ではない。そのまま追う）", ("w", s.Width), ("h", s.Height));
        if (s.WindowValid && s.Width > 0) (_lastW, _lastH) = (s.Width, s.Height);
        // はっきりした終了
        if (s.ProcessKnown && !s.ProcessAlive) return Recover("PROCESS_EXIT", s.ExitCode, processGone: true);
        // ウィンドウの消失（猶予の後）
        if (!s.WindowValid || s.HandleChanged)
        {
            _windowBadSince ??= now;
            if (s.HandleChanged || now - _windowBadSince >= _c.WindowLossGraceSec * 1000) return Recover(s.HandleChanged ? "WINDOW_HANDLE_CHANGED" : "WINDOW_LOST", null, processGone: false);
            return PollResult.Hold;
        }
        _windowBadSince = null;
        // 取得の途切れ（猶予の後）
        if (!s.CaptureOk || !s.InputOk)
        {
            _captureBadSince ??= now;
            if (now - _captureBadSince >= _c.CaptureLossGraceSec * 1000) return Recover(!s.CaptureOk ? "CAPTURE_LOST" : "INPUT_LOST", null, processGone: false);
            return PollResult.Hold;
        }
        _captureBadSince = null;
        // 応答なしの疑い: OS の信号が続くときだけ。記録と警告だけで、終了も再起動もしない
        if (s.WindowHung)
        {
            _hungSince ??= now;
            if (now - _hungSince >= _c.HangSuspectSec * 1000)
            {
                if (!_hangWarned) { _hangWarned = true; SuspectedHangs++; State = SupervisorState.SuspectedHang; Raise(SystemEventKind.SuspectedHang, "ウィンドウが応答しない状態が続いています（終了させずに記録と警告だけ。画面の静止だけでは判断しない）"); }
                return PollResult.Hold;
            }
            return PollResult.Proceed;
        }
        if (_hangWarned) { _hangWarned = false; State = SupervisorState.Normal; Raise(SystemEventKind.HangCleared, "応答が戻りました"); }
        _hungSince = null;
        return PollResult.Proceed;
    }

    /// <summary>外から（AI の例外など）問題を知らせる。復旧はせず、記録するだけ</summary>
    public void ReportAiException(string detail) => Raise(SystemEventKind.AiException, detail);

    private readonly List<double> _inputRecoveries = new();
    public int InputLosses { get; private set; }

    private PollResult Recover(string reason, int? exitCode, bool processGone)
    {
        if (reason == "INPUT_LOST")
        {
            // v0.23.1: 入力の方式の失敗は Supervisor の信号。つなぎ直しを繰り返しても直らないなら人を待つ（再起動と同じ上限と時間の窓）
            InputLosses++;
            double n0 = _h.Now; _inputRecoveries.RemoveAll(t => n0 - t > _c.RestartWindowSec * 1000);
            if (_inputRecoveries.Count >= _c.MaxRestarts)
            {
                var tx0 = new RecoveryTransaction { Id = _nextTx++, Reason = reason, StartedMs = n0 }; Transactions.Add(tx0); Current = tx0; State = SupervisorState.Recovering; LastInterruption = reason;
                Raise(SystemEventKind.ObservationLost, reason); _h.StopActions(); _h.FlushExperience();
                bool c0 = _h.CheckpointDb(out var d0); Raise(SystemEventKind.DbCheckpoint, c0 ? "ok" : d0, ("ok", c0));
                return Failed(tx0, n0, $"入力のつなぎ直しが {_c.RestartWindowSec / 60:0} 分の間に {_inputRecoveries.Count} 回", abort: true, abortReason: "INPUT_LOOP");
            }
            _inputRecoveries.Add(n0);
        }
        var tx = new RecoveryTransaction { Id = _nextTx++, Reason = reason, StartedMs = _h.Now };
        Transactions.Add(tx); Current = tx; State = SupervisorState.Recovering; LastInterruption = reason;
        _windowBadSince = _captureBadSince = _hungSince = null; _hangWarned = false;
        double t0 = _h.Now;
        Raise(SystemEventKind.ObservationLost, reason);
        _h.StopActions(); Stage("stop_actions", true, "");
        _h.FlushExperience(); Stage("flush_experience", true, "送る前の手・並び・目的・計画・予測の前提を捨てた");
        bool cp = _h.CheckpointDb(out var cpd); Stage("db_checkpoint", cp, cpd); Raise(SystemEventKind.DbCheckpoint, cp ? "ok" : cpd, ("ok", cp));
        try
        {
            if (!processGone)
            {
                if (reason is "WINDOW_LOST" or "WINDOW_HANDLE_CHANGED") Raise(reason == "WINDOW_LOST" ? SystemEventKind.WindowLost : SystemEventKind.WindowHandleChanged, reason);
                if (reason == "CAPTURE_LOST") Raise(SystemEventKind.CaptureLost, reason);
                if (reason == "INPUT_LOST") Stage("input_lost", false, "入力の方式が続けて失敗した");
                // L1 → L2（プロセスが生きていればここで戻ることが多い）
                if (TryReattach(out var why)) return Resumed(tx, t0);
                // L3: プロセスを確かめる
                var pc = _h.CheckProcess(out var code); Stage("L3_check_process", pc != ProcessCheck.Exited, pc.ToString());
                if (pc != ProcessCheck.Exited)
                {
                    // 生きている（または分からない）ときは終了させず、ウィンドウが戻るのを待つ
                    double until = _h.Now + _c.WindowTimeoutSec * 1000;
                    while (_h.Now < until && !_h.StopRequested) { _h.Sleep(1000); if (TryReattach(out why)) return Resumed(tx, t0); }
                    if (_h.StopRequested) return Stopped(tx, t0);
                    // v0.23.1: ウィンドウが戻らない。起動の方法があれば、終了させずに起動し直す（Steam・ゲームが前のプロセスを前面に出す／新しく起動する）。なければ人を待つ
                    if (_c.AutoRestart && _h.CanLaunch && reason is "WINDOW_LOST" or "WINDOW_HANDLE_CHANGED" or "CAPTURE_LOST")
                    {
                        Stage("relaunch_without_exit", true, $"プロセス {pc}・ウィンドウが {_c.WindowTimeoutSec:0} 秒戻らないので起動し直す（終了はさせない）: {why}");
                        return Restart(tx, t0);
                    }
                    return Failed(tx, t0, "プロセスは終了していないが、ウィンドウ・取得が戻らない（終了させない）: " + why, abort: true, abortReason: "WINDOW_NOT_RESTORED");
                }
                exitCode = code;
            }
            // はっきり終了した
            var cls = ExitClassifier.Classify(exitCode);
            ProcessExits++; if (cls is ExitClass.Abnormal or ExitClass.Crash) AbnormalExits++;
            Raise(SystemEventKind.ProcessExited, $"終了コード {(exitCode is int ec ? $"0x{unchecked((uint)ec):X8}" : "不明")}・分類 {ExitClassifier.Id(cls)}（OS の上の分類。ゲームの中の出来事とは結びつけない）", ("exit_code", exitCode), ("class", ExitClassifier.Id(cls)));
            Stage("process_exited", true, ExitClassifier.Id(cls));
            return Restart(tx, t0);
        }
        catch (Exception e) { return Failed(tx, t0, "復旧の途中の例外: " + e.Message, abort: true, abortReason: "RECOVERY_EXCEPTION"); }
    }

    private bool TryReattach(out string why)
    {
        why = "";
        if (!_h.FindWindow(out var fw)) { Stage("L1_find_window", false, fw); why = fw; return false; }
        Stage("L1_find_window", true, fw); Raise(SystemEventKind.WindowAcquired, fw);
        if (!_h.ReconnectCapture(out var rc)) { Stage("L2_capture", false, rc); why = rc; return false; }
        CaptureReconnects++; Stage("L2_capture", true, rc);
        if (!_h.ReconnectInput(out var ri)) { Stage("L2_input", false, ri); why = ri; return false; }
        InputReconnects++; Stage("L2_input", true, ri); Raise(SystemEventKind.InputReconnected, ri);
        if (!_h.WaitCapture(_c.CaptureTimeoutSec * 1000, out var wc)) { Stage("capture_wait", false, wc); why = wc; return false; }
        Stage("capture_wait", true, wc); Raise(SystemEventKind.CaptureRestored, wc);
        if (!_h.ReObserve(out var ro)) { Stage("re_observe", false, ro); why = ro; return false; }
        Stage("re_observe", true, ro); Raise(SystemEventKind.ObservationRestored, ro);
        return true;
    }

    private PollResult Restart(RecoveryTransaction tx, double t0)
    {
        if (!_c.AutoRestart || !_h.CanLaunch)
        {
            // 起動の方法がない: 人が起動し直すのを待つ（ウィンドウが戻れば続ける）
            Stage("wait_manual_restart", true, "起動の方法が設定されていないので、人が起動し直すのを待ちます");
            double until = _h.Now + _c.ManualRestartWaitSec * 1000;
            bool enabled = false;
            while (_h.Now < until && !_h.StopRequested)
            {
                _h.Sleep(2000);
                if (TryReattach(out _)) return Resumed(tx, t0);
                // v0.23.1 実機: 待っている間に画面で自動で起動し直すを ON にしたら、そのまま起動に進む
                if (_c.AutoRestart && _h.CanLaunch) { enabled = true; Stage("launch_enabled_while_waiting", true, "待っている間に起動の方法が設定された"); break; }
            }
            if (_h.StopRequested) return Stopped(tx, t0);
            if (!enabled) return Failed(tx, t0, "起動し直されなかった", abort: true, abortReason: "MANUAL_RESTART_TIMEOUT");
        }
        while (!_h.StopRequested)
        {
            // 回路遮断: 時間の窓の中の再起動の回数
            double now = _h.Now;
            _restartTimes.RemoveAll(t => now - t > _c.RestartWindowSec * 1000);
            if (_restartTimes.Count >= _c.MaxRestarts) { BreakerTrips++; return Failed(tx, t0, $"{_c.RestartWindowSec / 60:0} 分の間に再起動が {_restartTimes.Count} 回", abort: true, abortReason: "RESTART_LOOP"); }
            double wait = Math.Min(_c.BackoffMaxSec, _c.BackoffInitialSec * Math.Pow(2, _restartTimes.Count)) * 1000;
            Stage("backoff", true, $"{wait / 1000:0} 秒待つ");
            double until = _h.Now + wait; while (_h.Now < until && !_h.StopRequested) _h.Sleep(Math.Min(1000, until - _h.Now));
            if (_h.StopRequested) break;
            _restartTimes.Add(_h.Now); tx.Attempts++; Restarts++;
            Raise(SystemEventKind.SupervisorRestartBegin, $"試行 {tx.Attempts}", ("attempt", tx.Attempts), ("max", _c.MaxRestarts));
            if (!_h.Launch(out var ld)) { Stage("launch", false, ld); Raise(SystemEventKind.RecoveryFailed, "起動できない: " + ld, ("attempt", tx.Attempts)); continue; }
            Stage("launch", true, ld); Raise(SystemEventKind.ProcessStarted, ld);
            double wUntil = _h.Now + (_c.StartupTimeoutSec + _c.WindowTimeoutSec) * 1000; string why = "起動の後にウィンドウが出ない"; bool launcherExited = false;
            while (_h.Now < wUntil && !_h.StopRequested)
            {
                if (TryReattach(out why))
                {
                    // v0.23.1: 起動したゲームのプロセスを見張っているか（Steam 経由はウィンドウのプロセスを見張る）
                    var tp = _h.CheckProcess(out _); Stage("process_tracked", tp == ProcessCheck.Alive, tp.ToString());
                    return Resumed(tx, t0);
                }
                // v0.23.3 実機: 起動用の小さな実行ファイルが本体を起動して正常に終わることがある（終了コード 0）。そのときはウィンドウが出るのを待ち続ける
                if (_h.CheckProcess(out var lc) == ProcessCheck.Exited)
                {
                    if (lc == 0 && !launcherExited) { launcherExited = true; Stage("launcher_exited_ok", true, "起動した実行ファイルが終了コード 0 で終わった（本体を起動した可能性）。ウィンドウを待つ"); }
                    else if (lc != 0) { why = $"起動した直後に終了した（終了コード {lc?.ToString() ?? "不明"}）"; break; }
                }
                _h.Sleep(1000);
            }
            Stage("reattach", false, why); Raise(SystemEventKind.RecoveryFailed, "起動の後に戻らない: " + why, ("attempt", tx.Attempts));
        }
        return Stopped(tx, t0);
    }

    private PollResult Resumed(RecoveryTransaction tx, double t0)
    {
        ObservationLostMs += _h.Now - t0;
        tx.Outcome = "resumed"; tx.EndedMs = _h.Now; RecoveriesOk++;
        Raise(SystemEventKind.ExplorationResumed, $"観測を失っていた時間 {(_h.Now - t0) / 1000:0.0} 秒", ("lost_ms", Math.Round(_h.Now - t0)));
        Current = null; CurrentStage = null; State = SupervisorState.Normal;
        return PollResult.Recovered;
    }

    /// <summary>v0.23.1 実機: 復旧の途中で人が停止した。失敗・回路遮断ではない（診断 zip の自動の書き出しもしない）</summary>
    public int RecoveriesStopped { get; private set; }
    private PollResult Stopped(RecoveryTransaction tx, double t0)
    {
        ObservationLostMs += _h.Now - t0;
        tx.Outcome = "stopped"; tx.EndedMs = _h.Now; RecoveriesStopped++;
        Stage("stop_requested", true, "人が停止した");
        Current = null; CurrentStage = null; State = SupervisorState.Normal;
        return PollResult.Aborted;
    }

    private PollResult Failed(RecoveryTransaction tx, double t0, string detail, bool abort, string abortReason)
    {
        ObservationLostMs += _h.Now - t0;
        tx.Outcome = abort ? "aborted" : "failed"; tx.EndedMs = _h.Now; RecoveriesFailed++;
        Raise(SystemEventKind.RecoveryFailed, detail);
        if (abort) { State = SupervisorState.Aborted; AbortReason = abortReason; Raise(SystemEventKind.RecoveryAborted, $"{abortReason}: {detail}（探索と再起動を止め、人を待ちます）", ("reason", abortReason)); }
        Current = null; CurrentStage = null;
        return abort ? PollResult.Aborted : PollResult.Proceed;
    }
}
