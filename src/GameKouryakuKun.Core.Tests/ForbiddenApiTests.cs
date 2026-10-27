using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// 禁止API（ゲームプロセスのメモリ読取・プロセスハンドル取得・DLL注入・フック・キー状態のポーリング・ゲームファイル解析等）を
/// 使っていないことを2段階で検査する。
///  (1) ソース全体の文字列検査
///  (2) ビルド済み DLL のメタデータ検査: 全 P/Invoke を許可リストと照合し、Process の危険なメンバー参照も検出
/// </summary>
public class ForbiddenApiTests
{
    /// <summary>名前に含まれていたら不合格（P/Invoke 名・ソース中の識別子）</summary>
    public static readonly string[] DeniedApis =
    {
        "ReadProcessMemory", "WriteProcessMemory", "OpenProcess", "OpenThread", "VirtualAllocEx", "VirtualProtectEx", "VirtualQueryEx",
        "CreateRemoteThread", "NtReadVirtualMemory", "ZwReadVirtualMemory", "NtWriteVirtualMemory", "NtOpenProcess", "NtQueryInformationProcess",
        "DebugActiveProcess", "MiniDumpWriteDump", "CreateToolhelp32Snapshot", "Module32First", "Module32Next", "EnumProcessModules",
        "SetWindowsHookEx", "LoadLibrary", "GetProcAddress", "GetAsyncKeyState", "QueueUserAPC", "SetThreadContext", "GetThreadContext",
    };

    /// <summary>ソース中に現れたら不合格（ゲームファイル・インストール先・レジストリへのアクセスの兆候）</summary>
    private static readonly string[] DeniedSourcePatterns =
    {
        @"\bSteamApps\b", @"\.pak\b", @"\.uasset\b", @"SaveGames", @"\bRegistry\.", @"\.MainModule\b", @"\.Modules\b", @"GetProcessById",
        @"\bProcessModule\b", @"Process\.Start\(", @"GetProcessesByName", @"\.Kill\(",
    };

    /// <summary>権限の境界（v0.21.0 Game Supervisor）: プロセスの起動（Process.Start）と終了の確認（GetProcessById）はこのファイルだけに許す。
    /// Kill・ハンドル・モジュール・スレッドはここでも禁止のまま</summary>
    private const string ProcessBoundaryFile = "ProcessControl.cs";
    private static readonly string[] BoundaryAllowedPatterns = { @"GetProcessById", @"Process\.Start\(" };
    /// <summary>v0.23.1: 実行ファイルの場所だけを尋ねるため、境界のファイルにだけ OpenProcess（限られた情報の問い合わせの権限だけ）を許す</summary>
    private static readonly string[] BoundaryAllowedApis = { "OpenProcess" };
    private static readonly HashSet<string> BoundaryAllowedPInvokes = new(StringComparer.OrdinalIgnoreCase) { "kernel32!OpenProcess", "kernel32!QueryFullProcessImageNameW", "kernel32!CloseHandle" };

