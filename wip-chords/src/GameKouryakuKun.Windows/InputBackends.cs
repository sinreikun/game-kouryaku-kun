using System.Runtime.InteropServices;
using GameKouryakuKun.Core.Activity;
using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Input;

namespace GameKouryakuKun.Windows;

/// <summary>
/// ① WindowMessage: ゲームのウィンドウへ WM_KEYDOWN / WM_KEYUP を PostMessage する（WM_CHAR は送らない＝Phase 0 で不要と確認）。
/// OS の入力キューを通らないので、ユーザーの物理入力・前面アプリ・フォーカスに一切影響しない（分離等級 isolated）。
/// lParam にはテンキーを含む正しいスキャンコード・拡張ビット・前回状態・遷移ビットを入れる。
/// </summary>
public sealed class PostMessageInputBackend : PlannedInputBackend
{
    public PostMessageInputBackend(IClock clock, ISleeper? sleeper = null) : base(clock, sleeper) { }
    public override string Id => "window_message";
    public override string DisplayName => "WindowMessage (PostMessage WM_KEYDOWN/UP)";
    public override IsolationInfo Isolation { get; } = new(IsolationLevel.Isolated, RequiresFocus: false, AffectsUserInput: false, RequiresAdminSetup: false, Driver: null);
    public int LastError { get; private set; }

    public override InputProbeResult Probe(nint target) =>
        Native.IsWindow(target)
            ? new(true, "送信可能（送れたかは送信ごとに確かめる）。ゲームが管理者権限で動いている場合は UIPI により拒否されます")
            : new(false, "対象ウィンドウがありません");

    protected override bool Emit(PlannedKeyEvent e)
    {
        uint msg = e.Type == KeyEventType.Up ? Native.WM_KEYUP : Native.WM_KEYDOWN;
        long lp = InputPlanner.MakeKeyLParam(e.Key, e.Type);
        bool ok = Native.PostMessageW(Target, msg, (IntPtr)e.Key.Vk, new IntPtr(unchecked((int)lp)));
        if (!ok) LastError = Marshal.GetLastWin32Error();
        return ok;
    }
}

/// <summary>
/// ③ SendInput（最終手段・占有モード）。OS 全体の入力キューへ注入するため前面ウィンドウにしか届かず、ユーザー操作と競合する（foreground_shared）。
///  - ユーザーの明示的な同意がない限り Probe は利用不可を返し、Send も拒否する
///  - 各キー押下の直前に「前面がゲームか」を確認し、違えば送らない（途中なら中止して全キー解放）
///  - VK モードで送る（スキャンコードモードは NumLock 状態でテンキーが別キーになるため不使用：Phase 0 v2 の知見）
///  - dwExtraInfo に署名を入れ、UserActivityMonitor が自分の注入を見分けられるようにする
/// </summary>
public sealed class SendInputBackend : PlannedInputBackend
{
    private readonly bool _consent;
    public SendInputBackend(IClock clock, bool userConsented, ISleeper? sleeper = null) : base(clock, sleeper) { _consent = userConsented; }
    public override string Id => "sendinput";
    public override string DisplayName => "SendInput（前面専用・占有モード）";
    public override IsolationInfo Isolation { get; } = new(IsolationLevel.ForegroundShared, RequiresFocus: true, AffectsUserInput: true, RequiresAdminSetup: false, Driver: null);

    public override InputProbeResult Probe(nint target) => _consent
        ? new(true, "同意済み。ゲームが前面のときだけ送信します（ユーザーの作業と同時には使えません）")
        : new(false, "ユーザーの同意がないため無効（占有モードは --allow-sendinput で明示的に有効化）");

    protected override string? PreflightCheck()
    {
        if (!_consent) return "SendInput は同意がないため無効です";
        if (Native.GetForegroundWindow() != Target) return "前面ウィンドウがゲームではないため送信しません（誤入力防止）";
        return null;
    }

    protected override bool Emit(PlannedKeyEvent e)
    {
        uint flags = e.Key.Extended ? Native.KEYEVENTF_EXTENDEDKEY : 0;
        if (e.Type == KeyEventType.Up) flags |= Native.KEYEVENTF_KEYUP;
        var inp = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new() { ki = new() { wVk = e.Key.Vk, wScan = e.Key.Scan, dwFlags = flags, dwExtraInfo = new IntPtr(unchecked((int)ActivityClassifier.OwnSignature)) } },
        };
        return Native.SendInput(1, new[] { inp }, Marshal.SizeOf<Native.INPUT>()) == 1;
    }
}
