using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Turn;

public enum TurnState { Idle, InputSent, Changing, Settling }

public enum TurnKind
{
    /// <summary>変化→安定まで到達</summary>
    Normal,
    /// <summary>NoChangeTimeoutMs 以内に変化が起きなかった（無効入力の可能性。経験として記録する）</summary>
    NoChange,
    /// <summary>MaxMs 以内に安定しなかった（常時アニメ等）</summary>
    Timeout,
    /// <summary>一時停止（最小化・ユーザー操作・緊急停止）やサイズ変化で中断</summary>
    Aborted,
}

public sealed record TurnDetectorOptions
{
    /// <summary>入力後、この時間内に変化が無ければ NoChange</summary>
    public double NoChangeTimeoutMs { get; init; } = 1200;  // 根拠: 汎用（一般的な応答遅延＝数百ms の数倍）。探索では AdaptiveTurnTimeout が自己調整
    /// <summary>静止がこの時間続いたら Stable（境界）</summary>
    public double StableMs { get; init; } = 250;  // 根拠: 汎用（約15フレーム＝人が「止まった」と感じる長さ）。合成テスト TurnDetectorTests
    /// <summary>入力からこの時間を超えたら Timeout</summary>
    public double MaxMs { get; init; } = 8000;  // 根拠: 汎用（長い画面効果でも待つ上限）。探索では AdaptiveTurnTimeout が自己調整
    /// <summary>フレームがこの時間来なければ「静止」とみなす（WGC は内容が変わらないとフレームを送らないため）</summary>
    public double NoFrameStillMs { get; init; } = 250;  // 根拠: 汎用（取得方式の仕様: 内容が変わらないとフレームが来ない）
    public DiffOptions Diff { get; init; } = DiffOptions.Default;
}

/// <summary>1操作のサイクル境界（設計書 §2.3）。時間はすべて入力（最初のキー押下）からの経過ms。</summary>
public sealed record TurnBoundary(
    int Index, TurnKind Kind, string Action, double InputAtMs,
    double? FirstChangeMs, double? SettledMs, double BoundaryMs,
    bool NetChange, DiffResult? PeakFromBaseline, DiffResult? FinalFromBaseline,
    int FramesSeen, long LastFrameId, string? Note)
{
    /// <summary>
    /// 時間切れ（Timeout）のとき、ターンの終わりの区間（MaxMs の LateFraction 以降）に連続するフレームの間で変わり続けたブロック。
    /// 入力への応答が終わった後も動き続ける領域（常時アニメ）の候補。Timeout 以外は null
    /// </summary>
    public bool[]? LateMotion { get; init; }
    /// <summary>時間切れのとき、終わりの区間の連続するフレームの間の変化の量（変化率・最大連結領域）。ふだんの動きの量の観測（v0.17.0）</summary>
    public IReadOnlyList<(double Ratio, double Component)>? LateStats { get; init; }
    public BlockGrid LateGrid { get; init; }
    public override string ToString() =>
        $"#{Index} {Kind} {Action}: 変化開始={Fmt(FirstChangeMs)} 静止開始={Fmt(SettledMs)} 境界={BoundaryMs:0}ms 正味変化={(NetChange ? "あり" : "なし")} フレーム={FramesSeen}" +
        (Note != null ? $" ({Note})" : "");
    private static string Fmt(double? v) => v is double d ? $"{d:0}ms" : "-";
}

/// <summary>
/// ターン検出の土台となる「入力 → 画面変化 → 安定待ち」の状態機械（純粋ロジック。時刻はフレームの TimestampMs と OnTick の引数だけを使う）。
///
///  Idle --Begin--> InputSent --基準画面から変化--> Changing --連続フレームが静止--> Settling --StableMs継続--> 境界(Normal)
///                   InputSent --NoChangeTimeout--> 境界(NoChange)      Settling --再び動いた--> Changing
///                   どこでも --MaxMs--> 境界(Timeout)                  どこでも --Abort--> 境界(Aborted)
///
/// 判定は ChangeCriteria（変化率＋最大連結領域）で行い、待機アニメのマスク（NoiseCalibrator）を適用する。
/// 静止判定は「静止開始時点のフレーム（アンカー）」との差分で行うので、ゆっくりしたフェードも取りこぼさない。
/// </summary>
public sealed class TurnDetector
{
    /// <summary>NoChange だが局所的な小変化はあった、という注記（画面の一部だけが小さく動いた場合）</summary>
    public const string LocalChangeNote = "局所的な小変化あり";
    public TurnDetectorOptions Options { get; set; }
    public ChangeCriteria Criteria { get; set; }
    public BlockMask? Mask { get; set; }
    /// <summary>基準画面との差分が計算されるたびに呼ばれる（固定UI検出などに使う）</summary>
    public Action<DiffResult>? OnBaselineDiff { get; set; }

