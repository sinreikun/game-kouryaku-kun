using GameKouryakuKun.Core.Input;

namespace GameKouryakuKun.Core.Activity;

/// <summary>
/// ユーザーの作業との分離（ゲームを背面に置いたまま約10分、別アプリで並行作業）を、手探り探索の記録から判定するための集計。
/// 1手ごとに「その時点でゲームが前面だったか」を数え、ユーザー操作の扱い（停止に使った／分離中のため無視した）と一時停止の回数を合わせて見る。
/// </summary>
public sealed class SeparationStats
{
    public double DurationSec { get; set; }
    public int TurnsGameForeground { get; set; }
    public int TurnsOtherForeground { get; set; }
    public int UserEventsPausing { get; set; }
    public int UserEventsIgnored { get; set; }
    public Dictionary<string, int> Pauses { get; } = new();
    /// <summary>ゲームが背面のときの「変化なし」（入力が届かなかった可能性）</summary>
    public int NoChangeWhileBackground { get; set; }
    public bool EmergencyStopped { get; set; }
    /// <summary>ゲームが背面なのにユーザー操作で一時停止した回数（分離できていれば 0）</summary>
    public int UserActivityPausesWhileBackground { get; set; }

    public void CountPause(string reason) => Pauses[reason] = Pauses.TryGetValue(reason, out var n) ? n + 1 : 1;
    public int TotalTurns => TurnsGameForeground + TurnsOtherForeground;
    public double OtherForegroundShare => TotalTurns == 0 ? 0 : (double)TurnsOtherForeground / TotalTurns;
}

public static class SeparationReport
{
    public const double RequiredSec = 600;  // 根拠: 診断の合格条件（設計書 §10.3。ユーザー承認済み）
    public const double RequiredOtherShare = 0.5;  // 根拠: 同上

    /// <summary>
    /// 判定。Applicable=false は「分離の判定の条件（10分以上・半分以上の手でゲームが背面・その間のユーザー操作あり）を満たさないので判定しない」。
    /// 合格条件（共通）: 背面時にユーザー操作で止まっていない（＝背面中の操作はすべて無視）、緊急停止なし、占有モードでない。
    /// v0.15.0: 手探り探索の実行の中で判定する（既知操作の往復を使う専用の試験は廃止）。画面の反応は合否に使わない。
    /// </summary>
    public static (bool Applicable, bool Passed, List<string> Notes) Evaluate(SeparationStats s, IsolationLevel level)
    {
        var notes = new List<string>();
        bool applicable = true;
        if (s.DurationSec < RequiredSec) { applicable = false; notes.Add($"時間が足りません（{s.DurationSec:0} 秒 < {RequiredSec:0} 秒）"); }
        if (s.OtherForegroundShare < RequiredOtherShare) { applicable = false; notes.Add($"ゲームが背面だった手が {s.OtherForegroundShare:P0}（{RequiredOtherShare:P0} 以上が必要）"); }
        if (s.UserEventsIgnored == 0) { applicable = false; notes.Add("背面のあいだのユーザー操作が記録されていません（別アプリで作業していない）"); }
        bool passed = applicable;
        if (level == IsolationLevel.ForegroundShared) { passed = false; notes.Add("占有モード（SendInput）は分離の判定の対象外"); }
        if (s.EmergencyStopped) { passed = false; notes.Add("緊急停止で終わっています"); }
        if (s.UserActivityPausesWhileBackground > 0) { passed = false; notes.Add($"ゲームが背面なのにユーザー操作で {s.UserActivityPausesWhileBackground} 回止まりました"); }
        if (s.TurnsOtherForeground > 0)
            notes.Add($"参考: 背面時の「変化なし」{s.NoChangeWhileBackground}/{s.TurnsOtherForeground}（画面が反応しないのは未知の画面の性質のこともあるので、合否には使わない）");
        if (s.Pauses.TryGetValue(nameof(PauseReason.UserActivity), out var ua) && ua > 0)
            notes.Add($"ユーザー操作による一時停止 {ua} 回（うち背面時 {s.UserActivityPausesWhileBackground} 回）");
        if (applicable && passed) notes.Add("合格: 背面での並行作業中も止まらず、ユーザー操作はゲームへの入力に影響しませんでした");
        return (applicable, passed && applicable, notes);
    }
}
