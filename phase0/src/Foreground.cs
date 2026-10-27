namespace GameKouryakuKun.Phase0;

/// <summary>前面ウィンドウの制御。自動で前面化できない場合はユーザーにクリックを依頼し、確認できるまで待つ。</summary>
public static class Foreground
{
    public static bool IsForeground(IntPtr h) => Native.GetForegroundWindow() == h;

    /// <summary>ツールが前面ウィンドウを切り替えたか（コンソール入力前にクリックを促すため）</summary>
    public static bool FocusMoved { get; set; }

    public static bool TryActivate(IntPtr h)
    {
        if (h != Native.GetConsoleWindow()) FocusMoved = true;
        if (IsForeground(h)) return true;
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        Native.SetForegroundWindow(h);
        Thread.Sleep(150);
        if (IsForeground(h)) return true;
        // 前面スレッドへ一時的にアタッチして再試行（入力の注入は行わない）
        var fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        try { Native.BringWindowToTop(h); Native.SetForegroundWindow(h); }
        finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
        Thread.Sleep(200);
        return IsForeground(h);
    }

    /// <summary>自動前面化→失敗時はユーザーへ「〇〇をクリックしてください」と依頼して最大 timeoutSec 待つ。</summary>
    public static bool Ensure(IntPtr h, string label, int timeoutSec = 20)
    {
        FocusMoved = true;
        if (TryActivate(h)) return true;
        Con.Warn($"自動で「{label}」を前面にできませんでした。{timeoutSec}秒以内に「{label}」をマウスでクリックして前面にしてください…");
        var until = DateTime.Now.AddSeconds(timeoutSec);
        while (DateTime.Now < until)
        {
            if (IsForeground(h)) { Thread.Sleep(400); return IsForeground(h); }
            Thread.Sleep(100);
        }
        return false;
    }

    public static string Describe(IntPtr h) => h == IntPtr.Zero ? "(なし)" : WindowFinder.Describe(h).ToString();
}
