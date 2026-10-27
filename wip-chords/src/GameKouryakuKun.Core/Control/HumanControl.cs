namespace GameKouryakuKun.Core.Control;

/// <summary>
/// Human Control（v0.15.0）: 人間が攻略くんの入力経路で操作する。自動の手の選択だけを止め、取得・状態の認識・観測・記録は続ける。
/// 送れる操作は Game Profile の中立のキー名と wait だけ（意味の名前に変えない）。
/// 人間の手は世界の観測（状態A → human:操作 → 状態B）としてだけ使い、正解・報酬・推奨・模倣の対象にはしない。
/// 自動↔人間の切替のたびに、古い状態を前提に手を出さないよう、次の手の前に画面を観測し直す（TakeSwitch）
/// </summary>
public sealed class HumanControl
{
    private readonly object _lock = new();
    private readonly Queue<string> _q = new();
    private bool _enabled, _switchPending;
    private readonly HashSet<string> _allowed;
    public int Switches { get; private set; }
    public int Queued { get; private set; }
    public int Rejected { get; private set; }
    /// <summary>キューに積める上限（押しすぎで古い状態向けの手がたまらないように）</summary>
    public int MaxQueue { get; init; } = 3;   // 根拠: 汎用（1手ごとに観測し直すので、先送りは数手まで）

    public HumanControl(IEnumerable<string> allowedActions) { _allowed = new HashSet<string>(allowedActions, StringComparer.Ordinal); }

    public bool Enabled { get { lock (_lock) return _enabled; } }
    public IReadOnlyCollection<string> Allowed => _allowed;

    public void SetEnabled(bool on)
    {
        lock (_lock)
        {
            if (_enabled == on) return;
            _enabled = on; _switchPending = true; Switches++;
            _q.Clear();
        }
    }

    /// <summary>人間の操作を積む。Human Control でない・中立の名前にない・積みすぎは拒否</summary>
    public bool Enqueue(string action)
    {
        lock (_lock)
        {
            if (!_enabled || !_allowed.Contains(action) || _q.Count >= MaxQueue) { Rejected++; return false; }
            _q.Enqueue(action); Queued++; return true;
        }
    }

    public bool TryDequeue(out string action)
    {
        lock (_lock)
        {
            if (_enabled && _q.Count > 0) { action = _q.Dequeue(); return true; }
            action = ""; return false;
        }
    }

    /// <summary>積んだ操作を捨てる（v0.21.0: 観測が途切れた後、古い画面向けの人間の手を送らない）。Human Control の ON/OFF は変えない</summary>
    public int DiscardQueued() { lock (_lock) { int n = _q.Count; _q.Clear(); return n; } }

    /// <summary>切替があったか（読むと消える）。true なら次の手の前に画面を観測し直す</summary>
    public bool TakeSwitch() { lock (_lock) { bool s = _switchPending; _switchPending = false; return s; } }
}
