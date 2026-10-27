using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameKouryakuKun.Core.Supervisor;

/// <summary>
/// 画面の「ゲームが消えたら自動で起動し直す」の設定（v0.23.1。data\launch-settings.json）。Supervisor の環境の設定で、攻略の情報は持たない。
/// 書くのは画面（人の操作）と、接続したときに見つけた実行ファイルの記録だけ。攻略 AI（探索・目的・予測）からは参照しない（静的検査）。
/// </summary>
public sealed record LaunchSettings
{
    public const string FileName = "launch-settings.json";
    /// <summary>自動で起動し直す（既定 ON。見つけた実行ファイルがあれば使う）</summary>
    public bool AutoRelaunch { get; init; } = true;
    /// <summary>detected＝接続したゲームの実行ファイル（Steam のゲームと分かれば Steam 経由）・exe＝人が選んだ実行ファイル・steam＝人が書いた Steam app id</summary>
    public string Route { get; init; } = "detected";
    public string? DetectedExe { get; init; }
    public string? DetectedSteamAppId { get; init; }
    public string? DetectedSteamSource { get; init; }
    public string? DetectedAt { get; init; }
    public string? ChosenExe { get; init; }
    public string? SteamAppId { get; init; }
    /// <summary>v0.23.1: 画面で選んだ見張るファイル（supervisor.json の watch_files に足す）</summary>
    public IReadOnlyList<string> WatchFiles { get; init; } = Array.Empty<string>();

    public static LaunchSettings Load(string dir)
    {
        try
        {
            var p = Path.Combine(dir, FileName);
            if (!File.Exists(p)) return new LaunchSettings();
            var o = JsonNode.Parse(File.ReadAllText(p)) as JsonObject;
            if (o == null) return new LaunchSettings();
            string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var x) && !string.IsNullOrWhiteSpace(x) ? x : null;
            var route = S("route") ?? "detected";
            return new LaunchSettings
            {
                AutoRelaunch = o["auto_relaunch"] is JsonValue b && b.TryGetValue<bool>(out var bb) ? bb : true,
                Route = route is "detected" or "exe" or "steam" ? route : "detected",
                DetectedExe = S("detected_exe"), DetectedSteamAppId = S("detected_steam_app_id"), DetectedSteamSource = S("detected_steam_source"), DetectedAt = S("detected_at"),
                ChosenExe = S("chosen_exe"), SteamAppId = S("steam_app_id"),
                WatchFiles = o["watch_files"] is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() : Array.Empty<string>(),
            };
        }
        catch { return new LaunchSettings(); }
    }

    public void Save(string dir)
    {
        var o = new JsonObject
        {
            ["auto_relaunch"] = AutoRelaunch, ["route"] = Route, ["detected_exe"] = DetectedExe, ["detected_steam_app_id"] = DetectedSteamAppId,
            ["detected_steam_source"] = DetectedSteamSource, ["detected_at"] = DetectedAt, ["chosen_exe"] = ChosenExe, ["steam_app_id"] = SteamAppId,
            ["watch_files"] = new JsonArray(WatchFiles.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        };
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, FileName);
        File.WriteAllText(p + ".tmp", o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(p + ".tmp", p, true);
    }

    /// <summary>実際に使う設定: supervisor.json に起動の方法が書かれていればそれ。なければこの画面の設定から作る</summary>
    public SupervisorConfig Apply(SupervisorConfig file0)
    {
        var file = WatchFiles.Count == 0 ? file0 : file0 with { WatchFiles = file0.WatchFiles.Concat(WatchFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
        if (file.LaunchMethodInFile) return file;
        if (!AutoRelaunch) return file with { LaunchMethod = "none", LaunchSource = "画面の設定: 自動で起動し直さない" };
        switch (Route)
        {
            case "steam":
                return file with { LaunchMethod = "steam", SteamAppId = SteamAppId, LaunchSource = "画面の設定: Steam app id" };
            case "exe":
                return file with { LaunchMethod = "exe", Executable = ChosenExe, WorkingDirectory = null, Arguments = null, LaunchSource = "画面の設定: 選んだ実行ファイル" };
            default:
                if (DetectedSteamAppId is string id && id.All(char.IsDigit) && id.Length > 0)
                    return file with { LaunchMethod = "steam", SteamAppId = id, LaunchSource = $"画面の設定: 見つけた実行ファイルは Steam のゲーム（{DetectedSteamSource}）" };
                if (DetectedExe != null)
                    return file with { LaunchMethod = "exe", Executable = DetectedExe, WorkingDirectory = null, Arguments = null, LaunchSource = "画面の設定: 接続したゲームの実行ファイル" };
                return file with { LaunchMethod = "none", LaunchSource = "画面の設定: 実行ファイルをまだ見つけていない" };
        }
    }
}

/// <summary>
/// 起動の経路の手がかり（v0.23.1）: 実行ファイルが Steam のゲームか。読むのは実行ファイルの隣の steam_appid.txt と、
/// Steam ライブラリの appmanifest_*.acf の appid・installdir の 2 項目だけ（起動の経路の情報。ゲームの中身・データは読まない）。
/// </summary>
public static class LaunchRouteDetector
{
    public static (string? AppId, string? Source) Detect(string exePath, Func<string, bool>? exists = null, Func<string, string>? read = null, Func<string, string, IEnumerable<string>>? list = null)
    {
        exists ??= File.Exists; read ??= File.ReadAllText; list ??= (d, pat) => Directory.Exists(d) ? Directory.EnumerateFiles(d, pat) : Enumerable.Empty<string>();
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            // 1) 実行ファイルの隣（と上のフォルダ）の steam_appid.txt
            for (var d = dir; d != null; d = Path.GetDirectoryName(d))
            {
                var f = Path.Combine(d, "steam_appid.txt");
                if (exists(f)) { var t = read(f).Trim(); if (t.Length > 0 && t.Length < 12 && t.All(char.IsDigit)) return (t, "steam_appid.txt"); }
                if (Path.GetFileName(Path.GetDirectoryName(d) ?? "")?.Equals("common", StringComparison.OrdinalIgnoreCase) == true) break;
            }
            // 2) ライブラリの …\common\<installdir>\… にあれば、その 2 つ上のフォルダの appmanifest_*.acf で installdir が同じもの
            for (var d = dir; d != null; d = Path.GetDirectoryName(d))
            {
                var parent = Path.GetDirectoryName(d);
                if (parent == null || !Path.GetFileName(parent).Equals("common", StringComparison.OrdinalIgnoreCase)) continue;
                var install = Path.GetFileName(d); var lib = Path.GetDirectoryName(parent)!;
                foreach (var m in list(lib, "appmanifest_*.acf"))
                {
                    var txt = read(m);
                    if (!string.Equals(Field(txt, "installdir"), install, StringComparison.OrdinalIgnoreCase)) continue;
                    var id = Field(txt, "appid");
                    if (id != null && id.All(char.IsDigit)) return (id, Path.GetFileName(m));
                }
                break;
            }
        }
        catch { }
        return (null, null);
    }

    private static string? Field(string acf, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(acf, "\"" + key + "\"\\s+\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
