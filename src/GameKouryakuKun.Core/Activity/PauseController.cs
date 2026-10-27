namespace GameKouryakuKun.Core.Activity;

public enum PauseReason { Minimized, Cloaked, WindowLost, CaptureError, UserActivity, EmergencyStop, Manual }

/// <summary>
/// 一時停止理由の集約。どれか1つでも有効なら停止。
/// UserActivity は最後の操作から QuietMs 経過で自動解除、EmergencyStop は解除しない（プロセス終了まで）。
/// </summary>
public sealed class PauseController
{
    private readonly object _gate = new();
    private readonly HashSet<PauseReason> _active = new();
    private double _userUntil = double.NegativeInfinity;
    public double UserQuietMs { get; set; } = 5000; // 根拠: 汎用（人の操作の後は数秒手を出さない＝人の入力との非干渉）
    public event Action<PauseReason, bool, string>? Changed;

    public void Set(PauseReason r, bool on, string detail = "")
    {
        if (r == PauseReason.UserActivity) throw new ArgumentException("UserActivity は OnUserActivity を使ってください");
        bool changed;
        lock (_gate)
        {
            if (!on && r == PauseReason.EmergencyStop) return;
            changed = on ? _active.Add(r) : _active.Remove(r);
        }
        if (changed) Changed?.Invoke(r, on, detail);
    }

    public void OnUserActivity(double nowMs, string detail = "")
    {
        bool started;
        lock (_gate) { started = nowMs >= _userUntil; _userUntil = nowMs + UserQuietMs; }
        if (started) Changed?.Invoke(PauseReason.UserActivity, true, detail);
    }

    public bool IsEmergencyStopped { get { lock (_gate) return _active.Contains(PauseReason.EmergencyStop); } }

    public IReadOnlyList<PauseReason> ActiveReasons(double nowMs)
    {
        lock (_gate)
        {
            var l = _active.ToList();
            if (nowMs < _userUntil) l.Add(PauseReason.UserActivity);
            return l;
        }
    }

    public bool IsPaused(double nowMs) => ActiveReasons(nowMs).Count > 0;
}
