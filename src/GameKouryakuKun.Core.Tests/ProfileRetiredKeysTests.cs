using GameKouryakuKun.Core.Profile;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.15.1: 以前の版の Profile（廃止した diagnostics 欄つき）が起動を止めないこと</summary>
public class ProfileRetiredKeysTests
{
    private const string Base = """
    // 古い Profile（コメントつき）
    { "schema_version": 2, "profile_id": "p1", "profile_version": "1.0.0", "display_name": "x",
      "window": { "process_name": "a.exe" },
      "input": { "default_hold_ms": 60, "min_interval_ms": 100, "keys": { "button1": { "key": "Z" } } }
      EXTRA }
    """;

    private static string Tmp(string body)
    {
        var d = Path.Combine(Path.GetTempPath(), "gkk-prof-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        var p = Path.Combine(d, "profile.json"); File.WriteAllText(p, body); return p;
    }

    [Fact]
    public void Old_profile_with_diagnostics_loads_after_migration_and_is_backed_up()
    {
        var p = Tmp(Base.Replace("EXTRA", ", \"diagnostics\": { \"toggle\": { \"key\": \"X\" } }"));
        var original = File.ReadAllText(p);
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Load(p));
        var r = ProfileRetiredKeys.Migrate(p);
        Assert.True(r.Migrated); Assert.Equal(new[] { "diagnostics" }, r.Removed);
        Assert.True(File.Exists(r.BackupPath)); Assert.Equal(original, File.ReadAllText(r.BackupPath!));
        var prof = GameProfileLoader.Load(p);
        Assert.Equal("p1", prof.ProfileId);
        Assert.DoesNotContain("diagnostics", File.ReadAllText(p));
        // 2 回目は何もしない（バックアップも増やさない）
        Assert.False(ProfileRetiredKeys.Migrate(p).Migrated);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(p)!, "*.bak"));
    }

    [Fact]
    public void Other_unknown_keys_are_still_rejected_and_file_untouched()
    {
        var p = Tmp(Base.Replace("EXTRA", ", \"strategy\": 1"));
        var before = File.ReadAllText(p);
        Assert.False(ProfileRetiredKeys.Migrate(p).Migrated);
        Assert.Equal(before, File.ReadAllText(p));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p)!, "*.bak"));
        var ex = Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Load(p));
        Assert.Contains("strategy", ex.Message);
    }

    [Fact]
    public void Retired_and_unknown_together_removes_only_retired_and_still_rejects()
    {
        var p = Tmp(Base.Replace("EXTRA", ", \"diagnostics\": {}, \"strategy\": 1"));
        Assert.True(ProfileRetiredKeys.Migrate(p).Migrated);
        var ex = Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Load(p));
        Assert.Contains("strategy", ex.Message); Assert.DoesNotContain("'diagnostics'", ex.Message);
    }
}
