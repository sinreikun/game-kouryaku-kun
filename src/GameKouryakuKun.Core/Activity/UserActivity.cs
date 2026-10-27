using GameKouryakuKun.Core.Input;

namespace GameKouryakuKun.Core.Activity;

public enum ActivitySource
{
    /// <summary>自分が署名付きで注入した入力（SendInput の dwExtraInfo）</summary>
    Own,
    /// <summary>物理デバイス由来（hDevice あり・署名なし）</summary>
    Physical,
    /// <summary>他プロセスの注入（リモートデスクトップ・リモート操作ツール・自動化ツール等）</summary>
    OtherInjected,
}

public enum ActivityDevice { Keyboard, Mouse }

public enum UserActivityMode
{
    /// <summary>既定: 自分の印付き注入以外はすべてユーザー操作（リモートデスクトップ越しの操作も含む）</summary>
    AllButOwn,
    /// <summary>物理デバイス由来のみをユーザー操作とみなす</summary>
    PhysicalOnly,
}

public sealed record UserActivityEvent(double AtMs, ActivityDevice Device, string Kind, ActivitySource Source, bool CountsAsUser, string Detail);

public static class ActivityClassifier
{
    /// <summary>自分の注入を見分ける署名（"GKK0"）。RawInput の ExtraInformation に現れる</summary>
    public const uint OwnSignature = 0x474B4B30;  // 根拠: 識別子（自分の注入の印 "GKK0"）

    public static ActivitySource Classify(uint extraInformation, bool hasDevice) =>
        extraInformation == OwnSignature ? ActivitySource.Own : hasDevice ? ActivitySource.Physical : ActivitySource.OtherInjected;

    public static bool CountsAsUser(ActivitySource s, UserActivityMode mode) => s switch
    {
        ActivitySource.Own => false,
        ActivitySource.Physical => true,
        _ => mode == UserActivityMode.AllButOwn,
    };
}

public sealed class ActivityCounters
{
    public long[,] Counts { get; } = new long[2, 3]; // [device, source]
    public long Get(ActivityDevice d, ActivitySource s) => Counts[(int)d, (int)s];
    public ActivityCounters Clone() { var c = new ActivityCounters(); Array.Copy(Counts, c.Counts, Counts.Length); return c; }
    public override string ToString() =>
        $"キー(自分/物理/他注入)={Get(ActivityDevice.Keyboard, ActivitySource.Own)}/{Get(ActivityDevice.Keyboard, ActivitySource.Physical)}/{Get(ActivityDevice.Keyboard, ActivitySource.OtherInjected)} " +
        $"マウス={Get(ActivityDevice.Mouse, ActivitySource.Own)}/{Get(ActivityDevice.Mouse, ActivitySource.Physical)}/{Get(ActivityDevice.Mouse, ActivitySource.OtherInjected)}";
}

/// <summary>ユーザー操作の検出（入力内容は記録しない。種類と出所だけ）</summary>
public interface IUserActivityMonitor : IDisposable
{
    UserActivityMode Mode { get; set; }
    /// <summary>ユーザー操作とみなす入力を検知したとき（監視スレッドから呼ばれる。処理は軽く）</summary>
    event Action<UserActivityEvent>? UserActivity;
    double? LastUserActivityMs { get; }
    string Status { get; }
    ActivityCounters Snapshot();
}

public static class UserActivityPolicy
{
    /// <summary>
    /// ユーザー操作を検知したときにAIを止めるか。
    ///  - foreground_shared（SendInput等）: 常に止める（ユーザーの入力とAIの入力が混ざるため）
    ///  - isolated / device_global: ゲームが前面のときだけ止める（ユーザーがゲームを操作し始めた＝主導権をユーザーへ）。
    ///    ユーザーが別アプリで作業しているだけなら止めない（分離運用の本来の目的）。
    /// </summary>
    public static bool ShouldPause(IsolationLevel level, bool gameIsForeground) =>
        level == IsolationLevel.ForegroundShared || gameIsForeground;
}
