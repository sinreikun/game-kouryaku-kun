using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GameKouryakuKun.Windows;

/// <summary>素の Win32 ウィンドウ作成（WinForms/WPF 非依存＝exe を小さく）。</summary>
internal static class Win32Window
{
    private static readonly List<Native.WndProc> Keep = new();
    private static int _seq;

    public static IntPtr Create(string title, uint style, int x, int y, int w, int h, Native.WndProc proc, uint bgColor = 0x00302218)
    {
        lock (Keep) Keep.Add(proc);
        string cls = $"GKK_{Environment.ProcessId}_{Interlocked.Increment(ref _seq)}";
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

/// <summary>メッセージループを持つ専用スレッド。Invoke でそのスレッド上の処理を同期実行する。</summary>
public sealed class MessageThread : IDisposable
{
    private readonly ConcurrentQueue<Action> _q = new();
    private IntPtr _hwnd;
    private Thread? _thread;
    private int _threadId;
    private Native.WndProc? _proc;
    public string Name { get; }
    /// <summary>このスレッドのウィンドウに来たメッセージを横取りしたいとき（WM_HOTKEY 等）</summary>
    internal Func<uint, IntPtr, IntPtr, bool>? Hook { get; set; }
    internal IntPtr Hwnd => _hwnd;

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
                if (Hook != null && Hook(m, w, l)) return IntPtr.Zero;
                return Native.DefWindowProcW(h, m, w, l);
            };
            _hwnd = Win32Window.Create($"GKK-{Name}", 0, 0, 0, 0, 0, _proc);
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

    public void Dispose() { try { Invoke(() => Native.PostQuitMessage(0)); } catch { } }
}