    public TurnState State { get; private set; } = TurnState.Idle;
    public int Completed { get; private set; }
    /// <summary>ターンの終わりの区間の始まり（MaxMs に対する割合）。この区間で動き続けた所を常時アニメの候補として返す</summary>
    public const double LateFraction = 2.0 / 3;   // 根拠: 汎用（入力への応答の多くが終わった後の区間＝最後の 1/3）
    private bool[]? _late; private BlockGrid _lateGrid; private List<(double, double)>? _lateStats;

    private Frame? _baseline, _prev, _stillRef;
    private double _inputAt, _lastFrameAt;
    private double? _firstChange, _settleStart;
    private int _frames;
    private string _action = "";
    private DiffResult? _peak;

    public TurnDetector(TurnDetectorOptions? options = null, ChangeCriteria? criteria = null, BlockMask? mask = null)
    {
        Options = options ?? new TurnDetectorOptions();
        Criteria = criteria ?? ChangeCriteria.Default;
        Mask = mask;
    }

    /// <summary>入力を送った直後に呼ぶ。baseline は入力直前のフレーム、inputAtMs は最初のキー押下時刻。</summary>
    public void Begin(Frame baseline, double inputAtMs, string action = "")
    {
        if (State != TurnState.Idle) throw new InvalidOperationException($"前のターンが未完了です（{State}）");
        _baseline = baseline; _prev = baseline; _stillRef = null;
        _inputAt = inputAtMs; _lastFrameAt = inputAtMs;
        _firstChange = null; _settleStart = null; _frames = 0; _action = action; _peak = null; _late = null; _lateStats = null;
        State = TurnState.InputSent;
    }

    private DiffResult Diff(Frame a, Frame b) => FrameDiff.Compare(a, b, Options.Diff, Mask);

    public TurnBoundary? OnFrame(Frame f)
    {
        if (State == TurnState.Idle || _baseline == null) return null;
        if (f.TimestampMs < _inputAt) return null;                 // 入力前に撮られたフレーム
        if (_prev != null && f.Id == _prev.Id && _frames > 0) return OnTick(f.TimestampMs);
        if (!f.SameSize(_baseline)) return Finish(TurnKind.Aborted, f.TimestampMs, f, "ウィンドウサイズが変化");
        _frames++; _lastFrameAt = f.TimestampMs;
        double t = f.TimestampMs - _inputAt;

        if (t >= LateFraction * Options.MaxMs && _prev != null && _prev.SameSize(f))
        {
            var cd = Diff(_prev, f);
            if (cd.Comparable) (_lateStats ??= new()).Add((cd.ChangedRatio, cd.LargestComponentRatio));
            if (cd.ChangedBlocks is { } cb)
            {
                _late ??= new bool[cb.Length]; _lateGrid = cd.Grid;
                if (_late.Length == cb.Length) for (int k = 0; k < cb.Length; k++) _late[k] |= cb[k];
            }
        }
        var fromBase = Diff(_baseline, f);
        OnBaselineDiff?.Invoke(fromBase);
        if (fromBase.Comparable && (_peak == null || fromBase.ChangedRatio > _peak.ChangedRatio)) _peak = fromBase;

        switch (State)
        {
            case TurnState.InputSent:
                if (Criteria.IsChanged(fromBase)) { State = TurnState.Changing; _firstChange = t; }
                else if (t >= Options.NoChangeTimeoutMs) return Finish(TurnKind.NoChange, f.TimestampMs, f, null);
                break;
            case TurnState.Changing:
                if (Criteria.IsStill(Diff(_prev!, f))) { State = TurnState.Settling; _stillRef = _prev; _settleStart = _prev!.TimestampMs - _inputAt; }
                break;
            case TurnState.Settling:
                if (!Criteria.IsStill(Diff(_stillRef!, f))) { State = TurnState.Changing; _settleStart = null; _stillRef = null; }
                break;
        }
        _prev = f;
        if (State == TurnState.Settling && t - _settleStart!.Value >= Options.StableMs) return Finish(TurnKind.Normal, f.TimestampMs, f, null);
        if (t >= Options.MaxMs) return Finish(TurnKind.Timeout, f.TimestampMs, f, null);
        return null;
    }

