using GameKouryakuKun.Core.Goals;
using GameKouryakuKun.Core.Growth;
using GameKouryakuKun.Core.Activity;
using GameKouryakuKun.Core.Capture;
using GameKouryakuKun.Core.Control;
using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Input;
using GameKouryakuKun.Core.Perception;
using GameKouryakuKun.Core.Probe;
using GameKouryakuKun.Core.Supervisor;
using GameKouryakuKun.Core.Profile;
using GameKouryakuKun.Supervisor;
using GameKouryakuKun.Core.Turn;
using GameKouryakuKun.Core.Viewer;
using GameKouryakuKun.Storage;
using GameKouryakuKun.Windows;

namespace GameKouryakuKun.App;

public enum LogLevel { Info, Ok, Warn, Error, Dim }

/// <summary>ゲームウィンドウへの接続（取得だけ。入力はしない）。ライブ画面とセッションが共有する。</summary>
public sealed class GameConnection : IDisposable
{
    public WindowInfo Window { get; }
    public CaptureSupervisor Capture { get; }
    public IReadOnlyList<CaptureProbeResult> Probes { get; }
    public nint Hwnd => Window.Hwnd;

    private GameConnection(WindowInfo w, CaptureSupervisor cap, IReadOnlyList<CaptureProbeResult> probes) { Window = w; Capture = cap; Probes = probes; }

    /// <summary>ウィンドウを探し、取得方式をプローブして最良の方式で取得を始める（数秒かかる。UI スレッドで呼ばない）</summary>
    public static GameConnection? Connect(AppEnv env, Action<string, LogLevel> log, WindowMatch? match = null)
    {
        match ??= env.Profile.Window;
        var found = WindowLocator.Find(match);
        env.Log.Write("window_search", ("match", match.ToString()), ("found", found.Select(f => f.ToString()).ToList()));
        if (found.Count == 0) { log($"ゲームウィンドウが見つかりません（条件: {match}）。ゲームを起動してから「ゲームに接続」を押してください。", LogLevel.Warn); return null; }
        if (found.Count > 1) log($"条件に合うウィンドウが {found.Count} 個あります。最初のものを使います: {found[0]}", LogLevel.Warn);
        var w = found[0];
        log("ゲームウィンドウ: " + w, LogLevel.Ok);

        var wgc = WgcCaptureBackend.Supported();
        var candidates = new List<ICaptureBackend>();
        if (wgc.ok) candidates.Add(new WgcCaptureBackend()); else log($"WGC 非対応: {wgc.detail}", LogLevel.Warn);
        candidates.Add(new PrintWindowCaptureBackend());
        var probes = new List<CaptureProbeResult>();
        foreach (var b in candidates)
        {
            CaptureProbeResult r;
            try
            {
                b.Start(w.Hwnd);
                var last = b.WaitForNewFrame(0, 2500);
                var copy = new List<double>(); double t0 = MonoClock.Now;
                while (MonoClock.Now - t0 < 1200)
                {
                    var f = b.WaitForNewFrame(last?.Id ?? 0, 300);
                    if (f != null) { last = f; copy.Add(b is WgcCaptureBackend wb ? wb.LastCopyMs : b is PrintWindowCaptureBackend pb ? pb.LastCopyMs : 0); }
                }
                var st = last?.ComputeStats();
                r = new CaptureProbeResult(b.Id, last != null, b.FpsOver(1200), Percentile.Of(copy, 50), st?.LooksBlack == true ? 1 : 0, last?.Width ?? 0, last?.Height ?? 0,
                    last == null ? "フレームなし" + (WindowLocator.IsMinimized(w.Hwnd) ? "（最小化中）" : "") : b.Notes);
            }
            catch (Exception e) { r = new CaptureProbeResult(b.Id, false, 0, double.NaN, 1, 0, 0, $"{e.GetType().Name}: {e.Message}"); }
            finally { try { b.Stop(); } catch { } }
            probes.Add(r);
            log($"取得 {b.DisplayName}: {(r.Usable ? "使用可" : "使用不可")} {r.Width}x{r.Height} {r.Fps:0.#}fps — {r.Detail}", r.Usable ? LogLevel.Ok : LogLevel.Warn);
            env.Log.Write("capture_probe", ("backend", r.BackendId), ("usable", r.Usable), ("fps", r.Fps), ("w", r.Width), ("h", r.Height), ("detail", r.Detail));
            try
            {
                env.AppDb.RecordProbe(new ProbeRecord("connect", "capture", r.BackendId, env.Profile.ProfileId, w.ProcessName, w.ClassName, r.Available, r.Usable ? null : r.Detail,
                    null, r.Usable, null, r.Fps, r.BlackRatio, null, null, null, null, null, null, null, Json.Obj(("copy_ms_p50", r.CopyMsP50), ("w", r.Width), ("h", r.Height), ("detail", r.Detail))));
            }
            catch (Exception e) { log("app.sqlite への記録に失敗: " + e.Message, LogLevel.Warn); }
        }
        string? preferred = CaptureBackendSelector.SelectAuto(probes, new[] { "wgc", "printwindow" })?.BackendId;
        var cap = new CaptureSupervisor(w.Hwnd, candidates);
        cap.Log += s => env.Log.Write("capture_log", ("msg", s));
        if (cap.Start(preferred) == null && !WindowLocator.IsMinimized(w.Hwnd))
        {
            log("どの方式でもゲーム画面を取得できません。", LogLevel.Error);
            cap.Dispose();
            return null;
        }
        log($"取得開始: {cap.Current?.DisplayName}", LogLevel.Ok);
        return new GameConnection(w, cap, probes);
    }

    public void Dispose() => Capture.Dispose();
}

public sealed class PlaySessionOptions
{
    /// <summary>v0.25.2: 画面の文字の観測（意味は解釈しない。区切りの候補・場面を人が確かめる材料。報酬・選択には使わない）。null なら読まない</summary>
    public GameKouryakuKun.Core.Ocr.OcrEngineChain? Ocr { get; init; }
    public bool AllowSendInput { get; init; }
    /// <summary>手探り探索の手数の上限（0 = 上限なし）。時間の上限 TimeLimitSec と併用できる</summary>
    public int MaxSteps { get; init; }
    /// <summary>手探り探索の時間の上限（秒。0 = 上限なし）</summary>
    public double TimeLimitSec { get; init; }
    public int Seed { get; init; } = Environment.TickCount & 0x7FFFFFFF;
    /// <summary>v0.19.5: Human Control で始める（自動の探索を動かさずに人間が操作する）。入力・観測・記録の経路は同じで、行動の出どころは human。Human Control を OFF にすれば自動の探索に移る</summary>
    public bool StartWithHumanControl { get; init; }
    public double QuietMs { get; init; } = 5000;
    /// <summary>自己生成の一時的な目的（v0.16.0。経験から作る到達・確かめ直しだけ。直近の手の 3 割まで）</summary>
    public bool SelfGoals { get; init; } = true;
    /// <summary>v0.22.0 成長テスト: 長い実行・起動をまたぐ実行の計測を細かく取る（計測の間隔を短くするだけ。探索の手順は同じ）</summary>
    public bool GrowthTest { get; init; }
}

public enum SessionState { Idle, Preparing, Running, Paused, Stopping, Stopped }

/// <summary>1手分の「観測→行動→結果」</summary>
public sealed record TurnLogItem(DateTime At, int Step, string Op, string Action, string Reason, TurnKind Kind, double BoundaryMs, double? FirstChangeMs,
    bool Changed, string? Note, long BaselineFrameId, int Width, int Height, long? TurnId, string? Node = null);

public sealed record CandidateView(string Name, string Actions, int Weight, double Probability);

/// <summary>
/// 1回のプレイセッション。較正 → 入力方式の送達の確認（A: OS・入力方式として送れるか。キーは送らない）→ 手探り探索。
/// 手探り探索の各手は memory.sqlite に記録する。入力の視覚的な反応（B）は未知の状態の性質かもしれないので、数えるだけで止めない（v0.15.0）。
/// 特定のキーの往復で画面が変わって戻るといった、特定の状態の性質を使う診断（旧 互換性テスト・分離試験）は v0.15.0 で廃止した。
/// </summary>
public sealed class PlaySession
{
    public readonly GameKouryakuKun.Core.Observation.AppearanceTable AppTable = new();
    public readonly GameKouryakuKun.Core.Observation.EntityExperience EntityExp = new();
    private GameKouryakuKun.Core.Observation.DelayedChange? _pendingDelayed;
    /// <summary>v0.32.5: 人が入力を止めていた自律探索の手の数（Health）</summary>
    public long UserRestrictedSteps;
    private long _lastRestrictedStep = -1;
    private int _noOffsetSamples;
    /// <summary>v0.32.5: 使える入力がなく自律探索を一時停止している</summary>
    public volatile bool AllDisabledWaiting;
    private bool _allDisabledNotified;
    public long DelayedRecorded;
    public readonly GameKouryakuKun.Core.Observation.NumberDeltaTable NumTable = new();
    public readonly GameKouryakuKun.Core.Observation.RegionCovariation Covariation = new(GameKouryakuKun.Core.Ocr.TextReader.GridCols * GameKouryakuKun.Core.Ocr.TextReader.GridRows);
    private string TrackFrag()
    {
        var (mean, med, n) = Entities.Lifetimes();
        return $"追跡の寿命（候補として見えた手数） 平均 {mean}・中央値 {med}（{n} 本）・新しい追跡 1 手あたり {(Entities.Steps == 0 ? 0 : Math.Round((double)Entities.Created / Entities.Steps, 2))}・再同定 {Entities.Reidentified}・まとまりの中の番号の入れ替わり {Entities.GroupSwitches}・割れた塊をまとめた {Entities.FragmentsMerged}・学んだ確かな続き {Entities.Evidence.SameN - 1:0}";
    }
    private string CovarySummary()
    {
        var g = Covariation.Groups();
        string F(string k) { var x = g.Where(z => z.Kind == k).ToList(); return $"{x.Count} 群（最大 {(x.Count == 0 ? 0 : x.Max(z => z.Cells.Length))} 区画・離れた区画を含む {x.Count(z => !Adjacent(z.Cells))}）"; }
        static bool Adjacent(int[] c) { var s = c.ToHashSet(); int W = GameKouryakuKun.Core.Ocr.TextReader.GridCols; var seen = new HashSet<int> { c[0] }; var q = new Queue<int>(seen); while (q.Count > 0) { int v = q.Dequeue(); foreach (var n in new[] { v - 1, v + 1, v - W, v + W }) if (s.Contains(n) && seen.Add(n)) q.Enqueue(n); } return seen.Count == c.Length; }
        return $"手で変わる {F("action")}／入力なしで変わる {F("natural")}";
    }
    private readonly AppEnv _env;
    private GameConnection _con;
    private readonly PlaySessionOptions _o;
    private readonly StandInWindow _standIn;
    private readonly PauseController _pause = new();
    private Thread? _thread;
    private volatile bool _quit;
    private IInputBackend? _active;
    private List<IInputBackend> _inputs = new();

    public event Action<string, LogLevel>? Log;
    public event Action<TurnLogItem>? Turn;
    public event Action? Changed;

    public SessionState State { get; private set; } = SessionState.Idle;
    public string Phase { get; private set; } = "待機";
    public string? StopReason { get; private set; }
    public bool EmergencyStopped => _pause.IsEmergencyStopped;
    /// <summary>手探り探索の記録（memory.sqlite の runs）</summary>
    public long? RunId { get; private set; }
    public int Step { get; private set; }
    public string? InputName { get; private set; }
    public string? InputBadge { get; private set; }
    public IsolationLevel? InputLevel { get; private set; }
    public TurnLogItem? Last { get; private set; }
    public IReadOnlyList<CandidateView> Candidates { get; private set; } = Array.Empty<CandidateView>();
    public CalibrationResult? Calibration { get; private set; }
    public int NoChangeRun { get; private set; }
    public int UserEventsPausing, UserEventsIgnored;
    /// <summary>ユーザーの作業との分離の集計（1手ごとの前面状態・一時停止の回数。手探り探索の中で数える）</summary>
    public SeparationStats Separation { get; } = new();
    private readonly System.Diagnostics.Stopwatch _runClock = new();
    public string? LastScreenPath { get; private set; }
    /// <summary>入力の送信そのもの（A）が続けて失敗して止まったか（システムの失敗）</summary>
    public bool InputDeliveryFailed { get; private set; }
    /// <summary>入力の A（送達）と B（視覚的な反応）の集計</summary>
    public InputDeliveryMonitor Delivery { get; } = new();
    /// <summary>Human Control（人間が攻略くんの入力経路で操作する）。探索の開始後に作る</summary>
    public HumanControl? Human { get; private set; }
    /// <summary>1手サマリーの履歴と成長の計器（表示だけ）</summary>
    public StepHistory StepHistory { get; } = new();
    public GrowthTracker Growth { get; } = new();
    private GrowthMeters? _meters;
    public GrowthMeters? Meters => Volatile.Read(ref _meters);
    public int HumanSteps { get; private set; }
    public int AgentSteps { get; private set; }
    /// <summary>画面ごとの時間的な変化の性質（表示用の要約。観測は記録そのもの）</summary>
    private IReadOnlyList<TemporalProfile> _temporal = Array.Empty<TemporalProfile>();
    public IReadOnlyList<TemporalProfile> Temporal => Volatile.Read(ref _temporal);
    public int NoReactionWarnings { get; private set; }
    public int PatternEvents { get; private set; }
    /// <summary>連続履歴の境界の候補の数（この回）</summary>
    public int SequenceBoundaries { get; private set; }
    /// <summary>「変化なし」の判定の後、次の入力の前に画面が変わった回数（判定が早すぎた。システムの健全性の指標）</summary>
    public int LateChanges { get; private set; }
    /// <summary>直前の手の判断の記録（Viewer の「なぜこの操作か」。点数の内訳と予測・結果の比較）</summary>
    public DecisionRecord? LastDecision { get; private set; }
    /// <summary>この回の予測の外れによる内部モデルの修正（新しい順に最大 10 件）</summary>
    public IReadOnlyList<ModelRevision> RecentRevisions { get { lock (_recentRevisions) return _recentRevisions.ToArray(); } }
    private readonly LinkedList<ModelRevision> _recentRevisions = new();
    /// <summary>新発見の一覧（表示だけ。選択・学習には使わない。記録そのものは各表にある）</summary>
    public DiscoveryLog Discoveries { get; } = new();
    private IReadOnlyList<Hypothesis> _hypotheses = Array.Empty<Hypothesis>();
    /// <summary>仮説の板（表示だけ。HypothesisRefreshSteps 手ごとに内部モデル・分割・並びの記録から作り直す）</summary>
    public IReadOnlyList<Hypothesis> Hypotheses => Volatile.Read(ref _hypotheses);
    private const int HypothesisRefreshSteps = 10;   // 根拠: 汎用（表示の更新の間隔。毎手まとめ直すと関係の数に比例して時間がかかる）
    private long _lastSeq;
    /// <summary>v0.33.3: フラグ ON のときだけ experience-vnext へ併記する空シェル（学習は Memory のまま）</summary>
    private RuntimeKernel? _vnextKernel;
    public int PredictionChecks { get; private set; }
    public int PredictionHits { get; private set; }
    public int ModelRevisions { get; private set; }
    public int AmbiguousObservations { get; private set; }
    public int TimeoutTurns { get; private set; }
    /// <summary>動き続ける所の性質を数えている画面の種類の数（学習 DB に残る）</summary>
    public int MotionScreens { get; private set; }
    /// <summary>判定から外した所（起動時の較正・常時アニメのマスク）の中の変化も観測として残す: 外した所があった手・その中の変わったブロックの割合の和・外した所だけが変わった手</summary>
    public int MaskedTurns { get; private set; }
    public double MaskedActivitySum { get; private set; }
    public int MaskedOnlyTurns { get; private set; }
    /// <summary>入力なしの変化: 前の手の結果から次の入力の直前までに画面が変わった回数・そのうち別の画面になっていた回数・選び直した回数</summary>
    public int NoInputChanges { get; private set; }
    /// <summary>待つ手（キーを送らない中立の観測）の数・入力なしでも変わる画面のため時間切れを入力の効果としなかった手の数</summary>
    public int WaitTurns { get; private set; }
    // v0.27.0 入力なしの自然な変化の基準（健全性の材料）
    public int BaselineNodes { get; private set; } = -1; public int BaselineCovered { get; private set; } public int BaselineGapObs { get; private set; } public int BaselineWaitObs { get; private set; }
    public int AttrKnown { get; private set; } public int AttrNatural { get; private set; } public int AttrPairs { get; private set; }
    public int TimeoutWeak { get; private set; }
    /// <summary>待つ手で画面が変わった割合がこれを超える画面では、時間切れを入力の効果の証拠にしない</summary>
    private const double AutonomousChangeRate = 0.5;   // 根拠: 汎用（入力なしで変わる方が起きやすい）
    public int NoInputTransitions { get; private set; }
    public int Rechosen { get; private set; }
    /// <summary>入力なしの遷移が続くとき、選び直しを続ける上限（その後は今の画面のまま手を送る）</summary>
    private const int MaxRechoose = 2;   // 根拠: 汎用（変わり続ける画面で手を送れなくならないための小さな上限）
    /// <summary>まだ見ていない結果の出やすさの推定（内部モデルの予測分布の集中度 α）</summary>
    public double NoveltyAlpha { get; private set; } = double.NaN;
    public int MotionMasksLearned { get; private set; }
    /// <summary>系列の統計を作り直すときに流し直す連続履歴の手数</summary>
    public const int StreamReplayLimit = 20000;  // 根拠: 汎用（起動時の読み直しの時間と記憶量の釣り合い）

