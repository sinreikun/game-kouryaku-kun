using GameKouryakuKun.Core.Diagnostics;

namespace GameKouryakuKun.Core.Input;

/// <summary>分離等級（設計書 §2.10）。isolated=ゲームにだけ届く / device_global=AI専用仮想デバイス / foreground_shared=前面依存でユーザー操作と競合</summary>
public enum IsolationLevel { Isolated, DeviceGlobal, ForegroundShared }

public sealed record IsolationInfo(IsolationLevel Level, bool RequiresFocus, bool AffectsUserInput, bool RequiresAdminSetup, string? Driver)
{
    public string Badge => Level switch
    {
        IsolationLevel.Isolated => "isolated(緑)",
        IsolationLevel.DeviceGlobal => "device_global(黄)",
        _ => "foreground_shared(赤)",
    };
}

public sealed record InputProbeResult(bool Available, string Detail);

/// <summary>1回の送信の記録。DownAtMs = 最初のキーを押した時刻（MonoClock）。ターン検出の起点になる。</summary>
public sealed record InputReceipt(bool Ok, string BackendId, string Action, double DownAtMs, double UpAtMs, int EventsSent, string Detail)
{
    public static InputReceipt Rejected(string backendId, string action, string why, double now) => new(false, backendId, action, now, now, 0, why);
}

/// <summary>論理キー名 → 物理キー。Profile の input.keys から作る。</summary>
public sealed class LogicalKeyMap
{
    private readonly Dictionary<string, KeySpec> _map;
    public LogicalKeyMap(IDictionary<string, KeySpec> map) => _map = new(map, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, KeySpec> All => _map;
    public bool Contains(string logical) => _map.ContainsKey(logical);
    public KeySpec Resolve(string logical) =>
        _map.TryGetValue(logical, out var k) ? k : throw new KeyNotFoundException($"論理キー「{logical}」は Profile に割り当てがありません");
}

/// <summary>
/// 入力バックエンド（設計書 §2.10.6）。Bind で対象ウィンドウと論理キー割当を受け取り、Send で1操作を送る。
/// </summary>
public interface IInputBackend : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    IsolationInfo Isolation { get; }
    /// <summary>静的検査（API・ドライバ・同意の有無）。送れたか（A）は送信ごとの結果で、画面が反応したか（B）は探索の各手で観測する</summary>
    InputProbeResult Probe(nint target);
    void Bind(nint target, LogicalKeyMap map, ForbiddenKeyGuard guard, int defaultHoldMs);
    InputReceipt Send(InputAction action);
    /// <summary>押しっぱなしのキーをすべて離す（停止・異常時に必ず呼ぶ）</summary>
    void ReleaseAll();
}

public interface ISleeper { void SleepUntil(double targetMs); }

/// <summary>実時間で待つ。短い待ちは SpinWait で精度を上げる。</summary>
public sealed class RealSleeper : ISleeper
{
    private readonly IClock _clock;
    public RealSleeper(IClock clock) => _clock = clock;
    public void SleepUntil(double targetMs)
    {
        while (true)
        {
            double rest = targetMs - _clock.NowMs;
            if (rest <= 0) return;
            if (rest > 3) Thread.Sleep((int)Math.Min(rest - 2, 50)); else Thread.SpinWait(200);
        }
    }
}

/// <summary>
/// 共通部: 論理キー解決 → 禁止キー検査 → 計画（InputPlanner）→ 時刻どおりに Emit。押下中キーを追跡して ReleaseAll できる。
/// 具体的な送出方法（PostMessage / SendInput 等）は Emit だけを実装する。
/// </summary>
public abstract class PlannedInputBackend : IInputBackend
{
    protected readonly IClock Clock;
    private readonly ISleeper _sleeper;
    private readonly object _gate = new();
    private readonly HashSet<KeySpec> _down = new();
    protected nint Target { get; private set; }
    protected LogicalKeyMap? Map { get; private set; }
    protected ForbiddenKeyGuard Guard { get; private set; } = new();
    public int DefaultHoldMs { get; private set; } = 80;
    public InputTiming Timing { get; set; } = InputTiming.Default;

