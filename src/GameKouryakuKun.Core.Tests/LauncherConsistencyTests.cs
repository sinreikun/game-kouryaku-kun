using System.Text.Json;
using GameKouryakuKun.Core;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>ネイティブ起動ツール（src/launcher）と .NET 側・配布スクリプトの取り決めがずれていないか。</summary>
public class LauncherConsistencyTests
{
    private static string Src()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void Launcher_starts_the_published_exes_and_sets_home()
    {
        var cpp = File.ReadAllText(Path.Combine(Src(), "launcher", "win_main.cpp"));
        Assert.Contains("L\"GameKouryakuKun.exe\"", cpp);
        Assert.DoesNotContain("Harness", cpp);   // v0.15.0: 旧ハーネス（既知操作の往復を使う診断）は廃止
        Assert.Contains($"L\"{AppHome.EnvVar}\"", cpp);
        Assert.Contains("L\"DOTNET_ROOT\"", cpp);
        Assert.Contains("L\"DOTNET_MULTILEVEL_LOOKUP\"", cpp);
        Assert.Contains("g_root / \"app\"", cpp);
        var sh = File.ReadAllText(Path.Combine(Src(), "build.sh"));
        Assert.Contains(AppHome.LauncherFileName, sh);
    }

    [Fact]
    public void Runtime_manifest_matches_target_framework_and_is_complete()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Src(), "launcher", "runtime-manifest.json")));
        var comps = doc.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(new[] { "dotnet-runtime", "windowsdesktop-runtime" }, comps.Select(c => c.GetProperty("id").GetString()));
        var csproj = File.ReadAllText(Path.Combine(Src(), "GameKouryakuKun.App", "GameKouryakuKun.App.csproj"));
        Assert.Contains("<SelfContained>false</SelfContained>", csproj);
        foreach (var c in comps)
        {
            var ver = c.GetProperty("version").GetString()!;
            Assert.Contains($"<TargetFramework>net{ver.Split('.')[0]}.0-windows", csproj);
            Assert.Matches("^[0-9a-f]{128}$", c.GetProperty("sha512").GetString());
            Assert.Matches("^[0-9a-f]{64}$", c.GetProperty("sha256").GetString());
            Assert.True(c.GetProperty("size_bytes").GetInt64() > 1_000_000);
            Assert.Equal("MIT", c.GetProperty("license").GetProperty("spdx").GetString());
            foreach (var u in c.GetProperty("urls").EnumerateArray())
                Assert.StartsWith("https://builds.dotnet.microsoft.com/dotnet/", u.GetString());
            Assert.Contains($"/{ver}/", c.GetProperty("urls")[0].GetString());
        }
        // 埋め込み定数（manifest_gen.h）が JSON から再生成されたものか
        var gen = File.ReadAllText(Path.Combine(Src(), "launcher", "manifest_gen.h"));
        foreach (var c in comps) Assert.Contains(c.GetProperty("sha512").GetString()!, gen);
    }
}
