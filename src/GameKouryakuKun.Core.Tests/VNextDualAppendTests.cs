using System.Security.Cryptography;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class VNextDualAppendTests
{
    public VNextDualAppendTests() => RuntimeFlags.UseExperienceVNextWriter = false;

    [Fact]
    public void Flag_off_does_not_grow_vnext_and_does_not_open_kernel()
    {
        using var d = new TempDir();
        var profile = d["p"];
        Directory.CreateDirectory(profile);
        RuntimeKernel? kernel = null;
        var beforeExists = File.Exists(ExperienceVNextStore.DefaultPath(profile));

        var r = VNextDualAppend.Try(ref kernel, profile, "0.33.3",
            new ActionSpec("button1"), new StepContext(ObservationKind.Absent, Seq: 1, CallSite: "session_dual_append"));

        Assert.NotNull(r);
        Assert.True(r!.SkippedBecauseFlagOff);
        Assert.False(r.Wrote);
        Assert.Null(kernel);
        Assert.Equal(beforeExists, File.Exists(ExperienceVNextStore.DefaultPath(profile)));
    }

    [Fact]
    public void Flag_on_writes_vnext_only_and_leaves_memory_hash_unchanged()
    {
        using var d = new TempDir();
        var profile = d["p"];
        using (var mem = ExperienceStore.Open(profile, "prof", "1", "0.33.3"))
            mem.BeginRun("1", null, null, policyVersion: "explore");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var oldPath = Path.Combine(profile, "memory.sqlite");
        var before = Sha256(oldPath);

        RuntimeKernel? kernel = null;
        RuntimeFlags.UseExperienceVNextWriter = true;
        try
        {
            var r = VNextDualAppend.Try(ref kernel, profile, "0.33.3",
                new ActionSpec("dpad_up", "agent"),
                new StepContext(
                    ObservationKind.LegacyCoarse,
                    Seq: 42,
                    RunId: 1,
                    ResultChanged: true,
                    ResultNextState: "n2",
                    LegacyStateBefore: "n1",
                    Inferred: new[] { ("legacy_state_as_opaque_token", "test") },
                    CallSite: "session_dual_append"),
                onError: _ => Assert.Fail("should not error"));

            Assert.NotNull(r);
            Assert.True(r!.Wrote);
            Assert.NotNull(kernel);
            Assert.Equal(1, kernel!.Store.CountExperiences());
            Assert.Contains("session_dual_append", r.Record!.ProvenanceJson);
            Assert.Contains("model_bundle_placeholder", r.Record.ProvenanceJson);
            Assert.Equal(RuntimeKernel.ShellBundleId, r.Record.ModelBundleId);
        }
        finally
        {
            RuntimeFlags.UseExperienceVNextWriter = false;
            kernel?.Dispose();
        }

        Assert.Equal(before, Sha256(oldPath));
        Assert.True(File.Exists(ExperienceVNextStore.DefaultPath(profile)));
    }

    [Fact]
    public void Kernel_failure_returns_null_and_does_not_throw()
    {
        RuntimeFlags.UseExperienceVNextWriter = true;
        RuntimeKernel? kernel = null;
        Exception? seen = null;
        try
        {
            // 存在しない親ディレクトリを強制すると Open が失敗しうる → 代わりに破棄済み store を渡すのは難しいので
            // 無効な profileDataDir（ファイルをディレクトリとして）を使う
            using var d = new TempDir();
            var bog = Path.Combine(d.Path, "not-a-dir");
            File.WriteAllText(bog, "x");
            var r = VNextDualAppend.Try(ref kernel, bog, "0.33.3",
                new ActionSpec("button1"), new StepContext(ObservationKind.Absent, Seq: 1),
                onError: e => seen = e);
            Assert.Null(r);
            Assert.NotNull(seen);
            Assert.Null(kernel);
        }
        finally
        {
            RuntimeFlags.UseExperienceVNextWriter = false;
        }
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
