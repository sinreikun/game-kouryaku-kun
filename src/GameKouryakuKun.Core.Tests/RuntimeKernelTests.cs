using System.Security.Cryptography;
using System.Text.Json;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class RuntimeKernelTests
{
    public RuntimeKernelTests()
    {
        // 他テストとの干渉を防ぐ
        RuntimeFlags.UiEnabled = false;
        RuntimeFlags.UseExperienceVNextWriter = false;
        Environment.SetEnvironmentVariable(RuntimeFlags.EnvName, null);
    }

    [Fact]
    public void Flag_off_step_is_noop_and_does_not_create_or_grow_vnext()
    {
        RuntimeFlags.UseExperienceVNextWriter = false;
        using var d = new TempDir();
        var profile = d["p"];
        Directory.CreateDirectory(profile);
        var vnextPath = ExperienceVNextStore.DefaultPath(profile);
        Assert.False(File.Exists(vnextPath));

        using (var k = RuntimeKernel.Open(profile, "0.33.2"))
        {
            var r = k.Step(new ActionSpec("button1"), new StepContext(ObservationKind.Absent, ResultChanged: true, ResultNextState: "n2"));
            Assert.False(r.Wrote);
            Assert.True(r.SkippedBecauseFlagOff);
            Assert.Null(r.ExperienceId);
            Assert.Equal(0, k.Store.CountExperiences());
        }
        // Open は空スキーマを作るが experiences は 0 のまま
        Assert.True(File.Exists(vnextPath));
        using var store = ExperienceVNextStore.Open(vnextPath, "0.33.2");
        Assert.Equal(0, store.CountExperiences());
        Assert.False(File.Exists(Path.Combine(profile, "memory.sqlite")));
    }

    [Fact]
    public void Flag_on_appends_only_to_experience_vnext_with_bundle_placeholder()
    {
        using var d = new TempDir();
        var profile = d["p"];
        Directory.CreateDirectory(profile);

        // 旧 DB を別途用意し、Kernel 実行前後でハッシュ不変を確認
        string oldPath;
        using (var mem = ExperienceStore.Open(profile, "prof", "1", "0.33.2"))
        {
            oldPath = Path.Combine(profile, "memory.sqlite");
            mem.BeginRun("1", null, null, policyVersion: "explore");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var before = Sha256(oldPath);

        RuntimeFlags.UseExperienceVNextWriter = true;
        try
        {
            using var k = RuntimeKernel.Open(profile, "0.33.2");
            var r = k.Step(
                new ActionSpec("dpad_up", "agent"),
                new StepContext(
                    ObservationKind.LegacyCoarse,
                    Seq: 1,
                    ResultChanged: true,
                    ResultNextState: "opaque-b",
                    ResultChangeRatio: 0.1,
                    LegacyStateBefore: "opaque-a"));

            Assert.True(r.Wrote);
            Assert.False(r.SkippedBecauseFlagOff);
            Assert.True(r.ExperienceId is > 0);
            Assert.Equal(1, k.Store.CountExperiences());
            Assert.Equal(RuntimeKernel.ShellBundleId, r.Record!.ModelBundleId);
            Assert.Contains("runtime_kernel", r.Record.ProvenanceJson);
            Assert.Contains("model_bundle_placeholder", r.Record.ProvenanceJson);
            Assert.Contains("model_bundle_id", r.Record.ProvenanceJson);

            var r2 = k.Step(new ActionSpec("wait", "wait"), new StepContext(ObservationKind.Absent, Seq: 2));
            Assert.True(r2.Wrote);
            Assert.Equal(2, k.Store.CountExperiences());
        }
        finally
        {
            RuntimeFlags.UseExperienceVNextWriter = false;
        }

        Assert.Equal(before, Sha256(oldPath));
        Assert.True(File.Exists(ExperienceVNextStore.DefaultPath(profile)));
        Assert.NotEqual(Path.GetFullPath(oldPath), Path.GetFullPath(ExperienceVNextStore.DefaultPath(profile)));
    }

    [Fact]
    public void Flag_on_then_off_stops_further_writes()
    {
        using var d = new TempDir();
        var profile = d["p"];
        Directory.CreateDirectory(profile);
        using var k = RuntimeKernel.Open(profile, "0.33.2");

        RuntimeFlags.UseExperienceVNextWriter = true;
        Assert.True(k.Step(new ActionSpec("button1"), new StepContext(ObservationKind.Absent, Seq: 1)).Wrote);
        RuntimeFlags.UseExperienceVNextWriter = false;
        var skipped = k.Step(new ActionSpec("button2"), new StepContext(ObservationKind.Absent, Seq: 2));
        Assert.False(skipped.Wrote);
        Assert.True(skipped.SkippedBecauseFlagOff);
        Assert.Equal(1, k.Store.CountExperiences());
    }

    [Fact]
    public void Session_dual_append_is_gated_and_best_effort()
    {
        var session = File.ReadAllText(Path.Combine(SrcRoot(), "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("VNextDualAppend.Try", session);
        Assert.Contains("TryDualAppendVNext", session);
        Assert.Contains("RuntimeFlags.ApplyEffective", session);
        Assert.Contains("vnext_dual_append", session);
        Assert.Contains("experience-vnext への併記に失敗", session); // 失敗しても止めない
        Assert.Contains(RuntimeFlags.EnvName, File.ReadAllText(Path.Combine(SrcRoot(), "GameKouryakuKun.Storage", "ExperienceVNextModels.cs")));
    }

    private static string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("GameKouryakuKun.sln が見つかりません");
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
