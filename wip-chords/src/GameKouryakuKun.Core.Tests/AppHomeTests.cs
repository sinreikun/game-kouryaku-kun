using GameKouryakuKun.Core;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class AppHomeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "gkk-home");
    private static string P(params string[] p) => Path.Combine(new[] { Root }.Concat(p).ToArray());

    [Fact]
    public void Env_var_from_launcher_wins()
        => Assert.Equal(P("x"), AppHome.Resolve(P("x", "app"), P("x"), _ => false, d => d == P("x")));

    [Fact]
    public void App_folder_under_launcher_uses_parent_so_old_data_folder_is_kept()
        => Assert.Equal(P("x"), AppHome.Resolve(P("x", "app"), null, f => f == P("x", AppHome.LauncherFileName), _ => true));

    [Theory]
    [InlineData(false)]  // 親にランチャーがない
    [InlineData(true)]
    public void Otherwise_exe_folder(bool launcherInParentOfNonAppDir)
        => Assert.Equal(P("x", "bin"), AppHome.Resolve(P("x", "bin"), "", _ => launcherInParentOfNonAppDir, _ => true));

    [Fact]
    public void Missing_env_dir_is_ignored()
        => Assert.Equal(P("y"), AppHome.Resolve(P("y"), P("nope"), _ => false, _ => false));
}
