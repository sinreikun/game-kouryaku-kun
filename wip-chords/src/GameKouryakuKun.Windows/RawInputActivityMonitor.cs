using System.Runtime.InteropServices;
using GameKouryakuKun.Core.Activity;
using GameKouryakuKun.Core.Diagnostics;

namespace GameKouryakuKun.Windows;

/// <summary>
/// ユーザー操作の検出（RawInput INPUTSINK、専用スレッド）。Phase 0 で低レベルフックは自分の注入を取りこぼすことが分かったため、
/// フックは使わず RawInput だけで判定する。入力を遮断・改変せず、キーの内容（どのキーか）も記録しない。
/// 分類: ExtraInformation が署名 → 自分 / hDevice あり → 物理 / それ以外 → 他の注入（リモートデスクトップ等）。
/// 既定は AllButOwn（自分の印付き注入以外は全部ユーザー操作）。
/// </summary>
public sealed unsafe class RawInputActivityMonitor : IUserActivityMonitor
{
    private readonly MessageThread _thread;
    private IntPtr _sink;
    private Native.WndProc? _proc;
    private readonly ActivityCounters _c = new();
    private double _lastMoveRaised = double.NegativeInfinity;
    public UserActivityMode Mode { get; set; } = UserActivityMode.AllButOwn;
    public event Action<UserActivityEvent>? UserActivity;
    public double? LastUserActivityMs { get; private set; }
    public string Status { get; private set; } = "";
    public double MouseMoveThrottleMs { get; set; } = 200;

    public RawInputActivityMonitor(UserActivityMode mode = UserActivityMode.AllButOwn)
    {
        Mode = mode;
        _thread = new MessageThread("UserActivityMonitor").Start();
        _thread.Invoke(Install);
    }

    private void Install()
    {
        _proc = (h, m, w, l) =>
        {
            if (m == Native.WM_INPUT) { try { OnRawInput(l); } catch { } }
            return Native.DefWindowProcW(h, m, w, l);
        };
        _sink = Win32Window.Create("GKK-RawInputSink", 0, 0, 0, 0, 0, _proc);
        var devs = new[]
        {
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _sink },
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _sink },
        };
        Status = Native.RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>())
            ? "RawInput(INPUTSINK) キーボード+マウス 監視中" : $"RawInput 登録失敗({Marshal.GetLastWin32Error()})";
    }

    public ActivityCounters Snapshot() { lock (_c) return _c.Clone(); }

    private void OnRawInput(IntPtr hRaw)
    {
        uint size = 0, hs = (uint)sizeof(Native.RAWINPUTHEADER);
        Native.GetRawInputData(hRaw, Native.RID_INPUT, null, ref size, hs);
        if (size == 0 || size > 4096) return;
        byte* buf = stackalloc byte[(int)size];
        if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, hs) == uint.MaxValue) return;
        var hdr = *(Native.RAWINPUTHEADER*)buf;
        byte* body = buf + hs;
        bool hasDevice = hdr.hDevice != IntPtr.Zero;
        double now = MonoClock.Now;
        if (hdr.dwType == Native.RIM_TYPEKEYBOARD)
        {
            // RAWKEYBOARD: MakeCode(2) Flags(2) Reserved(2) VKey(2) Message(4) ExtraInformation(4)
            uint msg = *(uint*)(body + 8); uint extra = *(uint*)(body + 12);
            bool down = msg == 0x100 || msg == 0x104;
            Raise(now, ActivityDevice.Keyboard, down ? "キー押下" : "キー離し", ActivityClassifier.Classify(extra, hasDevice), down);
        }
        else if (hdr.dwType == Native.RIM_TYPEMOUSE)
        {
            // RAWMOUSE: usFlags(2) pad(2) usButtonFlags(2) usButtonData(2) ulRawButtons(4) lLastX(4) lLastY(4) ulExtraInformation(4)
            ushort btn = *(ushort*)(body + 4); uint extra = *(uint*)(body + 20);
            bool move = btn == 0;
            bool trigger = !move || now - _lastMoveRaised >= MouseMoveThrottleMs;
            if (move && trigger) _lastMoveRaised = now;
            Raise(now, ActivityDevice.Mouse, move ? "マウス移動" : "マウスボタン/ホイール", ActivityClassifier.Classify(extra, hasDevice), trigger);
        }
    }

    private void Raise(double now, ActivityDevice dev, string kind, ActivitySource src, bool trigger)
    {
        lock (_c) _c.Counts[(int)dev, (int)src]++;
        bool user = ActivityClassifier.CountsAsUser(src, Mode);
        if (!user) return;
        LastUserActivityMs = now;
        if (trigger) UserActivity?.Invoke(new UserActivityEvent(now, dev, kind, src, true, ""));
    }

    public void Dispose()
    {
        try
        {
            _thread.Invoke(() =>
            {
                var devs = new[]
                {
                    new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = Native.RIDEV_REMOVE },
                    new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = Native.RIDEV_REMOVE },
                };
                Native.RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
                if (_sink != IntPtr.Zero) Native.DestroyWindow(_sink);
            });
        }
        catch { }
        _thread.Dispose();
    }
}

/// <summary>緊急停止ホットキー（RegisterHotKey。入力内容の監視はしない）。既定 Ctrl+Shift+F9。</summary>
public sealed class EmergencyStopHotkey : IDisposable
{
    private readonly MessageThread _thread;
    private const int HotkeyId = 0x4B4B;
    public bool Registered { get; }
    public string Description { get; }
    public event Action? Pressed;

    public EmergencyStopHotkey(uint modifiers = Native.MOD_CONTROL | Native.MOD_SHIFT, uint vk = 0x78 /*F9*/, string description = "Ctrl+Shift+F9")
    {
        Description = description;
        _thread = new MessageThread("EmergencyHotkey").Start();
        _thread.Hook = (m, w, l) =>
        {
            if (m == Native.WM_HOTKEY && w.ToInt32() == HotkeyId) { Pressed?.Invoke(); return true; }
            return false;
        };
        Registered = _thread.Invoke(() => Native.RegisterHotKey(_thread.Hwnd, HotkeyId, modifiers | Native.MOD_NOREPEAT, vk));
    }

    public void Dispose()
    {
        try { _thread.Invoke(() => Native.UnregisterHotKey(_thread.Hwnd, HotkeyId)); } catch { }
        _thread.Dispose();
    }
}
