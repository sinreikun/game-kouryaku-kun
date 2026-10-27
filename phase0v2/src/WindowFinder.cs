using System.Diagnostics;
using System.Text;

namespace GameKouryakuKun.Phase0;

public sealed record WindowInfo(IntPtr Hwnd, uint Pid, string ProcessName, string Title, string ClassName, string Rect, bool Minimized)
{
    public string HwndHex => $"0x{Hwnd.ToInt64():X}";
    public override string ToString() => $"{HwndHex,-12} pid={Pid,-6} {ProcessName,-24} [{ClassName}] \"{Title}\" {Rect}{(Minimized ? " (最小化中)" : "")}";
}

/// <summary>トップレベルウィンドウの列挙と選択。ウィンドウタイトル・クラス名・プロセス名のみを参照する（メモリは読まない）。</summary>
public static class WindowFinder
{
    public static List<WindowInfo> List(bool includeUntitled = false)
    {
        var list = new List<WindowInfo>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var info = Describe(h);
            if (info.Title.Length == 0 && !includeUntitled) return true;
            list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo Describe(IntPtr h)
    {
        int len = Native.GetWindowTextLengthW(h);
        var sb = new StringBuilder(Math.Max(len + 1, 2));
        Native.GetWindowTextW(h, sb, sb.Capacity);
        var cls = new StringBuilder(256); Native.GetClassNameW(h, cls, 256);
        Native.GetWindowThreadProcessId(h, out uint pid);
        string pname = "?";
        try { using var p = Process.GetProcessById((int)pid); pname = p.ProcessName; } catch { }
        Native.GetWindowRect(h, out var r);
        return new WindowInfo(h, pid, pname, sb.ToString(), cls.ToString(), r.ToString(), Native.IsIconic(h));
    }

    /// <summary>タイトル部分一致・プロセス名一致（大文字小文字無視、.exe省略可）・HWND(0x..)で検索</summary>
    public static List<WindowInfo> Find(string query)
    {
        query = query.Trim();
        var all = List();
        if (query.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(query[2..], System.Globalization.NumberStyles.HexNumber, null, out long hv))
            return Native.IsWindow(new IntPtr(hv)) ? new() { Describe(new IntPtr(hv)) } : new();
        string q = query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? query[..^4] : query;
        var byProc = all.Where(w => w.ProcessName.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var byTitle = all.Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return byProc.Concat(byTitle).DistinctBy(w => w.Hwnd).ToList();
    }
}
