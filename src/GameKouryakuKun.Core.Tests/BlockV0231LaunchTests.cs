using GameKouryakuKun.Core.Supervisor;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.23.1: 手で JSON を書かずに自動で起動し直す</summary>
public class BlockV0231LaunchTests
{
    private static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "gkk-ls-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Default_is_auto_relaunch_of_detected_exe()
    {
        var c = new LaunchSettings { DetectedExe = "/g/Game.exe" }.Apply(new SupervisorConfig());
        Assert.Equal("exe", c.LaunchMethod); Assert.Equal("/g/Game.exe", c.Executable); Assert.True(c.CanLaunch);
        Assert.Contains("Game.exe", c.LaunchSummary);
    }

    [Fact]
    public void Nothing_detected_means_none()
    {
        var c = new LaunchSettings().Apply(new SupervisorConfig());
        Assert.Equal("none", c.LaunchMethod); Assert.False(c.CanLaunch);
    }

    [Fact]
    public void Toggle_off_disables_relaunch()
    {
        var c = new LaunchSettings { AutoRelaunch = false, DetectedExe = "/g/Game.exe" }.Apply(new SupervisorConfig());
        Assert.Equal("none", c.LaunchMethod);
    }

    [Fact]
    public void Detected_steam_game_goes_through_steam()
    {
        var c = new LaunchSettings { DetectedExe = "/g/Game.exe", DetectedSteamAppId = "123450", DetectedSteamSource = "steam_appid.txt" }.Apply(new SupervisorConfig());
        Assert.Equal("steam", c.LaunchMethod); Assert.Equal("123450", c.SteamAppId); Assert.True(c.CanLaunch);
    }

    [Fact]
    public void Chosen_exe_and_steam_id_routes()
    {
        Assert.Equal("/x/a.exe", new LaunchSettings { Route = "exe", ChosenExe = "/x/a.exe", DetectedExe = "/g/b.exe" }.Apply(new SupervisorConfig()).Executable);
        var s = new LaunchSettings { Route = "steam", SteamAppId = "42" }.Apply(new SupervisorConfig());
        Assert.Equal("steam", s.LaunchMethod); Assert.True(s.CanLaunch);
        Assert.False(new LaunchSettings { Route = "steam", SteamAppId = "4a" }.Apply(new SupervisorConfig()).CanLaunch);
    }

    [Fact]
    public void Hand_written_supervisor_json_wins()
    {
        var file = SupervisorConfig.Parse("""{ "launch_method": "steam", "steam_app_id": "7" }""");
        var c = new LaunchSettings { DetectedExe = "/g/Game.exe" }.Apply(file);
        Assert.Equal("steam", c.LaunchMethod); Assert.Equal("supervisor.json", c.LaunchSource);
        // launch_method none は書かれていても画面の設定を使う（既定のままのファイルで自動の起動を塞がない）
        var c2 = new LaunchSettings { DetectedExe = "/g/Game.exe" }.Apply(SupervisorConfig.Parse("""{ "launch_method": "none" }"""));
        Assert.Equal("exe", c2.LaunchMethod);
    }

    [Fact]
    public void Settings_persist()
    {
        var d = Tmp();
        new LaunchSettings { AutoRelaunch = false, Route = "steam", SteamAppId = "99", DetectedExe = "/g/G.exe" }.Save(d);
        var l = LaunchSettings.Load(d);
        Assert.False(l.AutoRelaunch); Assert.Equal("steam", l.Route); Assert.Equal("99", l.SteamAppId); Assert.Equal("/g/G.exe", l.DetectedExe);
        Assert.True(LaunchSettings.Load(Tmp()).AutoRelaunch);
    }

    [Fact]
    public void Steam_route_detection_reads_only_appid_file_or_manifest()
    {
        var root = Tmp();
        var game = Path.Combine(root, "lib", "common", "My Game", "bin"); Directory.CreateDirectory(game);
        var exe = Path.Combine(game, "g.exe"); File.WriteAllText(exe, "");
        Assert.Equal((null, null), LaunchRouteDetector.Detect(exe));
        File.WriteAllText(Path.Combine(root, "lib", "appmanifest_555.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"555\"\n\t\"installdir\"\t\t\"My Game\"\n}");
        Assert.Equal(("555", "appmanifest_555.acf"), LaunchRouteDetector.Detect(exe));
        File.WriteAllText(Path.Combine(game, "steam_appid.txt"), "777\n");
        Assert.Equal(("777", "steam_appid.txt"), LaunchRouteDetector.Detect(exe));
    }

    /// <summary>起動は ProcessControl.cs だけ。攻略 AI は起動の設定を参照・変更できない</summary>
    [Fact]
    public void Launch_settings_are_not_reachable_from_ai_code()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GameKouryakuKun.sln"))) root = root.Parent;
        var core = Path.Combine(root!.FullName, "GameKouryakuKun.Core");
        foreach (var dd in new[] { "Exploration", "Goals", "Turn", "Perception", "Growth", "Boundaries" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(core, dd), "*.cs", SearchOption.AllDirectories))
            {
                var t = File.ReadAllText(f);
                Assert.DoesNotContain("LaunchSettings", t); Assert.DoesNotContain("ProcessControl", t); Assert.DoesNotContain("launch-settings", t);
            }
        // 書くのは Core の型（Save）と、それを呼ぶ画面・Session だけ
        foreach (var f in Directory.EnumerateFiles(root.FullName, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains("Tests") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var t = File.ReadAllText(f);
            if (t.Contains(").Save(_env.DataRoot)") || t.Contains(".Save(dataRoot)") || t.Contains("ls.Save("))
                Assert.Contains(Path.GetFileName(f), new[] { "MainWindow.xaml.cs", "Session.cs" });
        }
    }
}

public class BlockV0231WatchUiTests
{
    [Fact]
    public void Ui_watch_files_are_used_without_editing_supervisor_json()
    {
        var c = new LaunchSettings { WatchFiles = new[] { "/a/settings.ini" } }.Apply(new SupervisorConfig());
        Assert.Contains("/a/settings.ini", c.WatchFiles);
        var d = Path.Combine(Path.GetTempPath(), "gkk-w-" + Guid.NewGuid().ToString("N"));
        new LaunchSettings { WatchFiles = new[] { "/a/x" } }.Save(d);
        Assert.Equal(new[] { "/a/x" }, LaunchSettings.Load(d).WatchFiles);
    }
}
