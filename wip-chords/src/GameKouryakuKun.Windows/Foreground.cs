using GameKouryakuKun.Core.Capture;
using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Input;
using GameKouryakuKun.Core.Probe;

namespace GameKouryakuKun.Windows;

/// <summary>前面ウィンドウの切替。入力の注入は行わない。</summary>
public static class ForegroundHelper
{
    public static bool TryActivate(nint h, int waitMs = 200)
    {
        if (Native.GetForegroundWindow() == h) return true;
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        Native.SetForegroundWindow(h);
        Thread.Sleep(waitMs);
        if (Native.GetForegroundWindow() == h) return true;
        var fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        try { Native.BringWindowToTop(h); Native.SetForegroundWindow(h); }
        finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
        Thread.Sleep(waitMs);
        return Native.GetForegroundWindow() == h;
    }
    public static nint ConsoleWindow => Native.GetConsoleWindow();
}

/// <summary>
/// 前面役ウィンドウ（状態B=ゲームが背面、のテストで前面に置く無害な窓）。届いたキーの数を数えるだけで何もしない。
/// PostMessage の送り先がずれて前面に漏れていないかの確認にも使う。
/// </summary>
public sealed class StandInWindow : IDisposable
{
    private readonly MessageThread _ui;
    private Native.WndProc? _proc;
    private IntPtr _font;
    private string _status = "";
    private int _keys;
    public nint Hwnd { get; private set; }
    public int KeysReceived => Volatile.Read(ref _keys);

    public StandInWindow()
    {
        _ui = new MessageThread("StandInWindow").Start();
        _ui.Invoke(() =>
        {
            _font = Native.CreateFontW(-20, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Yu Gothic UI");
            _proc = Proc;
            Hwnd = Win32Window.Create("ゲーム攻略くん 前面役ウィンドウ（テスト用・入力を受けても何もしません）", Native.WS_OVERLAPPEDWINDOW, 60, 60, 720, 260, _proc);
        });
    }

    private IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case Native.WM_KEYDOWN: case Native.WM_SYSKEYDOWN:
                Interlocked.Increment(ref _keys); Native.InvalidateRect(h, IntPtr.Zero, true); return IntPtr.Zero;
            case Native.WM_CHAR: return IntPtr.Zero;
            case Native.WM_PAINT:
            {
                var hdc = Native.BeginPaint(h, out var ps);
                Native.GetClientRect(h, out var rc);
                var old = Native.SelectObject(hdc, _font);
                Native.SetBkMode(hdc, 1); Native.SetTextColor(hdc, 0x00FFFFFF);
                rc.Top += 24; rc.Left += 16; rc.Right -= 16;
                string text = "ゲーム攻略くん 前面役ウィンドウ\n（ゲームを背面にしたままの入力テスト中。キーを受けても何もしません）\n\n" + _status + $"\n\nこの窓に届いたキー: {KeysReceived}";
                Native.DrawTextW(hdc, text, -1, ref rc, Native.DT_CENTER | Native.DT_WORDBREAK | Native.DT_NOPREFIX);
                Native.SelectObject(hdc, old);
                Native.EndPaint(h, ref ps);
                return IntPtr.Zero;
            }
            case Native.WM_CLOSE: Native.ShowWindow(h, Native.SW_HIDE); return IntPtr.Zero;
        }
        return Native.DefWindowProcW(h, m, w, l);
    }

    public void Show(string status)
    {
        _ui.Invoke(() => { _status = status; Native.ShowWindow(Hwnd, Native.SW_SHOW); Native.InvalidateRect(Hwnd, IntPtr.Zero, true); });
    }
    public bool BringToFront(string status) { Show(status); return ForegroundHelper.TryActivate(Hwnd); }
    public void Hide() => _ui.Invoke(() => Native.ShowWindow(Hwnd, Native.SW_HIDE));
    public void Dispose() { try { _ui.Invoke(() => Native.DestroyWindow(Hwnd)); } catch { } _ui.Dispose(); }
}

/// <summary>Thread.Sleep の精度を 1ms にする（押下時間 80ms を正確に守るため）。Dispose で元に戻す。</summary>
public sealed class TimerResolution : IDisposable
{
    private readonly bool _ok;
    public TimerResolution() { _ok = Native.timeBeginPeriod(1) == 0; }
    public void Dispose() { if (_ok) Native.timeEndPeriod(1); }
}
