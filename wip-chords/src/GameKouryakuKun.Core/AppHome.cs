namespace GameKouryakuKun.Core;

/// <summary>
/// アプリの「ホーム」（data\ を置く基準フォルダ）の決定。
/// 配布形態: {ホーム}\ゲーム攻略くん.exe（ランチャー）／{ホーム}\app\GameKouryakuKun.exe（本体）／{ホーム}\runtime\（.NET）／{ホーム}\data\。
/// ランチャーは環境変数 GKK_HOME にホームを渡す。直接 app\ の exe を起動した場合も、親にランチャーがあれば親をホームにする
/// （旧版の「exe の隣の data\」をそのまま引き継ぐため）。
/// </summary>
public static class AppHome
{
    public const string EnvVar = "GKK_HOME";
    public const string LauncherFileName = "ゲーム攻略くん.exe";

    public static string Resolve(string exeDir, string? envHome, Func<string, bool> fileExists, Func<string, bool> dirExists)
    {
        if (!string.IsNullOrWhiteSpace(envHome) && dirExists(envHome)) return Path.GetFullPath(envHome);
        var full = Path.GetFullPath(exeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(full);
        if (parent != null && string.Equals(Path.GetFileName(full), "app", StringComparison.OrdinalIgnoreCase)
            && fileExists(Path.Combine(parent, LauncherFileName)))
            return parent;
        return full;
    }
}