    /// <summary>v0.21.0 Game Supervisor（プロセス・ウィンドウ・取得・入力の見張りと復旧。攻略 AI とは別の権限）</summary>
    public SupervisorEngine Supervisor { get; }
    public SupervisorConfig SupervisorConfig { get; private set; }
    /// <summary>v0.23.1: 起動の方法は使うたびに読み直す（画面の設定の変更がすぐ効く）。supervisor.json に書かれていればそちらが優先</summary>
    private SupervisorConfig RefreshLaunchConfig()
    {
        var file = SupervisorConfig.Load(_env.DataRoot);
        SupervisorConfig = LaunchSettings.Load(_env.DataRoot).Apply(file);
        return SupervisorConfig;
    }
    public string LaunchSummary => SupervisorConfig.LaunchSummary;
    public int LaunchAttempts { get; private set; }
    public int LaunchSucceeded { get; private set; }
    /// <summary>v0.23.1: 接続したゲームの実行ファイルを記録する（環境の設定。Steam のゲームかの手がかりも）。実行ファイルの場所は ProcessControl が OS に尋ねる</summary>
    public static string? RecordDetectedExecutable(string dataRoot, int pid, Action<string>? log = null)
    {
        try
        {
            var exe = ProcessControl.ExecutablePathOf(pid);
            if (exe == null) { log?.Invoke("ゲームの実行ファイルの場所を取れませんでした（自動で起動し直すには画面で実行ファイルを選ぶか Steam app id を入れてください）"); return null; }
            var ls = LaunchSettings.Load(dataRoot);
            var (id, src) = LaunchRouteDetector.Detect(exe);
            if (ls.DetectedExe == exe && ls.DetectedSteamAppId == id) return exe;
            (ls with { DetectedExe = exe, DetectedSteamAppId = id, DetectedSteamSource = src, DetectedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") }).Save(dataRoot);
            log?.Invoke($"ゲームの実行ファイルを記録しました: {exe}{(id != null ? $"（Steam app {id}・{src}）" : "")}");
            return exe;
        }
        catch (Exception e) { log?.Invoke("ゲームの実行ファイルの記録に失敗: " + e.Message); return null; }
    }
    private readonly ProcessControl _proc = new();
    private readonly ProtectedFiles _watched = new();
    private volatile bool _inputFailed;
    /// <summary>接続を作り直したとき（ライブ画面の更新用）</summary>
    public event Action<GameConnection>? ConnectionReplaced;
    private Action? _flushForRecovery;
    private bool _reobserveNeeded;
    public int DbCheckpointsOk { get; private set; }
    public int DbCheckpointsFailed { get; private set; }
    public int AiExceptions { get; private set; }
    public int ResumedReobserved { get; private set; }
    public int StaleDiscarded { get; private set; }
    public int FileChanges { get; private set; }
    /// <summary>成長テストの計測の数（この回）と間隔</summary>
    public int GrowthSnapshots { get; private set; }
    public int BoundaryCandidates { get; private set; }
    public const int BoundaryIntervalSteps = 100;   // 根拠: 汎用（履歴全体を読み直すので間を空ける）
    /// <summary>較正の値として受け入れる取得の揺らぎの上限（画面の変化率）と、測り直しの回数</summary>
    public const double MaxNoiseRatio = 0.1;   // 根拠: 汎用（静止した画面の取得の揺らぎが画面の 1 割を超えることはない。超えるなら画面が動いている）
    public const int RecalibrateTries = 5;     // 根拠: 汎用（起動直後の動きが落ち着くまでの約 10 秒）
    /// <summary>画面の種類が変わったのに「変化なし」と判定した手（判定の基準が合っていない兆候。システムの健全性）</summary>
    public int NoChangeButNodeChanged { get; private set; }
    public const int GrowthIntervalSteps = 50, GrowthTestIntervalSteps = 25;   // 根拠: 汎用（推移を追える細かさと記録の量の釣り合い）
    /// <summary>診断 zip を自動で書き出したとき（UI がクリップボードへ置く）</summary>
    public event Action<string>? DiagnosticsExported;

    public PlaySession(AppEnv env, GameConnection con, StandInWindow standIn, PlaySessionOptions o)
    {
        _env = env; _con = con; _o = o; _standIn = standIn;
        RecordDetectedExecutable(env.DataRoot, WindowHealth.ProcessIdOf(con.Hwnd), m => env.Log.Write("launch_detect", ("msg", m)));
        SupervisorConfig = LaunchSettings.Load(env.DataRoot).Apply(SupervisorConfig.Load(env.DataRoot));
        Supervisor = new SupervisorEngine(new Host(this), SupervisorConfig);
        Supervisor.EventRaised += OnSystemEvent;
        _pause.UserQuietMs = o.QuietMs;
        _pause.Changed += (r, on, d) =>
        {
            if (r != PauseReason.UserActivity || on) Emit($"[{(on ? "一時停止" : "再開")}] {ReasonText(r)} {d}", on ? LogLevel.Warn : LogLevel.Ok);
            _env.Log.Write("pause", ("reason", r.ToString()), ("on", on), ("detail", d), ("game_fg", WindowLocator.IsForeground(_con.Hwnd)));
            if (on) lock (Separation)
            {
                Separation.CountPause(r.ToString());
                if (r == PauseReason.UserActivity && !WindowLocator.IsForeground(_con.Hwnd)) Separation.UserActivityPausesWhileBackground++;
            }
            Changed?.Invoke();
        };
    }

    public IReadOnlyList<PauseReason> PauseReasons => _pause.ActiveReasons(MonoClock.Now);

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(RunGuarded) { IsBackground = true, Name = "PlaySession" };
        _thread.Start();
    }

    /// <summary>手動の一時停止／再開</summary>
    public void TogglePause()
    {
        bool on = !_pause.ActiveReasons(MonoClock.Now).Contains(PauseReason.Manual);
        _pause.Set(PauseReason.Manual, on, "ボタン");
    }

    /// <summary>今の手が終わったら止める</summary>
    public void Stop() { if (State is SessionState.Stopped) return; _quit = true; SetState(SessionState.Stopping, "停止中…"); }

    /// <summary>緊急停止: 入力をただちに止めてキーをすべて離す</summary>
    public void EmergencyStop(string from)
    {
        _pause.Set(PauseReason.EmergencyStop, true, from);
        _quit = true;
        foreach (var b in _inputs) try { b.ReleaseAll(); } catch { }
        StopReason ??= $"緊急停止（{from}）";
        Emit($"[緊急停止] {from}。入力を止めました。", LogLevel.Error);
        Changed?.Invoke();
    }

    public void OnUserActivity(UserActivityEvent ev)
    {
        if (_active == null || State is SessionState.Stopped or SessionState.Idle) return;
        bool gameFg = WindowLocator.IsForeground(_con.Hwnd);
        if (UserActivityPolicy.ShouldPause(_active.Isolation.Level, gameFg))
        {
            Interlocked.Increment(ref UserEventsPausing);
            _pause.OnUserActivity(ev.AtMs, $"{ev.Kind}（{(gameFg ? "ゲームが前面" : "占有モード")}）");
        }
        else Interlocked.Increment(ref UserEventsIgnored);
    }

    public void OnCaptureStatus(CaptureStatus s)
    {
        var reason = s.Health switch { CaptureHealth.Minimized => PauseReason.Minimized, CaptureHealth.Cloaked => PauseReason.Cloaked, CaptureHealth.WindowLost => PauseReason.WindowLost, _ => PauseReason.CaptureError };
        foreach (var r in new[] { PauseReason.Minimized, PauseReason.Cloaked, PauseReason.WindowLost, PauseReason.CaptureError })
            if (r != reason || !s.RequiresPause) _pause.Set(r, false);
        if (s.RequiresPause) _pause.Set(reason, true, s.Detail);
        if (s.Health == CaptureHealth.WindowLost) NoteLoss(MonoClock.Now);
    }

    /// <summary>v0.29.2: 手の繰り返しを終えて、保存と session_end を書いている間（実機 #110: この間に書き出した診断で健全性の終わりの項目が抜けた）</summary>
    public bool Finishing { get; private set; }
    private void SetState(SessionState s, string phase) { State = s; Phase = phase; Changed?.Invoke(); }
    private void Emit(string msg, LogLevel lv = LogLevel.Info)
    {
        Log?.Invoke(msg, lv);
        // 注意・エラーの表示はログにも残す（実機確認は診断 zip だけで行うため。画面の表示だけでは残らない）
        if (lv is LogLevel.Warn or LogLevel.Error) { try { _env.Log.Write("notice", ("level", lv == LogLevel.Error ? "error" : "warn"), ("msg", msg)); } catch { } }
    }

    private void RunGuarded()
    {
        string endReason = "abort";
        try { endReason = Run(); }
        catch (Exception e)
        {
            AiExceptions++;
            try { Supervisor.ReportAiException($"{e.GetType().Name}: {e.Message}"); } catch { }
            Emit($"セッションで予期しないエラー: {e.GetType().Name}: {e.Message}", LogLevel.Error);
            _env.Log.Write("session_error", ("error", e.ToString()));
            StopReason ??= "エラー: " + e.Message;
            endReason = "error";
        }
        finally
        {
            foreach (var b in _inputs) try { b.ReleaseAll(); } catch { }
            if (RunId is long id)
                try { _env.Memory.EndRun(id, endReason, Json.Obj(("stop_reason", StopReason), ("steps", Step),
                    ("vnext_dual_append", RuntimeFlags.UseExperienceVNextWriter), ("vnext_source", RuntimeFlags.EffectiveSource))); } catch (Exception e) { Emit("探索の終了記録に失敗: " + e.Message, LogLevel.Warn); }
            try { FlushPeClosedLoop("session_end"); } catch { }
            try { FlushIntrinsicStats("session_end"); } catch { }
            try { TryEmitLongHorizonSnapshot(-1, "session_end"); } catch { }
            try { _peStore?.Dispose(); } catch { }
            _peStore = null;
            try { _intrinsicStore?.Dispose(); } catch { }
            _intrinsicStore = null;
            try { _rawAgg?.Dispose(); } catch { }
            _rawAgg = null;
            try { FlushOuterRegistry("session_end"); } catch { }
            try { _modelRegistry?.Dispose(); } catch { }
            _modelRegistry = null;
            try { _vnextKernel?.Dispose(); } catch { }
            _vnextKernel = null;
            foreach (var b in _inputs) try { b.Dispose(); } catch { }
            _env.Log.Write("session_end", ("reason", endReason), ("stop_reason", StopReason), ("steps", Step), ("duration_s", Math.Round(_runClock.Elapsed.TotalSeconds, 1)),
                ("vnext_dual_append", RuntimeFlags.UseExperienceVNextWriter), ("vnext_source", RuntimeFlags.EffectiveSource),
                ("causal_wm", RuntimeFlags.AttachCausalWm), ("importance", RuntimeFlags.AttachImportance), ("intrinsic_mixer", RuntimeFlags.UseIntrinsicMixer), ("active_exp_interrupts", ActiveExperimentInterrupts), ("pe_enqueued", PeLoop.Enqueued), ("pe_pending", PeLoop.PendingCount),
                ("pe_summary", RuntimeFlags.AttachCausalWm ? PeLoop.SummaryLine() : null),
                ("intrinsic_stats", RuntimeFlags.UseIntrinsicMixer ? IntrinsicStats.SummaryLine() : null),
                ("structured_goals", RuntimeFlags.UseStructuredGoals), ("skill_lib", RuntimeFlags.UseStructuredGoals ? SkillLibrary.SummaryLine() : null), ("structured_soft_adopts", StructuredSoftAdopts),
                ("outer_loop", RuntimeFlags.EnableOuterLoop), ("outer_promote", RuntimeFlags.OuterLoopPromoteEnabled), ("outer_registry", RuntimeFlags.EnableOuterLoop && _modelRegistry != null ? _modelRegistry.StatsSummary() : null),
                ("user_events_pausing", UserEventsPausing), ("user_events_ignored", UserEventsIgnored),
                ("input_sent", Delivery.Sent), ("input_delivered", Delivery.Delivered), ("input_failed", Delivery.Failed), ("input_reaction_observed", Delivery.ReactionObserved),
                ("input_reacted", Delivery.Reacted), ("longest_no_reaction", Delivery.LongestNoReactionStreak), ("agent_steps", AgentSteps), ("human_steps", HumanSteps),
                ("human_switches", Human?.Switches ?? 0), ("summaries", StepHistory.StepCount), ("system_separators", StepHistory.Count - StepHistory.StepCount), ("nochange_node_changed", NoChangeButNodeChanged), ("growth_points", Growth.Counts().Steps), ("temporal_screens", Temporal.Count),
                ("turns_aborted", AbortedTurns), ("skill_candidates", SkillCandidates), ("goal_skill_uses", GoalSkillUses), ("background_screens", BackgroundLearned), ("goals_created", Goals?.Created ?? 0), ("goals_achieved", Goals?.Achieved ?? 0), ("goals_failed", Goals?.Failed ?? 0), ("goals_abandoned", Goals?.Abandoned ?? 0), ("planning_horizon", Goals?.Horizon ?? 0), ("horizon_changes", Goals?.HorizonChanges ?? 0), ("max_goal_length", Goals?.MaxAchievedLength ?? 0),
                ("replans", Goals?.ReplanCount ?? 0), ("replan_goals", Goals?.ReplanGoals ?? 0), ("replan_achieved", Goals?.ReplanAchieved ?? 0), ("subgoals", Goals?.SubgoalsTotal ?? 0), ("subgoals_reached", Goals?.SubgoalsReachedTotal ?? 0), ("goal_segment_support", GoalSegmentSupport), ("goal_share", Math.Round(Goals?.Share ?? 0, 3)),
                ("frontier", Frontier?.Summary()), ("env_loss", Losses.Summary()), ("env_loss_repeats", Losses.RepeatsAfterLearning), ("ocr", StateText == null ? null : StateText.Summary()), ("frontier_regions", Frontier?.RegionCount), ("frontier_decayed", Frontier?.DecayedNodes), ("frontier_recoveries", Frontier?.Recoveries), ("frontier_values_ok", Frontier == null ? null : Frontier.Regions().All(r => double.IsFinite(r.Value) && r.Value >= 0.05 - 1e-9 && r.Value <= 4 + 1e-9)),
                ("goal_gain_baseline", Goals == null ? null : $"{Goals.Baseline.N}:{Goals.Baseline.Mean:0.0000}"), ("goal_gain", Goals == null ? null : string.Join(" ", Goals.GainModel.Select(kv => $"{kv.Key}={kv.Value.N}:{kv.Value.Mean:0.0000}:r{Goals.Ratio(kv.Key.StartsWith("reach") ? GoalKind.Reach : GoalKind.Retest, kv.Key.EndsWith("|1") ? 1 : kv.Key.EndsWith("|2") ? 2 : kv.Key.EndsWith("|3-4") ? 3 : 5):0.00}"))),
                ("turns_game_fg", Separation.TurnsGameForeground), ("turns_other_fg", Separation.TurnsOtherForeground), ("nochange_while_bg", Separation.NoChangeWhileBackground),
                ("ua_pauses_while_bg", Separation.UserActivityPausesWhileBackground), ("pauses", Separation.Pauses.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)),
                ("late_changes", LateChanges), ("delayed_recorded", DelayedRecorded), ("user_restricted_steps", UserRestrictedSteps), ("user_disabled_now", _env.Availability.Disabled.Count), ("input_delivery_failed", InputDeliveryFailed), ("emergency", _pause.IsEmergencyStopped),
                ("discoveries_by_kind", Discoveries.Counts().ToDictionary(kv => kv.Key.ToString(), kv => (object?)kv.Value)),
                ("hypotheses_by_status", Hypotheses.GroupBy(h => $"{h.Kind}.{h.Status}").ToDictionary(g => g.Key, g => (object?)g.Count())),
                ("ambiguous_observations", AmbiguousObservations), ("timeout_turns", TimeoutTurns), ("motion_masks", MotionMasksLearned),
                ("wait_turns", WaitTurns), ("ocr_each_step", _o.Ocr != null), ("ocr_requested", Ocr2.Requested), ("ocr_skipped", Ocr2.Skipped), ("ocr_full", Ocr2.Full), ("ocr_region", Ocr2.Region), ("ocr_reused", Ocr2.Reused), ("ocr_cache", Ocr2.Cache), ("ocr_deferred", Ocr2.Deferred), ("ocr_failed_steps", Ocr2.Failed),
                ("live_roi_deferred", LiveRoiDeferred), ("live_roi_forced", LiveRoiForcedRead), ("live_roi_considered", LiveRoiConsidered),
                ("ocr_ms_median", Ocr2.Latency().N > 0 ? Math.Round(Ocr2.Latency().Median, 1) : null), ("ocr_ms_p90", Ocr2.Latency().N > 0 ? Math.Round(Ocr2.Latency().P90, 1) : null), ("ocr_ms_max", Ocr2.Latency().N > 0 ? Math.Round(Ocr2.Latency().Max, 1) : null),
                ("ocr_area_mean", Ocr2.Full + Ocr2.Region > 0 ? Math.Round(Ocr2.AreaSum / (Ocr2.Full + Ocr2.Region), 3) : null),
                ("text_fresh", Ocr2.Fresh), ("text_stale", Ocr2.Stale), ("text_pending", Ocr2.Pending), ("text_pairs", Ocr2.Pairs), ("text_unpaired", Ocr2.Unpaired), ("text_changed", Ocr2.TextChanged), ("text_suppressed", Ocr2.Suppressed), ("text_book", TextBook.Summary()), ("text_checks", TextBook.Checks), ("text_hits", TextBook.Hits),
                ("busy_turns", Ocr2.BusyTurns), ("idle_turns", Ocr2.IdleTurns), ("busy_boundary_ms", R1(Ocr2.MeanBoundary().Busy)), ("idle_boundary_ms", R1(Ocr2.MeanBoundary().Idle)), ("busy_gap_ms", R1(Ocr2.MeanGap().Busy)), ("idle_gap_ms", R1(Ocr2.MeanGap().Idle)),
                ("foreground", Fg.Summary()), ("score_breakdown", ScoreLog.Summary()), ("score_logged", ScoreLog.Steps), ("score_text_top_flips", ScoreLog.TextFlips), ("score_scored", ScoreLog.Scored), ("obs_built", ObsBuilder.Built), ("obs_saved", ObsSaved), ("obs_failed", ObsFailed), ("obs_text_not_observed", ObsTextNotObserved),
                ("obs_lines_observed", ObsBuilder.TextObservedLines), ("obs_lines_inherited", ObsBuilder.TextInheritedLines), ("obs_tracks", ObsBuilder.Tracks), ("obs_continued_regions", ObsBuilder.ContinuedRegions), ("cells_stabilized", _textReader.Stabilizer.Stabilized), ("ocr_kept_unchanged_lines", _textReader.KeptUnchangedLines), ("cells_changed", _textReader.Stabilizer.Changed), ("fine_cells_stabilized", Entities.Stabilizer.Stabilized), ("fine_cells_changed", Entities.Stabilizer.Changed), ("entity_created", Entities.Created), ("entity_continued", Entities.Continued), ("entity_reidentified", Entities.Reidentified), ("entity_appearance_only", Entities.AppearanceOnly), ("common_shift_steps", Entities.CommonShiftSteps), ("common_shift_checked", Entities.Motion.Checked), ("common_shift_unexplained", Entities.Motion.Unexplained), ("common_shift_unexplained_floor", Entities.Motion.UnexplainedFloor), ("common_shift_unexplained_improved", Entities.Motion.UnexplainedImproved), ("common_shift_warp", Entities.Motion.WarpShifted), ("common_shift_warp_dir", string.Join(" ", Entities.Motion.WarpByDir.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"))), ("global_transform_compared", Entities.Motion.GlobalCompared), ("global_transform_decisions", string.Join(" ", Entities.Motion.GlobalByDecision.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"))), ("entity_periodic_returns", Entities.PeriodicReturns), ("entity_overlap_continued", Entities.OverlapContinued), ("entity_static_observed", Entities.StaticObserved), ("track_frag", TrackFrag()), ("track_quality", Entities.Quality.Summary()), ("entity_exp", EntityExp.Summary()), ("entity_grouped_steps", Entities.GroupedSteps), ("entity_large_suppressed", Entities.LargeSuppressed), ("local_context_used", LocalEvents.ContextUsed), ("covary_groups", CovarySummary()), ("app_table", $"{AppTable.Observed}/{AppTable.Predicted}/{AppTable.Hits}/{AppTable.Keys}"), ("num_table", $"{NumTable.Observed}/{NumTable.Predicted}/{NumTable.Hits}"), ("relations_pruned", Entities.RelationsPruned), ("local_prediction", LocalEvents.Summary()), ("local_persistent_errors", LocalEvents.PersistentErrors(6).Count()), ("local_info_calls", LocalInfoCalls), ("joint_predictions", Coarse.Joint), ("split_estimates", Coarse.SplitEstimates(LocalPersistentMinN, SplitEstimateMinN).Count), ("split_would_help", Coarse.SplitEstimates(LocalPersistentMinN, SplitEstimateMinN).Count(x => x.LogBf >= GameKouryakuKun.Core.Exploration.SplitReview.RevertMinLogBayesFactor && x.LooSplit < x.LooPooled)),
                ("split_top", string.Join(" / ", Coarse.SplitEstimates(LocalPersistentMinN, SplitEstimateMinN).OrderByDescending(x => x.LogBf).Take(3).Select(x => $"{x.Node} {x.Action} {x.Event} 観測 {x.N}・位置 {x.Contexts}・log BF {x.LogBf}・驚き {x.LooPooled}→{x.LooSplit}"))),
                ("skill_sequences", SkillEffects.Sequences), ("skill_effects", string.Join(" / ", SkillEffects.Effects(LocalPersistentMinN).Where(x => x.Effects.Count > 0).Take(3).Select(x => $"{x.Sequence}（{x.N} 回）: {string.Join(",", x.Effects)}"))), ("skill_with_effect", SkillEffects.Effects(LocalPersistentMinN).Count(x => x.Effects.Count > 0)), ("coarse_candidates", Coarse.Candidates(LocalPersistentMinN).Count()), ("coarse_top", string.Join(" / ", Coarse.Candidates(LocalPersistentMinN).Take(3).Select(c => $"{c.Node} {c.Action} {c.Event} 次の画面 {c.WorldHit:P0}・局所の驚き {c.LocalSurprise}（{c.N}）"))), ("local_info_raised", LocalInfoRaised), ("local_info_mean", LocalInfoCalls > 0 ? Math.Round(LocalInfoSum / LocalInfoCalls, 4) : null), ("local_info_excluded", LocalEvents.ExcludedPersistent), ("score_halves", ScoreLog.Halves()), ("score_contribution", ScoreLog.ContributionSummary()), ("score_outcomes", ScoreLog.Outcomes),
                ("wait_info_n", WaitInfoN), ("wait_info_mean", WaitInfoN > 0 ? Math.Round(WaitInfoSum / WaitInfoN, 3) : 0), ("wait_info_first", Math.Round(WaitInfoFirst, 3)), ("wait_info_last", Math.Round(WaitInfoLast, 3)), ("text_caused_sum", Math.Round(TextBook.CausedSum, 1)), ("text_book_changed", TextBook.Changed), ("text_natural_obs", TextBook.NaturalTextObservations), ("text_regions", TextRegionCounts()), ("pixel_regions", PixelRegionCounts()), ("text_link", TextLinkCounts()), ("fg_natural_lines_excluded", FgNaturalLinesExcluded), ("fg_merged", Fg.Merged), ("fg_separate", Fg.Separate), ("nb_nodes", BaselineNodes), ("nb_covered", BaselineCovered), ("nb_gap_obs", BaselineGapObs), ("nb_wait_obs", BaselineWaitObs), ("attr_pairs", AttrPairs), ("attr_known", AttrKnown), ("attr_natural", AttrNatural), ("timeout_weak", TimeoutWeak), ("motion_screens", MotionScreens),
                ("no_input_changes", NoInputChanges), ("no_input_transitions", NoInputTransitions), ("rechosen", Rechosen), ("masked_turns", MaskedTurns),
                ("masked_activity_mean", MaskedTurns > 0 ? Math.Round(MaskedActivitySum / MaskedTurns, 4) : null), ("masked_only_turns", MaskedOnlyTurns),
                ("novelty_alpha", double.IsFinite(NoveltyAlpha) ? Math.Round(NoveltyAlpha, 4) : null),
                ("supervisor_state", Supervisor.State.ToString().ToUpperInvariant()), ("supervisor_abort_reason", Supervisor.AbortReason), ("supervisor_launch_method", SupervisorConfig.LaunchMethod),
                ("sup_exits", Supervisor.ProcessExits), ("sup_abnormal_exits", Supervisor.AbnormalExits), ("sup_restarts", Supervisor.Restarts),
                ("sup_recoveries_ok", Supervisor.RecoveriesOk), ("sup_recoveries_failed", Supervisor.RecoveriesFailed), ("sup_breaker_trips", Supervisor.BreakerTrips),
                ("sup_observation_lost_s", Math.Round(Supervisor.ObservationLostMs / 1000, 1)), ("sup_capture_reconnects", Supervisor.CaptureReconnects), ("sup_input_reconnects", Supervisor.InputReconnects),
                ("sup_suspected_hangs", Supervisor.SuspectedHangs), ("sup_db_checkpoints_ok", DbCheckpointsOk), ("sup_db_checkpoints_failed", DbCheckpointsFailed),
                ("sup_ai_exceptions", AiExceptions), ("sup_resumed_reobserved", ResumedReobserved), ("sup_stale_discarded", StaleDiscarded), ("sup_file_changes", FileChanges),
                ("sup_events", Supervisor.Events.Count), ("sup_input_losses", Supervisor.InputLosses), ("sup_recoveries_stopped", Supervisor.RecoveriesStopped), ("launch_attempts", LaunchAttempts), ("launch_ok", LaunchSucceeded), ("launch_summary", SupervisorConfig.LaunchSummary), ("growth_snapshots", GrowthSnapshots), ("growth_test", _o.GrowthTest),
                ("sup_transactions", Supervisor.Transactions.Select(t => (object?)new Dictionary<string, object?> { ["id"] = t.Id, ["reason"] = t.Reason, ["outcome"] = t.Outcome, ["attempts"] = t.Attempts,
                    ["duration_s"] = t.EndedMs is double em ? Math.Round((em - t.StartedMs) / 1000, 1) : null, ["stages"] = t.Stages.Select(x => (object?)$"{x.Stage}:{(x.Ok ? "ok" : "ng")}").ToList() }).ToList()));
            if (Supervisor.State == SupervisorState.Aborted)
            {
                // 回路が開いた／復旧できない: DB は保存済み。診断 zip（健全性レポートを含む）を作って人を待つ
                try { var r = _env.ExportDiagnostics(); Emit($"[Supervisor] 診断データを書き出しました: {r.ZipPath}", LogLevel.Warn); DiagnosticsExported?.Invoke(r.ZipPath); }
                catch (Exception ex) { Emit("[Supervisor] 診断データの書き出しに失敗: " + ex.Message, LogLevel.Error); }
            }
            SetState(SessionState.Stopped, StopReason ?? "終了");
        }
    }

    private string Run()
    {
        var profile = _env.Profile;
        var keyMap = profile.KeyMap();
        var guard = profile.Guard();
        var cap = _con.Capture;
        nint game = _con.Hwnd;
        if (!WindowLocator.IsAlive(game)) { StopReason = "ゲームウィンドウがありません（再接続してください）"; return "window_lost"; }

        // ---- Supervisor（v0.21.0）: 見張るプロセス・設定の確かめ・見張るファイルの基準 ----
        if (SupervisorConfig.LoadProblem is string lp) RaiseConfigProblem("supervisor.json を読めません（既定の安全な値で続けます）: " + lp);
        try { var ex = Path.Combine(_env.DataRoot, "supervisor.example.json"); if (!File.Exists(ex)) File.WriteAllText(ex, SupervisorConfig.Example); } catch { }
        _proc.Attach(WindowHealth.ProcessIdOf(game));
        _watched.LoadOrCreateBaseline(SupervisorConfig.WatchFiles, Path.Combine(_env.DataRoot, ProtectedFiles.BaselineFileName));
        _env.Log.Write("supervisor_config", ("launch_method", SupervisorConfig.LaunchMethod), ("launch_source", SupervisorConfig.LaunchSource), ("launch_summary", SupervisorConfig.LaunchSummary), ("can_launch", SupervisorConfig.CanLaunch), ("auto_restart", SupervisorConfig.AutoRestart),
            ("max_restarts", SupervisorConfig.MaxRestarts), ("restart_window_sec", SupervisorConfig.RestartWindowSec), ("backoff_initial_sec", SupervisorConfig.BackoffInitialSec), ("backoff_max_sec", SupervisorConfig.BackoffMaxSec),
            ("watch_files", SupervisorConfig.WatchFiles.Count), ("watch_baseline", _watched.BaselineOrigin), ("pid_known", _proc.Pid != null));

        // ---- 較正 ----
        SetState(SessionState.Preparing, "ノイズ較正（入力なしで約1.5秒観測）");
        var diffOpt = DiffOptions.Default;
        var calib = CalibrateNoise(cap, diffOpt);
        if (calib == null) { StopReason = "ゲーム画面のフレームが届きません（最小化を解除してください）"; return "abort"; }

        // ---- 入力方式の選択（v0.15.0）: A＝OS・入力方式として対象のウィンドウへ送れるか、だけで選ぶ。起動時にゲームへキーは送らない。
        // B（画面が反応するか）は未知の状態の性質かもしれないので選択・停止の条件にしない（探索の各手で数える）
        var clock = MonoClock.Instance;
        var pm = new PostMessageInputBackend(clock);
        var si = new SendInputBackend(clock, _o.AllowSendInput);
        _inputs = new List<IInputBackend> { pm, si };
        foreach (var b in _inputs) b.Bind(game, keyMap, guard, profile.Input.DefaultHoldMs);
        SetState(SessionState.Preparing, "入力方式の送達の確認（A: キーは送らない）");
        var probes = new List<(IInputBackend B, bool Ok, string Detail)>();
        foreach (var b in _inputs)
        {
            var pr = b.Probe(game);
            probes.Add((b, pr.Available, pr.Detail));
            Emit($"入力 {b.DisplayName} [{b.Isolation.Badge}]: {(pr.Available ? "送信可能" : "利用不可")} — {pr.Detail}", pr.Available ? LogLevel.Info : LogLevel.Dim);
            _env.Log.Write("input_delivery_probe", ("backend", b.Id), ("available", pr.Available), ("isolation", b.Isolation.Level.ToString()), ("detail", pr.Detail));
        }
        // v0.26.0: 全操作（単独キー・Profile の chords）の物理キー・VK・押す/離す計画を、キーを送らずに確かめる
        var table = ActionTable.Build(profile);
        foreach (var r in table) _env.Log.Write("input_action_table", ("action", r.Action), ("group", r.Group), ("keys", r.PhysicalKeys), ("vk", r.Vks), ("events", r.Events), ("ok", r.Ok), ("problem", r.Problem));
        int bad = table.Count(r => !r.Ok);
        Emit($"操作の表: {table.Count} 件（単独 {table.Count(r => r.Group != "Chord")}・同時押し {table.Count(r => r.Group == "Chord")}）" + (bad > 0 ? $"・送れない {bad} 件: {string.Join(", ", table.Where(r => !r.Ok).Select(r => r.Action + "（" + r.Problem + "）"))}" : "・すべて送れる形"), bad > 0 ? LogLevel.Error : LogLevel.Info);
        if (bad > 0) { StopReason = "Profile の操作に送れないものがあります（システムの失敗）"; return "input_action_table_failed"; }
        var selection = InputBackendSelector.SelectByDelivery(probes.Select(x => (x.B, x.Ok)));
        Emit($"入力方式: {selection.BackendId ?? "なし"} — {selection.Reason}", selection.BackendId != null ? LogLevel.Ok : LogLevel.Error);
        try { _env.AppDb.RecordSelection(cap.Current?.Id, selection.BackendId, selection.Level == IsolationLevel.ForegroundShared ? "foreground" : "background", "delivery", _o.AllowSendInput, selection.Reason); } catch { }
        _env.Log.Write("input_selected", ("backend", selection.BackendId), ("reason", selection.Reason));
        if (selection.BackendId == null) { StopReason = "入力を送れる方式がありません（システムの失敗: " + selection.Reason + "）"; return "input_delivery_failed"; }
        if (_quit) { StopReason ??= "準備中に停止"; return "user_stop"; }
        var active = _inputs.First(b => b.Id == selection.BackendId);
        _active = active; InputName = active.DisplayName; InputBadge = active.Isolation.Badge; InputLevel = active.Isolation.Level;
        if (active.Isolation.Level == IsolationLevel.ForegroundShared) Emit("★ 占有モード（SendInput）: ゲームが前面のときだけ送ります。操作を検知すると即停止します。", LogLevel.Warn);

        var detOpt = new TurnDetectorOptions { Diff = diffOpt };
        return RunExplore(active, calib, detOpt);
    }

    private CalibrationResult? CalibrateNoise(CaptureSupervisor cap, DiffOptions diffOpt)
    {
        var frames = new List<Frame>(); double t0 = MonoClock.Now, lastAt = 0; long lastId = 0;
        while (MonoClock.Now - t0 < 1500 && frames.Count < 24 && !_quit)
        {
            var f = cap.WaitForNewFrame(lastId, 100);
            if (f == null) continue;
            lastId = f.Id;
            if (f.TimestampMs - lastAt >= 60) { frames.Add(f); lastAt = f.TimestampMs; }
        }
        if (frames.Count < 2 && cap.LatestFrame() is Frame lf0) frames.Add(lf0);
        if (frames.Count == 0) return null;
        var calib = NoiseCalibrator.Calibrate(frames, diffOpt);
        Calibration = calib;
        Emit("較正: " + calib, LogLevel.Ok);
        _env.Log.Write("calibration", ("samples", calib.Samples), ("change_ratio", calib.Criteria.ChangeRatio), ("change_comp", calib.Criteria.ChangeComponent), ("mask", calib.IdleMask?.ToString()));
        return calib;
    }

    // ======================== 本体: 手探り探索（攻略 AI） ========================
    /// <summary>
    /// 事前知識なしの手探り探索。キーの意味は使わない（中立の論理キー名・可逆性の安全規則）。止まるのは
    /// 停止・緊急停止・ウィンドウ消失（一時停止）・入力の送信そのもの（A）が続けて失敗したとき・ユーザーが指定した時間／手数の上限だけ。
    /// 画面が反応しない（B）ことだけでは止めない（未知の状態の性質かもしれない）。
    /// </summary>
    private string RunExplore(IInputBackend active, CalibrationResult calib, TurnDetectorOptions detOpt)
    {
        var profile = _env.Profile; var cap = _con.Capture; nint game = _con.Hwnd;
        _ = game;
        int maxSteps = _o.MaxSteps > 0 ? _o.MaxSteps : int.MaxValue;
        double timeLimit = _o.TimeLimitSec;
        // v0.33.4: dual-append 有効状態（env が最優先、次に UI トグル。既定 OFF）
        RuntimeFlags.ApplyEffective();
        Emit(RuntimeFlags.StatusText() + "。学習 DB は従来どおり memory.sqlite です",
            RuntimeFlags.UseExperienceVNextWriter ? LogLevel.Ok : LogLevel.Info);
        RuntimeFlags.ApplyBundleEffective();
        Emit(RuntimeFlags.BundleStatusText() + "（S2・探索挙動は変えません）",
            RuntimeFlags.AttachObservationBundle ? LogLevel.Ok : LogLevel.Info);
        RuntimeFlags.ApplyCausalWmEffective();
        Emit(RuntimeFlags.CausalWmStatusText() + "（S3・探索挙動は変えません）",
            RuntimeFlags.AttachCausalWm ? LogLevel.Ok : LogLevel.Info);
        RuntimeFlags.ApplyImportanceEffective();
        Emit(RuntimeFlags.ImportanceStatusText(), RuntimeFlags.AttachImportance ? LogLevel.Ok : LogLevel.Info);
        RuntimeFlags.ApplyImportanceEffective();
        Emit(GameKouryakuKun.Core.Importance.LiveRoiPolicy.StatusText(RuntimeFlags.AttachImportance),
            GameKouryakuKun.Core.Importance.LiveRoiPolicy.IsLiveEnabled(RuntimeFlags.AttachImportance) ? LogLevel.Ok : LogLevel.Info);
        RuntimeFlags.ApplyIntrinsicMixerEffective();
        Emit(RuntimeFlags.IntrinsicMixerStatusText(), RuntimeFlags.UseIntrinsicMixer ? LogLevel.Ok : LogLevel.Info);
        RuntimeFlags.ApplyStructuredGoalsEffective();
        Emit(RuntimeFlags.StructuredGoalsStatusText(), RuntimeFlags.UseStructuredGoals ? LogLevel.Ok : LogLevel.Info);
        RuntimeFlags.ApplyOuterLoopEffective();
        Emit(RuntimeFlags.OuterLoopStatusText(), RuntimeFlags.EnableOuterLoop ? LogLevel.Ok : LogLevel.Info);
        if (RuntimeFlags.AttachCausalWm) Emit(PeLoop.SummaryLine(), LogLevel.Info);
        try { TryRunOuterLoopShell(0); } catch { }
        try { TryEmitLongHorizonSnapshot(0, "session_start"); } catch { }
        RunId = _env.Memory.BeginRun(profile.ProfileVersion, active.Id, cap.Current?.Id, policyVersion: "explore-0.3",
            notesJson: Json.Obj(("seed", _o.Seed), ("growth_test", _o.GrowthTest), ("max_steps", _o.MaxSteps), ("time_limit_s", timeLimit), ("start_with_human_control", _o.StartWithHumanControl),
                ("vnext_dual_append", RuntimeFlags.UseExperienceVNextWriter), ("vnext_source", RuntimeFlags.EffectiveSource), ("vnext_ui", RuntimeFlags.UiEnabled)));
        var panels = new PanelDetector();
        var book = new ScreenModeBook();
        try { book.Load(_env.Memory.LoadScreenModes()); } catch (Exception e) { Emit("画面の種類の読込に失敗: " + e.Message, LogLevel.Warn); }
        // 見た目の状態（細かい配置）と遷移の特徴（別々の表現）・区画とキーの反応の対応表（記録と報告だけ）
        var vbook = new VisualStateBook();
        try { _env.Memory.LoadVisualStates(vbook); } catch (Exception e) { Emit("見た目の状態の読込に失敗: " + e.Message, LogLevel.Warn); }
        var reactions = new ReactionMap();
        try { reactions.Load(_env.Memory.LoadReactionMaps()); } catch (Exception e) { Emit("反応の対応表の読込に失敗: " + e.Message, LogLevel.Warn); }
        // キーを送る操作に、キーを送らずに待つ中立の観測（wait）を加える（入力なしで画面がどうなるかを経験から学ぶ。v0.15.0）
        var explorer = new Explorer(Explorer.FromProfile(profile.KeyMap().All.Keys, profile.Input.Chords), _o.Seed);
        explorer.Availability = _env.Availability;   // v0.32.5（t106u）: 人が止めた入力は自律探索の候補にしない（Human Control と入力の診断には関係しない）
        // 経験から見つけた操作の並び（ActionSequence #n）を先に候補へ加える（その遷移の記録を読み込むため）
        var seqMiner = new ActionSequenceMiner();
        var savedStatus = new Dictionary<int, SequenceStatus>();
        try
        {
            // 終わった区間の判断の記録を要約する（元の記録は消さない。何度呼んでも同じ）
            try { int nb = _env.Memory.RollupDecisions(); if (nb > 0) _env.Log.Write("decision_rollup", ("blocks", nb)); }
            catch (Exception e) { Emit("判断の記録の要約に失敗: " + e.Message, LogLevel.Warn); }
            var saved = _env.Memory.LoadActionSequences(); seqMiner.Load(saved);
            // 実行した結果の記録も引き継ぐ（休止の判断に使う。古いものは数えない）
            foreach (var (sid, sq, sur) in _env.Memory.LoadSequenceExecutions()) seqMiner.ObserveExecution(sid, sq, sur);
            foreach (var c in saved) { savedStatus[c.Id] = c.Status; explorer.AddSequence(c); if (c.Status == SequenceStatus.Dormant) explorer.Deactivate(c.Name); }
        }
        catch (Exception e) { Emit("操作の並びの読込に失敗: " + e.Message, LogLevel.Warn); }
        try { explorer.Load(_env.Memory.LoadTransitions()); explorer.LoadReversibility(_env.Memory.LoadReversibility()); } catch (Exception e) { Emit("遷移の記録の読込に失敗: " + e.Message, LogLevel.Warn); }
        // 分割で読み直した遷移を、直接の経験の集計に当て直す（読み直しであって継承ではない。直接の記録は書き換えない）
        try { foreach (var r in _env.Memory.LoadReclassified()) explorer.Reassign(r.OrigFrom, r.Action, r.OrigTo, r.NewFrom, r.NewTo, r.N, r.Changed); }
        catch (Exception e) { Emit("読み直した遷移の読込に失敗: " + e.Message, LogLevel.Warn); }
        // 系列の学習: 連続経験履歴（全実行を通した1本の系列）の続きから学ぶ。実行単位・冒険の区切りは渡さない
        var seqLearner = new SequenceLearner();
        long nextSeq = 1;
        try
        {
            seqLearner.LoadPatterns(_env.Memory.LoadSequencePatterns());
            seqLearner.Replay(_env.Memory.LoadStream(StreamReplayLimit));
            seqMiner.Replay(_env.Memory.LoadStream(StreamReplayLimit));
            nextSeq = _env.Memory.NextStreamSeq();
            // 流し直しの結果の状態（選択の候補／休止）に合わせる。変わったものは状態の変化として記録する
            foreach (var c in seqMiner.Candidates)
            {
                explorer.AddSequence(c);
                if (c.Status == SequenceStatus.Active) explorer.Activate(c.Name); else explorer.Deactivate(c.Name);
                bool known = savedStatus.TryGetValue(c.Id, out var before);
                if (known && before == c.Status) continue;
                var kind = !known ? (c.Status == SequenceStatus.Active ? "promoted" : "waiting") : c.Status == SequenceStatus.Active ? "reactivated" : "dormant";
                _env.Memory.AddSequenceEvent(new SequenceEvent(c, kind, "起動時に履歴を流し直した結果", nextSeq - 1));
            }
        }
        catch (Exception e) { Emit("連続経験履歴の読込に失敗: " + e.Message, LogLevel.Warn); }
        // 予測 → 比較 → 修正 の内部モデル。連続履歴の流し直しで作り直す（修正も同じ規則で起きる。起動時の流し直しの修正は記録しない）
        var model = BuildModel(explorer);
        // 予測の不確かさ（次の観測で関係について得られる情報の量）を選択に使う。修正で古い証拠の重みが下がると上がる
        // v0.27.0: 待つ手の不確かさ＝入力なしの基準の不確かさ（観測が増えれば自然に下がる。新しい定数なし）
        explorer.UncertaintyValue = (node, a) => a.IsWait && explorer.Baseline is { } nbu ? nbu.Uncertainty(node) : WithLocal(node, a.Name, UncWithText(node, a.Name, model.Uncertainty(node, a.Name)));
        // v0.27.2: 前景の鍵でまとめた仲間の試行・操作が起こした文字の変化
        explorer.PeerTries = (node, act) => { List<string> ps; lock (Fg) ps = Fg.Peers(node).ToList(); return ps.Sum(p => explorer.StatsOf(p, act)?.Tries ?? 0); };
        explorer.TextChangeValue = (node, a) => { lock (TextBook) return TextBook.ActionCausedRate(node, a.Name); };   // v0.28.0: 区画ごとの手によらない基準を引いた「手が起こす文字の変化」
        var runTransitions = new Dictionary<(string, string, string), int>();
        // 区画の反応の対応表は「まだ分かっていない反応の関係の情報の量」としてだけ選択に使う（変化の大きさを良いとしない）
        explorer.InformationValue = (node, a) => a.Group == ActionGroup.Sequence && a.Steps is { Count: > 0 } ss
            ? ss.Average(x => reactions.InformationGain(ModeContext(node), x.Name))
            : reactions.InformationGain(ModeContext(node), a.Name);
        string curNode = "m0"; int curMode = 0; int? curVis = null;
        bool splitAtStart = false;
        // 見た目の状態ごとの「次の画面」の回数を連続履歴から作り直す（分割と取り消しの証拠。保存しない）
        try { foreach (var x in _env.Memory.LoadStream(StreamReplayLimit)) if (x.VisState is int xv) vbook.ObserveNext(xv, x.State, x.Action, x.NextState); }
        catch (Exception e) { Emit("見た目の状態の次の画面の数え直しに失敗: " + e.Message, LogLevel.Warn); }
        // 読み直しの済み印のない分割（v0.10.0 より前に作った分割など）を読み直す
        try { foreach (var (pc, pv, pn) in _env.Memory.PendingSplitReclassifications()) { ReclassifyExistingSplit(explorer, pc, pv, pn); splitAtStart = true; } }
        catch (Exception e) { Emit("分割の読み直しの確認に失敗: " + e.Message, LogLevel.Warn); }
        foreach (var sp in vbook.RefreshSplits()) { ApplySplit(explorer, sp, 0); splitAtStart = true; }
        if (splitAtStart) model = BuildModel(explorer);
        RefreshPriors(explorer, vbook, model);
        if (cap.LatestFrame() is Frame fs0) { int v0; (curNode, curMode, _, _, v0) = Classify(book, vbook, panels, fs0); curVis = v0; }
        Candidates = explorer.Actions.Select(a => new CandidateView(a.Name, string.Join("+", a.Keys), 1, double.NaN)).ToList();
        Emit($"[探索] 画面の種類 {book.Modes.Count} 個・遷移 {explorer.Transitions().Count()} 件・連続履歴 {seqLearner.Steps} 手・系列のパターン {seqLearner.Patterns.Count} 個の記録から続けます。現在の画面: {curNode}", LogLevel.Info);
        Emit("[探索] キーの意味は使わず（中立のキー名）、画面ごとに経験から結果を予測し、予測の確かでない操作・まだ試していない操作・新しい画面に行けた操作を優先して進みます。予測が外れ続けた関係は内部モデルを直します。" +
             "別の画面へ移ったあと戻れなかったことの多い操作は、ほかの操作を一通り試すまで後回しにします。止まるのは停止ボタン・入力が届かないとき・指定の上限だけです。", LogLevel.Ok);
        SetState(SessionState.Running, $"手探り探索（seed={_o.Seed}{(timeLimit > 0 ? $"・{timeLimit / 60:0.#}分" : "")}{(_o.MaxSteps > 0 ? $"・{_o.MaxSteps}手" : "")}）");
        _runClock.Restart();
        _env.Log.Write("explore_begin", ("run_id", RunId), ("seed", _o.Seed), ("actions", explorer.Actions.Select(a => a.Name).ToList()), ("backend", active.Id),
            ("vnext_dual_append", RuntimeFlags.UseExperienceVNextWriter), ("vnext_source", RuntimeFlags.EffectiveSource), ("vnext_ui", RuntimeFlags.UiEnabled));

        var det = new TurnDetector(detOpt, calib.Criteria, calib.IdleMask);
        GameKouryakuKun.Core.Throughput.ThroughputFlags.ApplyEffective();   // v0.39.x: 高速経験収集のフラグ（既定 OFF）
        _env.Log.Write("throughput_flags", ("status", GameKouryakuKun.Core.Throughput.ThroughputFlags.StatusText()));
        var stepTiming = GameKouryakuKun.Core.Throughput.ThroughputFlags.StepTiming ? new GameKouryakuKun.Core.Throughput.StepTiming() : null;   // T0
        StepTimingRef = stepTiming;
        Ledger = GameKouryakuKun.Core.Throughput.ThroughputFlags.LedgerActive ? new GameKouryakuKun.Core.Throughput.ObservationLedger() : null;   // T1/T5: 非同期 Observation 契約
        var settleAdapt = GameKouryakuKun.Core.Throughput.ThroughputFlags.AdaptiveSettle ? new GameKouryakuKun.Core.Throughput.AdaptiveSettle() : null;   // T2
        string? prevSettleKey = null; double prevSettleUsed = 0; bool prevSettleShort = false;
        NoiseModel = GameKouryakuKun.Core.Throughput.ThroughputFlags.NaturalNoise ? new GameKouryakuKun.Core.Throughput.NaturalNoiseModel() : null;   // T3（注釈のみ）
        _pendingObs = null; RawCommitted = 0; RawExtraRows = 0; RawDuplicates = 0; RawAggErrors = 0; _runStartMs = MonoClock.Now; _actionsSent = 0;
        // 画面（の種類）ごとの常時アニメのマスク: 時間切れの手の終わりに動き続けた所を学ぶ（起動時の較正では測れない別の画面の動き）
        var background = new BackgroundActivityBook();
        var motion = new MotionMaskBook();
        try
        {
            int nb = background.Import(_env.Memory.LoadScreenBackground());
            if (nb > 0) _env.Log.Write("screen_background_loaded", ("screens", nb), ("learned", background.Screens));
            int nm = motion.Import(_env.Memory.LoadScreenMotion());
            if (nm > 0) _env.Log.Write("screen_motion_loaded", ("screens", nm), ("masks", motion.Learned));
        }
        catch (Exception e) { Emit("画面ごとの動き続ける所の読込に失敗: " + e.Message, LogLevel.Warn); }
        // 画面ごとの時間的な変化（待つ手・手と手の間の入力なしの変化）を、記録から数え直す（出どころは記録そのもの）
        var temporal = new TemporalBook();
        // v0.27.0: 画面ごとの入力なしの自然な変化の基準。保存がなければ空から（それまでは従来どおり待つ手の実績を使う）
        var natural = new NaturalBaseline(); explorer.Baseline = natural; ExploreBaseline = natural;
        // v0.28.1: 待つ手の情報の値打ち＝入力なしで何が起きるかの不確かさのうち、手の効果の見分けに要る分（画面の変化・移り・画素の区画・文字の区画）
        explorer.WaitInfoValue = node =>
        {
            var (o, c, l, _, _, _) = natural.Counts(node); var (pc, pl, pn) = explorer.PostActionCounts(node);
            double px, tx; lock (PixelRegions) px = PixelRegions.NaturalInformation(node).Value; lock (TextBook) tx = TextBook.Regions.NaturalInformation(node).Value;
            double lc; lock (ObsBuilder) lc = LocalEvents.NaturalInformation(node);   // v0.30.8（P-2）: 局所の出来事の成分
            return new WaitInformation(WaitInformation.Part(c, o, pc, pn), WaitInformation.Part(l, o, pl, pn), px, tx) { Local = lc };
        };
        try { if (_env.Memory.LoadGoalModel("natural_baseline") is string nbj) natural.ImportState(nbj); } catch (Exception e) { Emit("入力なしの基準の読込に失敗: " + e.Message, LogLevel.Warn); }
        var goals = new SelfGoals(); Goals = goals;
        goals.LocalUncertainty = (n, a) => { lock (ObsBuilder) return LocalEvents.InformationGain(n, a, LocalPersistentMinN); };   // v0.29.6: 目的の不確かさにも局所の予測を入れる（手の選び方と同じ合わせ方）
        // v0.20.0: 目的の状態（価値・Horizon・手数ごとの実績・区間の実績・結果の集計）を起動をまたいで引き継ぐ
        StateText = _o.Ocr == null ? null : new GameKouryakuKun.Core.Ocr.StateTextBook();
        try { if (_env.Memory.LoadGoalModel("text_change") is string xj) lock (TextBook) TextBook.ImportState(xj); } catch (Exception e) { Emit("文字の変化の記録の読込に失敗: " + e.Message, LogLevel.Warn); }
        try { if (_env.Memory.LoadGoalModel("region_causality") is string rj) lock (PixelRegions) PixelRegions.ImportState(rj); } catch (Exception e) { Emit("区画の記録の読込に失敗: " + e.Message, LogLevel.Warn); }
        try { if (StateText != null && _env.Memory.LoadGoalModel("state_text") is string tj) StateText.ImportState(tj); } catch (Exception e) { Emit("文字の観測の読込に失敗: " + e.Message, LogLevel.Warn); }
        // v0.27.3: 前景の鍵の文字の配置を、保存してある状態ごとの文字の観測（いちばん多い配置）から始める（前の起動の状態が「文字が未観測」のままにならない）
        if (StateText != null)
            foreach (var (st, en) in StateText.States)
                if (en.Samples > 0) Fg.ObserveText(st, en.Layouts.Count > 0 ? en.Layouts.OrderByDescending(x => x.Value).First().Key : 0, en.Layouts.Count > 0);
        try { if (_env.Memory.LoadGoalModel("env_loss") is string lj) { Losses.ImportState(lj); foreach (var le in Losses.Learnable) ApplyLoss(le, explorer, model); } } catch (Exception e) { Emit("環境の消失の記録の読込に失敗: " + e.Message, LogLevel.Warn); }
        Frontier = new FrontierRegions();
        try { if (_env.Memory.LoadGoalModel("frontier") is string fj) Frontier.ImportState(fj); } catch (Exception e) { Emit("地域の状態の読込に失敗: " + e.Message, LogLevel.Warn); }
        Frontier.Recompute(goals.PlanEdges(explorer, model));
        goals.RegionValue = Frontier.RegionValue;
        var frontier = Frontier;
        explorer.DestinationValue = (node, a) => frontier.DestinationRatio(node, model.Predict(node, a.Name).Top);
        try { if (_env.Memory.LoadGoalModel("self_goals") is string gj) { goals.ImportState(gj); _env.Log.Write("goal_model_loaded", ("horizon", goals.Horizon), ("max_achieved_length", goals.MaxAchievedLength)); } }
        catch (Exception e) { Emit("目的の状態の読込に失敗（既定から始めます）: " + e.Message, LogLevel.Warn); }
        var goalRng = new Random(_o.Seed ^ 0x6011);
        bool goalComposite = false;
        try
        {
            foreach (var x in _env.Memory.LoadStream(StreamReplayLimit)) temporal.ObserveStep(x.State, x.Action, x.Changed, x.NextState);
            foreach (var g in _env.Memory.LoadNoInputEvents()) temporal.ObserveGap(g.From, g.Transition);
            temporal.Break();
        }
        catch (Exception e) { Emit("時間的な変化の記録の読込に失敗: " + e.Message, LogLevel.Warn); }
        // Human Control: 送れるのは Profile の中立のキー名と wait だけ（並び・同時押しの候補は自動の探索だけのもの）
        Human = new HumanControl(explorer.Actions.Where(a => a.Group is ActionGroup.Dpad or ActionGroup.Button or ActionGroup.Chord or ActionGroup.Wait).Select(a => a.Name));
        if (_o.StartWithHumanControl) Human.SetEnabled(true);   // 最初の手の前に切り替え（画面の観測し直し・control_switch の記録）が通常どおり起きる
        var adapt = new AdaptiveTurnTimeout();
        // v0.21.0 Supervisor: 復旧の前に経験を保存する手順（探索の記録・目的の状態。学習 DB は消さない）
        // v0.22.0 成長テストの計測（記録だけ。手の選択・報酬には使わない）
        var runVisits = new Dictionary<string, int>(StringComparer.Ordinal);
        var runActs = new Dictionary<string, int>(StringComparer.Ordinal);
        int snapCreated = goals.Created, snapAchieved = goals.Achieved;
        void Snap(string why, int stepNow)
        {
            try
            {
                var byLen = new Dictionary<int, int>();
                foreach (var (_, len, _, w) in goals.AuditReach(explorer, model, curNode, goals.Horizon))
                {
                    if (w == "not_target") continue;
                    int k = w == "ok" ? len : w == "low_conf" ? -1 : 0;
                    byLen[k] = byLen.GetValueOrDefault(k) + 1;
                }
                var seqs = explorer.Actions.Where(a => a.Group == ActionGroup.Sequence).ToList();
                var om = goals.OutcomeModel.Values.ToList();
                var g = new GrowthSnapshot(RunId ?? 0, _lastSeq, stepNow, goals.Horizon, goals.HorizonChanges, goals.MaxAchievedLength,
                    seqs.Count, seqs.Count(a => explorer.IsActive(a.Name)), GoalSkillUses, SkillCandidates, goals.ReplanGoals, goals.ReplanAchieved,
                    runVisits.Count, explorer.Nodes.Count, GrowthSnapshot.Entropy(runVisits.Values),
                    goals.Value(GoalKind.Reach), goals.Value(GoalKind.Retest), om.Count,
                    om.Count == 0 ? 0 : om.Average(o => Math.Abs(o.DUnknown) + Math.Abs(o.DControl) + Math.Abs(o.DSkills) + Math.Abs(o.DOptions)),
                    goals.Created, goals.Achieved, goals.Created - snapCreated, goals.Achieved - snapAchieved, byLen, why) { AppVersion = AppEnv.Version };
                snapCreated = goals.Created; snapAchieved = goals.Achieved;
                _env.Memory.AppendGrowthSnapshot(g); GrowthSnapshots++;
                MineBoundaries(why, stepNow);
                _env.Log.Write("growth_snapshot", ("reason", why), ("step", stepNow), ("horizon", g.Horizon), ("states_run", g.StatesRun), ("skills_active", g.SkillsActive),
                    ("plan_candidates", byLen.ToDictionary(k => k.Key.ToString(), k => (object?)k.Value)));
            }
            catch (Exception e) { Emit("成長テストの計測の記録に失敗: " + e.Message, LogLevel.Warn); }
        }
        // v0.23.0 ブロック C: エピソードの区切りの候補（記録と表示だけ。報酬・価値・手の選択・目的には使わない）
        var boundaryMiner = new GameKouryakuKun.Core.Boundaries.FlowResetMiner();
        int lastBoundaryStep = int.MinValue;
        void MineBoundaries(string why, int stepNow)
        {
            if (why == "periodic" && stepNow - lastBoundaryStep < BoundaryIntervalSteps) return;
            lastBoundaryStep = stepNow;
            try
            {
                var r = boundaryMiner.Mine(_env.Memory.LoadStream(StreamReplayLimit), _env.Memory.LoadStreamBreaks());
                BoundaryCandidates = r.Accepted.Count;
                lock (_boundaryStates) { _boundaryStates.Clear(); foreach (var c in r.Accepted) _boundaryStates.Add(c.State); }
                foreach (var (st, kind) in _env.Memory.SyncBoundaryCandidates(r, _lastSeq))
                    if (kind != "withdrawn") Discoveries.Add(new Discovery(_lastSeq, DiscoveryKind.FlowReset, st, "区切りの候補（大きな遷移の後にくり返す流れから）"));
                _env.Log.Write("episode_boundaries", ("reason", why), ("steps", r.Steps), ("breaks", r.Breaks), ("examined", r.Examined.Count), ("candidates", r.Accepted.Count), ("large_threshold", r.LargeThreshold));
            }
            catch (Exception e) { Emit("エピソードの区切りの候補の計算に失敗: " + e.Message, LogLevel.Warn); }
        }
        Snap("start", 0);
        int calW = cap.LatestFrame()?.Width ?? 0, calH = cap.LatestFrame()?.Height ?? 0;
        int growthEvery = _o.GrowthTest ? GrowthTestIntervalSteps : GrowthIntervalSteps;
        _flushForRecovery = () =>
        {
            Snap("recovery", Step);
            SaveExploration(book, explorer, seqLearner, runTransitions, vbook, reactions, seqMiner);
            try { _env.Memory.SaveScreenMotion(motion.Export().ToList()); } catch (Exception e) { Emit("画面ごとの動き続ける所の保存に失敗: " + e.Message, LogLevel.Warn); }
            if (StateText != null) try { string tj; lock (StateText) tj = StateText.ExportState(); _env.Memory.SaveGoalModel("state_text", tj); } catch (Exception e) { Emit("文字の観測の保存に失敗: " + e.Message, LogLevel.Warn); }
            try { string xj; lock (TextBook) xj = TextBook.ExportState(); _env.Memory.SaveGoalModel("text_change", xj); string rj; lock (PixelRegions) rj = PixelRegions.ExportState(); _env.Memory.SaveGoalModel("region_causality", rj); } catch (Exception e) { Emit("文字の変化の記録の保存に失敗: " + e.Message, LogLevel.Warn); }
            try { _env.Memory.SaveGoalModel("frontier", Frontier!.ExportState()); } catch (Exception e) { Emit("地域の状態の保存に失敗: " + e.Message, LogLevel.Warn); }
            try { _env.Memory.SaveGoalModel("natural_baseline", natural.ExportState()); } catch (Exception e) { Emit("入力なしの基準の保存に失敗: " + e.Message, LogLevel.Warn); }
            try { _env.Memory.SaveGoalModel("self_goals", goals.ExportState()); } catch (Exception e) { Emit("目的の状態の保存に失敗: " + e.Message, LogLevel.Warn); }
        };
        RaiseSystem(SystemEventKind.SupervisorStarted, $"起動の方法 {SupervisorConfig.LaunchMethod}・再起動の上限 {SupervisorConfig.MaxRestarts} 回／{SupervisorConfig.RestartWindowSec / 60:0} 分");
        double lastBoundaryAt = MonoClock.Now; int dbErrors = 0; double lastFileCheck = MonoClock.Now;
        CheckWatchedFiles();   // v0.23.1 実機: アプリを閉じている間の変化は開始のときにすぐ知らせる（60 秒を待たない）
        string endReason = "user_stop";
        Frame? prevAfter = null; bool prevNoChange = false; bool prevWasWait = false; int rechoose = 0;
        // 操作の並びを送っている途中（1手ずつ普通の手として送り、最後まで送れたら並びの結果として数える）
        var pending = new Queue<ExploreAction>(); ExploreAction? composite = null; string compStart = ""; bool compChanged = false; int compDisc = 0;
        PredictedOutcome? compPred = null;
        for (int step = 1; !_quit && StopReason == null; step++)
        {
            if (step > maxSteps) { endReason = "steps_completed"; StopReason = $"指定の {_o.MaxSteps} 手を終えました"; break; }
            if (timeLimit > 0 && _runClock.Elapsed.TotalSeconds >= timeLimit) { endReason = "time_completed"; StopReason = $"指定の {timeLimit / 60:0.#} 分を終えました"; break; }
            if (!WaitRunnable()) break;
            // ---- v0.21.0 Game Supervisor: 手の前にプロセス・ウィンドウ・取得・応答を確かめる（ゲームの中身は見ない） ----
            var pr = Supervisor.Poll();
            if (pr == PollResult.Aborted && Supervisor.State != SupervisorState.Aborted) break;   // v0.23.1 実機: 復旧の途中で人が停止した（停止として終える）
            if (pr == PollResult.Aborted)
            {
                endReason = "recovery_aborted";
                StopReason = $"Supervisor: RECOVERY_ABORTED（{Supervisor.AbortReason}）。探索と再起動を止め、DB を保存しました。人の確認を待ちます";
                Emit(StopReason, LogLevel.Error);
                break;
            }
            if (pr == PollResult.Hold) { step--; Thread.Sleep(500); continue; }
            if (pr == PollResult.Recovered || _reobserveNeeded)
            {
                // 復旧した: 新しい接続で、取得の基準（較正）から測り直し、今の画面を観測し直す。
                // 中断の前の状態と後の状態を 1 つの「状態＋操作→結果」にしない（前の手の結果の画面を捨て、送る前の手・並び・目的・計画を捨てる）
                bool recovered = pr == PollResult.Recovered;
                ProcessLoss(explorer, model, detOpt.MaxMs);   // 帰属の時間: 1 手の結果を待つ上限（自分の手の結果として待つ長さと同じ）
                _reobserveNeeded = false;
                cap = _con.Capture; game = _con.Hwnd;
                foreach (var r in new[] { PauseReason.Minimized, PauseReason.Cloaked, PauseReason.WindowLost, PauseReason.CaptureError }) _pause.Set(r, false);
                // v0.22.1: 較正は「入力なしの静止した画面の取得の揺らぎ」を測るもの。実機 v0.21.0 で、起動し直した直後の動いている画面で測り直し
                // （変化率 0.86）、その後の手がすべて「変化なし」になった。解像度が同じなら取得の揺らぎは同じなので較正を引き継ぎ、
                // 解像度が変わったときだけ測り直す。測り直した値が揺らぎとしてありえない大きさなら、画面が動いている間とみなして待って測り直す
                var fr0 = cap.WaitForNewFrame(cap.Current?.LastFrameId ?? 0, 500) ?? cap.LatestFrame();
                if (fr0 != null && (fr0.Width, fr0.Height) != (calW, calH))
                {
                    CalibrationResult? nc = null;
                    for (int tries = 0; tries < RecalibrateTries && !_quit; tries++)
                    {
                        var c1 = CalibrateNoise(cap, detOpt.Diff);
                        if (c1 != null && c1.Criteria.ChangeRatio <= MaxNoiseRatio) { nc = c1; break; }
                        _env.Log.Write("calibration_rejected", ("step", step), ("try", tries + 1), ("change_ratio", c1?.Criteria.ChangeRatio));
                        Thread.Sleep(2000);
                    }
                    if (nc != null) { calib = nc; det = new TurnDetector(detOpt, calib.Criteria, calib.IdleMask); (calW, calH) = (fr0.Width, fr0.Height); }
                    else Emit("[Supervisor] 解像度が変わりましたが、画面が落ち着かず較正を測り直せませんでした。前の較正のまま続けます", LogLevel.Warn);
                }
                else { Calibration = calib; _env.Log.Write("calibration_kept", ("step", step), ("w", fr0?.Width), ("h", fr0?.Height), ("change_ratio", calib.Criteria.ChangeRatio)); }
                string oldNode = curNode;
                if (fr0 != null) { int v1; (curNode, curMode, _, _, v1) = Classify(book, vbook, panels, fr0); curVis = v1; }
                int stale = pending.Count + (composite != null ? 1 : 0) + Human.DiscardQueued();
                StaleDiscarded += stale;
                prevAfter = null; prevNoChange = false; pending.Clear(); composite = null; rechoose = 0; goalComposite = false;
                explorer.ResetContext(); temporal.Break(); goals.Abandon(recovered ? "観測の中断（Supervisor の復旧）" : "ウィンドウの大きさの変化", step);
                foreach (var ge in goals.Events.Skip(_goalEventsLogged).ToList()) LogGoal(ge, step);
                if (recovered)
                {
                    ResumedReobserved++;
                    _lastSupervisorRecoveryStep = step;
                    LatestPlanProposal = null; // 復旧前後の計画を接着しない
                }
                _env.Log.Write("supervisor_reobserve", ("step", step), ("recovered", recovered), ("from_node", oldNode), ("observed_node", curNode), ("stale_discarded", stale), ("human", Human.Enabled),
                    ("w", fr0?.Width), ("h", fr0?.Height));
                Emit($"[Supervisor] 画面を観測し直しました（今の画面 {curNode}・捨てた古い手 {stale}）。{(Human.Enabled ? "Human Control のまま続けます" : "探索を続けます")}", LogLevel.Ok);
                Changed?.Invoke();
            }
            if (MonoClock.Now - lastFileCheck > 60_000) { lastFileCheck = MonoClock.Now; CheckWatchedFiles(); }
            // 自動↔人間の切替: 古い画面を前提に手を出さないよう、今の画面を観測し直してから次の手を決める
            if (Human.TakeSwitch())
            {
                var fr = cap.WaitForNewFrame(cap.Current?.LastFrameId ?? 0, 200) ?? cap.LatestFrame();
                string oldNode = curNode;
                if (fr != null) { int v1; (curNode, curMode, _, _, v1) = Classify(book, vbook, panels, fr); curVis = v1; }
                prevAfter = null; prevNoChange = false; pending.Clear(); composite = null; rechoose = 0;
                explorer.ResetContext(); temporal.Break(); goals.Abandon("自動と人間の切替", step);
                _env.Log.Write("control_switch", ("step", step), ("human", Human.Enabled), ("from_node", oldNode), ("observed_node", curNode));
                Emit(Human.Enabled ? $"[Human Control] 人間の操作に切り替えました。自動の手の選択だけを止め、観測と記録は続けます（今の画面 {curNode}）"
                                   : $"[Human Control] 自動の探索に戻しました。画面を観測し直しました（今の画面 {curNode}）", LogLevel.Ok);
            }
            bool isHuman = Human.Enabled;
            if (isHuman || explorer.AnyUserAllowed) { if (_allDisabledNotified) { _allDisabledNotified = false; _env.Log.Write("explore_resumed_inputs_enabled", ("step", step)); } AllDisabledWaiting = false; }
            ExploreChoice choice;
            if (isHuman)
            {
                if (!Human.TryDequeue(out var hname)) { Thread.Sleep(30); step--; continue; }   // 人間の操作を待つ（観測と記録は手ごとに続く）
                var ha = explorer.Actions.First(a => a.Name == hname);
                choice = new ExploreChoice(ha, "人間の操作（Human Control）", 0) { Rule = "human" };
            }
            else if (!explorer.AnyUserAllowed)
            {
                // v0.32.5: 自律探索で使える入力がすべて止められている。手を送らずに一時停止し、入力を有効にするか Human Control に切り替えるのを待つ（観測は続かない）
                if (!_allDisabledNotified) { _allDisabledNotified = true; Emit("[入力の制限] 自律探索で使える入力がすべて止められています。右側の「自律探索で使う入力」で入力を有効にすると再開します（Human Control は使えます）", LogLevel.Warn); _env.Log.Write("explore_paused_all_disabled", ("step", step)); Changed?.Invoke(); }
                AllDisabledWaiting = true; Thread.Sleep(200); step--; continue;
            }
            else if (pending.Count > 0 && composite != null && !explorer.IsUserAllowed(pending.Peek().Name))
            {
                // v0.32.5: 並びの残りの手が人に止められた。並びを打ち切って次の手を選び直す
                _env.Log.Write("sequence_abandoned_user_disabled", ("step", step), ("sequence", composite.Name), ("next", pending.Peek().Name));
                pending.Clear(); composite = null; step--; continue;
            }
            else if (pending.Count > 0 && composite != null)
                choice = new ExploreChoice(pending.Dequeue(), $"{composite.Name} の {composite.Steps!.Count - pending.Count}/{composite.Steps!.Count} 手目", 0) { Rule = "sequence_step" };
            else
            {
                goalComposite = false;
                var gcand = GoalStepOrNull(goals, explorer, model, curNode, step, goalRng);
                if (gcand != null && !explorer.IsUserAllowed(gcand.Action.Name)) { goals.Abandon("人が止めた入力（自律探索で使わない）", step); foreach (var ge in goals.Events.Skip(_goalEventsLogged).ToList()) LogGoal(ge, step); gcand = null; }
                if (gcand is { } gc) { choice = gc; goalComposite = gc.Action.Group == ActionGroup.Sequence; }
                else
                {
                    choice = explorer.Choose(curNode);
                    // S5: Goal 経路は割込み禁止（Human/Sequence はこの分岐に来ない）
                    choice = TryApplyIntrinsicMixer(explorer, curNode, step, choice);
                }
                if (choice.Action.Group == ActionGroup.Sequence && choice.Action.Steps is { Count: > 0 } subs)
                {
                    composite = choice.Action; compStart = curNode; compChanged = false; compDisc = explorer.Discoveries; compPred = model.Predict(curNode, composite.Name);
                    pending.Clear(); foreach (var x in subs) pending.Enqueue(x);
                    _env.Log.Write("sequence_try", ("step", step), ("sequence", composite.Name), ("node", curNode), ("steps", subs.Select(x => x.Name).ToList()));
                    choice = choice with { Action = pending.Dequeue(), Reason = $"{composite.Name}（{string.Join(" → ", subs.Select(x => x.Name))}）の 1/{subs.Count} 手目 — {choice.Reason}" };
                }
            }
            var ca = choice.Action;
            // 手を送る前の予測（次の画面・画面が変わるか・証拠の量と出どころ）
            var prediction = model.Predict(curNode, ca.Name);
            _lastWorldPred = prediction;   // v0.29.5: 構造化した観測の予測の層に並べる
            var action = ca.IsWait ? new InputAction(Array.Empty<string>(), Label: "wait") : InputAction.Tap(ca.Keys);
            string source = isHuman ? "human" : "agent";
            // 入力なしの基準との比較（手を選ぶ前の経験から。記録と表示のため）
            var attribution = ca.IsWait ? null : NaturalChange.Attribute(explorer, curNode, ca.Name);
            string reason = isHuman ? $"人間の操作: 画面 {curNode} で「{ca.Name}」" : $"手探り探索: 画面 {curNode} で「{ca.Name}」— {choice.Reason}";
            // v0.27.5: 手の選択の点数の内訳（全候補。目的・並び・人間の手でも、乱数を使わない同じ計算で残す。選択そのものは変えない）
            try
            {
                var sc = choice.Rule == "scored" && choice.Candidates is { Count: > 0 } cc ? cc : explorer.ScoreCandidates(curNode);
                ScoreLog.Add(choice.Rule, sc.Where(x => !x.ExcludedByUser).ToList(), ca.Name);
                var userDisabled = _env.Availability.Disabled.OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (userDisabled.Count > 0 && !isHuman && step != _lastRestrictedStep) { UserRestrictedSteps++; _lastRestrictedStep = step; }   // v0.32.6: 選び直し（同じ手番）は 1 手に数える
                _chosenCand = sc.FirstOrDefault(x => x.Action == ca.Name);
                _lastChosenText = _chosenCand is { } cc0 ? $"点数 {cc0.Score:0.00}（確率 {cc0.Probability:0.00}・新しさ {cc0.Novelty:0.00}・変化 {cc0.Change:0.00}・文字 {cc0.Text:0.00}・不確かさ {cc0.UncTerm:0.00}・試した {cc0.DirectTries}）" : null;
                if (sc.FirstOrDefault(x => x.Action == ExploreAction.Wait.Name)?.Wait is { } wv) { WaitInfoN++; WaitInfoSum += wv.Total; if (step <= 1 || WaitInfoFirst < 0) WaitInfoFirst = wv.Total; WaitInfoLast = wv.Total; }
                _env.Log.Write("decision_scores", ("step", step), ("node", curNode), ("rule", choice.Rule), ("chosen", ca.Name), ("source", source), ("reason", choice.Reason), ("user_disabled", userDisabled),
                    ("fields", GameKouryakuKun.Core.Exploration.ScoreBreakdown.Fields), ("c", sc.Select(x => (object?)GameKouryakuKun.Core.Exploration.ScoreBreakdown.Row(x)).ToList()));
            }
            catch (Exception e) { Emit("点数の内訳の記録に失敗: " + e.Message, LogLevel.Warn); }
            double since = MonoClock.Now - lastBoundaryAt;
            if (since < profile.Input.MinIntervalMs) Thread.Sleep((int)(profile.Input.MinIntervalMs - since));
            var baseline = cap.WaitForNewFrame(cap.Current?.LastFrameId ?? 0, 200) ?? cap.LatestFrame();
            if (baseline == null) { Emit("フレームがありません。待機します。", LogLevel.Warn); Thread.Sleep(500); step--; continue; }
            // 「変化なし」と判定した後、次の入力の前に画面が変わっていた＝判定が早すぎた（打ち切られた標本）。待ち時間の自己調整に戻す
            string motionKey = $"m{curMode}";
            var turnMask = motion.MaskFor(motionKey, calib.IdleMask);
            var gap = NoInputGate.Check(prevAfter, baseline, calib.Criteria, detOpt.Diff, turnMask, curNode,
                f => Classify(book, vbook, panels, f), c => c.Node, rechoose, MaxRechoose);
            // v0.32.2（P-7）: 前の手の静止の後に来た変化を、次の手の構造化した観測の temporal.delayed に入れる
            _pendingDelayed = null;
            if (gap.Changed) try { _pendingDelayed = GameKouryakuKun.Core.Observation.DelayedChange.Of(prevAfter, baseline, step - 1, prevNoChange); if (_pendingDelayed != null) DelayedRecorded++; } catch { }
            if (prevNoChange && gap.Changed)
            {
                LateChanges++;
                adapt.AddLateChange(detOpt.NoChangeTimeoutMs);
                if (settleAdapt != null && prevSettleKey != null) settleAdapt.LateChange(prevSettleKey, prevSettleUsed, prevSettleShort);
                _env.Log.Write("late_change", ("step", step - 1), ("nochange_ms", Math.Round(detOpt.NoChangeTimeoutMs)));
            }
            // 入力なしの変化（v0.14.1。v0.15.0 で NoInputGate に切り出し、学習 DB に記録）: 前の手の結果の画面から、次の入力の直前までに画面が変わっていた。
            // 別の画面になっていたら（入力なしの遷移）、前の画面向けに選んだ手は送らずにこの画面で選び直す（入力なしの変化を次の手の結果に混ぜない）
            // v0.27.0: 手と手の間の確認はすべて入力なしの観測（変わらなかった区間も基準の材料。観測そのものは消さない）
            // v0.27.1: 判定は手の正味の変化と同じ基準（NetChanged）。キーを送った手の直後の区間は遅れた反応が混ざるので率に使わず、数えるだけ
            if (prevAfter != null && baseline.TimestampMs - prevAfter.TimestampMs > 0)
            {
                if (prevWasWait) natural.ObserveNatural(curNode, baseline.TimestampMs - prevAfter.TimestampMs, gap.NetChanged, gap.Transition && gap.NetChanged, fromWait: false);
                else natural.ObserveAfterAction(curNode, gap.NetChanged);
            }
            if (NoiseModel != null && prevAfter != null && prevAfter.Width == baseline.Width && prevAfter.Height == baseline.Height)
                ObserveNoise(motionKey, prevAfter, baseline, detOpt.Diff, step, GameKouryakuKun.Core.Throughput.FramePosition.Gap, natural: true);
            if (gap.Changed && prevAfter != null)
            {
                NoInputChanges++;
                var (nb, mb, mbNew, _, vb) = gap.Classified;
                double gapMs = Math.Round(baseline.TimestampMs - prevAfter.TimestampMs);
                _env.Log.Write("no_input_change", ("step", step), ("from", curNode), ("to", nb), ("transition", gap.Transition),
                    ("ratio", Math.Round(gap.Ratio, 4)), ("gap_ms", gapMs), ("rechosen", gap.Rechoose));
                temporal.ObserveGap(curNode, gap.Transition);
                if (gap.Transition) temporal.BreakRun(); else temporal.ObserveNaturalChange(curNode, baseline.TimestampMs);
                try { _env.Memory.AddNoInputEvent(RunId.Value, nextSeq, curNode, nb, gap.Transition, gap.Ratio, gapMs, gap.Rechoose && !isHuman); }
                catch (Exception e) { Emit("入力なしの変化の記録に失敗: " + e.Message, LogLevel.Warn); }
                if (gap.Transition)
                {
                    NoInputTransitions++;
                    Discoveries.Add(new Discovery(nextSeq, DiscoveryKind.NoInputTransition, $"{curNode} → {nb}", "手と手の間に入力なしで"));
                    if (mbNew) Discoveries.Add(new Discovery(nextSeq, DiscoveryKind.ScreenMode, $"m{mb}", $"{curNode} から入力なしで"));
                    if (gap.Rechoose && isHuman)
                    {
                        // 人間の手は捨てない: 今の画面を前提にして、そのまま送る（選び直すのは自動の手だけ）
                        curNode = nb; curMode = mb; curVis = vb; prevAfter = baseline; prevNoChange = false;
                    }
                    else if (gap.Rechoose)
                    {
                        rechoose++; Rechosen++;
                        curNode = nb; curMode = mb; curVis = vb; prevAfter = baseline; prevNoChange = false; pending.Clear(); composite = null;
                        step--; continue;
                    }
                }
            }
            rechoose = 0;
            // 待つ手（中立の観測）はキーを送らない。入力の直後と同じ判定で、何もしないと画面がどうなるかを見る
            bool waiting = ca.IsWait;
            if (stepTiming != null) { double tst = MonoClock.Now; stepTiming.Mark("decide", tst); stepTiming.SendStarted(tst); }
            Ledger?.BeginAction(step); _actionsSent++;
            var receipt = waiting ? new InputReceipt(true, "none", "wait", MonoClock.Now, MonoClock.Now, 0, "キーを送らずに待つ") : active.Send(action);
            stepTiming?.Mark("send", MonoClock.Now);
            // A（送達）: OS・入力方式として送れたか。続けて失敗したらシステムの失敗として止める
            if (!waiting && Delivery.Record(receipt))
            {
                // v0.23.1: 入力の方式の失敗は Supervisor の信号（次の見張りで INPUT_LOST → 入力のつなぎ直し。直らなければ INPUT_LOOP で止めて人を待つ）
                InputDeliveryFailed = true; _inputFailed = true;
                Emit($"入力の送信が {Delivery.ConsecutiveFailures} 回続けて失敗しました（システムの失敗: {receipt.Detail}）。Supervisor が入力をつなぎ直します", LogLevel.Warn);
                pending.Clear(); composite = null; step--; continue;
            }
            if (receipt.Ok) _lastSent = (curNode, ca.Name, source, MonoClock.Now, waiting, _lastSeq + 1);
            if (!receipt.Ok) { Emit($"[{step}] {action.Describe()} 送信拒否: {receipt.Detail}", LogLevel.Warn); pending.Clear(); composite = null; step--; Thread.Sleep(200); continue; }
            if (waiting) WaitTurns++;
            det.Mask = turnMask;
            det.Criteria = background.CriteriaFor(motionKey, calib.Criteria);   // その画面のふだんの動きの量まで静止の基準を緩める（学んだ画面だけ）
            if (settleAdapt != null)
            {
                // T2: 変えるのは no-change の待ちだけ（StableMs・差分の基準・上限はそのまま）。待つ手は全体の値（入力なしの観測を短くしない）
                double nc = waiting ? detOpt.NoChangeTimeoutMs : settleAdapt.Recommend(motionKey, detOpt.NoChangeTimeoutMs, detOpt.StableMs);
                det.Options = det.Options with { NoChangeTimeoutMs = nc };
                prevSettleKey = motionKey; prevSettleUsed = nc; prevSettleShort = nc < detOpt.NoChangeTimeoutMs;
            }
            det.Begin(baseline, receipt.DownAtMs, waiting ? "wait" : action.Describe());
            bool ocrBusyAtTurn = Volatile.Read(ref _ocrBusy) != 0;   // v0.27.2: 文字の読み取り中の手かどうか（画面の取り込み・静止の判定の遅れの比較）
            var b = TurnRunner.Await(det, baseline.Id, cap.WaitForNewFrame, () => MonoClock.Now, PauseReasonNow);
            stepTiming?.Mark("settle", MonoClock.Now);
            if (settleAdapt != null && b.Kind != TurnKind.Aborted)
            {
                if (!waiting && b.FirstChangeMs is double sfc) settleAdapt.ObserveFirstChange(motionKey, sfc);
                if (b.Kind == TurnKind.NoChange && prevSettleShort) settleAdapt.ShortenedNoChange(motionKey);
            }
            if (b.Kind != TurnKind.Aborted) Ocr2.TurnTiming(ocrBusyAtTurn, b.BoundaryMs, prevAfter == null ? double.NaN : baseline.TimestampMs - prevAfter.TimestampMs);
            if (b.Kind == TurnKind.Timeout) TimeoutTurns++;
            if (b.Kind == TurnKind.Aborted) AbortedTurns++;
            if (b.Kind == TurnKind.Normal && b.NetChange && b.PeakFromBaseline is { Comparable: true } pk) background.ObserveResponse(motionKey, pk.ChangedRatio);
            if (b.Kind == TurnKind.Timeout && b.LateStats is { Count: > 0 } ls)
            {
                var bu = background.Observe(motionKey, ls, calib.Criteria);
                try { _env.Memory.SaveScreenBackground(background.Export(motionKey)); } catch (Exception e) { Emit("ふだんの動きの量の保存に失敗: " + e.Message, LogLevel.Warn); }
                if (bu.Learned)
                {
                    BackgroundLearned++;
                    _env.Log.Write("background_activity", ("step", step), ("screen", motionKey), ("timeouts", bu.Timeouts), ("still_ratio", Math.Round(bu.StillRatio, 5)),
                        ("still_component", Math.Round(bu.StillComponent, 5)), ("capped", bu.Capped));
                    Emit($"[観測] 画面 {motionKey} のふだんの動きの量（変化率 {bu.StillRatio:P2}・まとまり {bu.StillComponent:P2}{(bu.Capped ? "。上限で止めた" : "")}）を、落ち着きの判定の基準にしました（時間切れ {bu.Timeouts} 回から）", LogLevel.Info);
                }
            }
            if (b.Kind == TurnKind.Timeout && b.LateMotion is { } late)
            {
                var mu = motion.Observe(motionKey, b.LateGrid, late);
                if (mu.Changed)
                {
                    if (!mu.TooWide) MotionMasksLearned++;
                    _env.Log.Write("motion_mask", ("step", step), ("screen", motionKey), ("timeouts", mu.Timeouts), ("blocks", mu.Blocks), ("coverage", Math.Round(mu.Coverage, 4)), ("too_wide", mu.TooWide));
                    if (!mu.TooWide) Discoveries.Add(new Discovery(nextSeq, DiscoveryKind.Motion, motionKey, $"画面の {mu.Coverage:P1}・時間切れ {mu.Timeouts} 回から"));
                    try { _env.Memory.SaveScreenMotion(motion.Export().Where(x => x.Key == motionKey)); } catch (Exception e) { Emit("画面ごとの動き続ける所の保存に失敗: " + e.Message, LogLevel.Warn); }
                    Emit(mu.TooWide
                        ? $"[観測] 画面 {motionKey} は入力の後も広い範囲（画面の {mu.Coverage:P0}）が動き続けます。隠すには広すぎるため、時間切れのまま続けます"
                        : $"[観測] 画面 {motionKey} で入力の後も動き続ける所（画面の {mu.Coverage:P1}）を、画面の落ち着きの判定から外しました（時間切れ {mu.Timeouts} 回から学習）", LogLevel.Info);
                }
            }
            lastBoundaryAt = MonoClock.Now;
            var after = cap.LatestFrame();
            Step = step;
            string? node2 = null; int mode2 = curMode; bool modeNew = false, panel2 = false; int vis2 = 0;
            if (b.Kind != TurnKind.Aborted) (node2, mode2, modeNew, panel2, vis2) = Classify(book, vbook, panels, after ?? baseline);
            long? turnId = null;
            try
            {
                var ids = _env.Memory.CommitTurn(RunId.Value, new TurnCommit(
                    new ObservationRecord(null, Json.Obj(("baseline_frame_id", baseline.Id), ("w", baseline.Width), ("h", baseline.Height), ("capture", cap.Current?.Id), ("node_before", curNode), ("node_after", node2)),
                        (after ?? baseline).Downscale(4), "turn"),
                    new ActionRecord(ca.Name, "explore", "explore", Json.Obj(("type", "explore"), ("source", source), ("seed", _o.Seed), ("node", curNode), ("text", reason), ("score", choice.Score),
                        ("prior", choice.Priors.ToList()), ("information", Math.Round(choice.Information, 4)), ("rule", choice.Rule),
                        ("predicted_next", prediction.TopNext), ("p_predicted", Math.Round(prediction.PTop, 4))), Score: choice.Score),
                    new OutcomeRecord(b.NetChange, KindId(b.Kind), b.BoundaryMs,
                        Json.Obj(("first_change_ms", b.FirstChangeMs), ("settled_ms", b.SettledMs), ("frames", b.FramesSeen), ("note", b.Note),
                            ("peak_ratio", b.PeakFromBaseline?.ChangedRatio), ("final_ratio", b.FinalFromBaseline?.ChangedRatio), ("input_backend", receipt.BackendId)))));
                turnId = ids.TurnId; dbErrors = 0; RawCommitted++;
            }
            catch (Exception e)
            {
                Emit("memory.sqlite への記録に失敗: " + e.Message, LogLevel.Error);
                if (++dbErrors >= 3) { StopReason = "記録の失敗が3回続いたため中止"; endReason = "abort"; }
            }
            if (GameKouryakuKun.Core.Throughput.ThroughputFlags.RawAggSplit && turnId != null && b.Kind != TurnKind.Aborted && node2 != null)
            {
                // T4 / C2: Raw は潰さず 1 手 1 行（memory の turn 行への参照と静止判定の根拠）。集約は現在の表現で別に数える
                try
                {
                    _rawAgg ??= RawAggStore.Open(_env.ProfileDataDir, AppEnv.Version);
                    var fr = after ?? baseline;
                    bool dup = _rawAgg.Append(new GameKouryakuKun.Core.Throughput.RawExperience(nextSeq, step, turnId, fr.Id, fr.TimestampMs, curNode, ca.Name, node2, b.NetChange, KindId(b.Kind),
                        Json.Obj(("baseline_frame_id", baseline.Id), ("boundary_ms", b.BoundaryMs), ("first_change_ms", b.FirstChangeMs), ("settled_ms", b.SettledMs), ("frames", b.FramesSeen),
                            ("peak_ratio", b.PeakFromBaseline?.ChangedRatio), ("final_ratio", b.FinalFromBaseline?.ChangedRatio), ("source", source), ("input_backend", receipt.BackendId))));
                    RawExtraRows++; if (dup) RawDuplicates++;
                }
                catch (Exception e) { RawAggErrors++; _env.Log.Write("raw_agg_failed", ("step", step), ("error", e.Message)); }
            }
            stepTiming?.Mark("commit", MonoClock.Now);
            if (NoiseModel != null && after != null && b.Kind != TurnKind.Aborted && after.Width == baseline.Width && after.Height == baseline.Height)
                ObserveNoise(motionKey, baseline, after, detOpt.Diff, step, GameKouryakuKun.Core.Throughput.FramePosition.Settle, natural: waiting);
            var item = new TurnLogItem(DateTime.Now, step, ca.Name, action.Describe(), reason, b.Kind, b.BoundaryMs, b.FirstChangeMs, b.NetChange, b.Note, baseline.Id, baseline.Width, baseline.Height, turnId, node2);
            Last = item;
            Turn?.Invoke(item);
            bool gameFgNow = WindowLocator.IsForeground(game);
            if (gameFgNow) Separation.TurnsGameForeground++; else Separation.TurnsOtherForeground++;
            if (!gameFgNow && b.Kind == TurnKind.NoChange) Separation.NoChangeWhileBackground++;
            double? maskedAct = null;
            if (turnMask != null && after != null && b.Kind != TurnKind.Aborted && turnMask.CountSet() > 0)
            {
                var full = FrameDiff.Compare(baseline, after, detOpt.Diff, null);
                if (full.ChangedBlocks is { } fb && fb.Length == turnMask.Bits.Length)
                {
                    int inMask = 0; for (int k = 0; k < fb.Length; k++) if (fb[k] && turnMask.Bits[k]) inMask++;
                    maskedAct = (double)inMask / turnMask.CountSet();
                    MaskedTurns++; MaskedActivitySum += maskedAct.Value; motion.ObserveActivity(motionKey, maskedAct.Value);
                    if (!b.NetChange && inMask > 0) MaskedOnlyTurns++;
                }
            }
            _env.Log.Write("turn", ("step", step), ("action", ca.Name), ("source", source), ("kind", b.Kind.ToString()), ("boundary_ms", b.BoundaryMs), ("net_change", b.NetChange), ("turn_id", turnId), ("game_fg", gameFgNow), ("node", node2), ("frames", b.FramesSeen),
                ("masked_activity", maskedAct is double ma ? Math.Round(ma, 4) : null), ("text_state", Ocr2.StateAt(step)));
            if (b.Kind == TurnKind.NoChange && node2 != null && node2 != curNode) NoChangeButNodeChanged++;
            prevAfter = after; prevNoChange = b.Kind == TurnKind.NoChange && !waiting;  prevWasWait = waiting;   // 待つ手の後の変化は「判定が早すぎた」ではない
            if (b.Kind == TurnKind.Aborted || node2 == null) { prevNoChange = false; pending.Clear(); composite = null; Changed?.Invoke(); continue; }   // 一時停止などで中断: 次の手で WaitRunnable が待つ

            // 時間切れ＝動き続ける画面。入力の効果とみるのは、その画面が入力なしでは変わらない（待つ手で変わった割合が半分以下・まだ待っていない）ときだけ（v0.15.0。K4）。
            // 入力なしでも変わる画面での時間切れは、画面の差（NetChange）だけを証拠にする
            bool changedNow = b.NetChange;
            if (b.Kind == TurnKind.Timeout && !b.NetChange)
            {
                var wp = waiting ? null : model.Predict(curNode, ExploreAction.Wait.Name);
                bool autonomous = wp != null && wp.DirectN + wp.PriorN > 0 && wp.PChange > AutonomousChangeRate;
                changedNow = waiting || !autonomous;
                if (!changedNow) TimeoutWeak++;
            }
            // B（視覚的な反応）: キーを送れた手で画面が変わったか。数えるだけで止めない（未知の状態の性質かもしれない）
            if (!waiting)
            {
                Delivery.RecordReaction(changedNow);
                if (Delivery.NoReactionStreak > 0 && Delivery.NoReactionStreak % NoReactionNoticeSteps == 0)
                {
                    NoReactionWarnings++;
                    _env.Log.Write("no_reaction_streak", ("step", step), ("streak", Delivery.NoReactionStreak), ("node", curNode));
                    Emit($"[観測] キーを送れた手で、画面が {Delivery.NoReactionStreak} 手続けて変わっていません（入力の送信は成功）。この画面の性質として記録し、探索を続けます", LogLevel.Info);
                }
            }
            if (isHuman) HumanSteps++; else AgentSteps++;
            temporal.ObserveStep(curNode, ca.Name, changedNow, node2);
            if (ca.IsWait) natural.ObserveNatural(curNode, b.BoundaryMs, changedNow, node2 != curNode, fromWait: true); else natural.ObserveActionTurn(curNode, b.BoundaryMs);
            if (ca.IsWait && changedNow && node2 == curNode) temporal.ObserveNaturalChange(curNode, (after ?? baseline).TimestampMs);
            else if (!ca.IsWait || node2 != curNode) temporal.BreakRun();
            int discBefore = explorer.Discoveries;
            {
                bool newNode = !explorer.Nodes.ContainsKey(node2);
                bool newEdge = !(explorer.Nodes.TryGetValue(curNode, out var nsx) && nsx.Actions.TryGetValue(ca.Name, out var asx) && asx.To.ContainsKey(node2));
                if (!isHuman) ScoreLog.Outcome(_chosenCand, changedNow, newEdge, newNode);
                _chosenCand = null;
            }
            explorer.Observe(curNode, ca, node2, changedNow);
            var key = (curNode, ca.Name, node2);
            runTransitions[key] = runTransitions.TryGetValue(key, out var tn) ? tn + 1 : 1;
            if (node2 != curNode) _env.Log.Write("mode_transition", ("step", step), ("from", curNode), ("key", ca.Name), ("to", node2), ("new_nodes", explorer.Discoveries));
            // 連続経験履歴に1手を足し、系列の統計（反復・循環・境界の候補）を更新する。実行単位は学習に渡さない（run_id は記録の照合用）
            var st = new StreamStep(nextSeq++, curNode, ca.Name, changedNow, node2, b.FinalFromBaseline?.ChangedRatio ?? 0);
            _lastSeq = st.Seq;
            if (modeNew) Discoveries.Add(new Discovery(st.Seq, DiscoveryKind.ScreenMode, $"m{mode2}", $"{curNode} で「{ca.Name}」の後"));
            if (explorer.Discoveries > discBefore) Discoveries.Add(new Discovery(st.Seq, DiscoveryKind.Node, node2, $"{curNode} で「{ca.Name}」"));
            var so = seqLearner.Observe(st);
            try { _env.Memory.AppendStream(RunId.Value, turnId, st, so.Surprisal, so.Boundary, so.Excess, curVis, source); } catch (Exception e) { Emit("連続経験履歴の記録に失敗: " + e.Message, LogLevel.Warn); }
            // 予測と結果を比べ、外れ方が持続的に変わった関係を修正する。判断の記録（点数の内訳・予測・比較）を残す
            // 観測の確かさ: 割り当てが境目の近く（どちらの状態ともいえる）なら、その手の外れは修正のきっかけにしない（予測の集計には入れる）
            double obsVis = vbook.LastRatio, obsMode = _lastModeRatio, frameAge = MonoClock.Now - (after ?? baseline).TimestampMs;
            bool ambiguous = vbook.LastAmbiguous || (double.IsFinite(obsMode) && Math.Abs(obsMode - 1) <= ModeAmbiguousBand);
            double uBefore = model.Uncertainty(curNode, ca.Name);   // v0.24.0: 1 手の学習の量（その関係の予測の不確かさの減り）
            var check = model.Observe(st.Seq, curNode, ca.Name, node2, changedNow, ambiguous: ambiguous);
            double stepGain = Math.Max(0, uBefore - model.Uncertainty(curNode, ca.Name));
            NoveltyAlpha = model.Novelty.Alpha;
            long? revId = check.Revision is { } rv0 ? RecordRevision(rv0, step) : null;
            PredictionChecks++; if (check.Hit) PredictionHits++;
            var decision = new DecisionRecord(st.Seq, turnId, curNode, ca.Name, composite?.Name, choice.Rule, choice.Probability, choice.Score, choice.Candidates, prediction, check, revId)
                { ObsVisRatio = obsVis, ObsModeRatio = obsMode, FrameAgeMs = frameAge, Ambiguous = ambiguous, ActionSource = source, Attribution = attribution };
            if (ambiguous) AmbiguousObservations++;
            LastDecision = decision;
            try { _env.Memory.AddDecision(decision); } catch (Exception e) { Emit("判断の記録に失敗: " + e.Message, LogLevel.Warn); }
            // 1手サマリーと成長の計器（表示だけ）
            // 自己生成の目的（v0.16.0）: 目的の手の結果で達成・失敗を決め、数手後に学習の進みで価値を更新する（成長の計器は使わない）
            bool goalAchieved = false;
            if (choice.Rule == "goal" && !goalComposite)
            {
                int achBefore = goals.Achieved;
                goals.Observed(curNode, ca.Name, node2, check.Hit, step);
                goalAchieved = goals.Achieved > achBefore;
            }
            goals.Tick(choice.Rule == "goal" || (goalComposite && choice.Rule == "sequence_step"));
            // v0.27.2: 毎手の文字の観測（手の流れを止めない。届いた時から状態の見方・観測に入る）
            // v0.29.0: この手の観測（文字を含む）を作り終えてから次の手を決める（1 手 = 1 完全な観測）
            // v0.39.1（T1・GKK_PIPELINE_SENSING）: 前の手の観測の完成は、この手の観測を始める前に必ず待つ（文字の差分の順序を保つ）。
            // この手の観測は待たずに次の手へ進む（pending。完成したら元の step/seq/frame に合流＝late-amend。§7 C1）
            if (_pendingObs != null) { _pendingObs.GetAwaiter().GetResult(); _pendingObs = null; }
            var obsTask = ReadTextEachStep(step, curNode, ca.Name, ca.IsWait || isHuman ? (ca.IsWait ? "wait" : "human") : "agent", node2, after, b.BoundaryMs,
                st.Seq, "v" + vis2, changedNow, natural.Rates(curNode)?.Change);
            if (GameKouryakuKun.Core.Throughput.ThroughputFlags.PipelineSensing) _pendingObs = obsTask;
            else obsTask.GetAwaiter().GetResult();
            stepTiming?.Mark("obs_wait", MonoClock.Now);
            // v0.33.3 dual-append: 旧 memory 学習はそのまま。フラグ ON のときだけ vNext へ併記（失敗しても探索は続ける）
            TryDualAppendVNext(st, ca, source, curNode, node2, changedNow, so, check, prediction);
            if (stepTiming != null)
            {
                stepTiming.Mark("post", MonoClock.Now);
                var ph = stepTiming.EndStep();
                _env.Log.Write("step_timing", ("step", step), ("decide_ms", Math.Round(ph[0], 1)), ("send_ms", Math.Round(ph[1], 1)), ("settle_ms", Math.Round(ph[2], 1)),
                    ("commit_ms", Math.Round(ph[3], 1)), ("obs_wait_ms", Math.Round(ph[4], 1)), ("post_ms", Math.Round(ph[5], 1)), ("total_ms", Math.Round(ph.Sum(), 1)), ("boundary_ms", b.BoundaryMs));
            }
            if (!ca.IsWait && !isHuman && Frontier != null)
            {
                Frontier.Observe(curNode, ca.Name, node2, explorer.Discoveries > discBefore, explorer.Nodes.TryGetValue(node2, out var ns2) ? explorer.Actions.Count(a => !a.IsWait && a.Group is not (ActionGroup.Sequence or ActionGroup.Chord) && (ns2.Actions.GetValueOrDefault(a.Name)?.Tries ?? 0) == 0) : 0, stepGain);
                if (Frontier.Steps % Frontier.RecomputeEvery == 0)
                {
                    Frontier.Recompute(goals.PlanEdges(explorer, model));
                    lock (Fg) Fg.Review(explorer.Nodes, vbook.MinTries);   // v0.27.2: 前景の鍵の組を比べ直す（地域と同じ間隔）
                }
            }
            if (!ca.IsWait && !isHuman) goals.StepGain(stepGain, choice.Rule == "goal" || (goalComposite && choice.Rule == "sequence_step"));
            foreach (var ge in goals.Events.Skip(_goalEventsLogged).ToList()) LogGoal(ge, step);
            foreach (var ge in goals.Review(explorer, model, step)) LogGoal(ge, step);
            AfterGoalEvents(goals, step);
            GoalText = GoalSummary(goals);
            var summary = StepSummary.Of(st.Seq, decision, node2, changedNow, explorer.Discoveries > discBefore, goalAchieved);
            StepHistory.Add(summary); Growth.Add(summary, waiting);
            if (curVis is int cv) { vbook.ObserveTransition(cv, ca.Name, changedNow, vis2); vbook.ObserveNext(cv, curNode, ca.Name, node2); }
            if (after != null) try { reactions.Observe($"m{curMode}", ca.Name, baseline, after); } catch (Exception e) { Emit("反応の対応表の更新に失敗: " + e.Message, LogLevel.Warn); }
            // 操作の並びの候補（繰り返しの回数・始まりの似かより・遷移の再現性がそろったものだけ。名前は中立、事前の価値は上乗せしない）
            // 状態の変化: 候補の条件を満たした（枠が空いていれば選択の候補へ）・休止・再開。入れ替えの基準は一般の統計だけ。記録は消さない
            // 操作の並びの候補は自動の手だけから見つける（人間の手の並びを候補にすると、お手本の模倣になるため）
            // 目的を同じ道筋で繰り返し達成した経験を、並びの候補（待ち）として足す。枠に入るかは連続履歴の候補と同じ規則（v0.18.0）
            var skillEvents = new List<SequenceEvent>();
            foreach (var sk in goals.SkillCandidates)
            {
                // すでに同じ並びの候補がある（連続履歴から見つけた）ときは、目的での再利用の裏づけとして数えるだけ（状態は既存の規則のまま）
                if (seqMiner.Has(sk.Steps)) { GoalSegmentSupport++; _env.Log.Write("goal_segment_existing", ("seq", st.Seq), ("steps", sk.Steps.ToList()), ("start", sk.Start), ("achieved", sk.Achieved), ("attempts", sk.Attempts)); }
                else if (seqMiner.AddFromGoal(sk.Steps, sk.Start, sk.End, sk.Achieved, sk.Attempts, st.Seq) is { } se) { skillEvents.Add(se); SkillCandidates++; }
                // S6: Skill 表面へ昇格（並び学習ロジックは seqMiner のまま）
                RuntimeFlags.ApplyStructuredGoalsEffective();
                if (RuntimeFlags.UseStructuredGoals)
                {
                    var sr = SkillLibrary.PromoteFromGoalSegment(sk.Steps, sk.Start, sk.End, sk.Achieved, sk.Attempts, "goal");
                    if (sr != null)
                        _env.Log.Write("skill_record", ("step", step), ("summary", sr.SummaryLine()), ("status", sr.Status),
                            ("origin", sr.Origin), ("body", sr.Body.ToList()), ("conf", sr.EffectConfidence));
                }
            }
            goals.SkillCandidates.Clear();
            RuntimeFlags.ApplyStructuredGoalsEffective();
            if (RuntimeFlags.UseStructuredGoals)
            {
                foreach (var sr in SkillLibrary.SyncFromMiner(seqMiner).Take(3))
                    _env.Log.Write("skill_record", ("step", step), ("summary", sr.SummaryLine()), ("status", sr.Status),
                        ("origin", sr.Origin), ("body", sr.Body.ToList()), ("conf", sr.EffectConfidence));
            }
            foreach (var ev in skillEvents.Concat(isHuman ? Enumerable.Empty<SequenceEvent>() : seqMiner.Observe(st)))
            {
                var nc = ev.Candidate;
                if (ev.Kind is "promoted" or "reactivated") { if (explorer.AddSequence(nc) == null) continue; explorer.Activate(nc.Name); }
                if (ev.Kind == "promoted") Discoveries.Add(new Discovery(st.Seq, DiscoveryKind.Sequence, nc.Name, $"{string.Join(" → ", nc.Steps)}・{nc.Start} から"));
                else if (ev.Kind == "dormant") explorer.Deactivate(nc.Name);
                try { _env.Memory.AddSequenceEvent(ev); } catch (Exception e) { Emit("操作の並びの状態の記録に失敗: " + e.Message, LogLevel.Warn); }
                if (ev.Kind != "waiting")
                    Emit(ev.Kind switch
                    {
                        "promoted" => $"[系列] 操作の並びの候補 {nc.Name}（{string.Join(" → ", nc.Steps)}・始まり {nc.Start} から {nc.AtStart} 回・再現性 {nc.Reproducibility:P0}・始まりの似かより {nc.StartSimilarity:F2}）。価値はこれから試して学びます",
                        "dormant" => $"[系列] {nc.Name} を休止にしました（{ev.Reason}）。記録は残し、条件がそろえば戻します",
                        _ => $"[系列] {nc.Name} を選択の候補に戻しました（{ev.Reason}）",
                    }, LogLevel.Ok);
                _env.Log.Write("action_sequence", ("seq", st.Seq), ("event", ev.Kind), ("reason", ev.Reason), ("sequence", nc.Name), ("steps", nc.Steps.ToList()), ("start", nc.Start), ("at_start", nc.AtStart), ("total", nc.Total),
                    ("reproducibility", Math.Round(nc.Reproducibility, 3)), ("recent_reproducibility", Math.Round(nc.RecentReproducibility, 3)), ("start_similarity", Math.Round(nc.StartSimilarity, 3)), ("origin", nc.Origin));
            }
            if (composite != null)
            {
                compChanged |= changedNow;
                if (pending.Count == 0)
                {
                    explorer.ObserveComposite(compStart, composite, node2, compChanged, explorer.Discoveries > compDisc);
                    var cchk = model.Observe(st.Seq, compStart, composite.Name, node2, compChanged);
                    if (cchk.Revision is { } crv) RecordRevision(crv, step);
                    // 実行の結果を並びの評価に入れる: 予測があったのに明らかに外れた（1 ビット以上の驚き）かどうか
                    var cp = cchk.Prediction;
                    bool cinformed = cp.DirectN + cp.ReinterpretedN + cp.PriorN > 0, csur = cinformed && cchk.Excess >= model.MinExcessBits;
                    if (composite.SequenceId is int sid)
                    {
                        seqMiner.ObserveExecution(sid, st.Seq, csur);
                        try { _env.Memory.AddSequenceExecution(sid, composite.Name, st.Seq, compStart, node2, cchk, cinformed, csur); }
                        catch (Exception e) { Emit("並びの実行の記録に失敗: " + e.Message, LogLevel.Warn); }
                    }
                    var ck = (compStart, composite.Name, node2);
                    runTransitions[ck] = runTransitions.TryGetValue(ck, out var cn) ? cn + 1 : 1;
                    lock (ObsBuilder) SkillEffects.Observe(composite.Name, _lastEvents);   // v0.29.6: 並びの最後の手の局所の出来事（並びの局所の効果）
                    _env.Log.Write("sequence_done", ("step", step), ("sequence", composite.Name), ("from", compStart), ("to", node2), ("changed", compChanged),
                        ("predicted_next", compPred?.TopNext), ("hit", cchk.Hit), ("excess", Math.Round(cchk.Excess, 3)));
                    if (goalComposite)
                    {
                        // 目的の道筋の 1 本としてスキル（並び）を使った: 並びを最後まで送った結果で目的を進める
                        int achB = goals.Achieved;
                        goals.Observed(compStart, composite.Name, node2, cchk.Hit, step);
                        if (goals.Achieved > achB) Growth.AddEvent(new GrowthEvent(st.Seq, GrowthEventKind.GoalAchieved, $"#{st.Seq} ◎目的達成（{composite.Name} を使って）"));
                        foreach (var ge in goals.Events.Skip(_goalEventsLogged).ToList()) LogGoal(ge, step);
                        AfterGoalEvents(goals, step);
                        GoalSkillUses++; goalComposite = false;
                    }
                    composite = null;
                }
            }
            if (step % HypothesisRefreshSteps == 0 || step == 1)
            {
                RefreshHypotheses(model, vbook, seqMiner, st.Seq, motion);
                RefreshTemporal(temporal, motion);
                RefreshMeters(explorer, model, seqMiner, st.Seq);
            }
            if (step % SplitRefreshSteps == 0)
            {
                bool split = false;
                // 分けたノードと粗いノードの分けた後の経験が「同じ」と強くいえるようになった分割は取り消す（記録は消さず、読み直しで戻す）
                foreach (var (rid, rsp) in vbook.Splits.ToList())
                {
                    var (revert, rlbf, rk) = SplitReview.Review(SplitReview.NextCounts(rsp.Node, explorer.Nodes.GetValueOrDefault(rsp.Node)),
                        SplitReview.NextCounts(rsp.Coarse, explorer.Nodes.GetValueOrDefault(rsp.Coarse)), vbook.MinTries);
                    if (!revert) continue;
                    RevertSplit(explorer, vbook, rid, rsp.Coarse, rsp.Node, rlbf, rk, step); split = true;
                    if (curNode == rsp.Node) curNode = rsp.Coarse;
                }
                foreach (var sp in vbook.RefreshSplits()) { ApplySplit(explorer, sp, step); split = true; }
                if (split) { model = BuildModel(explorer); foreach (var le in Losses.Learnable) ApplyLoss(le, null, model); }   // 読み直した履歴で内部モデルを作り直す（ノード名が変わったため）
                RefreshPriors(explorer, vbook, model);
            }
            if (so.Boundary.Count > 0) SequenceBoundaries++;
            foreach (var np in so.NewPatterns)
            {
                PatternEvents++;
                Discoveries.Add(new Discovery(st.Seq, DiscoveryKind.Pattern, $"P{np.Id}", $"{KindText(np.Kind)}・長さ {np.Length}"));
                Emit($"[系列] 新しいパターン P{np.Id}（{KindText(np.Kind)}・長さ {np.Length}・{np.Count} 回目）: {Shorten(np.Signature)}", LogLevel.Ok);
                _env.Log.Write("sequence_pattern", ("seq", st.Seq), ("pattern", np.Id), ("kind", np.Kind.ToString().ToLowerInvariant()), ("length", np.Length), ("count", np.Count));
            }
            curNode = node2; curMode = mode2; curVis = vis2;
            runVisits[node2] = runVisits.GetValueOrDefault(node2) + 1;
            runActs[curNode] = runActs.GetValueOrDefault(curNode) + 1;   // v0.27.2: この実行での画面ごとの手の数（健全性の分母）
            if (step % growthEvery == 0) Snap("periodic", step);
            // 待ち時間の自己調整（実測の分布から）
            // 待ち時間の自己調整は入力への応答の時間だけから（待つ手の変化は応答ではない）
            if (!waiting && b.Kind == TurnKind.Normal && b.NetChange) adapt.Add(b.BoundaryMs);
            if (!waiting && b.FirstChangeMs is double fc) adapt.AddFirstChange(fc);
            var newOpt = detOpt with { MaxMs = adapt.Current, NoChangeTimeoutMs = adapt.NoChangeCurrent };
            if (Math.Abs(newOpt.MaxMs - detOpt.MaxMs) > 0.1 * detOpt.MaxMs || Math.Abs(newOpt.NoChangeTimeoutMs - detOpt.NoChangeTimeoutMs) > 0.1 * detOpt.NoChangeTimeoutMs)
            {
                detOpt = newOpt;
                det = new TurnDetector(detOpt, calib.Criteria, calib.IdleMask);
                _env.Log.Write("turn_timeout_adapted", ("step", step), ("max_ms", Math.Round(detOpt.MaxMs)), ("nochange_ms", Math.Round(detOpt.NoChangeTimeoutMs)),
                    ("raw_max_ms", Math.Round(adapt.RawCurrent)), ("raw_nochange_ms", Math.Round(adapt.RawNoChange)));
            }
            NoChangeRun = explorer.NoChangeStreak;
            Changed?.Invoke();
        }
        foreach (var bk in _inputs) bk.ReleaseAll();
        if (_rawAgg != null)
        {
            bool ok; try { ok = _rawAgg.VerifyReplay(); } catch { ok = false; }
            _env.Log.Write("raw_agg_summary", ("raw", _rawAgg.RawCount()), ("agg", _rawAgg.Index.Keys), ("run_raw", RawExtraRows), ("run_dup", RawDuplicates),
                ("dup_ratio", Math.Round(_rawAgg.Index.DuplicateRatio, 4)), ("retention", RawExtraRows + RawAggErrors == 0 ? 1 : Math.Round((double)RawExtraRows / (RawExtraRows + RawAggErrors), 4)),
                ("errors", RawAggErrors), ("replay_ok", ok.ToString()));
        }
        if (NoiseModel != null) _env.Log.Write("noise_summary", ("summary", NoiseModel.Summary()), ("screens_with_baseline", NoiseModel.ScreensWithBaseline), ("screens", NoiseModel.Screens),
            ("natural", NoiseModel.NaturalCandidates), ("input", NoiseModel.InputCandidates), ("no_baseline", NoiseModel.NoBaseline));
        if (settleAdapt != null)
            _env.Log.Write("adaptive_settle_summary", ("applied", settleAdapt.Applied), ("reverted", settleAdapt.Reverted), ("short_nochange", settleAdapt.ShortNoChange),
                ("late_on_short", settleAdapt.LateOnShort), ("late_rate", Math.Round(settleAdapt.LateRate, 4)), ("screens", settleAdapt.ScreensSummary()));
        if (_pendingObs != null) { try { _pendingObs.GetAwaiter().GetResult(); } catch (Exception e) { _env.Log.Write("observation_failed", ("step", Step), ("error", e.Message)); } _pendingObs = null; }   // drain（§7.2-4）
        if (Ledger != null)
        {
            var un = Ledger.Unresolved();
            var m = CurrentThroughput();
            _env.Log.Write("ledger_summary", ("opened", Ledger.Opened), ("observed", Ledger.Observed), ("inherited", Ledger.Inherited), ("late", Ledger.LateAmended),
                ("missing", Ledger.Missing), ("violations", Ledger.Violations), ("unresolved", un.Count), ("decided_with_pending", Ledger.DecidedWithPending),
                ("late_amend_success", Math.Round(m.LateAmendSuccessRate, 4)), ("cio_per_min", Math.Round(m.CausalObservationsPerMinute, 1)), ("actions_per_min", Math.Round(m.ActionsPerMinute, 1)),
                ("raw_retention", Math.Round(m.RawRetentionRatio, 4)), ("metrics", m.Summary()));
        }
        if (stepTiming != null && stepTiming.Steps > 0)
            _env.Log.Write("step_timing_summary", ("steps", stepTiming.Steps), ("actions_per_min", Math.Round(stepTiming.ActionsPerMinute(), 1)),
                ("coverage", Math.Round(stepTiming.CoverageRatio(), 3)), ("median", string.Join(" ", stepTiming.MedianByPhase().Select(k => $"{k.Key}:{k.Value}"))));
        Finishing = true; SetState(State, "終わりの保存中…");   // v0.29.2: 終わりの保存（session_end まで）の間は診断の書き出しを待たせる
        Snap("end", Step);
        RefreshTemporal(temporal, motion);
        // ユーザーの作業との分離（この探索の記録から。画面の反応は合否に使わない）
        lock (Separation)
        {
            Separation.DurationSec = _runClock.Elapsed.TotalSeconds; Separation.UserEventsPausing = UserEventsPausing; Separation.UserEventsIgnored = UserEventsIgnored;
            Separation.EmergencyStopped = _pause.IsEmergencyStopped;
            var (sapp, spass, snotes) = SeparationReport.Evaluate(Separation, active.Isolation.Level);
            _env.Log.Write("separation_check", ("verdict", sapp ? (spass ? "pass" : "fail") : "not_applicable"), ("notes", snotes.Cast<object?>().ToList()),
                ("turns_other_fg", Separation.TurnsOtherForeground), ("user_events_ignored", UserEventsIgnored), ("ua_pauses_while_bg", Separation.UserActivityPausesWhileBackground));
        }
        try
        {
            // v0.27.2（提案 4）: 分母は「この実行で手が 3 回以上あった画面」（しきい値は同じ。過去の画面で永久に WARN にしない）
            var ev = runActs.Where(kv => kv.Value >= TemporalBook.MinEvidence).Select(kv => kv.Key).ToList();
            BaselineNodes = ev.Count; BaselineCovered = ev.Count(n => natural.Observations(n) > 0);
            BaselineGapObs = natural.GapObservations; BaselineWaitObs = natural.WaitObservations;
            var at = explorer.Nodes.SelectMany(kv => kv.Value.Actions.Where(x => x.Value.Tries > 0 && x.Key != ExploreAction.Wait.Name).Select(x => NaturalChange.Attribute(explorer, kv.Key, x.Key))).ToList();
            AttrPairs = at.Count; AttrKnown = at.Count(x => !double.IsNaN(x.PActionCaused)); AttrNatural = at.Count(x => !double.IsNaN(x.PActionCaused) && x.PActionCaused < goals.MinActionCaused);
            _env.Log.Write("natural_baseline", ("screens", natural.Nodes.Count()), ("observations", natural.TotalObservations), ("gap_observations", BaselineGapObs), ("wait_observations", BaselineWaitObs), ("after_action_observations", natural.AfterActionObservations),
                ("evidence_screens", BaselineNodes), ("covered", BaselineCovered), ("attr_pairs", AttrPairs), ("attr_known", AttrKnown), ("attr_natural", AttrNatural),
                ("rates", natural.Nodes.Select(n => (n, r: natural.Rates(n), c: natural.Counts(n))).Where(x => x.r != null).OrderByDescending(x => x.c.Obs).Take(20).Select(x => $"{x.n}={x.c.Obs}:{x.c.Changes}:{x.r!.Value.Change:0.000}").ToList()));
            _env.Memory.SaveGoalModel("natural_baseline", natural.ExportState());
        }
        catch (Exception e) { Emit("入力なしの基準の集計・保存に失敗: " + e.Message, LogLevel.Warn); }
        _env.Log.Write("temporal_profiles", ("screens", Temporal.Count), ("wait_observations", temporal.WaitObservations), ("gap_observations", temporal.GapObservations),
            ("traits", Temporal.SelectMany(t => t.Traits).GroupBy(x => x).ToDictionary(g => g.Key, g => (object?)g.Count())));
        SaveExploration(book, explorer, seqLearner, runTransitions, vbook, reactions, seqMiner);
        try { var ms = motion.Export().ToList(); _env.Memory.SaveScreenMotion(ms); MotionScreens = ms.Count; } catch (Exception e) { Emit("画面ごとの動き続ける所の保存に失敗: " + e.Message, LogLevel.Warn); }
        // v0.26.2 実機 #86→#87: 終わりのときに目的・地域・文字の状態を保存していなかった（目的の出来事と復旧のときだけ）。
        // 最後の目的の出来事の後の探索の基準の更新が失われ、次の開始の値打ちが終わりの値打ちと違っていた（1.48→3.41）
        if (StateText != null) try { string tj; lock (StateText) tj = StateText.ExportState(); _env.Memory.SaveGoalModel("state_text", tj); } catch (Exception e) { Emit("文字の観測の保存に失敗: " + e.Message, LogLevel.Warn); }
        try { string xj; lock (TextBook) xj = TextBook.ExportState(); _env.Memory.SaveGoalModel("text_change", xj); string rj; lock (PixelRegions) rj = PixelRegions.ExportState(); _env.Memory.SaveGoalModel("region_causality", rj); } catch (Exception e) { Emit("文字の変化の記録の保存に失敗: " + e.Message, LogLevel.Warn); }
        try { _env.Memory.SaveGoalModel("frontier", Frontier!.ExportState()); } catch (Exception e) { Emit("地域の状態の保存に失敗: " + e.Message, LogLevel.Warn); }
        try { _env.Memory.SaveGoalModel("self_goals", goals.ExportState()); _env.Log.Write("goal_model_saved", ("at", "end"), ("value_reach", goals.Value(GoalKind.Reach)), ("value_retest", goals.Value(GoalKind.Retest))); } catch (Exception e) { Emit("目的の状態の保存に失敗: " + e.Message, LogLevel.Warn); }
        if (_pause.IsEmergencyStopped) return "emergency";
        if (endReason == "user_stop" && StopReason == null) StopReason = "停止ボタン";
        return endReason;
    }

    /// <summary>粗いノード（色の分布＋矩形の配置）と見た目の状態（細かい配置）を決める。分割の証拠がそろった見た目の状態だけ、別のノードにする</summary>
    /// <summary>直前の Classify の観測の確かさ（画面の種類の割り当ての距離 ÷ 新しい種類を作る距離）</summary>
    private double _lastModeRatio = double.NaN;
    /// <summary>画面の種類の割り当てが境目の近くとみなす幅（見た目の状態と同じ）</summary>
    private const double ModeAmbiguousBand = 0.25;   // 根拠: 汎用（新しい種類を作る距離の ±25%＝どちらの種類ともいえる境目）

    private (string Node, int Mode, bool ModeIsNew, bool Panel, int Vis) Classify(ScreenModeBook book, VisualStateBook vbook, PanelDetector panels, Frame f)
    {
        double thr = book.NewModeDistance;
        var (mode, isNew, dist) = book.Assign(ScreenSignature.Of(f));
        _lastModeRatio = book.Modes.Count <= 1 && isNew ? double.NaN : dist / Math.Max(1e-9, thr);
        var ps = panels.Detect(f);
        var coarse = ScreenModeBook.NodeName(mode, ScreenModeBook.PanelLayout(ps, f.Width, f.Height));
        var (vis, _) = vbook.Assign(VisualLayout.Of(f), coarse);
        return (vbook.Node(coarse, vis), mode, isNew, ps.Count > 0, vis);
    }

    /// <summary>ノード名の先頭の画面の種類（m番号）。区画の反応の対応表の文脈</summary>
    private static string ModeContext(string node) { int k = node.IndexOfAny(new[] { '|', '#' }); return k < 0 ? node : node[..k]; }

    /// <summary>分割前のノードの、読み直せなかった集計を弱い事前の推定として使う強さ（仮の観測の回数）</summary>
    private const double ParentPriorStrength = 2;   // 根拠: 汎用（直接の経験が数回でほぼ支配する小さな値）

    /// <summary>
    /// 分割の反映: 見た目の状態の記録が残っている過去の手を新しいノードとして読み直し（stream_reinterpretations・derived_transitions）、探索の集計を当て直す。
    /// 読み直せない集計（見た目の状態の記録がない古い手・並びの集計など）は分割前のノードに残り、新しいノードには弱い事前の推定（parent）として効く
    /// </summary>
    private void ApplySplit(Explorer explorer, SplitDecision sp, int step)
    {
        int moved = 0;
        try
        {
            if (sp.Resplit) _env.Memory.AddSplitEvent(sp.Id, sp.Coarse, sp.Node, "resplit", sp.EvidenceKind, sp.Distance, sp.LogBayesFactor, 0, "取り消しの後の観測で、次の画面の違いの証拠が再びそろった");
            foreach (var r in _env.Memory.ReclassifyForSplit(sp.Coarse, sp.Id, sp.Node, sp.Distance, sp.Leader)) { explorer.Reassign(r.OrigFrom, r.Action, r.OrigTo, r.NewFrom, r.NewTo, r.N, r.Changed); moved += r.N; }
            if (!sp.Resplit) _env.Memory.AddSplitEvent(sp.Id, sp.Coarse, sp.Node, "split", sp.EvidenceKind, sp.Distance, sp.LogBayesFactor, moved,
                sp.EvidenceKind == "next" ? "見た目は近いが次の画面の分布が違う" : "見た目は近いが結果の3通りの分布が違う");
        }
        catch (Exception e) { Emit("分割の読み直しに失敗: " + e.Message, LogLevel.Warn); }
        string what = sp.EvidenceKind == "next" ? "次の画面の分布" : "遷移の結果の分布";
        Discoveries.Add(new Discovery(_lastSeq, DiscoveryKind.Split, sp.Node, $"{sp.Coarse} から{(sp.Resplit ? "分け直し" : "")}・{what}の違い"));
        Emit($"[状態] 見た目は近いが{what}が違う状態を{(sp.Resplit ? "分け直し" : "分け")}ました: {sp.Coarse} → {sp.Node}（距離 {sp.Distance:F2}・証拠のベイズ因子の対数 {sp.LogBayesFactor:F1}・過去の手を {moved} 件読み直し）", LogLevel.Info);
        _env.Log.Write("state_split", ("step", step), ("coarse", sp.Coarse), ("vis_state", sp.Id), ("node", sp.Node), ("distance", Math.Round(sp.Distance, 3)), ("log_bayes_factor", Math.Round(sp.LogBayesFactor, 3)),
            ("evidence", sp.EvidenceKind), ("resplit", sp.Resplit), ("reclassified", moved));
    }

    /// <summary>読み直しの済み印のない既存の分割（v0.10.0 より前に作ったもの）を読み直す。読み直しであって継承ではない</summary>
    private void ReclassifyExistingSplit(Explorer explorer, string coarse, int vis, string node)
    {
        int moved = 0;
        foreach (var r in _env.Memory.ReclassifyForSplit(coarse, vis, node)) { explorer.Reassign(r.OrigFrom, r.Action, r.OrigTo, r.NewFrom, r.NewTo, r.N, r.Changed); moved += r.N; }
        _env.Memory.AddSplitEvent(vis, coarse, node, "reclassified", null, double.NaN, double.NaN, moved, "読み直しの記録がない分割（読み直しの機能より前に作った分割）を読み直した");
        Emit($"[状態] 以前の分割 {coarse} → {node} の過去の手を {moved} 件読み直しました（読み直しの機能より前に作った分割）", LogLevel.Info);
        _env.Log.Write("state_split_reclassified", ("coarse", coarse), ("vis_state", vis), ("node", node), ("reclassified", moved));
    }

    /// <summary>分割の取り消し: 分けたノードとして解釈していた手を粗いノードとして読み直し、探索の集計を当て直す（直接の記録は消さない）</summary>
    private void RevertSplit(Explorer explorer, VisualStateBook vbook, int vis, string coarse, string node, double logBf, int common, int step)
    {
        int moved = 0;
        try
        {
            vbook.Revert(vis);
            foreach (var r in _env.Memory.ReclassifyForRevert(node, vis, coarse)) { explorer.Reassign(r.OrigFrom, r.Action, r.OrigTo, r.NewFrom, r.NewTo, r.N, r.Changed); moved += r.N; }
            _env.Memory.AddSplitEvent(vis, coarse, node, "reverted", "next", double.NaN, logBf, moved, $"分けた後の経験で、次の画面の分布が同じという強い証拠（比べた入力 {common} 個）");
        }
        catch (Exception e) { Emit("分割の取り消しに失敗: " + e.Message, LogLevel.Warn); }
        Discoveries.Add(new Discovery(_lastSeq, DiscoveryKind.SplitReverted, node, $"{coarse} に戻した・同じという証拠"));
        Emit($"[状態] 分割を取り消しました: {node} → {coarse}（同じという証拠のベイズ因子の対数 {-logBf:F1}・比べた入力 {common} 個・過去の手を {moved} 件読み直し。記録は消していません）", LogLevel.Info);
        _env.Log.Write("state_split_reverted", ("step", step), ("coarse", coarse), ("vis_state", vis), ("node", node), ("log_bayes_factor", Math.Round(logBf, 3)), ("common", common), ("reclassified", moved));
    }

    /// <summary>弱い事前の推定を置き直す: 分割前のノード（parent）と、似かよりの証拠が十分な似た画面（borrowed）。記録にも残す</summary>
    private void RefreshPriors(Explorer explorer, VisualStateBook vbook, TransitionModel model)
    {
        foreach (var (n, p) in explorer.Priors().ToList()) explorer.SetPrior(n, p.Source, p.Origin, 0);
        var ev = new Dictionary<(string, string, string), (double W, string E)>();
        foreach (var (vis, sp) in vbook.Splits) ev[(sp.Node, sp.Coarse, "parent")] = (ParentPriorStrength, $"split vis {vis}");
        foreach (var bp in vbook.BorrowPairs())
            foreach (var (to, from) in new[] { (bp.NodeA, bp.NodeB), (bp.NodeB, bp.NodeA) })
                if (!ev.TryGetValue((to, from, "borrowed"), out var have) || have.W < bp.Strength)
                    ev[(to, from, "borrowed")] = (bp.Strength, $"vis {bp.A}~{bp.B} distance {bp.Distance:F3} common {bp.Common}");
        foreach (var ((n, src, o), (w, _)) in ev) explorer.SetPrior(n, src, o, w);
        model.SetPriors(explorer.Priors());
        try { _env.Memory.SavePriors(ev.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.W, kv.Value.E))); }
        catch (Exception e) { Emit("弱い事前の推定の記録に失敗: " + e.Message, LogLevel.Warn); }
        if (ev.Count > 0) _env.Log.Write("experience_priors", ("parent", ev.Keys.Count(k => k.Item3 == "parent")), ("borrowed", ev.Keys.Count(k => k.Item3 == "borrowed")));
    }

    /// <summary>
    /// 内部モデルを連続履歴（読み直しを反映したもの）から作り直す。連続履歴にない操作の並びの結果は、探索の集計から足す。
    /// 起動時・分割の後に呼ぶ（流し直しで起きる修正は、すでに記録したものか、同じ規則での作り直しなので記録しない）
    /// </summary>
    private TransitionModel BuildModel(Explorer explorer)
    {
        var m = new TransitionModel();
        try
        {
            m.Replay(_env.Memory.LoadStream(StreamReplayLimit));
            var seqNames = explorer.Actions.Where(a => a.Group == ActionGroup.Sequence).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var (from, ns) in explorer.Nodes)
                foreach (var (a, st) in ns.Actions)
                    if (seqNames.Contains(a) && st.Tries > 0)
                        foreach (var (to, n) in st.To) m.Seed(from, a, to, n, (int)Math.Round(n * (double)st.Changes / st.Tries));
            m.SetPriors(explorer.Priors());
        }
        catch (Exception e) { Emit("内部モデルの作り直しに失敗（空のモデルから続けます）: " + e.Message, LogLevel.Warn); }
        _env.Log.Write("model_rebuilt", ("relations", m.Relations), ("checks", m.Checks), ("revisions_in_replay", m.Revisions));
        return m;
    }

    /// <summary>B（画面の無反応）の連続をログに残す間隔（止めない）</summary>
    private const int NoReactionNoticeSteps = 40;   // 根拠: 汎用（全キーを数回ずつ試すくらいの手数）

    /// <summary>画面ごとの時間的な変化の要約を作り直す（読むだけ。表示用）</summary>

    public FrontierRegions? Frontier { get; private set; }
    /// <summary>v0.25.4（t52u）: 環境の消失（ウィンドウ・プロセス）を直前の自分の手の結果として学ぶ</summary>
    public EnvironmentLossBook Losses { get; } = new();
    private (string State, string Action, string Source, double SentAt, bool Wait, long Seq)? _lastSent;
    private double _pendingLossAt = double.NaN;
    private void NoteLoss(double atMs) => Interlocked.CompareExchange(ref _pendingLossAt, atMs, double.NaN);
    private void ApplyLoss(LossEvent e, Explorer? ex, TransitionModel m)
    {
        ex?.ObserveTerminal(e.State, e.Action);
        m.Observe(e.Seq, e.State, e.Action, EnvironmentLossBook.Terminal, true);
    }
    private void ProcessLoss(Explorer explorer, TransitionModel model, double windowMs)
    {
        double at = Interlocked.Exchange(ref _pendingLossAt, double.NaN);
        if (double.IsNaN(at)) return;
        var ls = _lastSent; _lastSent = null;
        bool attr = EnvironmentLossBook.Attributable(ls != null, ls?.Wait ?? false, false, ls?.SentAt ?? 0, at, windowMs);
        var ev = Losses.Record(attr, ls?.State ?? "", ls?.Action ?? "", ls?.Source ?? "", ls?.Seq ?? _lastSeq, ls == null ? double.NaN : at - ls.Value.SentAt, DateTimeOffset.Now.ToString("o"));
        if (ev is { Learned: true }) ApplyLoss(ev, explorer, model);
        _env.Log.Write("env_loss", ("attributed", attr), ("state", ev?.State), ("action", ev?.Action), ("source", ev?.Source), ("gap_ms", ev == null ? null : Math.Round(ev.GapMs)), ("learned", ev?.Learned ?? false), ("window_ms", Math.Round(windowMs)));
        try { _env.Memory.SaveGoalModel("env_loss", Losses.ExportState()); } catch (Exception e) { Emit("環境の消失の記録の保存に失敗: " + e.Message, LogLevel.Warn); }
    }
    public GameKouryakuKun.Core.Ocr.StateTextBook? StateText { get; private set; }
    private int _ocrBusy;
    // v0.27.2 毎手の文字の観測
    public GameKouryakuKun.Core.Ocr.TextStepStats Ocr2 { get; } = new();
    public GameKouryakuKun.Core.Exploration.ScoreBreakdown ScoreLog { get; } = new();
    private static double? R1(double v) => double.IsFinite(v) ? Math.Round(v, 1) : null;
    public ForegroundStates Fg { get; } = new();
    public TextChangeBook TextBook { get; } = new();
    /// <summary>v0.28.0: 画素の区画（16×12）ごとの、手で変わる所と手によらず変わる所の区別</summary>
    public RegionCausality PixelRegions { get; } = new(GameKouryakuKun.Core.Ocr.TextReader.GridCols * GameKouryakuKun.Core.Ocr.TextReader.GridRows);
    /// <summary>探索中の NaturalBaseline 参照（S3 Comparer 用。学習ロジックは触らない）。</summary>
    public GameKouryakuKun.Core.Exploration.NaturalBaseline? ExploreBaseline { get; private set; }
    public TextTransitionLink TextLink { get; } = new();
    private readonly Dictionary<long, (bool Changed, List<string> Texts)> _textByStep = new();
    public long FgNaturalLinesExcluded;
    private GameKouryakuKun.Core.Exploration.CandidateScore? _chosenCand;
    private volatile string? _lastChosenText;
    private int _shiftSamples;
    /// <summary>v0.30.0（P-1）: 最新の構造化した観測（重ね表示用。表示だけで選択・学習には使わない）</summary>
    public volatile GameKouryakuKun.Core.Observation.StructuredObservation? LatestObservation;
    /// <summary>S2: フラグ ON 時のみ Assembler が埋める Observation Bundle（探索・学習は参照しない）。</summary>
    public volatile GameKouryakuKun.Core.Observation.ObservationBundle? LatestObservationBundle;
    /// <summary>S3: フラグ ON 時の H0–H4 比較（探索・学習は参照しない）。</summary>
    public volatile GameKouryakuKun.Core.Causality.CausalComparisonRecord? LatestCausalComparison;
    /// <summary>S3: 短時間 Predictive WM 読み取り（フラグ ON 時。探索・学習は参照しない）。</summary>
    public volatile GameKouryakuKun.Core.Causality.ShortHorizonForecast? LatestShortHorizon;
    /// <summary>S4: 重要度レポート（フラグ ON 時。ライブ OCR defer は GKK_LIVE_ROI≠0 かつ Importance ON）。</summary>
    public volatile GameKouryakuKun.Core.Importance.ImportanceReport? LatestImportance;
    /// <summary>S5: Intrinsic Mixer レポート（フラグ ON 時）。</summary>
    public volatile GameKouryakuKun.Core.Intrinsic.IntrinsicMixerReport? LatestIntrinsic;
    public int ActiveExperimentInterrupts;
    /// <summary>S6: ObservationGoal / Plan（フラグ ON 時。並行ログ）。</summary>
    public volatile GameKouryakuKun.Core.Goals.ObservationGoal? LatestObservationGoal;
    public volatile GameKouryakuKun.Core.Goals.PlanProposal? LatestPlanProposal;
    /// <summary>S6: Skill 表面（並び学習は ActionSequenceMiner のまま）。</summary>
    public readonly GameKouryakuKun.Core.Goals.SkillMiner SkillLibrary = new();
    public int StructuredSoftAdopts;
    private readonly List<long> _softAdoptSteps = new();
    private long? _lastSoftAdoptStep;
    private long? _lastSupervisorRecoveryStep;
    /// <summary>S5: 寄与・割込み統計（フラグ ON 時。S7 メタ重み材料・本スライスは学習しない）。</summary>
    public readonly GameKouryakuKun.Core.Intrinsic.IntrinsicContributionStats IntrinsicStats = new();
    private readonly List<long> _activeExpInterruptSteps = new();
    private long? _lastActiveExpStep;
    private string? _lastActiveExpAction;
    private IntrinsicStatsStore? _intrinsicStore;
    private ModelRegistryStore? _modelRegistry;
    /// <summary>S7: 適用中ポートフォリオ重み（promote/rollback と対）。フラグ OFF 時は Default のまま。</summary>
    public readonly GameKouryakuKun.Core.Outer.PortfolioWeightState PortfolioWeights = new();
    private long _lastOuterLoopStep = -1;
    /// <summary>S3 PE 閉ループ: タグ→特徴候補（フラグ ON 時）。</summary>
    public readonly GameKouryakuKun.Core.Causality.PeClosedLoopRecorder PeLoop = new();
    private PeClosedLoopStore? _peStore;
    /// <summary>v0.30.3: LatestObservation を作った画面（重ね表示をこの画面の上に描く）</summary>
    public volatile Tuple<GameKouryakuKun.Core.Observation.StructuredObservation, GameKouryakuKun.Core.Imaging.Frame>? LatestViewBox;
    public (GameKouryakuKun.Core.Observation.StructuredObservation Obs, GameKouryakuKun.Core.Imaging.Frame Frame)? LatestView
    {
        get => LatestViewBox is { } b ? (b.Item1, b.Item2) : null;
        set => LatestViewBox = value is { } v ? Tuple.Create(v.Obs, v.Frame) : null;
    }
    /// <summary>重ね表示の横の表に出す、点数・目的・計画・並びの局所の効果（表示用の文字列）</summary>
    public IReadOnlyList<string> OverlayExtras()
    {
        var l = new List<string>();
        if (_lastChosenText is { } c) l.Add("選んだ手の " + c);
        if (GoalText is { } g) l.Add("目的: " + g);
        if (Goals?.Active is { } a && a.Plan.Count > 0) l.Add("計画: " + string.Join(" → ", a.Plan.Select(x => x.Action)));
        lock (ObsBuilder) foreach (var e in SkillEffects.Effects(LocalPersistentMinN).Where(x => x.Effects.Count > 0).Take(2)) l.Add($"並びの局所の効果: {e.Sequence}（{e.N} 回）{string.Join(",", e.Effects)}");
        return l;
    }
    public long WaitInfoN; public double WaitInfoSum, WaitInfoFirst = -1, WaitInfoLast;
    public string TextRegionCounts() { lock (TextBook) { var c = TextBook.Regions.ClassCounts(); return $"action={c.Action} independent={c.Independent} unknown={c.Unknown}"; } }
    public string PixelRegionCounts() { lock (PixelRegions) { var c = PixelRegions.ClassCounts(); return $"action={c.Action} independent={c.Independent} unknown={c.Unknown}"; } }
    public string TextLinkCounts() { lock (TextLink) { var c = TextLink.Counts(); return $"predictive={c.Predictive} not={c.NotPredictive} undecided={c.Undecided} obs={TextLink.Observed}"; } }
    private readonly GameKouryakuKun.Core.Ocr.TextReader _textReader = new();
    private GameKouryakuKun.Core.Ocr.TextSnapshot? _textLast; private long _textLastStep = long.MinValue;
    private int _ocrStepBusy;
    /// <summary>v0.29.4（ブロック 13）: 局所の出来事の予測の不確かさを、既存の不確かさと別の問いとして合わせる（1 − (1 − 既存)(1 − 局所)）。局所の観測がなければ既存のまま</summary>
    private double WithLocal(string node, string action, double u)
    {
        double? g; lock (ObsBuilder) g = LocalEvents.InformationGain(node, action, LocalPersistentMinN);
        if (g is not double v) return u;
        double r = 1 - (1 - Math.Clamp(u, 0, 1)) * (1 - Math.Clamp(v, 0, 1));
        LocalInfoCalls++; if (r > u + 1e-9) LocalInfoRaised++; LocalInfoSum += v;
        return r;
    }
    public long LocalInfoCalls, LocalInfoRaised; public double LocalInfoSum;
    private const int SplitEstimateMinN = 20;   // 根拠: 指示どおり（分けたときの試算は観測 20 回以上の組だけ）
    private const int LocalPersistentMinN = 6;   // 根拠: 健全性の「外れが続く」と同じ回数（RegionCausality の MinTries と同じ桁）

    private double UncWithText(string node, string action, double visual)
    {
        lock (TextBook) return TextBook.Tries(node, action) == 0 ? visual : Math.Max(visual, TextBook.Uncertainty(node, action));   // 文字の結果がまだ分かっていない分も不確かさ（観測がなければ画面だけ）
    }
    /// <summary>
    /// v0.27.2: 手の結果の静止した画面の文字を、手の流れを止めずに読む（同時に 1 つ。読み取り中なら読まずに数える）。
    /// 変わった区画だけを読み、同じ見た目の区画は前の結果を使う。届いたら: 前の手の結果の文字との差を（手の前の状態, 手）の観測にし、前景の鍵の文字の配置を更新する
    /// </summary>

    /// <summary>
    /// v0.33.3 dual-append。フラグ OFF なら即 return。ON なら RuntimeKernel で experience-vnext.sqlite へ併記。
    /// 旧 memory.sqlite の学習記録は触らない。失敗は警告のみ。
    /// </summary>
    private void TryDualAppendVNext(StreamStep st, ExploreAction ca, string source, string curNode, string node2, bool changedNow,
        SequenceObservation so, PredictionCheck check, PredictedOutcome prediction)
    {
        if (!RuntimeFlags.UseExperienceVNextWriter) return;
        try
        {
            var inferred = new List<(string Code, string Detail)>();
            ObservationKind kind;
            string? obsJson = null;
            int? obsVer = null;
            var latest = LatestObservation;
            string? bundleJson = null;
            if (latest != null)
            {
                kind = ObservationKind.Structured;
                obsJson = latest.ToJson();
                obsVer = latest.Schema;
                // S2: Kernel 正規観測面として Bundle を併記（学習は Structured / memory.sqlite のまま）
                var forKernel = TryBuildBundleForKernel(latest);
                if (forKernel != null) bundleJson = forKernel.ToJson();
            }
            else
            {
                kind = ObservationKind.LegacyCoarse;
                inferred.Add(("no_structured_at_dual_append", "LatestObservation was null; used legacy_coarse"));
                inferred.Add(("legacy_state_as_opaque_token", "curNode/node2 copied as opaque tokens"));
            }

            string actionSource = ca.IsWait ? "wait" : source;
            if (ca.IsWait && source != "wait")
                inferred.Add(("action_source_mapped", $"session source={source} → wait for wait action"));

            string? predJson = null;
            try
            {
                predJson = Json.Obj(
                    ("predicted_next", prediction.TopNext),
                    ("p_top", prediction.PTop),
                    ("p_change", prediction.PChange),
                    ("hit", check.Hit),
                    ("note", "opaque copy from session prediction; not reinterpreted"));
            }
            catch { /* ignore prediction serialize */ }

            var ctx = new StepContext(
                ObservationKind: kind,
                Seq: st.Seq,
                RunId: RunId,
                ObservationJson: obsJson,
                ObservationSchemaVersion: obsVer,
                ResultChanged: changedNow,
                ResultNextState: node2,
                ResultChangeRatio: st.ChangeRatio,
                PredictionJson: predJson,
                Surprisal: so.Surprisal,
                Excess: so.Excess,
                LegacyStateBefore: curNode,
                Inferred: inferred,
                CallSite: "session_dual_append",
                ObservationBundleJson: bundleJson,
                CausalComparisonJson: LatestCausalComparison?.ToJson());

            var result = VNextDualAppend.Try(ref _vnextKernel, _env.ProfileDataDir, AppEnv.Version,
                new ActionSpec(ca.Name, actionSource), ctx,
                e => Emit("experience-vnext への併記に失敗: " + e.Message, LogLevel.Warn));
            if (result is { Wrote: true })
                _env.Log.Write("vnext_dual_append", ("seq", st.Seq), ("action", ca.Name), ("experience_id", result.ExperienceId));
        }
        catch (Exception e)
        {
            Emit("experience-vnext への併記に失敗: " + e.Message, LogLevel.Warn);
        }
    }

    private Task ReadTextEachStep(int step, string before, string action, string source, string after, GameKouryakuKun.Core.Imaging.Frame? frame, double boundaryMs,
        long seq = 0, string? visState = null, bool changedNow = false, double? naturalChange = null)
    {
        var chain = _o.Ocr;
        if (frame == null) return Task.CompletedTask;
        var prev = _textLast; long prevStep = _textLastStep;
        var worldPred = _lastWorldPred;   // v0.39.1: 観測を始めた時点の世界予測（後の手の予測で上書きされても、この観測は自分の手の予測と比べる）
        var ledger = Ledger; double obsStart = MonoClock.Now;
        ledger?.Open(step, seq, "ocr", frame.Id, frame.TimestampMs, GameKouryakuKun.Core.Throughput.FramePosition.Settle, obsStart);
        if (chain == null)
        {
            // OCR がなくても、画素の区画と大域の層は毎手作る（文字は not_observed）
            var cells = _textReader.StableCells(frame);
            var noText = new GameKouryakuKun.Core.Ocr.TextSnapshot(frame.Id, frame.Width, frame.Height, Array.Empty<GameKouryakuKun.Core.Ocr.OcrLine>(), cells);
            EmitObservation(step, seq, before, action, source, after, visState, prev, null, "none", null, null, 0, ChangedCells(prev, noText), boundaryMs, changedNow, naturalChange, null, frame, worldPred);
            _textLast = noText; _textLastStep = step;
            if (ledger != null) LogSensor(ledger.Complete(step, "ocr", frame.Id, seq, obsStart, MonoClock.Now, inherited: false, detail: "no_engine"));
            return Task.CompletedTask;
        }
        Interlocked.Increment(ref Ocr2.Requested);
        // v0.29.0: 1 手 = 1 完全な観測。前の手の読み取りを待ってから次の手を決めるので、読み取り中で読まない手はない（呼び出し側が待つ）
        Interlocked.Exchange(ref _ocrStepBusy, 1);
        Interlocked.Exchange(ref _ocrBusy, 1);
        return Task.Run(async () =>
        {
            GameKouryakuKun.Core.Ocr.TextSnapshot? snapOut = null; GameKouryakuKun.Core.Ocr.TextDiff? diffOut = null; string kindOut = "failed"; double causedOut = 0; List<int>? pixOut = null; ulong? fgOut = null;
            GameKouryakuKun.Core.Ocr.OcrBox? boxOut = null;
            try
            {
                RuntimeFlags.ApplyImportanceEffective();
                bool liveRoi = GameKouryakuKun.Core.Importance.LiveRoiPolicy.IsLiveEnabled(RuntimeFlags.AttachImportance);
                double ocrW = LatestImportance?.Attention?.Ocr ?? 1.0;
                bool wantDefer = false; string deferReason = "n/a";
                if (liveRoi && prev != null)
                {
                    Interlocked.Increment(ref LiveRoiConsidered);
                    wantDefer = GameKouryakuKun.Core.Importance.LiveRoiPolicy.ShouldDeferEngineRead(ocrW, _ocrEngineStepsSince, out deferReason);
                    if (!wantDefer && deferReason == "rare_recheck_floor") Interlocked.Increment(ref LiveRoiForcedRead);
                }
                var (snap, kind, ms, area) = await _textReader.ReadAsync(frame, prev, async f => (await chain.RecognizeAsync(f).ConfigureAwait(false)).Result, forceDeferEngine: wantDefer).ConfigureAwait(false);
                _textLast = snap; _textLastStep = step; snapOut = snap; kindOut = kind; boxOut = _textReader.LastRegion;
                if (kind is "full" or "region") _ocrEngineStepsSince = 0;
                else _ocrEngineStepsSince++;
                if (kind == "deferred")
                {
                    Interlocked.Increment(ref LiveRoiDeferred);
                    _env.Log.Write("live_roi", ("step", step), ("mode", "deferred"), ("ocr_weight", ocrW), ("reason", deferReason),
                        ("steps_since_engine", _ocrEngineStepsSince), ("policy", "live_ocr_defer"));
                }
                else if (liveRoi && wantDefer == false && deferReason == "rare_recheck_floor")
                    _env.Log.Write("live_roi", ("step", step), ("mode", "forced_read"), ("ocr_weight", ocrW), ("reason", deferReason), ("kind", kind), ("policy", "live_ocr_defer"));
                else if (liveRoi)
                    _env.Log.Write("live_roi", ("step", step), ("mode", kind is "full" or "region" ? "engine" : kind), ("ocr_weight", ocrW), ("reason", deferReason), ("kind", kind), ("policy", "live_ocr_defer"));
                Ocr2.Arrived(step, kind, ms, area);
                var feat = snap.Features;
                // v0.28.0: 前景の鍵の文字の配置からは、手によらず変わると分かった文字の区画の行を除く（観測・文字の記録からは除かない）
                List<GameKouryakuKun.Core.Ocr.OcrLine> keyLines;
                lock (TextBook) keyLines = snap.Lines.Where(l => TextBook.Regions.Class(after, GameKouryakuKun.Core.Ocr.TextDiff.LayoutCell(l, snap.Width, snap.Height)) != "independent").ToList();
                if (keyLines.Count != snap.Lines.Count) Interlocked.Add(ref FgNaturalLinesExcluded, snap.Lines.Count - keyLines.Count);
                var keyFeat = keyLines.Count == snap.Lines.Count ? feat : GameKouryakuKun.Core.Ocr.TextFeatures.Of(new GameKouryakuKun.Core.Ocr.OcrResult("snapshot", keyLines, 0, ""), snap.Width, snap.Height);
                lock (Fg) Fg.ObserveText(after, keyFeat.LayoutHash, feat.HasText);
                fgOut = keyFeat.LayoutHash;
                string diffKind = "unpaired";
                if (prev != null && prevStep == step - 1)
                {
                    var d = GameKouryakuKun.Core.Ocr.TextDiff.Of(prev, snap); diffKind = d.Kind;
                    if (d.PixelUnconfirmed > 0) Interlocked.Add(ref Ocr2.Suppressed, d.PixelUnconfirmed);
                    Interlocked.Increment(ref Ocr2.Pairs); if (d.Any) Interlocked.Increment(ref Ocr2.TextChanged);
                    var pix = new List<int>();
                    if (prev.CellHashes.Length == snap.CellHashes.Length) for (int i = 0; i < snap.CellHashes.Length; i++) if (prev.CellHashes[i] != snap.CellHashes[i]) pix.Add(i);
                    double caused = 0; (int Cell, double Rate, double Confidence, string Basis) top = (-1, 0, 0, "none");
                    diffOut = d; pixOut = pix;
                    lock (TextBook)
                    {
                        if (source == "wait") { TextBook.Natural.ObserveNatural(before, boundaryMs, d.Any, false, fromWait: true); TextBook.ObserveNaturalDiff(before, d); }
                        else { TextBook.ObserveAction(before, action, d); TextBook.Natural.ObserveActionTurn(before, boundaryMs); caused = TextBook.LastCaused; top = TextBook.TopCause(before, action); }
                    }
                    lock (PixelRegions) { if (source == "wait") PixelRegions.ObserveNatural(before, pix); else PixelRegions.ObserveAction(before, action, pix); }
                    var changedTexts = d.Cells.SelectMany(c => snap.Lines.Concat(prev.Lines).Where(l => GameKouryakuKun.Core.Ocr.TextDiff.LayoutCell(l, snap.Width, snap.Height) == c).Select(l => l.Text.Trim())).Where(t => t.Length > 0).Distinct().ToList();
                    lock (_textByStep)
                    {
                        _textByStep[step] = (d.Any, changedTexts);
                        if (_textByStep.TryGetValue(step - 1, out var pv)) lock (TextLink) TextLink.Observe(before, action, pv.Changed, after, pv.Texts);
                        _textByStep.Remove(step - 2);
                    }
                    if (d.Any) _env.Log.Write("text_change", ("step", step), ("state", before), ("action", action), ("source", source), ("kind", d.Kind), ("added", d.Added), ("removed", d.Removed), ("changed", d.Changed), ("numbers_only", d.NumbersOnly), ("cells", d.Cells.ToList()), ("pixel_unconfirmed", d.PixelUnconfirmed),
                        ("action_caused", Math.Round((causedOut = caused), 3)), ("top_cell", top.Cell), ("top_rate", Math.Round(top.Rate, 3)), ("cause_confidence", Math.Round(top.Confidence, 3)), ("basis", top.Basis), ("pixel_cells", pix.Count));
                }
                else Interlocked.Increment(ref Ocr2.Unpaired);
                if (kind is "full" or "region" && StateText is { } book)
                {
                    bool cand; lock (_boundaryStates) cand = _boundaryStates.Contains(after);
                    bool want; lock (book) want = book.Wants(after, cand);
                    if (want)   // 状態ごとの文字の例（v0.25.2 の集計）は従来どおり決まった回数まで
                    {
                        var r = new GameKouryakuKun.Core.Ocr.OcrResult(chain.FirstAvailable()?.Id ?? "ocr", snap.Lines, ms, kind);
                        lock (book) book.Add(after, r, snap.Width, snap.Height);
                        _env.Log.Write("ocr_observation", ("state", after), ("engine", r.EngineId), ("lines", feat.Lines), ("chars", feat.Chars), ("numbers", feat.Numbers),
                            ("text_hash", feat.TextHash.ToString("x16")), ("layout_hash", feat.LayoutHash.ToString("x16")), ("ms", Math.Round(ms, 1)), ("candidate", cand));
                    }
                }
                if (kind is "full" or "region")
                    _env.Log.Write("ocr_step", ("step", step), ("state", after), ("kind", kind), ("ms", Math.Round(ms, 1)), ("area", Math.Round(area, 3)), ("lines", feat.Lines), ("layout_hash", feat.LayoutHash.ToString("x16")), ("diff", diffKind));
            }
            catch (Exception e) { Interlocked.Increment(ref Ocr2.Failed); _env.Log.Write("ocr_failed", ("state", after), ("error", e.Message), ("each_step", true)); }
            finally
            {
                try
                {
                    if (snapOut == null) { var cells = _textReader.StableCells(frame); pixOut = ChangedCells(prev, new GameKouryakuKun.Core.Ocr.TextSnapshot(frame.Id, frame.Width, frame.Height, Array.Empty<GameKouryakuKun.Core.Ocr.OcrLine>(), cells)); }
                    EmitObservation(step, seq, before, action, source, after, visState, prev, snapOut, kindOut, boxOut, diffOut, causedOut, pixOut ?? ChangedCells(prev, snapOut!), boundaryMs, changedNow, naturalChange, fgOut, frame, worldPred);
                    if (ledger != null)
                        LogSensor(snapOut != null
                            ? ledger.Complete(step, "ocr", frame.Id, seq, obsStart, MonoClock.Now, inherited: kindOut is not ("full" or "region"), detail: kindOut)
                            : ledger.Fail(step, "ocr", MonoClock.Now, "ocr_failed"));
                }
                catch (Exception e)
                {
                    _env.Log.Write("observation_failed", ("step", step), ("error", e.Message));
                    if (ledger != null && ledger.Unresolved().Any(u => u.Step == step && u.Sensor == "ocr")) LogSensor(ledger.Fail(step, "ocr", MonoClock.Now, "observation_failed"));
                }
                Interlocked.Exchange(ref _ocrStepBusy, 0); Interlocked.Exchange(ref _ocrBusy, 0);
            }
        });
    }
    private readonly HashSet<string> _boundaryStates = new(StringComparer.Ordinal);
    public GameKouryakuKun.Core.Throughput.StepTiming? StepTimingRef { get; private set; }
    public GameKouryakuKun.Core.Throughput.ObservationLedger? Ledger { get; private set; }
    private Task? _pendingObs; private double _runStartMs; private int _actionsSent;
    public long RawCommitted;
    public GameKouryakuKun.Core.Throughput.NaturalNoiseModel? NoiseModel { get; private set; }
    /// <summary>T3: 区画の変化を自然変化のモデルに入れる（入力なし）か、注釈する（入力の手）。観測・学習は変えない</summary>
    private void ObserveNoise(string key, GameKouryakuKun.Core.Imaging.Frame a, GameKouryakuKun.Core.Imaging.Frame b, GameKouryakuKun.Core.Imaging.DiffOptions diff, int step,
        GameKouryakuKun.Core.Throughput.FramePosition pos, bool natural)
    {
        try
        {
            var blocks = GameKouryakuKun.Core.Imaging.FrameDiff.Compare(a, b, diff, null).ChangedBlocks;
            if (blocks == null || NoiseModel == null) return;
            lock (NoiseModel)
            {
                if (natural) NoiseModel.ObserveNatural(key, blocks, b.TimestampMs);
                else
                {
                    var an = NoiseModel.Annotate(key, blocks);
                    _env.Log.Write("noise_annotation", ("step", step), ("screen", key), ("frame_id", b.Id), ("position", GameKouryakuKun.Core.Throughput.SensorResult.Name(pos)),
                        ("changed", an.Changed), ("natural", an.Natural), ("input", an.Input), ("no_baseline", an.NoBaseline), ("periodic", an.Periodic));
                }
            }
        }
        catch (Exception e) { _env.Log.Write("noise_failed", ("step", step), ("error", e.Message)); }
    }
    public long RawExtraRows, RawDuplicates, RawAggErrors;   // T4: Raw ストアの行（memory の turn 行に加えて）
    private RawAggStore? _rawAgg;
    public GameKouryakuKun.Core.Throughput.ThroughputMetrics CurrentThroughput()
    {
        var l = Ledger;
        return new GameKouryakuKun.Core.Throughput.ThroughputMetrics((MonoClock.Now - _runStartMs) / 60000.0, _actionsSent, l?.CausallyIntact ?? 0, l?.Violations ?? 0,
            l?.LateAmended ?? 0, l?.Unresolved().Count ?? 0, l?.Missing ?? 0, RawCommitted, l?.DecidedWithPending ?? 0);
    }
    private void LogSensor(GameKouryakuKun.Core.Throughput.SensorResult? r)
    {
        if (r == null) return;
        _env.Log.Write("sensor_result", ("step", r.Step), ("seq", r.Seq), ("frame_id", r.FrameId), ("capture_ms", Math.Round(r.CaptureMs, 1)), ("position", GameKouryakuKun.Core.Throughput.SensorResult.Name(r.Position)),
            ("sensor", r.Sensor), ("start_ms", Math.Round(r.StartMs, 1)), ("finish_ms", Math.Round(r.FinishMs, 1)), ("provenance", GameKouryakuKun.Core.Throughput.SensorResult.Name(r.Provenance)), ("detail", r.Detail));
    }
    public GameKouryakuKun.Core.Observation.ObservationBuilder ObsBuilder { get; } = new();
    public long ObsSaved, ObsFailed, ObsTextNotObserved;
    public GameKouryakuKun.Core.Observation.EntityTracker Entities { get; } = new();
    public GameKouryakuKun.Core.Observation.CoarseStateEvidence Coarse { get; } = new();
    private volatile GameKouryakuKun.Core.Exploration.PredictedOutcome? _lastWorldPred;
    private GameKouryakuKun.Core.Observation.EntityLayer? _prevEntity;
    private int[] _lastEvents = Array.Empty<int>();
    private string _lastLocalCtx = "none";
    /// <summary>S4: 直前の OCR エンジン（full/region）からの手数（rare recheck 用）。</summary>
    private int _ocrEngineStepsSince;
    public long LiveRoiDeferred, LiveRoiForcedRead, LiveRoiConsidered;
    public GameKouryakuKun.Core.Observation.SkillLocalEffects SkillEffects { get; } = new();
    public GameKouryakuKun.Core.Observation.LocalEventModel LocalEvents { get; } = new();
    private static List<int> ChangedCells(GameKouryakuKun.Core.Ocr.TextSnapshot? a, GameKouryakuKun.Core.Ocr.TextSnapshot b)
    {
        var l = new List<int>();
        if (a == null || a.CellHashes.Length != b.CellHashes.Length) return l;
        for (int i = 0; i < b.CellHashes.Length; i++) if (a.CellHashes[i] != b.CellHashes[i]) l.Add(i);
        return l;
    }
    /// <summary>v0.29.0: 1 手の構造化した観測を作って DB とログに残す（意味は付けない）</summary>
    private void EmitObservation(int step, long seq, string before, string action, string source, string after, string? visState,
        GameKouryakuKun.Core.Ocr.TextSnapshot? prev, GameKouryakuKun.Core.Ocr.TextSnapshot? snap, string readKind, GameKouryakuKun.Core.Ocr.OcrBox? box, GameKouryakuKun.Core.Ocr.TextDiff? diff, double caused,
        List<int> pix, double boundaryMs, bool changedNow, double? naturalChange, ulong? fg, GameKouryakuKun.Core.Imaging.Frame frame,
        GameKouryakuKun.Core.Exploration.PredictedOutcome? worldPred = null)
    {
        GameKouryakuKun.Core.Observation.StructuredObservation o;
        lock (ObsBuilder)
        {
            var parts = after.Split('|', 2);
            string? mode = parts[0], layout = parts.Length > 1 ? parts[1].Split('#')[0] : null;
            var global = new GameKouryakuKun.Core.Observation.GlobalScene(after, mode, layout, visState, fg?.ToString("x16"), GameKouryakuKun.Core.Observation.LayerStatus.Observed);
            IReadOnlyList<GameKouryakuKun.Core.Observation.ChangeRegion> regs;
            // v0.30.4: 局所パターンの追跡（共通のずれの判定を含む）を先に行い、共通のずれがあれば変化した領域はずれを差し引いた後の変化で作る（学習の入力 pix はそのまま）
            GameKouryakuKun.Core.Observation.EntityLayer? entPre = null;
            try { entPre = Entities.Update(GameKouryakuKun.Core.Observation.FineGrid.Of(frame, Entities.Stabilizer), frame); }
            catch (Exception e) { _env.Log.Write("entity_failed", ("step", step), ("error", e.Message)); }
            // v0.30.6: 画面の過半が変わった手の縮小画像（128×96 の明るさ）を最初の 24 回だけ記録する（共通のずれの判定を後で確かめる。判定・学習には使わない）
            bool noOff = Entities.Motion.LastReason == "no_offset";
            if (Entities.Motion.LastSample is { } smp && (noOff ? _noOffsetSamples < 12 : _shiftSamples < 24))   // v0.32.6: ずらしても差が減らなかった手は別に 12 回まで
            {
                if (noOff) _noOffsetSamples++; else _shiftSamples++;
                _env.Log.Write("shift_sample", ("step", step), ("action", action), ("w", GameKouryakuKun.Core.Observation.CommonMotion.Sx), ("h", GameKouryakuKun.Core.Observation.CommonMotion.Sy),
                    ("probe", entPre?.ShiftProbe is { } spb ? string.Join(",", spb) : null), ("floor", Entities.Motion.LastFloor),
                    ("median0", Entities.Motion.LastMedian0), ("improved", Entities.Motion.LastImproved), ("comparable", Entities.Motion.LastComparable), ("reason", Entities.Motion.LastReason),
                    ("prev", Convert.ToBase64String(smp.Prev)), ("cur", Convert.ToBase64String(smp.Cur)),
                    ("hi_prev", Entities.Motion.LastHiCrop is { } hc ? Convert.ToBase64String(hc.Prev) : null), ("hi_cur", Entities.Motion.LastHiCrop is { } hc2 ? Convert.ToBase64String(hc2.Cur) : null));
            }
            IReadOnlyList<int> pixObs = pix;
            if (entPre?.CommonShift != null && entPre.ResidualCells is { } resid)
                pixObs = resid.Select(c => (c / GameKouryakuKun.Core.Observation.FineGrid.DefaultCols / 2) * GameKouryakuKun.Core.Ocr.TextReader.GridCols + (c % GameKouryakuKun.Core.Observation.FineGrid.DefaultCols) / 2).Distinct().OrderBy(x => x).ToList();
            Covariation.Observe(pixObs, source == "wait");   // v0.31.0（P-4）
            lock (PixelRegions) regs = ObsBuilder.Regions(pixObs, i => (PixelRegions.Class(before, i), PixelRegions.NaturalRate(before, i)?.P));
            var regions = new GameKouryakuKun.Core.Observation.RegionLayer(prev == null ? GameKouryakuKun.Core.Observation.LayerStatus.NotObserved : GameKouryakuKun.Core.Observation.LayerStatus.Observed, pixObs.Count, regs)
                { CommonShift = entPre?.CommonShift, RawChangedCells = entPre?.CommonShift != null ? pix.Count : null };
            var text = _o.Ocr == null ? new GameKouryakuKun.Core.Observation.TextLayer(GameKouryakuKun.Core.Observation.LayerStatus.NotObserved, "no_ocr", "unknown", Array.Empty<GameKouryakuKun.Core.Observation.TextLineObs>(), Array.Empty<GameKouryakuKun.Core.Observation.NumberObs>(), 0, 0, 0)
                : ObsBuilder.Text(prev, snap, readKind, box, diff, caused);
            if (text.Status == GameKouryakuKun.Core.Observation.LayerStatus.NotObserved) ObsTextNotObserved++;
            var temporal = new GameKouryakuKun.Core.Observation.TemporalLayer(Math.Round(boundaryMs, 1), changedNow, naturalChange is double nc ? Math.Round(nc, 4) : null, GameKouryakuKun.Core.Observation.LayerStatus.Observed) { Delayed = _pendingDelayed };
            o = ObsBuilder.Build(step, source == "human" ? "human" : source == "wait" ? "wait" : "agent", action, before, after, global, regions, text, temporal);
            // v0.29.1: 局所パターンの候補・追跡・見た目の状態・関係と、層ごとの出来事の予測と外れ（記録と評価だけ。手の選び方は v0.29.0 のまま）
            try
            {
                var ent = entPre ?? throw new InvalidOperationException("entity update failed earlier");
                var evs = GameKouryakuKun.Core.Observation.LocalEventModel.EventsOf(ent, text);
                var lctx = GameKouryakuKun.Core.Observation.CoarseStateEvidence.Context(_prevEntity);   // v0.30.9（P-3）: 局所の文脈で分けた予測
            var pr = LocalEvents.PredictContext(before, action, lctx);
                double[] surp = ent.Status == GameKouryakuKun.Core.Observation.LayerStatus.Observed ? LocalEvents.Observe(before, action, o.Provenance == "wait", evs, ent, lctx) : Array.Empty<double>();
                o = o with { EntityLayer = ent, Prediction = new GameKouryakuKun.Core.Observation.PredictionObs(pr == null ? GameKouryakuKun.Core.Observation.LayerStatus.NotObserved : GameKouryakuKun.Core.Observation.LayerStatus.Observed,
                    pr?.P.Select(x => Math.Round(x, 4)).ToArray(), evs, surp.Select(x => Math.Round(x, 4)).ToArray(), pr?.Move is { } m ? new[] { m.Dx, m.Dy } : null) };
                if ((worldPred ?? _lastWorldPred) is { } wp && wp.Node == before && wp.Action == action && o.Prediction != null)
                {
                    bool hit = wp.TopNext == after;
                    o = o with { Prediction = o.Prediction with { WorldNext = wp.TopNext, WorldP = wp.Top.Count > 0 ? Math.Round(wp.Top[0].P, 4) : null, WorldHit = hit } };
                    Coarse.Observe(before, action, hit, surp);
                }
                if (ent.Status == GameKouryakuKun.Core.Observation.LayerStatus.Observed)
                    Coarse.ObserveContext(before, action, GameKouryakuKun.Core.Observation.CoarseStateEvidence.Context(_prevEntity), evs);   // v0.29.6: 分けたときの試算の材料（分けない）
                lock (AppTable) AppTable.Observe(_prevEntity, ent, action);   // v0.31.5（P-6 の一部）: 見た目の表（記録だけ）
                lock (EntityExp) EntityExp.Observe(_prevEntity, ent, action, evs.Contains(Array.IndexOf(GameKouryakuKun.Core.Observation.LocalEventModel.Events, "text_changed")), evs.Contains(Array.IndexOf(GameKouryakuKun.Core.Observation.LocalEventModel.Events, "number_changed")));   // v0.32.0（P-6）
                lock (NumTable) NumTable.Observe(o.Text, action);
                _prevEntity = ent; _lastEvents = evs; _lastLocalCtx = lctx;
            }
            catch (Exception e) { _env.Log.Write("entity_failed", ("step", step), ("error", e.Message)); }
        }
        LatestView = (o, frame); LatestObservation = o;   // v0.30.0: 重ね表示（監査用）。v0.30.3: 観測した画面も渡す
        try { if (RunId is long rid) _env.Memory.AddObservation(rid, seq, step, o.Schema, o.Provenance, o.ToJson()); Interlocked.Increment(ref ObsSaved); }
        catch (Exception e) { Interlocked.Increment(ref ObsFailed); _env.Log.Write("observation_failed", ("step", step), ("error", e.Message)); }
        _env.Log.Write("observation", ("step", step), ("schema", o.Schema), ("provenance", o.Provenance), ("text", o.Text.Status), ("lines", o.Text.Lines.Count), ("observed", o.Text.Observed), ("inherited", o.Text.Inherited),
            ("numbers", o.Text.Numbers.Count), ("regions", o.Regions.Regions.Count), ("changed_cells", o.Regions.ChangedCells), ("new_tracks", o.Regions.Regions.Count(r => r.NewTrack)),
            ("entities", o.EntityLayer?.Candidates.Count), ("relations", o.EntityLayer?.Relations.Count), ("events", o.Prediction == null ? null : string.Join(",", o.Prediction.Occurred.Select(i => GameKouryakuKun.Core.Observation.LocalEventModel.Events[i]))));
        // v0.33.5 S2: Observation Bundle 派生（既定 OFF。探索・学習・GT は不変）
        TryAttachObservationBundle(o, step);
        // v0.33.9 S3: Causal WM + Predictive WM short-horizon / H3（既定 OFF）
        TryAttachCausalWm(o, step);
        // v0.34.0 S4: Importance & Attention（既定 OFF・advisory）
        TryAttachImportance(o, step);
    }

    /// <summary>
    /// S2 Sensor Stack: StructuredObservation から Bundle を派生し診断ログへ要約。
    /// <see cref="RuntimeFlags.AttachObservationBundle"/> 既定 OFF（GKK_ATTACH_OBSERVATION_BUNDLE=1 で ON）。
    /// </summary>
    private void TryAttachObservationBundle(GameKouryakuKun.Core.Observation.StructuredObservation o, long step)
    {
        RuntimeFlags.ApplyBundleEffective();
        if (!RuntimeFlags.AttachObservationBundle) { LatestObservationBundle = null; return; }
        try
        {
            GameKouryakuKun.Core.Observation.ObservationBundle bundle;
            lock (EntityExp)
                bundle = GameKouryakuKun.Core.Observation.SensorStackAssembler.FromStructured(o, entityExperience: EntityExp);
            LatestObservationBundle = bundle;
            _env.Log.Write("observation_bundle",
                ("step", step),
                ("schema", bundle.SchemaVersion),
                ("assembler", GameKouryakuKun.Core.Observation.SensorStackAssembler.AssemblerId),
                ("gt_decision", bundle.CoordinateFrame?.Decision),
                ("gt_accepted", bundle.CoordinateFrame?.Accepted),
                ("candidates", bundle.Candidates.Count),
                ("tracks", bundle.Tracks.Count),
                ("relations", bundle.Relations.Count),
                ("ee_observed", bundle.EntityExperience?.Observed),
                ("ee_keys", bundle.EntityExperience?.Keys),
                ("layer_cf", bundle.Layers.CoordinateFrame),
                ("layer_ee", bundle.Layers.EntityExperience),
                ("layer_cf_ent", bundle.Layers.Candidates),
                ("changed_cells", bundle.ChangeField?.ChangedCells));
        }
        catch (Exception e)
        {
            LatestObservationBundle = null;
            _env.Log.Write("observation_bundle_failed", ("step", step), ("error", e.Message));
        }
    }

    /// <summary>S2: dual-append / Kernel 用に Bundle を派生（Attach フラグ OFF でも dual-append ON なら組む）。学習は触らない。</summary>
    private GameKouryakuKun.Core.Observation.ObservationBundle? TryBuildBundleForKernel(GameKouryakuKun.Core.Observation.StructuredObservation o)
    {
        try
        {
            if (LatestObservationBundle is { } cached && cached.Step == o.Step
                && (LatestCausalComparison == null || cached.CausalComparison != null)) return cached;
            lock (EntityExp)
                return GameKouryakuKun.Core.Observation.SensorStackAssembler.FromStructured(
                    o, entityExperience: EntityExp, causalComparison: LatestCausalComparison);
        }
        catch (Exception e)
        {
            _env.Log.Write("observation_bundle_kernel_failed", ("step", o.Step), ("error", e.Message));
            return null;
        }
    }


    private void FlushPeClosedLoop(string reason)
    {
        RuntimeFlags.ApplyCausalWmEffective();
        if (!RuntimeFlags.AttachCausalWm) return;
        try
        {
            _env.Log.Write("pe_closed_loop_summary",
                ("reason", reason),
                ("summary", PeLoop.SummaryLine()),
                ("pending", PeLoop.PendingCount),
                ("enqueued", PeLoop.Enqueued),
                ("dropped", PeLoop.Dropped),
                ("tags", string.Join(",", PeLoop.TagCounts().Select(kv => kv.Key + ":" + kv.Value))),
                ("preferred", string.Join(",", PeLoop.PreferredCounts().Select(kv => kv.Key + ":" + kv.Value))));
            if (_peStore != null) _peStore.PersistSnapshot(PeLoop);
            else if (PeLoop.Enqueued > 0)
            {
                _peStore = PeClosedLoopStore.Open(_env.ProfileDataDir, AppEnv.Version);
                _peStore.PersistSnapshot(PeLoop);
            }
        }
        catch (Exception e) { _env.Log.Write("pe_closed_loop_summary_failed", ("error", e.Message)); }
    }

    /// <summary>
    /// S3 Causal WM: H0–H4 比較を既存 NaturalBaseline / RegionCausality / delayed から派生。
    /// <see cref="RuntimeFlags.AttachCausalWm"/> 既定 OFF（GKK_ATTACH_CAUSAL_WM=1）。
    /// </summary>
    private void TryAttachCausalWm(GameKouryakuKun.Core.Observation.StructuredObservation o, long step)
    {
        RuntimeFlags.ApplyCausalWmEffective();
        if (!RuntimeFlags.AttachCausalWm) { LatestCausalComparison = null; LatestShortHorizon = null; return; }
        try
        {
            // Prefer residual / region cells if present; RegionLayer stores bbox only — use Entity residual when available.
            IReadOnlyCollection<int>? changedCells = o.EntityLayer?.ResidualCells;
            if (changedCells == null || changedCells.Count == 0)
            {
                // Synthesize cell indices from region bbox for coarse contrast (formal grid indices).
                var list = new List<int>();
                int cols = ObsBuilder.Cols;
                foreach (var r in o.Regions.Regions)
                    for (int y = r.Y0; y <= r.Y1; y++)
                        for (int x = r.X0; x <= r.X1; x++)
                            list.Add(y * cols + x);
                changedCells = list;
            }
            bool trackUnstable = o.EntityLayer?.Candidates.Any(c => c.IdentityPosterior is <= 0 || (!c.Continuous && !c.New)) == true;
            GameKouryakuKun.Core.Causality.ShortHorizonForecast preSh;
            lock (ObsBuilder)
            {
                lock (EntityExp)
                    preSh = GameKouryakuKun.Core.Causality.PredictiveWmShortHorizon.Forecast(
                        new GameKouryakuKun.Core.Causality.ShortHorizonInput(
                            o.Before, o.Action, LocalEvents, _lastLocalCtx,
                            o.Prediction?.Occurred, o.Prediction?.Surprise,
                            WorldModel: null,
                            ActualNextNode: o.After,
                            EntityExperience: EntityExp));
            }
            LatestShortHorizon = preSh;
            var input = new GameKouryakuKun.Core.Causality.CausalCompareInput(
                Step: step,
                Action: o.Action,
                Node: o.Before,
                Changed: o.Regions.ChangedCells > 0 || (o.Temporal.Changed),
                Provenance: o.Provenance,
                NaturalBaseline: ExploreBaseline,
                RegionCausality: PixelRegions,
                ChangedCells: changedCells,
                DelayedPresent: o.Temporal.Delayed != null,
                DelayedCells: o.Temporal.Delayed?.Cells,
                TrackUnstableHint: trackUnstable,
                ResidualCellCount: o.EntityLayer?.ResidualCells?.Count,
                LocalEvents: LocalEvents,
                LocalContextKey: _lastLocalCtx,
                ObservedLocalEvents: o.Prediction?.Occurred,
                LocalSurprise: o.Prediction?.Surprise,
                ActualNextNode: o.After,
                EntityExperience: EntityExp,
                ShortHorizon: preSh);
            var rec = GameKouryakuKun.Core.Causality.CausalHypothesisComparer.Compare(input);
            LatestCausalComparison = rec;
            // PE closed-loop: record→queue (WM accept remains stub)
            var cands = PeLoop.Observe(rec, RunId);
            try
            {
                _peStore ??= PeClosedLoopStore.Open(_env.ProfileDataDir, AppEnv.Version);
                _peStore.PersistSnapshot(PeLoop);
            }
            catch (Exception pe) { _env.Log.Write("pe_closed_loop_persist_failed", ("error", pe.Message)); }
            foreach (var c in cands)
                _env.Log.Write("pe_feature_candidate",
                    ("candidate_id", c.CandidateId), ("tag", c.Tag), ("status", c.Status), ("step", c.Step), ("preferred", c.PreferredHypothesis));
            // Refresh Bundle with causal if attach-bundle also on
            if (LatestObservationBundle != null && RuntimeFlags.AttachObservationBundle)
            {
                lock (EntityExp)
                    LatestObservationBundle = GameKouryakuKun.Core.Observation.SensorStackAssembler.FromStructured(
                        o, entityExperience: EntityExp, causalComparison: rec);
            }
            _env.Log.Write("causal_wm",
                ("step", step),
                ("preferred", rec.Preferred),
                ("tags", string.Join(",", rec.PeRoutingTags)),
                ("sources", string.Join(",", rec.EvidenceSources)),
                ("sh_status", preSh.Status),
                ("sh_miss", preSh.PredictionMiss),
                ("sh_ctx_help", preSh.ContextHelpEvents),
                ("h0", rec.Hypotheses.FirstOrDefault(h => h.Id == GameKouryakuKun.Core.Causality.CausalHypothesisIds.H0Natural)?.Status),
                ("h1", rec.Hypotheses.FirstOrDefault(h => h.Id == GameKouryakuKun.Core.Causality.CausalHypothesisIds.H1Immediate)?.Status),
                ("h2", rec.Hypotheses.FirstOrDefault(h => h.Id == GameKouryakuKun.Core.Causality.CausalHypothesisIds.H2Delayed)?.Status),
                ("h3", rec.Hypotheses.FirstOrDefault(h => h.Id == GameKouryakuKun.Core.Causality.CausalHypothesisIds.H3Context)?.Status));
            // S5: 割込み後の PE/因果 ambiguity 落ちを観察（報酬に使わない）
            if (IntrinsicStats.InterruptsApplied > 0)
                IntrinsicStats.ObserveAftermath(step, rec.Preferred, rec.PeRoutingTags);
        }
        catch (Exception e)
        {
            LatestCausalComparison = null;
            LatestShortHorizon = null;
            _env.Log.Write("causal_wm_failed", ("step", step), ("error", e.Message));
        }
    }


    /// <summary>
    /// S4 Importance & Attention: 重要度推定・注意重み・PE 推奨（WM 自動拡大なし）。
    /// <see cref="RuntimeFlags.AttachImportance"/> 既定 OFF（GKK_ATTACH_IMPORTANCE=1）。
    /// </summary>
    
    /// <summary>
    /// S5: Intrinsic Mixer 並行ログ + 条件付き能動実験ソフト割込み。
    /// Human / Goal / Sequence mid-run では呼ばない／拒否。フラグ OFF なら choice をそのまま返す。
    /// 予算は割込みスパム・同一失敗ループ用（馬鹿プレイ禁止ではない）。
    /// </summary>
    private ExploreChoice TryApplyIntrinsicMixer(Explorer explorer, string node, long step, ExploreChoice choice)
    {
        RuntimeFlags.ApplyIntrinsicMixerEffective();
        if (!RuntimeFlags.UseIntrinsicMixer) { LatestIntrinsic = null; return choice; }
        // 明示ガード（呼び出し側でも Goal を除外するが二重化）
        if (choice.Rule is "human" or "goal" or "sequence_step" or "sequence")
        {
            IntrinsicStats.NoteBlocked("guard:" + choice.Rule);
            LatestIntrinsic = null;
            return choice;
        }
        try
        {
            var sc = choice.Candidates is { Count: > 0 } ? choice.Candidates : explorer.ScoreCandidates(node);
            string? block = GameKouryakuKun.Core.Intrinsic.ActiveExperimentBudget.BlockReason(
                ActiveExperimentInterrupts,
                GameKouryakuKun.Core.Intrinsic.ActiveExperimentBudget.DefaultMaxInterrupts,
                step, _lastActiveExpStep, _activeExpInterruptSteps, _lastActiveExpAction, null);
            var report = GameKouryakuKun.Core.Intrinsic.IntrinsicValueMixer.Mix(
                new GameKouryakuKun.Core.Intrinsic.IntrinsicMixInput(
                    sc, step, LatestCausalComparison, LatestShortHorizon,
                    LatestCausalComparison?.PeRoutingTags,
                    ActiveExperimentInterrupts,
                    GameKouryakuKun.Core.Intrinsic.ActiveExperimentBudget.DefaultMaxInterrupts,
                    SoftInterruptBlock: block,
                    PortfolioWeights: PortfolioWeights.Current));
            LatestIntrinsic = report;
            IntrinsicStats.NoteMixStep();
            _env.Log.Write("intrinsic_mix",
                ("step", step), ("summary", report.SummaryLine()), ("mode", report.Mode),
                ("top", report.TopMixedAction),
                ("stats", IntrinsicStats.SummaryLine()),
                ("scores", report.Scores.Select(x => new Dictionary<string, object?>
                {
                    ["action"] = x.Action, ["mixed"] = x.Mixed, ["components"] = x.Components,
                }).ToList()));

            var exp = report.Experiment;
            bool applied = false;
            string? blockApplied = block;
            if (exp is { Status: "proposed" } && choice.Rule is "scored" or "fallback" or "momentum" or "chord_probe"
                && !string.Equals(exp.Action, choice.Action.Name, StringComparison.Ordinal))
            {
                // 提案 Action に対する同一失敗ループ冷却を再評価
                blockApplied = GameKouryakuKun.Core.Intrinsic.ActiveExperimentBudget.BlockReason(
                    ActiveExperimentInterrupts,
                    GameKouryakuKun.Core.Intrinsic.ActiveExperimentBudget.DefaultMaxInterrupts,
                    step, _lastActiveExpStep, _activeExpInterruptSteps, _lastActiveExpAction, exp.Action);
                if (blockApplied != null)
                {
                    IntrinsicStats.NoteBlocked(blockApplied);
                    exp = new GameKouryakuKun.Core.Intrinsic.ActiveExperimentProposal
                    {
                        Action = exp.Action,
                        Status = "blocked",
                        ExpectedInfoGain = exp.ExpectedInfoGain,
                        CompeteHypotheses = exp.CompeteHypotheses,
                        Note = $"soft_interrupt_block={blockApplied}; was={exp.Note}",
                    };
                }
                else
                {
                    var act = explorer.Actions.FirstOrDefault(a => a.Name == exp.Action && explorer.IsUserAllowed(a.Name));
                    if (act != null)
                    {
                        var cand = sc.FirstOrDefault(c => c.Action == act.Name);
                        choice = new ExploreChoice(act,
                            $"能動実験（仮説識別）: {exp.Note} gain={exp.ExpectedInfoGain:0.00} — 元: {choice.Reason}",
                            cand?.Score ?? choice.Score)
                        {
                            Rule = "active_experiment",
                            Probability = cand?.Probability ?? choice.Probability,
                            Candidates = sc,
                            Information = cand?.Information ?? choice.Information,
                            Priors = choice.Priors,
                        };
                        ActiveExperimentInterrupts++;
                        _lastActiveExpStep = step;
                        _lastActiveExpAction = act.Name;
                        _activeExpInterruptSteps.Add(step);
                        IntrinsicStats.NoteInterruptApplied(step, act.Name,
                            LatestCausalComparison?.Preferred, LatestCausalComparison?.PeRoutingTags);
                        applied = true;
                    }
                }
            }
            else if (exp is { Status: "blocked" or "budget_exhausted" })
            {
                string reason = exp.Status == "budget_exhausted" ? "budget" : (block ?? "guard");
                IntrinsicStats.NoteBlocked(reason);
            }

            // 最終選択の成分寄与
            var chosenScore = report.Scores.FirstOrDefault(s => s.Action == choice.Action.Name);
            IntrinsicStats.NoteChosen(chosenScore);

            if (exp != null)
                _env.Log.Write("active_experiment",
                    ("step", step), ("status", exp.Status), ("action", exp.Action),
                    ("gain", exp.ExpectedInfoGain), ("compete", exp.CompeteHypotheses),
                    ("applied", applied), ("interrupts", ActiveExperimentInterrupts),
                    ("block", blockApplied), ("note", exp.Note),
                    ("stats", IntrinsicStats.SummaryLine()));

            if (step % 25 == 0) try { FlushIntrinsicStats("periodic"); } catch { }
            if (step % 50 == 0) try { TryRunOuterLoopShell(step); } catch { }
            return choice;
        }
        catch (Exception e)
        {
            LatestIntrinsic = null;
            _env.Log.Write("intrinsic_mix_failed", ("step", step), ("error", e.Message));
            return choice;
        }
    }

    private void FlushIntrinsicStats(string reason)
    {
        if (!RuntimeFlags.UseIntrinsicMixer) return;
        if (IntrinsicStats.MixSteps == 0 && IntrinsicStats.InterruptsApplied == 0) return;
        try
        {
            _intrinsicStore ??= IntrinsicStatsStore.Open(_env.ProfileDataDir, AppEnv.Version);
            _intrinsicStore.Persist(IntrinsicStats);
            _env.Log.Write("intrinsic_stats_flush", ("reason", reason), ("summary", IntrinsicStats.SummaryLine()),
                ("store", _intrinsicStore.StatsSummary()));
        }
        catch (Exception e)
        {
            _env.Log.Write("intrinsic_stats_flush_failed", ("reason", reason), ("error", e.Message));
        }
    }



    /// <summary>S8: 長期スイート要約ログ（フラグ ON 時は詳細。OFF でも軽量 snapshot 可だが既定はフラグ連動）。</summary>
    private void TryEmitLongHorizonSnapshot(long step, string reason)
    {
        RuntimeFlags.ApplyLongHorizonSuiteEffective();
        RuntimeFlags.ApplyOuterLoopEffective();
        if (!RuntimeFlags.EnableLongHorizonSuite && reason != "session_end") return;
        try
        {
            var peProxies = new List<double>();
            // outer_compare ログは別経路。ここでは in-memory PE 代理を1点だけ
            if (PeLoop.Enqueued > 0)
            {
                var prefs = PeLoop.PreferredCounts();
                long tot = prefs.Values.Sum();
                double und = tot > 0 ? (double)prefs.GetValueOrDefault("undecided") / tot : 0.5;
                peProxies.Add(Math.Clamp(und, 0.05, 1.0));
            }
            var input = new GameKouryakuKun.Core.LongHorizon.LongHorizonInput
            {
                PeStatsSummary = PeLoop.SummaryLine(),
                PeEnqueued = PeLoop.Enqueued,
                PePending = PeLoop.PendingCount,
                IntrinsicStatsSummary = IntrinsicStats.SummaryLine(),
                IntrinsicMixSteps = IntrinsicStats.MixSteps,
                IntrinsicInterrupts = IntrinsicStats.InterruptsApplied,
                IntrinsicBlockedBudget = IntrinsicStats.BlockedBudget,
                IntrinsicAmbiguityFell = IntrinsicStats.AmbiguityFellAfter,
                RegistrySummary = _modelRegistry?.StatsSummary(),
                ChampionId = _modelRegistry?.ChampionId(),
                RegistryRollbackAvailable = _modelRegistry?.RollbackAvailable() ?? false,
                SoftAdopts = StructuredSoftAdopts,
                HumanSwitches = Human?.Switches ?? 0,
                OuterLoopEnabled = RuntimeFlags.EnableOuterLoop,
                OuterPromoteEnabled = RuntimeFlags.OuterLoopPromoteEnabled,
                SupervisorRecoveries = Supervisor.Events.Count,
                LiveRoiDeferHints = 0,
                RecentPeProxies = peProxies.Count >= 1 ? peProxies : null,
                SaveApiAbsent = true,
            };
            var rep = GameKouryakuKun.Core.LongHorizon.LongHorizonHealth.Build(input);
            int warn = rep.Checks.Count(c => c.Level == GameKouryakuKun.Core.LongHorizon.LhLevel.Warn);
            _env.Log.Write("long_horizon_snapshot",
                ("step", step), ("reason", reason), ("summary", rep.SummaryLine),
                ("warn_count", warn), ("suite", RuntimeFlags.EnableLongHorizonSuite),
                ("eval", GameKouryakuKun.Core.LongHorizon.EvalSuiteHooks.DescribeAll()),
                ("checks", rep.Checks.Select(c => new Dictionary<string, object?>
                {
                    ["name"] = c.Name, ["level"] = c.Level.ToString(), ["detail"] = c.Detail,
                }).Take(24).ToList()));
            if (RuntimeFlags.EnableLongHorizonSuite)
                _env.Log.Write("long_horizon_suite", ("step", step), ("summary", rep.SummaryLine),
                    ("hooks", GameKouryakuKun.Core.LongHorizon.EvalSuiteHooks.All.Select(e => e.Id).ToList()));
        }
        catch (Exception e)
        {
            _env.Log.Write("long_horizon_failed", ("step", step), ("reason", reason), ("error", e.Message));
        }
    }

    /// <summary>S7: Outer Loop — 同一経験スライスの MetricSnapshot + 任意 promote/weight apply。ライブ入力増なし。</summary>
    private void TryRunOuterLoopShell(long step)
    {
        RuntimeFlags.ApplyOuterLoopEffective();
        if (!RuntimeFlags.EnableOuterLoop) return;
        // 根拠: 同一セッション内の再評価間隔（入力は増やさない・評価のみ）
        if (step > 0 && _lastOuterLoopStep >= 0 && step - _lastOuterLoopStep < 50) return;
        try
        {
            _modelRegistry ??= ModelRegistryStore.Open(_env.ProfileDataDir, AppEnv.Version);
            _lastOuterLoopStep = step;
            _env.Log.Write("outer_registry", ("step", step), ("summary", _modelRegistry.StatsSummary()),
                ("rollback", _modelRegistry.RollbackAvailable()), ("champion", _modelRegistry.ChampionId()),
                ("weights", PortfolioWeights.SummaryLine()));

            var champId = _modelRegistry.ChampionId();
            if (champId == null) return;
            var champ = _modelRegistry.GetBundle(champId);
            if (champ == null) return;

            var candWeights = BuildCandidateWeightsFromStats(IntrinsicStats);
            var cand = new GameKouryakuKun.Core.Outer.ModelBundleSpec
            {
                Id = $"candidate/intrinsic-weights@{step}",
                Kind = GameKouryakuKun.Core.Outer.ModelBundleKinds.Weights,
                ParentId = champId,
                Hypothesis = "reweight portfolio from intrinsic contribution + PE tags (same experience slice)",
                Origin = "intrinsic_stats",
                Parameters = candWeights,
                Note = "S7 experience Candidate; applied only on dual-flag promote",
            };
            _modelRegistry.AddCandidate(cand);

            var champW = champ.Parameters != null && champ.Kind == GameKouryakuKun.Core.Outer.ModelBundleKinds.Weights
                ? GameKouryakuKun.Core.Intrinsic.IntrinsicPortfolioWeights.FromParameters(champ.Parameters)
                : PortfolioWeights.Current;
            var (cm, dm, sliceNote) = GameKouryakuKun.Core.Outer.OuterMetricSnapshotBuilder.PairOnSameSlice(
                IntrinsicStats, PeLoop, LatestCausalComparison, champW, candWeights);
            // bootstrap 初回は previous が無くてもポインタ記録で rollback 可能になるため評価を許可
            bool rollbackPathOk = _modelRegistry.RollbackAvailable() || champId.StartsWith("champion/bootstrap", StringComparison.Ordinal);
            var cmp = GameKouryakuKun.Core.Outer.OuterLoopEvaluator.Compare(champ, cand, cm, dm, rollbackAvailable: rollbackPathOk);
            _modelRegistry.RecordComparison(cmp);
            _env.Log.Write("outer_compare", ("step", step), ("summary", cmp.SummaryLine()),
                ("winner", cmp.Winner), ("can_promote", cmp.CanPromote), ("status", cmp.Status),
                ("method", cm.Method), ("slice", sliceNote),
                ("reasons", cmp.Reasons.ToList()), ("provenance", cmp.ProvenanceJson),
                ("champ_pe", cm.PeProxy), ("cand_pe", dm.PeProxy));

            if (cmp.CanPromote && RuntimeFlags.OuterLoopPromoteEnabled)
                TryPromoteOuterCandidate(cand, step);
        }
        catch (Exception e)
        {
            _env.Log.Write("outer_loop_failed", ("step", step), ("error", e.Message));
        }
    }

    /// <summary>レジストリ昇格と重み適用を対で行う。失敗時は重みを戻す。</summary>
    private void TryPromoteOuterCandidate(GameKouryakuKun.Core.Outer.ModelBundleSpec cand, long step)
    {
        if (_modelRegistry == null) return;
        var applied = PortfolioWeights.ApplyFromBundle(cand);
        if (!applied.Ok)
        {
            _env.Log.Write("outer_promote", ("step", step), ("ok", false), ("detail", applied.Detail), ("candidate", cand.Id));
            return;
        }
        var (ok, detail) = _modelRegistry.TryPromote(cand.Id, promoteFlagEnabled: true, reason: "flagged_promote_after_compare");
        if (!ok)
        {
            PortfolioWeights.Rollback();
            _env.Log.Write("outer_promote", ("step", step), ("ok", false), ("detail", detail), ("candidate", cand.Id),
                ("weights_rolled", true));
            return;
        }
        _env.Log.Write("outer_promote", ("step", step), ("ok", true), ("detail", detail), ("candidate", cand.Id),
            ("weights", PortfolioWeights.SummaryLine()));
    }

    /// <summary>レジストリ + 重みを対で rollback（明示呼び出し・悪化時用）。</summary>
    public bool TryRollbackOuter(string? reason = null)
    {
        RuntimeFlags.ApplyOuterLoopEffective();
        if (_modelRegistry == null) return false;
        var (ok, detail) = _modelRegistry.TryRollback(reason);
        if (!ok)
        {
            _env.Log.Write("outer_rollback", ("ok", false), ("detail", detail));
            return false;
        }
        var w = PortfolioWeights.Rollback();
        _env.Log.Write("outer_rollback", ("ok", true), ("detail", detail), ("weights", w.Detail),
            ("summary", _modelRegistry.StatsSummary()));
        return true;
    }

    private static System.Collections.Generic.Dictionary<string, double> BuildCandidateWeightsFromStats(
        GameKouryakuKun.Core.Intrinsic.IntrinsicContributionStats stats)
    {
        // 寄与票がある成分を厚く、無ければ info_gain_pe 寄り（S5 材料・自動昇格ではない）。
        var counts = stats.ComponentChosenCounts();
        long total = counts.Values.Sum();
        var w = new System.Collections.Generic.Dictionary<string, double>();
        if (total > 0)
        {
            foreach (var id in GameKouryakuKun.Core.Intrinsic.IntrinsicComponents.All)
            {
                double share = (double)counts.GetValueOrDefault(id) / total;
                w[id] = 0.12 + 0.4 * share; // 根拠: 床 + 寄与比例（正規化は FromParameters）
            }
        }
        else
        {
            w["info_gain_pe"] = 0.36;
            w["controllability"] = 0.16;
            w["reach_unknown"] = 0.16;
            w["reversibility_options"] = 0.16;
            w["skill_wm_long_term"] = 0.16;
        }
        return w;
    }

    private void FlushOuterRegistry(string reason)
    {
        RuntimeFlags.ApplyOuterLoopEffective();
        if (!RuntimeFlags.EnableOuterLoop || _modelRegistry == null) return;
        try
        {
            _env.Log.Write("outer_registry", ("reason", reason), ("summary", _modelRegistry.StatsSummary()),
                ("rollback", _modelRegistry.RollbackAvailable()), ("champion", _modelRegistry.ChampionId()),
                ("weights", PortfolioWeights.SummaryLine()));
        }
        catch (Exception e)
        {
            _env.Log.Write("outer_registry_flush_failed", ("reason", reason), ("error", e.Message));
        }
    }

    private void TryAttachImportance(GameKouryakuKun.Core.Observation.StructuredObservation o, long step)
    {
        RuntimeFlags.ApplyImportanceEffective();
        if (!RuntimeFlags.AttachImportance) { LatestImportance = null; return; }
        try
        {
            IReadOnlyList<string> tags = LatestCausalComparison?.PeRoutingTags ?? Array.Empty<string>();
            if (tags.Count == 0 && PeLoop.PendingCount > 0)
                tags = PeLoop.Snapshot(50).Select(c => c.Tag).Distinct().ToList();
            bool trackUnstable = o.EntityLayer?.Candidates.Any(c => c.IdentityPosterior is <= 0 || (!c.Continuous && !c.New)) == true;
            var input = new GameKouryakuKun.Core.Importance.ImportanceEstimateInput(
                Step: step,
                PeTags: tags,
                Causal: LatestCausalComparison,
                ShortHorizon: LatestShortHorizon,
                TrackUnstableHint: trackUnstable,
                ResidualCellCount: o.EntityLayer?.ResidualCells?.Count);
            var report = GameKouryakuKun.Core.Importance.FeatureImportanceEstimator.Estimate(input, PeLoop);
            LatestImportance = report;
            foreach (var f in report.Features.Where(x => x.Status == "scored").Take(8))
                _env.Log.Write("feature_importance",
                    ("step", step), ("feature_id", f.FeatureId), ("score", f.Score), ("method", f.Method), ("status", f.Status),
                    ("evidence", f.EvidenceSummary));
            bool liveOn = GameKouryakuKun.Core.Importance.LiveRoiPolicy.IsLiveEnabled(true);
            _env.Log.Write("attention_weights",
                ("step", step),
                ("summary", report.SummaryLine()),
                ("regions", report.Attention.Regions),
                ("tracks", report.Attention.Tracks),
                ("ocr", report.Attention.Ocr),
                ("residual", report.Attention.Residual),
                ("policy", liveOn ? "live_ocr_defer" : report.Attention.Policy),
                ("live", liveOn));
            if (report.RecommendationsWritten > 0)
                _env.Log.Write("pe_importance_recommend",
                    ("step", step), ("count", report.RecommendationsWritten),
                    ("pending", PeLoop.PendingCount));
            if (LatestObservationBundle != null && RuntimeFlags.AttachObservationBundle)
            {
                RuntimeFlags.ApplyBundleEffective();
                lock (EntityExp)
                    LatestObservationBundle = GameKouryakuKun.Core.Observation.SensorStackAssembler.FromStructured(
                        o, entityExperience: EntityExp, causalComparison: LatestCausalComparison, attentionWeights: report.Attention);
            }
        }
        catch (Exception e)
        {
            LatestImportance = null;
            _env.Log.Write("importance_failed", ("step", step), ("error", e.Message));
        }
    }

    public SelfGoals? Goals { get; private set; }
    private int _goalEventsLogged;
    /// <summary>目的の欄の表示（探索のスレッドで作った文字列だけを渡す）</summary>
    public string? GoalText { get; private set; }
    public int AbortedTurns { get; private set; }
    public int SkillCandidates { get; private set; }
    public int GoalSkillUses { get; private set; }
    public int GoalSegmentSupport { get; private set; }
    private int _horizonLogged;

    /// <summary>目的の出来事のあと: Horizon の伸び縮みを記録し、目的の状態を保存する（v0.20.0）</summary>
    private void AfterGoalEvents(SelfGoals goals, long step)
    {
        bool changed = goals.Events.Count != _goalEventsLogged;
        _goalEventsLogged = goals.Events.Count;
        foreach (var h in goals.HorizonEvents.Skip(_horizonLogged))
        {
            _env.Log.Write("planning_horizon", ("step", h.Step), ("from", h.From), ("to", h.To), ("reason", h.Reason));
            Emit($"[計画] 届く手数（Horizon）を {h.From} → {h.To} 手にしました（{h.Reason}）", LogLevel.Info);
        }
        _horizonLogged = goals.HorizonEvents.Count;
        if (changed) try { _env.Memory.SaveGoalModel("self_goals", goals.ExportState()); } catch (Exception e) { Emit("目的の状態の保存に失敗: " + e.Message, LogLevel.Warn); }
    }
    public int BackgroundLearned { get; private set; }

    private static string GoalSummary(SelfGoals g)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(g.Active is { } a ? $"今の目的 #{a.Id}: {a.Text}（{a.Steps}/{a.Budget} 手）\n  根拠: {a.Origin}" : "今の目的: なし（手探り探索）");
        sb.AppendLine($"作成 {g.Created}・◎達成 {g.Achieved}・失敗 {g.Failed}・放棄 {g.Abandoned}・直近の手に占める割合 {g.Share:0.00}（上限 {g.MaxGoalShare:0.0}）");
        sb.AppendLine($"種類ごとの価値（学習の進みの移動平均）: 到達 {g.Value(GoalKind.Reach):0.00}・確かめ直し {g.Value(GoalKind.Retest):0.00}");
        sb.AppendLine($"計画の届く手数（Horizon）{g.Horizon} 手・達成した最長 {g.MaxAchievedLength} 手・引き直し {g.ReplanCount} 回（引き直して達成 {g.ReplanAchieved}/{g.ReplanGoals}）・小目的 {g.SubgoalsReachedTotal}/{g.SubgoalsTotal}");
        foreach (var e in g.Events.Where(x => x.Status == "reviewed").TakeLast(5).Reverse())
            sb.AppendLine($"  #{e.GoalId} {(e.Kind == "reach" ? "到達" : "確かめ直し")} {e.Target}{(e.Action != null ? " 「" + e.Action + "」" : "")}: 学習の進み {e.Progress:0.00}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>目的があればその次の手。なければ候補を作って選ぶ（選ばないこともある）。目的の手は Rule = "goal"</summary>
    private ExploreChoice? GoalStepOrNull(SelfGoals goals, Explorer explorer, TransitionModel model, string cur, long step, Random rng)
    {
        if (!_o.SelfGoals) return null;
        // Human Control 中は目的 soft adopt 経路に入らない（呼び出し側でも除外）
        if (Human?.Enabled == true) return null;
        var g = goals.Active ?? goals.Propose(explorer, model, cur, step, rng);
        if (g == null) return null;
        if (g.Index == 0 && g.Steps == 0 && g.Replans == 0)
            _env.Log.Write("goal_created", ("step", step), ("goal", g.Id), ("kind", g.Kind.ToString().ToLowerInvariant()), ("target", g.Target), ("action", g.TargetAction),
                ("origin", g.Origin), ("plan", g.Plan.Select(p => $"{p.Node}:{p.Action}").ToList()), ("budget", g.Budget), ("unc_before", Math.Round(g.UncBefore, 4)));
        var name = goals.NextAction(explorer, model, cur, step);
        if (name == null)
        {
            RuntimeFlags.ApplyStructuredGoalsEffective();
            if (RuntimeFlags.UseStructuredGoals && LatestObservationGoal != null)
            {
                var hint = GameKouryakuKun.Core.Goals.StructuredPlanner.FailureHint("abandon", g.EndReason ?? "next_null");
                LatestPlanProposal = hint;
                _env.Log.Write("plan_propose", ("step", step), ("summary", hint.SummaryLine()), ("status", hint.Status), ("note", hint.Note), ("soft_adopt", false));
            }
            return null;
        }
        var a = explorer.Actions.FirstOrDefault(x => x.Name == name);
        if (a == null) { goals.Abandon("操作が見つからない", step); return null; }

        RuntimeFlags.ApplyStructuredGoalsEffective();
        if (RuntimeFlags.UseStructuredGoals)
        {
            // Skill sync は SkillCandidates flush / periodic SyncSkills で行う
            var obs = GameKouryakuKun.Core.Goals.ObservationGoalBridge.FromSelfGoal(g);
            LatestObservationGoal = obs;
            _env.Log.Write("structured_goal", ("step", step), ("summary", obs.SummaryLine()),
                ("pred", obs.Predicate.Id), ("pred_status", obs.Predicate.Status),
                ("value", obs.ExpectedValue.Combined), ("legacy", obs.LegacyKind), ("status", obs.Status));

            // Bundle/Entity 形式述語（SelfGoal と並存・達成数は報酬にしない）
            foreach (var eg in GameKouryakuKun.Core.Goals.ObservationGoalBridge.ProposeFromEvidence(
                LatestObservationBundle, LatestObservation, cur, name))
            {
                _env.Log.Write("structured_goal", ("step", step), ("summary", eg.SummaryLine()),
                    ("pred", eg.Predicate.Id), ("pred_status", eg.Predicate.Status),
                    ("value", eg.ExpectedValue.Combined), ("legacy", eg.LegacyKind), ("status", eg.Status),
                    ("origin", eg.Origin));
            }

            string? softBlock = GameKouryakuKun.Core.Goals.SoftAdoptBudget.BlockReason(
                StructuredSoftAdopts, GameKouryakuKun.Core.Goals.SoftAdoptBudget.DefaultMaxSoftAdopts,
                step, _lastSoftAdoptStep, _softAdoptSteps, lastRecoveryStep: _lastSupervisorRecoveryStep, humanControl: false);

            string legacyNext = name;
            var plan = GameKouryakuKun.Core.Goals.StructuredPlanner.Propose(
                obs, SkillLibrary.Skills, cur, legacyNext, LatestShortHorizon, softBlock);
            LatestPlanProposal = plan;
            bool soft = false;
            if (plan.Status == "soft_adopt_available" && plan.ExpandedSkillId is int adoptId
                && SkillLibrary.Skills.FirstOrDefault(s => s.Id == adoptId) is { } skill
                && !string.Equals(skill.Body.Count > 0 ? skill.Body[0] : null, name, StringComparison.Ordinal))
            {
                // Soft adopt: 既存の Active ActionSequence マクロのみ（primitive 差し替えはしない）
                string? seqName = skill.SequenceId is int q ? $"ActionSequence #{q}" : null;
                var seqAct = explorer.Actions.FirstOrDefault(x =>
                    x.Group == ActionGroup.Sequence && explorer.IsUserAllowed(x.Name)
                    && ((seqName != null && x.Name == seqName)
                        || (x.Steps != null && x.Steps.Select(s => s.Name).SequenceEqual(skill.Body))));
                if (seqAct != null)
                {
                    a = seqAct;
                    soft = true;
                    StructuredSoftAdopts++;
                    _lastSoftAdoptStep = step;
                    _softAdoptSteps.Add(step);
                }
            }
            _env.Log.Write("plan_propose", ("step", step), ("summary", plan.SummaryLine()), ("status", plan.Status),
                ("first", plan.FirstAction), ("skill", plan.ExpandedSkillId), ("note", plan.Note),
                ("soft_adopt", soft), ("legacy_next", legacyNext), ("soft_block", softBlock),
                ("wm", plan.WmLookaheadStatus), ("recovery_step", _lastSupervisorRecoveryStep));
            string reason = soft
                ? $"目的 #{g.Id}: {g.Text}（{g.Steps + 1}/{g.Budget} 手目・soft_adopt=skill_macro）"
                : $"目的 #{g.Id}: {g.Text}（{g.Steps + 1}/{g.Budget} 手目）";
            return new ExploreChoice(a, reason, 0) { Rule = "goal" };
        }

        return new ExploreChoice(a, $"目的 #{g.Id}: {g.Text}（{g.Steps + 1}/{g.Budget} 手目）", 0) { Rule = "goal" };
    }

    private void LogGoal(GoalEvent e, long step)
    {
        _env.Log.Write("goal_event", ("step", step), ("goal", e.GoalId), ("kind", e.Kind), ("target", e.Target), ("action", e.Action), ("status", e.Status), ("reason", e.Reason),
            ("steps", e.Steps), ("progress", e.Progress is double pg ? Math.Round(pg, 4) : null), ("value_after", double.IsNaN(e.ValueAfter) ? null : Math.Round(e.ValueAfter, 4)),
            ("horizon", e.Horizon), ("planned_length", e.PlannedLength), ("replans", e.Replans), ("subgoals", e.Subgoals), ("subgoals_reached", e.SubgoalsReached), ("outcome", e.Outcome));
        try { if (RunId != null) _env.Memory.AddGoalEvent(RunId.Value, _lastSeq, e); } catch (Exception ex) { Emit("目的の記録に失敗: " + ex.Message, LogLevel.Warn); }
        if (e.Status != "reviewed") Emit($"[目的 #{e.GoalId}] {(e.Status == "achieved" ? "◎達成" : e.Status == "failed" ? "失敗" : "放棄")}（{e.Reason}）{e.Target}{(e.Action != null ? " 「" + e.Action + "」" : "")}", LogLevel.Info);
    }

    private void RefreshTemporal(TemporalBook temporal, MotionMaskBook motion)
    {
        try
        {
            var ms = motion.Summaries().ToDictionary(m => m.Screen, StringComparer.Ordinal);
            Volatile.Write(ref _temporal, temporal.Nodes.OrderBy(n => n, StringComparer.Ordinal).Select(n =>
            {
                string mk = n.Split('/')[0].Split('.')[0];
                var m = ms.GetValueOrDefault(mk);
                return temporal.Profile(n, m?.Learned == true ? m.Coverage : 0, m?.MeanActivity ?? 0);
            }).ToList());
        }
        catch (Exception e) { _env.Log.Write("temporal_error", ("detail", e.Message)); }
    }

    /// <summary>成長の計器を作り直す（読むだけ。表示用。報酬にはしない）</summary>
    private void RefreshMeters(Explorer explorer, TransitionModel model, ActionSequenceMiner miner, long seq)
    {
        try
        {
            var acc = Growth.Series(GrowthMetric.PredictionAccuracy); var cal = Growth.Series(GrowthMetric.CalibrationGap); var unk = Growth.Series(GrowthMetric.UnknownRate);
            var hs = Hypotheses;
            Volatile.Write(ref _meters, new GrowthMeters(seq, explorer.Nodes.Count, hs.Count(h => h.Kind == HypothesisKind.Relation && h.Status == HypothesisStatus.Settled),
                hs.Count, miner.Candidates.Count(c => c.Status == SequenceStatus.Active),
                acc.Count > 0 ? acc[^1].Value : double.NaN, cal.Count > 0 ? cal[^1].Value : double.NaN, unk.Count > 0 ? unk[^1].Value : double.NaN,
                HumanSteps, 0, 0, 0));
        }
        catch (Exception e) { _env.Log.Write("meters_error", ("detail", e.Message)); }
    }

    /// <summary>仮説の板を作り直す（読むだけ。表示用）</summary>
    private void RefreshHypotheses(TransitionModel model, VisualStateBook vbook, ActionSequenceMiner miner, long now, MotionMaskBook? motion = null)
    {
        try
        {
            var splits = vbook.Splits.Select(kv => (kv.Key, kv.Value.Coarse, kv.Value.Node, true))
                .Concat(vbook.Reverted.Select(kv => (kv.Key, kv.Value.Coarse, kv.Value.Node, false)));
            Volatile.Write(ref _hypotheses, HypothesisBoard.Build(model.Summaries(), splits, miner.Candidates.ToList(), now, miner.DormantAfterSteps, motion?.Summaries().ToList()));
        }
        catch (Exception e) { _env.Log.Write("hypotheses_error", ("detail", e.Message)); }
    }

    /// <summary>予測の外れによる修正を記録する（DB の model_revisions・ログ・画面）。直接の記録は消さない</summary>
    private long? RecordRevision(ModelRevision r, int step)
    {
        ModelRevisions++;
        Discoveries.Add(new Discovery(r.Seq, DiscoveryKind.Revision, $"{r.Node}「{r.Action}」", $"証拠の重み {r.WeightBefore:0.#} → {r.WeightAfter:0.#}"));
        lock (_recentRevisions) { _recentRevisions.AddFirst(r); while (_recentRevisions.Count > 10) _recentRevisions.RemoveLast(); }
        long? id = null;
        try { id = _env.Memory.AddModelRevision(r); } catch (Exception e) { Emit("内部モデルの修正の記録に失敗: " + e.Message, LogLevel.Warn); }
        Emit($"[修正] 画面 {r.Node} の「{r.Action}」の予測を作り直しました（{r.Reason}）", LogLevel.Info);
        _env.Log.Write("model_revision", ("step", step), ("seq", r.Seq), ("node", r.Node), ("action", r.Action), ("weight_before", Math.Round(r.WeightBefore, 3)), ("weight_after", Math.Round(r.WeightAfter, 3)), ("revision_id", id));
        return id;
    }

    /// <summary>見た目の状態の分割を見直す間隔（手）</summary>
    private const int SplitRefreshSteps = 100;   // 根拠: 汎用（証拠がたまるのを待つ間隔。ノード名を頻繁に変えない）

    private void SaveExploration(ScreenModeBook book, Explorer explorer, SequenceLearner seq, Dictionary<(string, string, string), int> delta,
        VisualStateBook vbook, ReactionMap reactions, ActionSequenceMiner seqMiner)
    {
        try
        {
            _env.Memory.SaveVisualStates(vbook);
            _env.Memory.SaveReactionMaps(reactions.Export());
            _env.Memory.SaveActionSequences(seqMiner.Candidates);
            var (rp, rc) = reactions.Summary();
            var groups = vbook.TransitionGroups();
            Emit($"[探索] 見た目の状態 {vbook.States.Count} 個（遷移の群 {groups.Values.Distinct().Count()} 個・分割 {vbook.Splits.Count} 件）・反応の対応表 {reactions.Tables.Count} 組（反応する区画のある組 {rp}・区画 {rc}）・操作の並び {seqMiner.Candidates.Count} 個を保存しました。", LogLevel.Ok);
            _env.Log.Write("exploration_v6", ("run_id", RunId), ("visual_states", vbook.States.Count), ("transition_groups", groups.Values.Distinct().Count()), ("splits", vbook.Splits.Count),
                ("reaction_pairs", reactions.Tables.Count), ("reactive_pairs", rp), ("reactive_cells", rc), ("action_sequences", seqMiner.Candidates.Count),
                ("action_sequences_active", seqMiner.Active.Count()), ("priors", explorer.Priors().Count()),
                ("prediction_checks", PredictionChecks), ("prediction_hits", PredictionHits), ("model_revisions", ModelRevisions),
                ("sequence_tries", explorer.Nodes.Values.Sum(n => n.Actions.Where(a => a.Key.StartsWith("ActionSequence #", StringComparison.Ordinal)).Sum(a => a.Value.Tries))));
        }
        catch (Exception e) { Emit("v6 の記録の保存に失敗: " + e.Message, LogLevel.Warn); }
        try
        {
            _env.Memory.SaveScreenModes(book.Modes.Select(m => (m.Id, m.Prototype, m.Seen)));
            _env.Memory.AddTransitions(delta.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value)));
            _env.Memory.SaveReversibility(explorer.ReversibilityByAction.Select(kv => (kv.Key, kv.Value.Returned, kv.Value.NotReturned, kv.Value.UndoHits)));
            _env.Memory.SaveSequencePatterns(seq.Patterns);
            Emit($"[探索] 画面の種類 {book.Modes.Count} 個・画面ノード {explorer.Nodes.Count} 個・この回の遷移 {delta.Values.Sum()} 件・連続履歴 {seq.Steps} 手（境界の候補 {seq.Boundaries} 件）・系列のパターン {seq.Patterns.Count} 個を保存しました。", LogLevel.Ok);
            _env.Log.Write("exploration", ("run_id", RunId), ("modes", book.Modes.Count), ("nodes", explorer.Nodes.Count), ("transitions", delta.Values.Sum()),
                ("stream_steps", seq.Steps), ("boundaries", seq.Boundaries), ("patterns", seq.Patterns.Count), ("discoveries", explorer.Discoveries), ("late_changes", LateChanges));
        }
        catch (Exception e) { Emit("探索の記録の保存に失敗: " + e.Message, LogLevel.Warn); }
    }

    private static string KindText(SequencePatternKind k) => k switch { SequencePatternKind.Motif => "反復系列", SequencePatternKind.Cycle => "循環", _ => "区間" };
    private static string Shorten(string s) => s.Length <= 120 ? s : s[..117] + "…";

    private bool WaitRunnable()
    {
        if (!_pause.IsPaused(MonoClock.Now)) return !_quit;
        var prevPhase = Phase; SetState(SessionState.Paused, "一時停止中: " + string.Join("・", _pause.ActiveReasons(MonoClock.Now).Select(ReasonText)));
        double t0 = MonoClock.Now;
        while (!_quit)
        {
            var rs = _pause.ActiveReasons(MonoClock.Now);
            if (rs.Count == 0) break;
            if (_pause.IsEmergencyStopped) { _quit = true; break; }
            // v0.21.0: ウィンドウの消失・プロセスの終了・取得の停止は Supervisor に渡す（一時停止のまま待ち続けない）
            if (SystemBreakageVisible()) { SetState(SessionState.Running, "Supervisor が確認中"); return !_quit; }
            string ph = "一時停止中: " + string.Join("・", rs.Select(ReasonText));
            if (ph != Phase) SetState(SessionState.Paused, ph);
            Thread.Sleep(100);
        }
        if (!_quit) { Emit($"再開（{(MonoClock.Now - t0) / 1000:0.0}秒停止）", LogLevel.Ok); SetState(SessionState.Running, prevPhase.StartsWith("一時停止") ? "実行中" : prevPhase); Thread.Sleep(500); }
        return !_quit;
    }

    private bool SystemBreakageVisible()
    {
        if (!WindowLocator.IsAlive(_con.Hwnd)) return true;
        if (_proc.Check(out _) == ProcessCheck.Exited) return true;
        return _con.Capture.Status.Health is CaptureHealth.Error or CaptureHealth.Stopped;
    }

    // ======================== v0.21.0 Game Supervisor（攻略 AI とは別の権限。ゲームの中身は見ない） ========================
    private void RaiseSystem(SystemEventKind k, string detail)
    {
        var e = new SystemEvent(k, MonoClock.Now, null, detail, new Dictionary<string, object?>());
        OnSystemEvent(e);
    }
    private void RaiseConfigProblem(string detail) => RaiseSystem(SystemEventKind.ConfigProblem, detail);

    private readonly List<SystemEvent> _sysLog = new();
    /// <summary>システムの出来事の一覧（UI の展開できる記録。新しいものが後）</summary>
    public IReadOnlyList<SystemEvent> SystemEvents { get { lock (_sysLog) return _sysLog.ToList(); } }

    /// <summary>システムの出来事を、ゲームの経験とは別の表（system_events）とログに残す。報酬・価値・予測・並びには渡さない</summary>
    private void OnSystemEvent(SystemEvent e)
    {
        if (e.Kind is SystemEventKind.ObservationLost or SystemEventKind.ProcessExited or SystemEventKind.WindowLost) NoteLoss(MonoClock.Now);
        lock (_sysLog) { _sysLog.Add(e); if (_sysLog.Count > 500) _sysLog.RemoveAt(0); }
        string data = Json.Obj(e.Data.Select(kv => (kv.Key, kv.Value)).ToArray());
        try { _env.Memory.AppendSystemEvent(RunId, _lastSeq == 0 ? null : _lastSeq, e.Name, e.RecoveryId, e.Detail, data); } catch (Exception ex) { try { _env.Log.Write("system_event_store_failed", ("error", ex.Message)); } catch { } }
        try { _env.Log.Write("system_event", ("kind", e.Name), ("recovery_id", e.RecoveryId), ("detail", e.Detail), ("seq_before", _lastSeq), ("step", Step), ("data", data)); } catch { }
        string? sep = e.Kind switch
        {
            SystemEventKind.ProcessExited => "PROCESS EXIT", SystemEventKind.ObservationLost => "OBSERVATION LOST", SystemEventKind.SupervisorRestartBegin => "SUPERVISOR RESTART",
            SystemEventKind.WindowAcquired => "WINDOW ACQUIRED", SystemEventKind.ObservationRestored => "OBSERVATION RESTORED", SystemEventKind.ExplorationResumed => "EXPLORATION RESUMED",
            SystemEventKind.RecoveryAborted => "RECOVERY ABORTED", SystemEventKind.SuspectedHang => "SUSPECTED HANG", SystemEventKind.WindowResized => "WINDOW RESIZED", _ => null,
        };
        if (sep != null) StepHistory.Add(StepSummary.Separator(_lastSeq, sep));
        if (e.Kind == SystemEventKind.WindowResized) _reobserveNeeded = true;
        var lv = e.Kind is SystemEventKind.RecoveryAborted or SystemEventKind.RecoveryFailed ? LogLevel.Error
               : e.Kind is SystemEventKind.ProcessExited or SystemEventKind.ObservationLost or SystemEventKind.SuspectedHang or SystemEventKind.FileChanged or SystemEventKind.ConfigProblem or SystemEventKind.AiException ? LogLevel.Warn
               : e.Kind is SystemEventKind.ExplorationResumed ? LogLevel.Ok : LogLevel.Info;
        Emit($"[Supervisor] {e.Name}: {e.Detail}", lv);
        Changed?.Invoke();
    }

    private void CheckWatchedFiles()
    {
        foreach (var (path, before, after) in _watched.NewChanges())
        {
            FileChanges++;
            var e = new SystemEvent(SystemEventKind.FileChanged, MonoClock.Now, null,
                $"{Path.GetFileName(path)} が基準から変わりました（大きさ {before.Size} → {after.Size}・{(after.Exists ? "あり" : "なし")}）。戻さずに記録と警告だけ",
                new Dictionary<string, object?> { ["path"] = path, ["size_before"] = before.Size, ["size_after"] = after.Size, ["hash_before"] = before.Hash, ["hash_after"] = after.Hash, ["exists_after"] = after.Exists });
            OnSystemEvent(e);
        }
    }

    private WindowMatch SupervisorWindowMatch()
    {
        var p = _env.Profile.Window; var c = SupervisorConfig;
        return new WindowMatch
        {
            ProcessName = c.ProcessName ?? p.ProcessName, ClassName = c.WindowClass ?? p.ClassName,
            TitleRegex = c.WindowTitleContains != null ? System.Text.RegularExpressions.Regex.Escape(c.WindowTitleContains) : p.TitleRegex,
        };
    }

    /// <summary>Supervisor が使う OS とアプリの操作（どれもゲームの中身に依らない）。攻略 AI からは呼べない</summary>
    private sealed class Host : ISupervisorHost
    {
        private readonly PlaySession _s;
        private nint _lastHwnd;
        public Host(PlaySession s) { _s = s; }
        public double Now => MonoClock.Now;
        public bool StopRequested => _s._quit;
        public void Sleep(double ms) => Thread.Sleep((int)Math.Max(1, ms));

        public SupervisorSignals Probe()
        {
            var con = _s._con; nint h = con.Hwnd;
            bool valid = WindowLocator.IsAlive(h);
            var pc = _s._proc.Check(out var code);
            bool changed = _lastHwnd != 0 && _lastHwnd != h; _lastHwnd = h;
            var health = con.Capture.Status.Health;
            bool capOk = health is not (CaptureHealth.Error or CaptureHealth.Stopped);
            bool hung = valid && WindowHealth.IsHung(h, 300);
            var f = con.Capture.LatestFrame();
            return new SupervisorSignals(pc != ProcessCheck.Unknown, pc == ProcessCheck.Alive, code, valid, changed, hung, capOk, capOk ? null : con.Capture.Status.Detail, !_s._inputFailed && _s._active != null, f?.Width ?? 0, f?.Height ?? 0);
        }

        public void StopActions() { foreach (var b in _s._inputs) try { b.ReleaseAll(); } catch { } }

        public void FlushExperience()
        {
            try { _s._flushForRecovery?.Invoke(); } catch (Exception e) { _s.Emit("[Supervisor] 経験の保存に失敗: " + e.Message, LogLevel.Warn); }
        }

        public bool CheckpointDb(out string detail)
        {
            try { _s._env.Memory.Checkpoint(); _s.DbCheckpointsOk++; detail = "ok"; return true; }
            catch (Exception e) { _s.DbCheckpointsFailed++; detail = e.Message; return false; }
        }

        public bool FindWindow(out string detail)
        {
            var found = WindowLocator.Find(_s.SupervisorWindowMatch());
            if (found.Count == 0) { detail = "条件に合うウィンドウなし"; return false; }
            detail = found[0].ToString(); return true;
        }

        public bool ReconnectCapture(out string detail)
        {
            var old = _s._con;
            var con = GameConnection.Connect(_s._env, (m, l) => _s.Emit("[Supervisor] " + m, l == LogLevel.Error ? LogLevel.Warn : l), _s.SupervisorWindowMatch());
            if (con == null) { detail = "取得を始められない"; return false; }
            con.Capture.StatusChanged += _s.OnCaptureStatus;
            _s._con = con; _lastHwnd = con.Hwnd;
            if (!ReferenceEquals(old, con)) { try { _s.ConnectionReplaced?.Invoke(con); } catch { } }
            _s._proc.Attach(WindowHealth.ProcessIdOf(con.Hwnd));
            RecordDetectedExecutable(_s._env.DataRoot, WindowHealth.ProcessIdOf(con.Hwnd), m => _s._env.Log.Write("launch_detect", ("msg", m)));
            detail = $"{con.Capture.Current?.Id}・pid {(_s._proc.Pid?.ToString() ?? "不明")}";
            return true;
        }

        public bool ReconnectInput(out string detail)
        {
            var p = _s._env.Profile; nint h = _s._con.Hwnd;
            foreach (var b in _s._inputs) b.Bind(h, p.KeyMap(), p.Guard(), p.Input.DefaultHoldMs);
            if (_s._active == null) { detail = "入力方式なし"; return false; }
            var pr = _s._active.Probe(h);
            detail = $"{_s._active.Id}: {pr.Detail}";
            if (pr.Available) { _s._inputFailed = false; _s.Delivery.ResetStreak(); }
            return pr.Available;
        }

        public ProcessCheck CheckProcess(out int? exitCode) => _s._proc.Check(out exitCode);
        public bool CanLaunch => _s.RefreshLaunchConfig().CanLaunch;
        public bool Launch(out string detail)
        {
            _s._proc.Detach();
            var c = _s.RefreshLaunchConfig();
            bool ok = ProcessControl.Launch(c, out detail, out var pid);
            if (ok && pid is int lp) { _s._proc.Attach(lp); detail += $"・pid {lp} を見張る"; }
            else if (ok) detail += "・ゲームのプロセスはウィンドウが出てからそのプロセスを見張る";
            _s.LaunchAttempts++; if (ok) _s.LaunchSucceeded++;
            _s._env.Log.Write("supervisor_launch", ("method", c.LaunchMethod), ("source", c.LaunchSource), ("summary", c.LaunchSummary), ("ok", ok), ("detail", detail));
            return ok;
        }

        public bool WaitCapture(double timeoutMs, out string detail)
        {
            var cap = _s._con.Capture; double t0 = MonoClock.Now;
            while (MonoClock.Now - t0 < timeoutMs && !_s._quit)
            {
                var f = cap.WaitForNewFrame(cap.Current?.LastFrameId ?? 0, 500);
                if (f != null) { detail = $"フレーム #{f.Id} {f.Width}x{f.Height}"; return true; }
            }
            detail = "フレームが届かない"; return false;
        }

        public bool ReObserve(out string detail)
        {
            var f = _s._con.Capture.LatestFrame();
            if (f == null) { detail = "フレームなし"; return false; }
            _s._reobserveNeeded = true;   // 分類は探索の手順の中で行う（通常の状態の認識と同じ処理）
            detail = $"新しいフレーム #{f.Id} を通常の状態の認識で観測し直す";
            return true;
        }
    }

    private string? PauseReasonNow()
    {
        if (_quit) return "停止要求";
        var rs = _pause.ActiveReasons(MonoClock.Now);
        return rs.Count == 0 ? null : string.Join(",", rs.Select(ReasonText));
    }

    public static string KindId(TurnKind k) => k switch
    {
        TurnKind.Normal => ExperienceStore.BoundaryNormal, TurnKind.NoChange => ExperienceStore.BoundaryNoChange,
        TurnKind.Timeout => ExperienceStore.BoundaryTimeout, _ => ExperienceStore.BoundaryAborted,
    };
    public static string KindText(TurnKind k) => k switch { TurnKind.Normal => "完了", TurnKind.NoChange => "変化なし", TurnKind.Timeout => "時間切れ", _ => "中断" };
    public static string ReasonText(PauseReason r) => r switch
    {
        PauseReason.Minimized => "ゲームの最小化",
        PauseReason.Cloaked => "ゲームが非表示（クローク）",
        PauseReason.WindowLost => "ゲームウィンドウ消失",
        PauseReason.CaptureError => "取得エラー",
        PauseReason.UserActivity => "ユーザー操作",
        PauseReason.EmergencyStop => "緊急停止",
        _ => "手動",
    };
}
