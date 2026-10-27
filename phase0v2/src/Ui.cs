using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GameKouryakuKun.Phase0;

/// <summary>素の Win32 ウィンドウ作成ヘルパ（WinForms 非依存＝exe を小さくするため）。</summary>
internal static class Win32Window
{
    private static readonly List<Native.WndProc> Keep = new(); // GC 回収防止
    private static int _seq;

    public static unsafe IntPtr Create(string title, uint style, int x, int y, int w, int h, Native.WndProc proc, uint bgColor = 0x00302218)
    {
        lock (Keep) Keep.Add(proc);
        string cls = $"GKK_Phase0_{Environment.ProcessId}_{Interlocked.Increment(ref _seq)}";
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = Native.GetModuleHandleW(null),
            hCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
            hbrBackground = Native.CreateSolidBrush(bgColor),
            lpszClassName = cls,
        };
        if (Native.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx失敗 {Marshal.GetLastWin32Error()}");
        var hwnd = Native.CreateWindowExW(0, cls, title, style, x, y, w, h, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx失敗 {Marshal.GetLastWin32Error()}");
        return hwnd;
    }
}

/// <summary>メッセージループを持つ専用スレッド。Invoke でそのスレッド上の処理を同期実行できる。</summary>
public sealed class MessageThread
{
    private readonly ConcurrentQueue<Action> _q = new();
    private IntPtr _hwnd;
    private Thread? _thread;
    private int _threadId;
    private Native.WndProc? _proc;
    public string Name { get; }
    public MessageThread(string name) { Name = name; }

    public MessageThread Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Environment.CurrentManagedThreadId;
            _proc = (h, m, w, l) =>
            {
                if (m == Native.WM_APP) { while (_q.TryDequeue(out var a)) a(); return IntPtr.Zero; }
                return Native.DefWindowProcW(h, m, w, l);
            };
            _hwnd = Win32Window.Create($"GKK-{Name}-invoker", 0, 0, 0, 0, 0, _proc);
            ready.Set();
            while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0) { Native.TranslateMessage(ref msg); Native.DispatchMessageW(ref msg); }
        }) { IsBackground = true, Name = Name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        return this;
    }

    public T Invoke<T>(Func<T> f)
    {
        if (Environment.CurrentManagedThreadId == _threadId) return f();
        T result = default!; Exception? err = null; using var done = new ManualResetEventSlim();
        _q.Enqueue(() => { try { result = f(); } catch (Exception e) { err = e; } finally { done.Set(); } });
        Native.PostMessageW(_hwnd, Native.WM_APP, IntPtr.Zero, IntPtr.Zero);
        if (!done.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException($"{Name} スレッドが応答しません");
        if (err != null) throw err;
        return result;
    }
    public void Invoke(Action a) => Invoke(() => { a(); return 0; });

    public void Stop()
    {
        try { Invoke(() => Native.PostQuitMessage(0)); } catch { }
    }
}

public static class Ui
{
    public static MessageThread Thread { get; private set; } = null!;
    public static void Start() => Thread = new MessageThread("UI").Start();
    public static T Invoke<T>(Func<T> f) => Thread.Invoke(f);
    public static void Invoke(Action a) => Thread.Invoke(a);
}

/// <summary>
/// テスト用の無害なダミー窓。背面テスト時の「前面アプリ役」で、届いたキーを記録・表示するだけで何も実行しない。
/// v2: WinForms を使わず素の Win32 で実装。UI スレッド上に作る（入力監視フックとは別スレッド）。
/// </summary>
public sealed class DummyWindow
{
    public IntPtr Hwnd { get; private set; }
    private readonly List<string> _log = new();
    private string _status = "";
    private IntPtr _font;
    private Native.WndProc? _proc;
    public int KeyDownCount { get; private set; }
    public List<string> ReceivedKeys { get { lock (_log) return _log.ToList(); } }

    public void Create()
    {
        _font = Native.CreateFontW(-22, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Yu Gothic UI");
        _proc = WndProc;
        Hwnd = Win32Window.Create("ゲーム攻略くん Phase0 ダミー窓（テスト用・入力の受け皿）", Native.WS_OVERLAPPEDWINDOW, 40, 40, 760, 300, _proc);
    }

    private unsafe IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case Native.WM_KEYDOWN:
            case Native.WM_SYSKEYDOWN:
                KeyDownCount++;
                lock (_log) _log.Add(KeySpec.VkName((ushort)w));
                Native.InvalidateRect(h, IntPtr.Zero, true);
                return IntPtr.Zero; // 既定処理（Alt+メニュー等）に渡さない
            case Native.WM_CHAR:
                return IntPtr.Zero;
            case Native.WM_PAINT:
            {
                var hdc = Native.BeginPaint(h, out var ps);
                Native.GetClientRect(h, out var rc);
                var old = Native.SelectObject(hdc, _font);
                Native.SetBkMode(hdc, 1); Native.SetTextColor(hdc, 0x00FFFFFF);
                rc.Top += 30; rc.Left += 20; rc.Right -= 20;
                string text = "ゲーム攻略くん Phase0 ダミー窓（キーを受けても何も実行しません）\n\n" + _status +
                              (KeyDownCount > 0 ? $"\n\n受信キー: {string.Join(" ", ReceivedKeys.TakeLast(12))}" : "");
                Native.DrawTextW(hdc, text, -1, ref rc, Native.DT_CENTER | Native.DT_WORDBREAK | Native.DT_NOPREFIX);
                Native.SelectObject(hdc, old);
                Native.EndPaint(h, ref ps);
                return IntPtr.Zero;
            }
            case Native.WM_CLOSE:
                Native.ShowWindow(h, Native.SW_HIDE);
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(h, m, w, l);
    }

    public void SetStatus(string s) { _status = s; Native.InvalidateRect(Hwnd, IntPtr.Zero, true); }
    public void ResetLog() { lock (_log) _log.Clear(); KeyDownCount = 0; Native.InvalidateRect(Hwnd, IntPtr.Zero, true); }
}

public static class Dummy
{
    private static DummyWindow? _w;
    private static DummyWindow W => _w ??= Ui.Invoke(() => { var d = new DummyWindow(); d.Create(); return d; });
    public static IntPtr Hwnd => W.Hwnd;
    public static void Show(string status) => Ui.Invoke(() => { W.SetStatus(status); Native.ShowWindow(W.Hwnd, Native.SW_SHOW); Native.SetForegroundWindow(W.Hwnd); });
    public static void Hide() { if (_w != null) Ui.Invoke(() => Native.ShowWindow(W.Hwnd, Native.SW_HIDE)); }
    public static void Reset() => Ui.Invoke(() => W.ResetLog());
    public static (int count, List<string> keys) Received() => Ui.Invoke(() => (W.KeyDownCount, W.ReceivedKeys));
}
