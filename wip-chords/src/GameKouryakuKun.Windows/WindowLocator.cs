using System.Diagnostics;
using System.Text;
using GameKouryakuKun.Core.Profile;

namespace GameKouryakuKun.Windows;

public sealed record WindowInfo(nint Hwnd, uint Pid, string ProcessName, string Title, string ClassName, string Rect, bool Minimized)
{
    public string HwndHex => $"0x{Hwnd:X}";
    public override string ToString() => $"{HwndHex} pid={Pid} {ProcessName} [{ClassName}] \"{Title}\" {Rect}{(Minimized ? " (最小化中)" : "")}";
}

/// <summary>
/// トップレベルウィンドウの列挙と Profile 条件での特定。
/// 参照するのはウィンドウのタイトル・クラス名と、OS のプロセス一覧にある実行ファイル名だけ（プロセスを開かない・メモリを読まない）。
/// </summary>
public static class WindowLocator
{
    /// <summary>PID → プロセス名（OS のプロセス一覧スナップショットから。プロセスハンドルは開かない）</summary>
    public static Dictionary<uint, string> ProcessNames()
    {
        var d = new Dictionary<uint, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { d[(uint)p.Id] = p.ProcessName; } catch { }
            finally { p.Dispose(); }
        }
        return d;
    }

    public static List<WindowInfo> List(bool includeUntitled = false)
    {
        var names = ProcessNames();
        var list = new List<WindowInfo>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            var info = Describe(h, names);
            if (info.Title.Length == 0 && !includeUntitled) return true;
            list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo Describe(nint h, Dictionary<uint, string>? names = null)
    {
        int len = Native.GetWindowTextLengthW(h);
        var sb = new StringBuilder(Math.Max(len + 1, 2));
        Native.GetWindowTextW(h, sb, sb.Capacity);
        var cls = new StringBuilder(256); Native.GetClassNameW(h, cls, 256);
        Native.GetWindowThreadProcessId(h, out uint pid);
        names ??= ProcessNames();
        Native.GetWindowRect(h, out var r);
        return new WindowInfo(h, pid, names.TryGetValue(pid, out var n) ? n : "?", sb.ToString(), cls.ToString(), r.ToString(), Native.IsIconic(h));
    }

    public static List<WindowInfo> Find(WindowMatch match) =>
        List(includeUntitled: true).Where(w => match.Matches(w.ProcessName, w.ClassName, w.Title)).ToList();

    public static bool IsAlive(nint h) => Native.IsWindow(h);
    public static bool IsMinimized(nint h) => Native.IsIconic(h);
    public static bool IsCloaked(nint h) => Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;
    public static bool IsForeground(nint h) => Native.GetForegroundWindow() == h;
    public static nint Foreground() => Native.GetForegroundWindow();
}
