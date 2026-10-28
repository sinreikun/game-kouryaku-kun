using System.Security.Cryptography;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>S1 OldExperienceAdapter 縦スライス。ゲーム意味ラベルを持たない合成 fixture のみ。</summary>
public class OldExperienceAdapterTests
{
    [Fact]
    public void Opens_old_db_read_only_and_maps_stream_with_honest_loss_report()
    {
        using var d = new TempDir();
        var profileDir = d["p"];
        using (var mem = ExperienceStore.Open(profileDir, "prof", "1", "0.33.1"))
        {
            var run = mem.BeginRun("1", "b", "c", policyVersion: "explore");
            mem.AppendStream(run, null, new StreamStep(1, "n1", "button1", true, "n2", 0.2), 1.5, Array.Empty<BoundaryReason>(), 0.25, 3, "agent");
            mem.AppendStream(run, null, new StreamStep(2, "n2", "wait", false, "n2", 0.0), 0.1, Array.Empty<BoundaryReason>(), actionSource: "agent");
            mem.AddObservation(run, 1, 1, 1, "agent", """{"layers":[],"note":"synthetic opaque observation"}""");
        }

        var oldPath = Path.Combine(profileDir, "memory.sqlite");
        // WAL を本体へ取り込み、ハッシュ比較を安定させる
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var beforeHash = Sha256(oldPath);
        var beforeLen = new FileInfo(oldPath).Length;

        using var adapter = OldExperienceAdapter.OpenReadOnly(oldPath);
        Assert.Equal(17, adapter.ReadSchemaVersion());
        var result = adapter.ConvertRecent(limit: 10);

        Assert.Equal(2, result.Records.Count);
        Assert.Equal(2, result.Loss.MappedCount);
        Assert.Equal(beforeHash, Sha256(oldPath));
        Assert.Equal(beforeLen, new FileInfo(oldPath).Length);

        var r1 = result.Records.Single(x => x.Seq == 1);
        Assert.Equal(ObservationKind.Structured, r1.ObservationKind);
        Assert.Contains("synthetic opaque observation", r1.ObservationJson);
        Assert.Equal("button1", r1.Action);
        Assert.Equal("agent", r1.ActionSource);
        Assert.Equal(ExperienceVNextContract.DefaultLegacyBundleId, r1.ModelBundleId);
        Assert.True(r1.ResultChanged);
        Assert.Equal("n2", r1.ResultNextState);

        var r2 = result.Records.Single(x => x.Seq == 2);
        Assert.Equal(ObservationKind.LegacyCoarse, r2.ObservationKind);
        Assert.Null(r2.ObservationJson);
        Assert.Equal("wait", r2.Action);

        Assert.Contains(result.Loss.Dropped, x => x.Code == "no_structured_observation");
        Assert.Contains(result.Loss.Inferred, x => x.Code == "model_bundle_placeholder");
        Assert.Contains(result.Loss.Inferred, x => x.Code == "legacy_state_as_opaque_token");
        Assert.Contains(result.Loss.Dropped, x => x.Code == "no_decision_record");
        Assert.Contains(result.Loss.Notes, n => n.Contains("read-only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Persists_into_separate_vnext_file_without_touching_old_db()
    {
        using var d = new TempDir();
        var profileDir = d["p"];
        using (var mem = ExperienceStore.Open(profileDir, "prof", "1", "0.33.1"))
        {
            var run = mem.BeginRun("1", null, null, policyVersion: "explore");
            mem.AppendStream(run, null, new StreamStep(1, "a", "dpad_up", true, "b", 0.1), 0.5, Array.Empty<BoundaryReason>());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var oldPath = Path.Combine(profileDir, "memory.sqlite");
        var before = Sha256(oldPath);

        var vnextPath = ExperienceVNextStore.DefaultPath(profileDir);
        Assert.False(File.Exists(vnextPath));

        using var adapter = OldExperienceAdapter.OpenReadOnly(oldPath);
        using var vnext = ExperienceVNextStore.Open(vnextPath, "0.33.1");
        var result = adapter.ConvertRecent(limit: 5, persistTo: vnext);

        Assert.Equal(1, result.Records.Count);
        Assert.True(result.LossReportId is > 0);
        Assert.Equal(1, vnext.CountExperiences());
        Assert.Contains("experiences", vnext.Db.Tables());
        Assert.Contains("loss_reports", vnext.Db.Tables());
        Assert.Contains("model_bundles", vnext.Db.Tables());
        Assert.Equal(2, vnext.Db.SchemaVersion); // S2 migration: observation_kind bundle
        Assert.Equal(before, Sha256(oldPath));
        Assert.True(File.Exists(vnextPath));
        Assert.NotEqual(Path.GetFullPath(oldPath), Path.GetFullPath(vnextPath));
    }

    [Fact]
    public void Read_only_open_rejects_missing_file()
    {
        using var d = new TempDir();
        Assert.Throws<FileNotFoundException>(() => OldExperienceAdapter.OpenReadOnly(Path.Combine(d.Path, "missing.sqlite")));
    }

    [Fact]
    public void Runtime_flag_defaults_off()
    {
        Assert.False(RuntimeFlags.UseExperienceVNextWriter);
        var session = File.ReadAllText(Path.Combine(SrcRoot(), "GameKouryakuKun.App", "Session.cs"));
        Assert.DoesNotContain("OldExperienceAdapter", session); // Adapter は Session から呼ばない（Replay 専用）
        Assert.Contains("VNextDualAppend", session);
        Assert.Contains("UseExperienceVNextWriter", session);
    }

    [Fact]
    public void Contract_doc_exists()
    {
        var root = Path.GetFullPath(Path.Combine(SrcRoot(), ".."));
        Assert.True(File.Exists(Path.Combine(root, "docs", "s1-runtime-contract.md")));
        var text = File.ReadAllText(Path.Combine(root, "docs", "s1-runtime-contract.md"));
        Assert.Contains("LossReport", text);
        Assert.Contains("experience-vnext.sqlite", text);
        Assert.Contains("model_bundle_id", text);
        Assert.Contains("OldExperienceAdapter", text);
    }

    private static string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("GameKouryakuKun.sln が見つかりません");
    }

    private static string Sha256(string path)
    {
        // WAL がある場合でも主ファイルの内容ハッシュで「Adapter が書いていない」ことを見る
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
