namespace GameKouryakuKun.Core.Supervisor;

/// <summary>
/// システムの出来事（v0.21.0）。ゲームの経験（状態＋操作→結果）とは別に記録し、報酬・価値・予測・並びの材料にしない。
/// 時刻と連続履歴の通し番号（前後）で、ゲームの経験と時間の上で突き合わせて分析できるようにする。
/// 再起動をエピソードの終わりとは決めない。
/// </summary>
public enum SystemEventKind
{
    SupervisorStarted, ObservationLost, WindowLost, WindowHandleChanged, WindowAcquired, WindowResized, CaptureLost, CaptureRestored, InputReconnected,
    ProcessExited, SupervisorRestartBegin, ProcessStarted, ObservationRestored, ExplorationResumed, SuspectedHang, HangCleared,
    RecoveryFailed, RecoveryAborted, AiException, DbCheckpoint, FileChanged, ConfigProblem,
}

/// <summary>OS の上での終了の分類（ゲームの中の出来事とは結びつけない。報酬にも使わない）</summary>
public enum ExitClass { Unknown, Normal, Abnormal, Crash }

public static class ExitClassifier
{
    /// <summary>終了コードだけから分類する: 0＝normal・NTSTATUS のエラーの範囲（0xC0000000 以上）＝crash・それ以外の 0 でない値＝abnormal（強制終了と区別できないことがある）</summary>
    public static ExitClass Classify(int? exitCode) => exitCode switch
    {
        null => ExitClass.Unknown,
        0 => ExitClass.Normal,
        int c when unchecked((uint)c) >= 0xC0000000u => ExitClass.Crash,
        _ => ExitClass.Abnormal,
    };

    public static string Id(ExitClass c) => c.ToString().ToLowerInvariant();
}

public sealed record SystemEvent(SystemEventKind Kind, double AtMs, long? RecoveryId, string Detail, IReadOnlyDictionary<string, object?> Data)
{
    /// <summary>記録の名前（ログ・DB）: OBSERVATION_LOST など</summary>
    public string Name => KindName(Kind);
    public static string KindName(SystemEventKind k) => string.Concat(k.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
