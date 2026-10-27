using System.Runtime.InteropServices;
using System.Text;

namespace GameKouryakuKun.Phase0;

public enum StopMode
{
    /// <summary>既定: 自分の署名付き注入以外はすべてユーザー操作とみなす（リモート操作ツールの注入も含む）</summary>
    AllButOwn,
    /// <summary>物理デバイス由来（INJECTEDフラグなし / hDevice≠0）のみをユーザー操作とみなす</summary>
    PhysicalOnly,
}

public enum InputClass { Own, Physical, OtherInjected }

/// <summary>
/// ユーザー操作の検出。v2: フックと RawInput 受信窓は「専用スレッド」で動かす（ダミー窓/UIとは別スレッド）。
///  (A) 低レベルフック: INJECTED フラグ＋dwExtraInfo 署名で Own / Physical / OtherInjected に分類
///  (B) RawInput(INPUTSINK): hDevice と RAWKEYBOARD/RAWMOUSE の ExtraInformation 署名で同様に分類
/// 観測のみで入力を遮断・改変しない（常に CallNextHookEx）。全イベントを時刻付きでログに残す。
/// </summary>
public sealed class InputMonitor : IDisposable
{
    public sealed class Counters
    {
        public long[,] Hook = new long[2, 3];  // [0=key,1=mouse][InputClass]
        public long[,] Raw = new long[2, 3];
        public HashSet<string> RawDevices = new();
        public Counters Clone() => new() { Hook = (long[,])Hook.Clone(), Raw = (long[,])Raw.Clone(), RawDevices = new(RawDevices) };
        public long H(int dev, InputClass c) => Hook[dev, (int)c];
        public long R(int dev, InputClass c) => Raw[dev, (int)c];
        public string Summary() =>
            $"Hook キー(自分/物理/他注入)={H(0, InputClass.Own)}/{H(0, InputClass.Physical)}/{H(0, InputClass.OtherInjected)} " +
            $"マウス={H(1, InputClass.Own)}/{H(1, InputClass.Physical)}/{H(1, InputClass.OtherInjected)} | " +
            $"Raw キー={R(0, InputClass.Own)}/{R(0, InputClass.Physical)}/{R(0, InputClass.OtherInjected)} " +
            $"マウス={R(1, InputClass.Own)}/{R(1, InputClass.Physical)}/{R(1, InputClass.OtherInjected)}";
        public long UserEvents(StopMode m) => m == StopMode.PhysicalOnly
            ? H(0, InputClass.Physical) + H(1, InputClass.Physical) + R(0, InputClass.Physical) + R(1, InputClass.Physical)
            : H(0, InputClass.Physical) + H(1, InputClass.Physical) + H(0, InputClass.OtherInjected) + H(1, InputClass.OtherInjected)
              + R(0, InputClass.Physical) + R(1, InputClass.Physical) + R(0, InputClass.OtherInjected) + R(1, InputClass.OtherInjected);
    }

    public sealed record Ev(DateTime At, string Source, string Device, string Kind, InputClass Class, string Detail);

    public StopMode Mode { get; set; } = StopMode.AllButOwn;
    /// <summary>ユーザー操作（Mode に従う）を検知したとき（専用スレッド上で呼ばれる。処理は軽く）</summary>
    public event Action<Ev>? UserInput;
    private readonly Counters _c = new();
    private readonly List<Ev> _log = new();
    private int _moveLogged;
    private MessageThread? _thread;
    private IntPtr _kbHook, _msHook, _sink;
    private Native.HookProc? _kbProc, _msProc;
    private Native.WndProc? _sinkProc;
    public string HookStatus { get; private set; } = "";
    public string RawStatus { get; private set; } = "";
    public DateTime? FirstUserHook { get; private set; }
    public DateTime? FirstUserRaw { get; private set; }

    public Counters Snapshot() { lock (_c) return _c.Clone(); }
    public List<Ev> Events() { lock (_log) return _log.ToList(); }

    public static InputMonitor StartNew(StopMode mode = StopMode.AllButOwn)
    {
        var m = new InputMonitor { Mode = mode };
        m._thread = new MessageThread("InputMonitor").Start();
        m._thread.Invoke(m.Install);
        return m;
    }

