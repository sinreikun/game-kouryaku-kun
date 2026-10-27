namespace GameKouryakuKun.Core.Input;

/// <summary>
/// 入力の確かめを2つに分ける（v0.15.0）。
/// A（送達）: OS・入力方式として、対象のウィンドウへ入力を送れたか（InputReceipt.Ok）。続けて失敗したらシステムの失敗として止める理由になる。
/// B（視覚的な反応）: その入力のあとゲームの画面が変わったか。反応しないのは未知の状態の性質かもしれないので、それだけでは異常とせず、止めない（数えるだけ）。
/// 特定のキーで画面が変わる・メニューが開閉するといった、特定の状態の性質は正常の条件に使わない
/// </summary>
public sealed class InputDeliveryMonitor
{
    /// <summary>A の失敗がこの回数続いたらシステムの失敗とする</summary>
    public int MaxConsecutiveFailures { get; init; } = 5;   // 根拠: 汎用（一時的な前面の切替などの1〜2回の拒否で止めない最小の繰り返し）

    public int Sent { get; private set; }
    public int Delivered { get; private set; }
    public int Failed { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public string? LastFailure { get; private set; }
    /// <summary>B: キーを送れた手のうち、画面が変わった手（情報。停止には使わない）</summary>
    public int Reacted { get; private set; }
    public int ReactionObserved { get; private set; }
    /// <summary>B: 画面が変わらなかった連続（情報）</summary>
    public int NoReactionStreak { get; private set; }
    public int LongestNoReactionStreak { get; private set; }

    /// <summary>v0.23.1: Supervisor が入力をつなぎ直した後、続けての失敗の数を戻す（合計は残す）</summary>
    public void ResetStreak() => ConsecutiveFailures = 0;
    public bool SystemFailure => ConsecutiveFailures >= MaxConsecutiveFailures;

    /// <summary>A の結果を数える。返り値はシステムの失敗になったか</summary>
    public bool Record(InputReceipt r)
    {
        Sent++;
        if (r.Ok) { Delivered++; ConsecutiveFailures = 0; }
        else { Failed++; ConsecutiveFailures++; LastFailure = r.Detail; }
        return SystemFailure;
    }

    /// <summary>B の結果を数える（キーを送れた手だけ）</summary>
    public void RecordReaction(bool changed)
    {
        ReactionObserved++;
        if (changed) { Reacted++; NoReactionStreak = 0; }
        else { NoReactionStreak++; LongestNoReactionStreak = Math.Max(LongestNoReactionStreak, NoReactionStreak); }
    }
}
