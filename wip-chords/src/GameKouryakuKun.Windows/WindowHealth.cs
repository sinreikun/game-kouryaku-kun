namespace GameKouryakuKun.Windows;

/// <summary>ウィンドウの応答の確認（v0.21.0 Supervisor）。中身は見ない。WM_NULL は何もしないメッセージで、ゲームの状態を変えない</summary>
public static class WindowHealth
{
    private const uint WM_NULL = 0x0000, SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>OS が応答なしと判定し、かつ WM_NULL にも timeoutMs の間に応答しないときだけ true（片方だけでは疑わない）</summary>
    public static bool IsHung(IntPtr hwnd, uint timeoutMs = 1000)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        if (!Native.IsHungAppWindow(hwnd)) return false;
        return Native.SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, out _) == IntPtr.Zero;
    }

    public static int ProcessIdOf(IntPtr hwnd) { Native.GetWindowThreadProcessId(hwnd, out uint pid); return (int)pid; }
}