    protected PlannedInputBackend(IClock clock, ISleeper? sleeper = null) { Clock = clock; _sleeper = sleeper ?? new RealSleeper(clock); }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract IsolationInfo Isolation { get; }
    public abstract InputProbeResult Probe(nint target);

    public virtual void Bind(nint target, LogicalKeyMap map, ForbiddenKeyGuard guard, int defaultHoldMs)
    {
        Target = target; Map = map; Guard = guard; DefaultHoldMs = defaultHoldMs;
    }

    /// <summary>送信前の確認（例: SendInput は前面がゲームか）。送ってはいけないなら理由を返す</summary>
    protected virtual string? PreflightCheck() => null;

    /// <summary>1イベントを送出。成功なら true</summary>
    protected abstract bool Emit(PlannedKeyEvent e);

    public IReadOnlyCollection<KeySpec> KeysDown { get { lock (_gate) return _down.ToList(); } }

    public InputReceipt Send(InputAction action)
    {
        var label = action.Describe();
        if (Map == null || Target == 0) return InputReceipt.Rejected(Id, label, "Bind されていません", Clock.NowMs);
        List<KeySpec> keys;
        try { keys = action.LogicalKeys.Select(Map.Resolve).ToList(); }
        catch (KeyNotFoundException e) { return InputReceipt.Rejected(Id, label, e.Message, Clock.NowMs); }
        if (Guard.Check(keys) is string why) return InputReceipt.Rejected(Id, label, "送信拒否: " + why, Clock.NowMs);
        int hold = action.HoldMs ?? DefaultHoldMs;
        var plan = InputPlanner.Plan(keys, hold, action.Mode, Timing);
        lock (_gate)
        {
            if (PreflightCheck() is string pre) return InputReceipt.Rejected(Id, label, pre, Clock.NowMs);
            double t0 = Clock.NowMs, downAt = double.NaN, upAt = t0; int sent = 0; bool ok = true; var notes = new List<string>();
            try
            {
                foreach (var e in plan)
                {
                    _sleeper.SleepUntil(t0 + e.AtMs);
                    if (e.Type == KeyEventType.Down && e.AtMs > 0 && PreflightCheck() is string mid)
                    { notes.Add("途中中止: " + mid); ok = false; break; }
                    bool r = Emit(e);
                    double now = Clock.NowMs;
                    if (double.IsNaN(downAt)) downAt = now;
                    if (e.Type == KeyEventType.Down) _down.Add(e.Key);
                    if (e.Type == KeyEventType.Up) { _down.Remove(e.Key); upAt = now; }
                    if (r) sent++; else { ok = false; notes.Add($"{e.Type} {e.Key.Name} 送信失敗"); }
                }
            }
            finally { if (_down.Count > 0) ReleaseAllLocked(notes); }
            int reps = plan.Count(p => p.Type == KeyEventType.Repeat);
            string detail = $"{string.Join("+", keys.Select(k => k.Name))} hold={hold}ms {action.Mode}{(reps > 0 ? $" repeat={reps}" : "")} events={sent}/{plan.Count}" +
                            (notes.Count > 0 ? " " + string.Join(" / ", notes) : "");
            return new InputReceipt(ok, Id, label, double.IsNaN(downAt) ? t0 : downAt, upAt, sent, detail);
        }
    }

    public void ReleaseAll() { lock (_gate) ReleaseAllLocked(null); }

    private void ReleaseAllLocked(List<string>? notes)
    {
        foreach (var k in _down.ToList())
        {
            try { Emit(new PlannedKeyEvent(0, k, KeyEventType.Up)); } catch { }
            _down.Remove(k);
            notes?.Add($"{k.Name} を強制解放");
        }
    }

    public virtual void Dispose() => ReleaseAll();
}