    /// <summary>v0.23.1: 境界のファイルの OpenProcess は PROCESS_QUERY_LIMITED_INFORMATION（0x1000）の定数だけで開く</summary>
    [Fact]
    public void Boundary_opens_process_only_with_limited_query_rights()
    {
        var f = ProductSources().Single(IsBoundary);
        var t = File.ReadAllText(f);
        Assert.Contains("private const uint QueryLimitedInformationOnly = 0x1000;", t);
        var calls = Regex.Matches(t, @"OpenLimited\(([^,]+),").Select(m => m.Groups[1].Value.Trim()).Where(a => a != "uint access").ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, a => Assert.Equal("QueryLimitedInformationOnly", a));
        Assert.DoesNotContain("ReadProcessMemory", t);
    }

    /// <summary>許可する P/Invoke（DLL名!関数名）。追加するときはレビューのうえでここに足す</summary>
    private static readonly HashSet<string> AllowedPInvokes = new(StringComparer.OrdinalIgnoreCase)
    {
        "user32!EnumWindows", "user32!GetWindowTextW", "user32!GetWindowTextLengthW", "user32!GetClassNameW", "user32!IsWindowVisible",
        "user32!IsWindow", "user32!IsIconic", "user32!GetWindowThreadProcessId", "user32!GetWindowRect", "user32!GetClientRect",
        "user32!GetForegroundWindow", "user32!SetForegroundWindow", "user32!BringWindowToTop", "user32!ShowWindow", "user32!GetWindow",
        "user32!GetWindowLongPtrW", "user32!AttachThreadInput", "user32!SetProcessDpiAwarenessContext", "user32!GetKeyState",
        "user32!RegisterClassExW", "user32!CreateWindowExW", "user32!DefWindowProcW", "user32!GetMessageW", "user32!TranslateMessage",
        "user32!DispatchMessageW", "user32!PostQuitMessage", "user32!DestroyWindow", "user32!InvalidateRect", "user32!BeginPaint",
        "user32!EndPaint", "user32!DrawTextW", "user32!LoadCursorW", "user32!RegisterHotKey", "user32!UnregisterHotKey",
        "user32!PrintWindow", "user32!GetDC", "user32!ReleaseDC", "user32!PostMessageW", "user32!SendInput",
        "user32!RegisterRawInputDevices", "user32!GetRawInputData", "user32!ClientToScreen",
        "user32!IsHungAppWindow", "user32!SendMessageTimeoutW",  // v0.21.0 Supervisor: 応答の確認だけ（WM_NULL）
        "kernel32!GetCurrentThreadId", "kernel32!GetConsoleWindow", "kernel32!GetModuleHandleW",
        "dwmapi!DwmGetWindowAttribute",
        "gdi32!SetBkMode", "gdi32!SetTextColor", "gdi32!CreateSolidBrush", "gdi32!CreateFontW", "gdi32!CreateCompatibleDC",
        "gdi32!DeleteDC", "gdi32!SelectObject", "gdi32!DeleteObject", "gdi32!CreateDIBSection",
        "winmm!timeBeginPeriod", "winmm!timeEndPeriod",
        "d3d11!D3D11CreateDevice", "d3d11!CreateDirect3D11DeviceFromDXGIDevice",
        "combase!WindowsCreateString", "combase!WindowsDeleteString", "combase!RoGetActivationFactory",
    };

    /// <summary>System.Diagnostics.Process の禁止メンバー（ハンドル取得やモジュール列挙はプロセスを開くため）</summary>
    private static readonly HashSet<string> DeniedProcessMembers = new() { "get_Handle", "get_SafeHandle", "get_MainModule", "get_Modules", "GetProcessById", "get_Threads", "Start", "Kill", "get_StandardOutput" };

    private static string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("GameKouryakuKun.sln が見つかりません");
    }

    private static IEnumerable<string> ProductSources() =>
        Directory.EnumerateFiles(SrcRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains("GameKouryakuKun.Core.Tests"));

    [Fact]
    public void Source_does_not_mention_denied_apis_or_game_file_access()
    {
        var files = ProductSources().ToList();
        Assert.True(files.Count >= 15, $"検査対象のソースが少なすぎます: {files.Count}");
        var hits = new List<string>();
        foreach (var f in files)
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var api in DeniedApis) if (lines[i].Contains(api, StringComparison.Ordinal) && !(IsBoundary(f) && BoundaryAllowedApis.Contains(api))) hits.Add($"{Path.GetFileName(f)}:{i + 1}: {api}");
                foreach (var pat in DeniedSourcePatterns)
                    if (!(IsBoundary(f) && BoundaryAllowedPatterns.Contains(pat)) && Regex.IsMatch(lines[i], pat)) hits.Add($"{Path.GetFileName(f)}:{i + 1}: /{pat}/");
            }
        }
        Assert.True(hits.Count == 0, "禁止APIの記述が見つかりました:\n" + string.Join("\n", hits));
    }

    private static bool IsBoundary(string f) => Path.GetFileName(f) == ProcessBoundaryFile && f.Contains($"{Path.DirectorySeparatorChar}GameKouryakuKun.Supervisor{Path.DirectorySeparatorChar}");

    /// <summary>権限の境界のファイルは 1 つだけで、Supervisor のプロジェクトにある</summary>
    [Fact]
    public void Process_boundary_is_a_single_file_in_supervisor_project()
    {
        var files = ProductSources().Where(f => Path.GetFileName(f) == ProcessBoundaryFile).ToList();
        Assert.Single(files);
        Assert.True(IsBoundary(files[0]));
        var text = File.ReadAllText(files[0]);
        Assert.DoesNotContain(".Kill(", text);
        Assert.Contains("steam://rungameid/", text);
    }

    /// <summary>攻略 AI（探索・目的・予測・並び）は Supervisor を参照しない（再起動・起動・設定の意味を学ばない）</summary>
    [Fact]
    public void Ai_code_does_not_reference_supervisor()
    {
        var core = Path.Combine(SrcRoot(), "GameKouryakuKun.Core");
        var aiDirs = new[] { "Exploration", "Goals", "Turn", "Model", "Prediction", "Learning" }.Select(d => Path.Combine(core, d)).Where(Directory.Exists).ToList();
        Assert.NotEmpty(aiDirs);
        var hits = aiDirs.SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories)).Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bSupervisor\b|SystemEvent")).ToList();
        Assert.True(hits.Count == 0, "攻略 AI のコードが Supervisor を参照しています: " + string.Join(", ", hits));
        var csproj = File.ReadAllText(Path.Combine(core, "GameKouryakuKun.Core.csproj"));
        Assert.DoesNotContain("GameKouryakuKun.Supervisor", csproj);
    }

    [Fact]
    public void DllImport_appears_only_in_Native_cs()
    {
        var re = new Regex(@"\bDllImport(Attribute)?\b|\bLibraryImport");
        // v0.23.1: 権限の境界（ProcessControl.cs）だけは、実行ファイルの場所を尋ねる 3 つの宣言を持つ（BoundaryAllowedPInvokes。DLL の検査で数も確かめる）
        var hits = ProductSources().Where(f => Path.GetFileName(f) != "Native.cs" && !IsBoundary(f) && re.IsMatch(File.ReadAllText(f))).ToList();
        Assert.True(hits.Count == 0, "Native.cs 以外に P/Invoke 宣言があります: " + string.Join(", ", hits));
    }

    /// <summary>ネイティブライブラリの動的読込は OrtNative.cs（ONNX Runtime を導入先から読む）だけ</summary>
    [Fact]
    public void NativeLibrary_is_used_only_in_OrtNative_cs()
    {
        var hits = ProductSources().Where(f => Path.GetFileName(f) != "OrtNative.cs" && Regex.IsMatch(File.ReadAllText(f), @"\bNativeLibrary\b|\bMarshal\.GetDelegateForFunctionPointer")).ToList();
        Assert.True(hits.Count == 0, "OrtNative.cs 以外でネイティブライブラリを読み込んでいます: " + string.Join(", ", hits));
    }

    /// <summary>ネットワーク通信はセットアップマネージャ（GameKouryakuKun.Setup）だけ</summary>
    [Fact]
    public void Network_access_is_confined_to_setup_project()
    {
        var re = new Regex(@"\b(HttpClient|HttpWebRequest|WebClient|TcpClient|UdpClient|Socket|SocketsHttpHandler|WebSocket|ClientWebSocket)\b");
        var hits = ProductSources().Where(f => !f.Contains($"{Path.DirectorySeparatorChar}GameKouryakuKun.Setup{Path.DirectorySeparatorChar}") && re.IsMatch(File.ReadAllText(f))).ToList();
        Assert.True(hits.Count == 0, "Setup 以外に通信コードがあります: " + string.Join(", ", hits));
    }

    /// <summary>OCR はキャプチャ画像（Frame）だけを入力にする: IOcrEngine の入力型を固定</summary>
    [Fact]
    public void Ocr_engines_take_only_captured_frames()
    {
        var m = typeof(GameKouryakuKun.Core.Ocr.IOcrEngine).GetMethod("RecognizeAsync")!;
        Assert.Equal(typeof(GameKouryakuKun.Core.Imaging.Frame), m.GetParameters()[0].ParameterType);
    }

    public static IEnumerable<object[]> ProductAssemblies()
    {
        var root = SrcRoot();
        foreach (var (proj, name) in new[] { ("GameKouryakuKun.Core", "GameKouryakuKun.Core.dll"), ("GameKouryakuKun.Windows", "GameKouryakuKun.Windows.dll"),
                                           ("GameKouryakuKun.Storage", "GameKouryakuKun.Storage.dll"), ("GameKouryakuKun.Setup", "GameKouryakuKun.Setup.dll"),
                                           ("GameKouryakuKun.Ocr", "GameKouryakuKun.Ocr.dll"), ("GameKouryakuKun.App", "GameKouryakuKun.dll"), ("GameKouryakuKun.Supervisor", "GameKouryakuKun.Supervisor.dll") })
            yield return new object[] { proj, name };
    }

    private static string FindBuilt(string proj, string name)
    {
        var dir = Path.Combine(SrcRoot(), proj, "bin");
        var f = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        return f ?? throw new FileNotFoundException($"{proj} のビルド出力 {name} が見つかりません（dotnet build を先に実行してください）");
    }

    [Theory]
    [MemberData(nameof(ProductAssemblies))]
    public void Compiled_assembly_uses_only_allowed_pinvokes_and_no_process_handles(string proj, string dllName)
    {
        var path = FindBuilt(proj, dllName);
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var problems = new List<string>();
        int pinvokes = 0;
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            var imp = m.GetImport();
            if (imp.Module.IsNil) continue;
            pinvokes++;
            string module = md.GetString(md.GetModuleReference(imp.Module).Name).ToLowerInvariant().Replace(".dll", "");
            string entry = imp.Name.IsNil ? md.GetString(m.Name) : md.GetString(imp.Name);
            string key = $"{module}!{entry}";
            if (proj == "GameKouryakuKun.Supervisor" && BoundaryAllowedPInvokes.Contains(key)) continue;
            if (DeniedApis.Any(d => entry.Contains(d, StringComparison.OrdinalIgnoreCase))) problems.Add("禁止API: " + key);
            else if (!AllowedPInvokes.Contains(key)) problems.Add("許可リストにない P/Invoke: " + key);
        }
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            string ns = md.GetString(tr.Namespace), tn = md.GetString(tr.Name), mn = md.GetString(mr.Name);
            if (ns == "System.Diagnostics" && tn == "Process" && (mn == "Kill" || (DeniedProcessMembers.Contains(mn) && !(proj == "GameKouryakuKun.Supervisor" && mn is "Start" or "GetProcessById")) || (proj != "GameKouryakuKun.Supervisor" && mn is "GetProcessesByName" or "get_ExitCode" or "get_HasExited"))) problems.Add($"禁止メンバー: Process.{mn}");
            if (ns == "System.Runtime.InteropServices" && tn == "NativeLibrary" && proj != "GameKouryakuKun.Ocr") problems.Add("NativeLibrary の使用: " + mn);
            if (ns == "System.Runtime.InteropServices" && tn == "NativeLibrary" && mn is not ("TryLoad" or "SetDllImportResolver")) problems.Add("NativeLibrary の想定外メンバー: " + mn);
        }
        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            string full = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
            if (proj != "GameKouryakuKun.Setup" && (full.StartsWith("System.Net.Http.", StringComparison.Ordinal) || full.StartsWith("System.Net.Sockets.", StringComparison.Ordinal) || full == "System.Net.WebClient"))
                problems.Add("Setup 以外の通信型の参照: " + full);
            if (full is "System.Diagnostics.ProcessModule" or "System.Diagnostics.ProcessThread" or "Microsoft.Win32.Registry" or "Microsoft.Win32.RegistryKey")
                problems.Add("禁止型の参照: " + full);
        }
        Assert.True(problems.Count == 0, $"{dllName}:\n" + string.Join("\n", problems));
        if (proj == "GameKouryakuKun.Windows") Assert.True(pinvokes > 30, "Windows 実装の P/Invoke が検出できていません");
        if (proj is "GameKouryakuKun.Core" or "GameKouryakuKun.Storage" or "GameKouryakuKun.Setup" or "GameKouryakuKun.Ocr") Assert.Equal(0, pinvokes);
        if (proj == "GameKouryakuKun.Supervisor") Assert.Equal(BoundaryAllowedPInvokes.Count, pinvokes);   // v0.23.1: 実行ファイルの場所の問い合わせの 3 つだけ
    }
}
