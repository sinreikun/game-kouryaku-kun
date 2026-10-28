using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class RuntimeFlagsTests : IDisposable
{
    private readonly string? _prevEnv;

    public RuntimeFlagsTests()
    {
        _prevEnv = Environment.GetEnvironmentVariable(RuntimeFlags.EnvName);
        Environment.SetEnvironmentVariable(RuntimeFlags.EnvName, null);
        RuntimeFlags.UiEnabled = false;
        RuntimeFlags.UseExperienceVNextWriter = false;
        RuntimeFlags.ApplyEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.EnvName, _prevEnv);
        RuntimeFlags.UiEnabled = false;
        RuntimeFlags.UseExperienceVNextWriter = false;
        RuntimeFlags.ApplyEffective();
    }

    [Fact]
    public void Default_is_off()
    {
        RuntimeFlags.ApplyEffective();
        Assert.False(RuntimeFlags.UseExperienceVNextWriter);
        Assert.Equal("off", RuntimeFlags.EffectiveSource);
        Assert.Contains("OFF", RuntimeFlags.StatusText());
    }

    [Fact]
    public void Ui_on_enables_without_env()
    {
        RuntimeFlags.UiEnabled = true;
        RuntimeFlags.ApplyEffective();
        Assert.True(RuntimeFlags.UseExperienceVNextWriter);
        Assert.Equal("ui", RuntimeFlags.EffectiveSource);
        Assert.Contains("画面のトグル", RuntimeFlags.StatusText());
    }

    [Fact]
    public void Env_forces_on_even_when_ui_off()
    {
        RuntimeFlags.UiEnabled = false;
        Environment.SetEnvironmentVariable(RuntimeFlags.EnvName, "1");
        RuntimeFlags.ApplyEffective();
        Assert.True(RuntimeFlags.UseExperienceVNextWriter);
        Assert.Equal("env", RuntimeFlags.EffectiveSource);
        Assert.Contains("環境変数", RuntimeFlags.StatusText());
    }

    [Fact]
    public void Env_wins_over_ui()
    {
        RuntimeFlags.UiEnabled = true;
        Environment.SetEnvironmentVariable(RuntimeFlags.EnvName, "1");
        RuntimeFlags.ApplyEffective();
        Assert.Equal("env", RuntimeFlags.EffectiveSource);
        Assert.True(RuntimeFlags.UseExperienceVNextWriter);
    }

    [Fact]
    public void LoadUiPreference_roundtrip()
    {
        RuntimeFlags.LoadUiPreference("1");
        Assert.True(RuntimeFlags.UiEnabled);
        Assert.Equal("1", RuntimeFlags.SerializeUiPreference());
        RuntimeFlags.LoadUiPreference("0");
        Assert.False(RuntimeFlags.UiEnabled);
        Assert.Equal("0", RuntimeFlags.SerializeUiPreference());
        Assert.Equal("off", RuntimeFlags.EffectiveSource);
    }

    [Fact]
    public void SettingsKey_and_MainWindow_wire_ui_toggle()
    {
        Assert.Equal("experience_vnext_dual_append", RuntimeFlags.SettingsKey);
        var main = File.ReadAllText(Path.Combine(SrcRoot(), "GameKouryakuKun.App", "MainWindow.xaml.cs"));
        Assert.Contains("BuildVNextDualAppendPanel", main);
        Assert.Contains("Experience vNext dual-append", main);
        Assert.Contains(RuntimeFlags.SettingsKey, main);
        var env = File.ReadAllText(Path.Combine(SrcRoot(), "GameKouryakuKun.App", "AppEnv.cs"));
        Assert.Contains("vnext_dual_append effective=", env);
        Assert.Contains("RuntimeFlags.StatusText()", env);
    }

    private static string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("GameKouryakuKun.sln が見つかりません");
    }
}
