using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameKouryakuKun.Core.Supervisor;

namespace GameKouryakuKun.Supervisor;

/// <summary>
/// 権限の境界（v0.21.0。src/supervisor-design.md §権限）: アプリの中でプロセスを起動し、終了を確かめるのはこの型だけ。
///  - 起動できるのは Supervisor の設定（data\supervisor.json）、または画面の「自動で起動し直す」の設定（data\launch-settings.json。接続したゲームの実行ファイル・人が選んだ実行ファイル）の実行ファイル、または steam://rungameid/{数字} だけ。任意のコマンド・シェルは起動しない。
///  - v0.23.1: 実行ファイルの場所だけを OS に尋ねる（限られた情報の問い合わせの権限だけで開き、すぐ閉じる）。
///  - 確かめるのは「生きているか」と「終了コード」だけ。メモリ・モジュール・スレッド・ハンドルは読まない。終了させる（Kill）ことはしない。
///  - 攻略 AI の操作（中立のキー入力）からは呼べない（Core の探索・目的はこのプロジェクトを参照しない。静的に検査する）。
/// </summary>
public sealed class ProcessControl : IDisposable
{
    private Process? _p;
    public int? Pid { get; private set; }

    /// <summary>ウィンドウから分かったプロセス ID を見張る（終了の検出だけ）</summary>
    public bool Attach(int pid)
    {
        Detach();
        try
        {
            _p = Process.GetProcessById(pid); Pid = pid;
            // v0.22.1: 終了の知らせを受け取る設定にして、見張りの間ハンドルを保つ（実機 v0.21.0 で終了の後に終了コードが取れず unknown だった）
            try { _p.EnableRaisingEvents = true; } catch { }
            return true;
        }
        catch { _p = null; Pid = null; return false; }
    }

    public void Detach() { _p?.Dispose(); _p = null; Pid = null; }

    /// <summary>Alive＝生きている・Exited＝終了した（終了コードが取れれば返す）・Unknown＝確かめられない</summary>
    public ProcessCheck Check(out int? exitCode)
    {
        exitCode = null;
        if (_p == null) return ProcessCheck.Unknown;
        try
        {
            _p.Refresh();
            if (!_p.HasExited) return ProcessCheck.Alive;
            try { exitCode = _p.ExitCode; } catch { exitCode = null; }
            return ProcessCheck.Exited;
        }
        catch { return ProcessCheck.Unknown; }
    }

    /// <summary>設定の方法で起動する。起動の方法・実行ファイルが設定にないときは何もしない</summary>
    public static bool Launch(SupervisorConfig c, out string detail) => Launch(c, out detail, out _);

    /// <summary>pid: 実行ファイルを直接起動したときの新しいプロセス（Steam 経由は null。ウィンドウが出てからそのプロセスを見張る）</summary>
    public static bool Launch(SupervisorConfig c, out string detail, out int? pid)
    {
        pid = null;
        try
        {
            ProcessStartInfo psi;
            if (c.LaunchMethod == "exe" && c.CanLaunch)
            {
                if (!File.Exists(c.Executable)) { detail = "実行ファイルがありません: " + c.Executable; return false; }
                psi = new ProcessStartInfo(c.Executable!) { Arguments = c.Arguments ?? "", UseShellExecute = false, WorkingDirectory = c.WorkingDirectory ?? Path.GetDirectoryName(c.Executable!) ?? "" };
                detail = $"exe: {Path.GetFileName(c.Executable)}";
            }
            else if (c.LaunchMethod == "steam" && c.CanLaunch)
            {
                psi = new ProcessStartInfo("steam://rungameid/" + c.SteamAppId) { UseShellExecute = true };
                detail = "steam: rungameid/" + c.SteamAppId;
            }
            else { detail = "起動の方法が設定されていません"; return false; }
            using var p = Process.Start(psi);
            if (c.LaunchMethod == "exe") try { pid = p?.Id; } catch { pid = null; }
            return true;
        }
        catch (Exception e) { detail = "起動に失敗: " + e.Message; return false; }
    }

    // v0.23.1: 起動し直しのための実行ファイルの場所だけを OS に尋ねる（環境の設定。ゲームの中身ではない）。
    // 開く権限は「限られた情報の問い合わせ」（PROCESS_QUERY_LIMITED_INFORMATION）だけ。メモリ・モジュール・スレッドは読まない。終了させない。
    private const uint QueryLimitedInformationOnly = 0x1000;
    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)] private static extern IntPtr OpenLimited(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryImageName(IntPtr h, uint flags, StringBuilder buf, ref int size);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] private static extern bool CloseH(IntPtr h);

    /// <summary>プロセスの実行ファイルの場所（取れなければ null）。引数・作業フォルダは他のプロセスのメモリを読まないと取れないので取らない</summary>
    public static string? ExecutablePathOf(int pid)
    {
        if (pid <= 0) return null;
        IntPtr h = OpenLimited(QueryLimitedInformationOnly, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryImageName(h, 0, sb, ref n) && n > 0 ? sb.ToString(0, n) : null;
        }
        finally { CloseH(h); }
    }

    public void Dispose() => Detach();
}
