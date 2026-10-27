using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameKouryakuKun.Phase0;

/// <summary>
/// 物理入力の検出。2系統を同時に使い、結果を比較できるようにする。
///  (A) 低レベルフック WH_KEYBOARD_LL / WH_MOUSE_LL: flags の INJECTED ビットと dwExtraInfo 署名で「自分の注入 / 他者の注入 / 物理」を区別
///  (B) Raw Input (RIDEV_INPUTSINK): RAWINPUTHEADER.hDevice が 0 なら注入（SendInput由来）、非0なら実デバイス
/// フックは観測のみで、入力を握りつぶすことは絶対にしない（常に CallNextHookEx）。
/// </summary>
public sealed class InputMonitor : IDisposable
{
    public sealed class Counters
    {
        public long HookKeyPhysical, HookKeyInjectedOwn, HookKeyInjectedOther;
        public long HookMousePhysical, HookMouseInjectedOwn, HookMouseInjectedOther;
        public long RawKeyDevice, RawKeyNoDevice, RawMouseDevice, RawMouseNoDevice;
        public HashSet<string> RawDevices = new();
        public Counters Clone() { var c = (Counters)MemberwiseClone(); c.RawDevices = new(RawDevices); return c; }
    }

    public event Action<string, string>? PhysicalInput; // (source, description)
    private readonly Counters _c = new();
    private IntPtr _kbHook, _msHook;
    private Native.HookProc? _kbProc, _msProc;
    private RawSink? _sink;
    public string HookStatus { get; private set; } = "";
    public string RawStatus { get; private set; } = "";
    public DateTime? FirstPhysicalHook { get; private set; }
    public DateTime? FirstPhysicalRaw { get; private set; }

    public Counters Snapshot() { lock (_c) return _c.Clone(); }

    public static InputMonitor StartOnUi() => Ui.Invoke(() => { var m = new InputMonitor(); m.Install(); return m; });

    private void Install()
    {
        _kbProc = KbProc; _msProc = MsProc;
        var hMod = Native.GetModuleHandleW(null);
        _kbHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0);
        int e1 = Marshal.GetLastWin32Error();
        _msHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _msProc, hMod, 0);
        int e2 = Marshal.GetLastWin32Error();
        HookStatus = $"keyboardLL={(_kbHook != IntPtr.Zero ? "OK" : "失敗(" + e1 + ")")}, mouseLL={(_msHook != IntPtr.Zero ? "OK" : "失敗(" + e2 + ")")}";
        _sink = new RawSink(this);
        RawStatus = _sink.Register();
    }

    private IntPtr KbProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
            bool down = wParam == 0x100 || wParam == 0x104;
            lock (_c)
            {
                if (!injected) _c.HookKeyPhysical++;
                else if (k.dwExtraInfo == Native.InjectSignature) _c.HookKeyInjectedOwn++;
                else _c.HookKeyInjectedOther++;
            }
            if (!injected && down)
            {
                FirstPhysicalHook ??= DateTime.Now;
                PhysicalInput?.Invoke("LLフック", $"物理キー {(Keys)k.vkCode} (scan=0x{k.scanCode:X})");
            }
        }
        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MsProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            bool injected = (m.flags & Native.LLMHF_INJECTED) != 0;
            lock (_c)
            {
                if (!injected) _c.HookMousePhysical++;
                else if (m.dwExtraInfo == Native.InjectSignature) _c.HookMouseInjectedOwn++;
                else _c.HookMouseInjectedOther++;
            }
            if (!injected)
            {
                FirstPhysicalHook ??= DateTime.Now;
                string kind = wParam.ToInt32() switch { 0x200 => "移動", 0x201 or 0x204 or 0x207 => "ボタン押下", 0x20A or 0x20E => "ホイール", _ => "その他" };
                PhysicalInput?.Invoke("LLフック", $"物理マウス {kind}");
            }
        }
        return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    internal unsafe void OnRawInput(IntPtr hRaw)
    {
        uint size = 0, hs = (uint)sizeof(Native.RAWINPUTHEADER);
        Native.GetRawInputData(hRaw, Native.RID_INPUT, null, ref size, hs);
        if (size == 0 || size > 4096) return;
        byte* buf = stackalloc byte[(int)size];
        if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, hs) == uint.MaxValue) return;
        var hdr = *(Native.RAWINPUTHEADER*)buf;
        bool hasDevice = hdr.hDevice != IntPtr.Zero;
        bool isKeyDown = false;
        if (hdr.dwType == Native.RIM_TYPEKEYBOARD)
        {
            // RAWKEYBOARD: MakeCode(2) Flags(2) Reserved(2) VKey(2) Message(4) ExtraInformation(4)
            byte* kb = buf + hs;
            uint msg = *(uint*)(kb + 8);
            isKeyDown = msg == 0x100 || msg == 0x104;
        }
        lock (_c)
        {
            if (hdr.dwType == Native.RIM_TYPEKEYBOARD) { if (hasDevice) _c.RawKeyDevice++; else _c.RawKeyNoDevice++; }
            else if (hdr.dwType == Native.RIM_TYPEMOUSE) { if (hasDevice) _c.RawMouseDevice++; else _c.RawMouseNoDevice++; }
            if (hasDevice && _c.RawDevices.Count < 32) _c.RawDevices.Add($"0x{hdr.hDevice.ToInt64():X}");
        }
        if (hasDevice && (hdr.dwType == Native.RIM_TYPEMOUSE || isKeyDown))
        {
            FirstPhysicalRaw ??= DateTime.Now;
            PhysicalInput?.Invoke("RawInput", $"{(hdr.dwType == Native.RIM_TYPEKEYBOARD ? "キーボード" : "マウス")} hDevice=0x{hdr.hDevice.ToInt64():X}");
        }
    }

    public void Dispose()
    {
        Ui.Invoke(() =>
        {
            if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
            if (_msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_msHook);
            _kbHook = _msHook = IntPtr.Zero;
            _sink?.Unregister(); _sink?.DestroyHandle(); _sink = null;
        });
    }

    private sealed class RawSink : NativeWindow
    {
        private readonly InputMonitor _owner;
        public RawSink(InputMonitor owner) { _owner = owner; CreateHandle(new CreateParams { Caption = "GKK-Phase0-RawSink" }); }
        public string Register()
        {
            var devs = new[]
            {
                new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = Handle },
                new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = Handle },
            };
            bool ok = Native.RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
            return ok ? "RawInput(INPUTSINK) keyboard+mouse=OK" : $"RawInput登録失敗({Marshal.GetLastWin32Error()})";
        }
        public void Unregister()
        {
            var devs = new[]
            {
                new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = Native.RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = Native.RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
            };
            Native.RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)Native.WM_INPUT) { try { _owner.OnRawInput(m.LParam); } catch { } }
            base.WndProc(ref m);
        }
    }
}