    /// <summary>フレームが来ない間も定期的に呼ぶ（WGC は静止画面ではフレームを送らない）。</summary>
    public TurnBoundary? OnTick(double nowMs)
    {
        if (State == TurnState.Idle) return null;
        double t = nowMs - _inputAt;
        double idle = nowMs - _lastFrameAt;
        switch (State)
        {
            case TurnState.InputSent:
                if (t >= Options.NoChangeTimeoutMs && idle >= Math.Min(Options.NoFrameStillMs, Options.NoChangeTimeoutMs))
                    return Finish(TurnKind.NoChange, nowMs, _prev, "期限内にフレーム変化なし");
                break;
            case TurnState.Changing:
                if (idle >= Options.NoFrameStillMs) { State = TurnState.Settling; _stillRef = _prev; _settleStart = _lastFrameAt - _inputAt; }
                break;
        }
        if (State == TurnState.Settling && t - _settleStart!.Value >= Options.StableMs && idle >= 0) return Finish(TurnKind.Normal, nowMs, _prev, _frames == 0 ? "フレーム到着なし" : null);
        if (t >= Options.MaxMs) return Finish(TurnKind.Timeout, nowMs, _prev, null);
        return null;
    }

    public TurnBoundary? Abort(double nowMs, string reason) => State == TurnState.Idle ? null : Finish(TurnKind.Aborted, nowMs, _prev, reason);

    private TurnBoundary Finish(TurnKind kind, double nowMs, Frame? last, string? note)
    {
        DiffResult? final = last != null && _baseline != null ? Diff(_baseline, last) : null;
        bool net = final != null && Criteria.IsChanged(final) && final.Comparable;
        if (kind == TurnKind.NoChange && _peak != null && _peak.Comparable && _peak.ChangedRatio > Criteria.ChangeRatio)
            note = (note == null ? "" : note + "／") + LocalChangeNote; // 画素は動いたが、まとまった変化ではない（小さなキャラの移動・待機アニメ等。判別は上位層で）
        var b = new TurnBoundary(++Completed, kind, _action, _inputAt, _firstChange,
            kind == TurnKind.Normal ? _settleStart : null, nowMs - _inputAt, net, _peak, final, _frames, last?.Id ?? -1, note)
            { LateMotion = kind == TurnKind.Timeout ? _late : null, LateGrid = _lateGrid, LateStats = kind == TurnKind.Timeout ? _lateStats : null };
        State = TurnState.Idle; _baseline = _prev = _stillRef = null;
        return b;
    }
}

/// <summary>
/// 「変化なし」が続いたら操作を止めるための見張り（ゲームオーバー画面など、入力が効かない画面での空打ちを防ぐ）。
/// 中断（Aborted）は数えずリセットもしない。Limit ≤ 0 なら無効。
/// </summary>
public sealed class NoChangeRunGuard
{
    public int Limit { get; }
    public int Run { get; private set; }
    public NoChangeRunGuard(int limit) => Limit = limit;
    /// <summary>境界を記録し、上限に達したら true</summary>
    public bool Record(TurnKind kind)
    {
        if (kind == TurnKind.NoChange) Run++;
        else if (kind != TurnKind.Aborted) Run = 0;
        return Limit > 0 && Run >= Limit;
    }
}

/// <summary>
/// TurnDetector を取得バックエンドのフレーム列で駆動する補助（ハーネス・Phase 2 のループ共通）。
/// waitFrame(afterId, timeoutMs) は新しいフレームか null を返す関数（ICaptureBackend.WaitForNewFrame を渡す）。
/// pauseCheck が理由文字列を返したら中断する。
/// </summary>
public static class TurnRunner
{
    public static TurnBoundary Await(TurnDetector det, long lastFrameId, Func<long, int, Frame?> waitFrame,
        Func<double> now, Func<string?>? pauseCheck = null, int pollMs = 20)
    {
        long last = lastFrameId;
        while (true)
        {
            if (pauseCheck?.Invoke() is string why) return det.Abort(now(), why)!;
            var f = waitFrame(last, pollMs);
            TurnBoundary? b;
            if (f != null) { last = f.Id; b = det.OnFrame(f); }
            else b = det.OnTick(now());
            if (b != null) return b;
            if (det.State == TurnState.Idle) throw new InvalidOperationException("TurnDetector が開始されていません");
        }
    }
}