    private void Install()
    {
        _kbProc = KbProc; _msProc = MsProc;
        var hMod = Native.GetModuleHandleW(null);
        _kbHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0); int e1 = Marshal.GetLastWin32Error();
        _msHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _msProc, hMod, 0); int e2 = Marshal.GetLastWin32Error();
        HookStatus = $"専用スレッド keyboardLL={(_kbHook != IntPtr.Zero ? "OK" : "失敗(" + e1 + ")")}, mouseLL={(_msHook != IntPtr.Zero ? "OK" : "失敗(" + e2 + ")")}";
        _sinkProc = (h, m, w, l) =>
        {
            if (m == Native.WM_INPUT) { try { OnRawInput(l); } catch { } }
            return Native.DefWindowProcW(h, m, w, l);
        };
        _sink = Win32Window.Create("GKK-Phase0-RawSink", 0, 0, 0, 0, 0, _sinkProc);
        var devs = new[]
        {
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _sink },
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _sink },
        };
        RawStatus = Native.RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>())
            ? "専用スレッド RawInput(INPUTSINK) keyboard+mouse=OK" : $"RawInput登録失敗({Marshal.GetLastWin32Error()})";
    }

    private void Record(string source, int dev, string kind, InputClass cls, string detail, bool trigger)
    {
        var ev = new Ev(DateTime.Now, source, dev == 0 ? "キー" : "マウス", kind, cls, detail);
        lock (_c) { if (source == "Hook") _c.Hook[dev, (int)cls]++; else _c.Raw[dev, (int)cls]++; }
        // マウス移動は大量なので、ログは移動以外＋移動は間引き
        lock (_log) { if (_log.Count < 20000 && (kind != "移動" || _moveLogged++ < 2000)) _log.Add(ev); }
        if (!trigger || cls == InputClass.Own) return;
        bool isUser = Mode == StopMode.AllButOwn || cls == InputClass.Physical;
        if (!isUser) return;
        if (source == "Hook") FirstUserHook ??= ev.At; else FirstUserRaw ??= ev.At;
        UserInput?.Invoke(ev);
    }

    private IntPtr KbProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
            var cls = !injected ? InputClass.Physical : (uint)k.dwExtraInfo.ToInt64() == Native.InjectSignatureValue ? InputClass.Own : InputClass.OtherInjected;
            bool down = wParam == 0x100 || wParam == 0x104;
            Record("Hook", 0, down ? "押下" : "離す", cls, $"{KeySpec.VkName((ushort)k.vkCode)} scan=0x{k.scanCode:X} flags=0x{k.flags:X} extra=0x{k.dwExtraInfo.ToInt64():X}", down);
        }
        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MsProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            bool injected = (m.flags & Native.LLMHF_INJECTED) != 0;
            var cls = !injected ? InputClass.Physical : (uint)m.dwExtraInfo.ToInt64() == Native.InjectSignatureValue ? InputClass.Own : InputClass.OtherInjected;
            string kind = wParam.ToInt32() switch { 0x200 => "移動", 0x201 or 0x204 or 0x207 => "ボタン押下", 0x20A or 0x20E => "ホイール", _ => "その他" };
            Record("Hook", 1, kind, cls, $"({m.x},{m.y}) flags=0x{m.flags:X} extra=0x{m.dwExtraInfo.ToInt64():X}", kind != "その他");
        }
        return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    private unsafe void OnRawInput(IntPtr hRaw)
    {
        uint size = 0, hs = (uint)sizeof(Native.RAWINPUTHEADER);
        Native.GetRawInputData(hRaw, Native.RID_INPUT, null, ref size, hs);
        if (size == 0 || size > 4096) return;
        byte* buf = stackalloc byte[(int)size];
        if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, hs) == uint.MaxValue) return;
        var hdr = *(Native.RAWINPUTHEADER*)buf;
        byte* body = buf + hs;
        bool hasDevice = hdr.hDevice != IntPtr.Zero;
        if (hasDevice) lock (_c) if (_c.RawDevices.Count < 32) _c.RawDevices.Add($"0x{hdr.hDevice.ToInt64():X}");
        if (hdr.dwType == Native.RIM_TYPEKEYBOARD)
        {
            // RAWKEYBOARD: MakeCode(2) Flags(2) Reserved(2) VKey(2) Message(4) ExtraInformation(4)
            ushort vkey = *(ushort*)(body + 6); uint msg = *(uint*)(body + 8); uint extra = *(uint*)(body + 12);
            var cls = extra == Native.InjectSignatureValue ? InputClass.Own : hasDevice ? InputClass.Physical : InputClass.OtherInjected;
            bool down = msg == 0x100 || msg == 0x104;
            Record("Raw", 0, down ? "押下" : "離す", cls, $"{KeySpec.VkName(vkey)} hDevice=0x{hdr.hDevice.ToInt64():X} extra=0x{extra:X}", down);
        }
        else if (hdr.dwType == Native.RIM_TYPEMOUSE)
        {
            // RAWMOUSE: usFlags(2) pad(2) usButtonFlags(2) usButtonData(2) ulRawButtons(4) lLastX(4) lLastY(4) ulExtraInformation(4)
            ushort btn = *(ushort*)(body + 4); int dx = *(int*)(body + 12), dy = *(int*)(body + 16); uint extra = *(uint*)(body + 20);
            var cls = extra == Native.InjectSignatureValue ? InputClass.Own : hasDevice ? InputClass.Physical : InputClass.OtherInjected;
            string kind = btn != 0 ? "ボタン/ホイール" : "移動";
            Record("Raw", 1, kind, cls, $"d=({dx},{dy}) btn=0x{btn:X} hDevice=0x{hdr.hDevice.ToInt64():X} extra=0x{extra:X}", true);
        }
    }

    public void SaveCsv(string path)
    {
        var sb = new StringBuilder("time,source,device,kind,class,detail\n");
        foreach (var e in Events()) sb.Append($"{e.At:HH:mm:ss.fff},{e.Source},{e.Device},{e.Kind},{e.Class},\"{e.Detail.Replace("\"", "'")}\"\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public void Dispose()
    {
        if (_thread == null) return;
        try
        {
            _thread.Invoke(() =>
            {
                if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
                if (_msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_msHook);
                _kbHook = _msHook = IntPtr.Zero;
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
        _thread.Stop(); _thread = null;
    }
}
