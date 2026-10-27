using System.Text.Json;

namespace GameKouryakuKun.Core.Supervisor;

/// <summary>
/// Supervisor の設定（v0.21.0。src/supervisor-design.md）。ゲームの Profile とは別のファイル（data\supervisor.json）で、攻略の情報は持たない。
/// 攻略 AI（探索・目的・予測）からは参照しない（静的チェックで確かめる）。AI はこのファイルも実行ファイルも変更できない（書く処理はない）。
/// 数値の既定値は「無人の時間を無駄にしない・壊さない」ための汎用の安全な値。
/// </summary>
public sealed record SupervisorConfig
{
    public const string FileName = "supervisor.json";

    /// <summary>起動の方法: none＝Supervisor は起動しない（人が起動し直すのを待つ）・exe＝実行ファイル・steam＝steam://rungameid/{SteamAppId}</summary>
    public string LaunchMethod { get; init; } = "none";
    public string? Executable { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? SteamAppId { get; init; }
    /// <summary>プロセスの見分け（プロセス名。省略時は Profile のウィンドウ条件で見つけたウィンドウのプロセス）</summary>
    public string? ProcessName { get; init; }
    /// <summary>ウィンドウの見分け（省略時は Profile のウィンドウ条件）</summary>
    public string? WindowTitleContains { get; init; }
    public string? WindowClass { get; init; }
    /// <summary>取得・入力の方式の希望（省略時は自動の選択）</summary>
    public string? CaptureBackend { get; init; }
    public string? InputBackend { get; init; }

    public double StartupTimeoutSec { get; init; } = 120;      // 根拠: 汎用（起動に時間のかかるアプリでも待つ）
    public double WindowTimeoutSec { get; init; } = 60;        // 根拠: 汎用（プロセスの起動からウィンドウが出るまで）
    public double CaptureTimeoutSec { get; init; } = 30;       // 根拠: 汎用（取得が始まるまで）
    public double WindowLossGraceSec { get; init; } = 5;       // 根拠: 汎用（一時的なウィンドウの作り直しを故障と呼ばない猶予）
    public double CaptureLossGraceSec { get; init; } = 5;      // 根拠: 汎用（一時的な取得の途切れを故障と呼ばない猶予）
    public double HangSuspectSec { get; init; } = 10;          // 根拠: 汎用（OS が「応答なし」と言い続ける時間。これだけでは終了させない）
    public double ManualRestartWaitSec { get; init; } = 600;   // 根拠: 汎用（起動の方法が none のとき、人が起動し直すのを待つ時間）
    /// <summary>再起動の上限: TimeWindowSec の中で MaxRestarts 回まで。超えたら回路を開いて止め、人を待つ</summary>
    public int MaxRestarts { get; init; } = 3;                 // 根拠: 汎用（同じ失敗の繰り返しで無人の時間を使い切らない）
    public double RestartWindowSec { get; init; } = 1800;      // 根拠: 汎用（30 分）
    /// <summary>再起動の前の待ち（回数ごとに倍、上限まで）</summary>
    public double BackoffInitialSec { get; init; } = 10;       // 根拠: 汎用
    public double BackoffMaxSec { get; init; } = 300;          // 根拠: 汎用（5 分）
    public bool AutoRestart { get; init; } = true;
    /// <summary>変化を見張るファイル（明示したものだけ。推測で増やさない。変化の検出・差分の記録・警告だけで、戻さない）</summary>
    public IReadOnlyList<string> WatchFiles { get; init; } = Array.Empty<string>();

    public string? LoadProblem { get; init; }
    /// <summary>起動の方法の出どころ: supervisor.json（人が書いた）／画面の設定（launch-settings.json）／なし</summary>
    public string LaunchSource { get; init; } = "既定";
    /// <summary>supervisor.json に launch_method が書かれていたか（書かれていれば画面の設定より優先）</summary>
    public bool LaunchMethodInFile { get; init; }

    /// <summary>表示用の起動の方法</summary>
    public string LaunchSummary => LaunchMethod switch
    {
        "exe" => $"exe {Path.GetFileName(Executable ?? "")}{(CanLaunch ? "" : "（未設定）")}（{LaunchSource}）",
        "steam" => $"Steam app {SteamAppId}{(CanLaunch ? "" : "（不正な ID）")}（{LaunchSource}）",
        _ => $"自動では起動しない（{LaunchSource}）",
    };

    public bool CanLaunch => LaunchMethod switch
    {
        "exe" => !string.IsNullOrWhiteSpace(Executable),
        "steam" => !string.IsNullOrWhiteSpace(SteamAppId) && SteamAppId!.All(char.IsDigit),
        _ => false,
    };

    public static SupervisorConfig Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return new SupervisorConfig();
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception e) { return new SupervisorConfig { LoadProblem = e.Message }; }
    }

    public static SupervisorConfig Parse(string json)
    {
        using var d = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var r = d.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double D(string k, double def) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Max(0, v.GetDouble()) : def;
        int I(string k, int def) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Max(0, v.GetInt32()) : def;
        bool B(string k, bool def) => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
        var c = new SupervisorConfig();
        var method = (S("launch_method") ?? "none").ToLowerInvariant();
        if (method is not ("none" or "exe" or "steam")) throw new FormatException($"launch_method は none／exe／steam のどれかです: {method}");
        return new SupervisorConfig
        {
            LaunchMethod = method, LaunchMethodInFile = r.TryGetProperty("launch_method", out var lmv) && lmv.ValueKind == JsonValueKind.String && method != "none", LaunchSource = "supervisor.json", Executable = S("executable"), Arguments = S("arguments"), WorkingDirectory = S("working_directory"), SteamAppId = S("steam_app_id"),
            ProcessName = S("process_name"), WindowTitleContains = S("window_title_contains"), WindowClass = S("window_class"),
            CaptureBackend = S("capture_backend"), InputBackend = S("input_backend"),
            StartupTimeoutSec = D("startup_timeout_sec", c.StartupTimeoutSec), WindowTimeoutSec = D("window_timeout_sec", c.WindowTimeoutSec), CaptureTimeoutSec = D("capture_timeout_sec", c.CaptureTimeoutSec),
            WindowLossGraceSec = D("window_loss_grace_sec", c.WindowLossGraceSec), CaptureLossGraceSec = D("capture_loss_grace_sec", c.CaptureLossGraceSec), HangSuspectSec = D("hang_suspect_sec", c.HangSuspectSec),
            ManualRestartWaitSec = D("manual_restart_wait_sec", c.ManualRestartWaitSec),
            MaxRestarts = I("max_restarts", c.MaxRestarts), RestartWindowSec = D("restart_window_sec", c.RestartWindowSec),
            BackoffInitialSec = D("backoff_initial_sec", c.BackoffInitialSec), BackoffMaxSec = D("backoff_max_sec", c.BackoffMaxSec), AutoRestart = B("auto_restart", c.AutoRestart),
            WatchFiles = r.TryGetProperty("watch_files", out var wf) && wf.ValueKind == JsonValueKind.Array ? wf.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : Array.Empty<string>(),
        };
    }

    /// <summary>設定の見本（初回に data\supervisor.example.json として置く。読み込むのは supervisor.json だけ）</summary>
    public const string Example = """
{
  // Supervisor の設定（ゲームの Profile とは別。攻略の情報は書かない）
  // launch_method: none（自動では起動しない。人が起動し直すのを待つ）／exe／steam
  "launch_method": "none",
  "executable": null,
  "arguments": null,
  "working_directory": null,
  "steam_app_id": null,
  "process_name": null,
  "window_title_contains": null,
  "window_class": null,
  "startup_timeout_sec": 120,
  "window_timeout_sec": 60,
  "capture_timeout_sec": 30,
  "window_loss_grace_sec": 5,
  "capture_loss_grace_sec": 5,
  "hang_suspect_sec": 10,
  "manual_restart_wait_sec": 600,
  "auto_restart": true,
  "max_restarts": 3,
  "restart_window_sec": 1800,
  "backoff_initial_sec": 10,
  "backoff_max_sec": 300,
  // 変化を見張るファイル（明示したものだけ。検出と記録・警告だけで、戻さない）
  "watch_files": []
}
""";
}
