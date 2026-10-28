using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class PeClosedLoopTests : IDisposable
{
    public PeClosedLoopTests()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.CausalWmEnvName, null);
        RuntimeFlags.AttachCausalWm = false;
        RuntimeFlags.ApplyCausalWmEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.CausalWmEnvName, null);
        RuntimeFlags.AttachCausalWm = false;
        RuntimeFlags.ApplyCausalWmEffective();
    }

    private static CausalComparisonRecord Rec(params string[] tags) => new()
    {
        Step = 3,
        Action = "button1",
        Node = "n1",
        Changed = true,
        Provenance = "agent",
        Preferred = CausalHypothesisIds.H0Natural,
        PeRoutingTags = tags,
        EvidenceSources = new[] { "natural_baseline" },
        Hypotheses = CausalHypothesisIds.All.Select(id => new HypothesisScore
        {
            Id = id,
            Status = id == CausalHypothesisIds.H0Natural ? "scored" : "not_implemented",
        }).ToList(),
    };

    [Fact]
    public void Observe_enqueues_one_candidate_per_tag_and_counts()
    {
        var loop = new PeClosedLoopRecorder();
        var created = loop.Observe(Rec(PeRoutingTags.WaitSameDistribution, PeRoutingTags.TrackUnstable), runId: 9);
        Assert.Equal(2, created.Count);
        Assert.Equal(2, loop.PendingCount);
        Assert.Equal(2, loop.Enqueued);
        Assert.Equal(1, loop.TagCounts()[PeRoutingTags.WaitSameDistribution]);
        Assert.Equal(1, loop.PreferredCounts()[CausalHypothesisIds.H0Natural]);
        Assert.All(created, c => Assert.Equal(PeCandidateStatus.Pending, c.Status));
        Assert.DoesNotContain("キャラ", created[0].EvidenceJson);
        Assert.DoesNotContain("中央最大", loop.SummaryLine());
    }

    [Fact]
    public void Accept_is_status_only_stub()
    {
        var loop = new PeClosedLoopRecorder();
        var c = loop.Observe(Rec(PeRoutingTags.RepresentationCoarse)).Single();
        Assert.True(loop.TrySetStatus(c.CandidateId, PeCandidateStatus.Accepted, "manual_stub"));
        Assert.Equal(0, loop.PendingCount);
        Assert.Equal(PeCandidateStatus.Accepted, loop.Snapshot().Single(x => x.CandidateId == c.CandidateId).Status);
    }

    [Fact]
    public void Store_persists_without_touching_memory_sqlite()
    {
        using var d = new TempDir();
        var profile = d["p"];
        Directory.CreateDirectory(profile);
        var memoryPath = Path.Combine(profile, "memory.sqlite");
        File.WriteAllText(memoryPath, "sentinel");
        var loop = new PeClosedLoopRecorder();
        loop.Observe(Rec(PeRoutingTags.WaitSameDistribution));
        using (var store = PeClosedLoopStore.Open(profile, "0.33.8"))
        {
            store.PersistSnapshot(loop);
            Assert.True(store.CountCandidates() >= 1);
            Assert.Contains("wait_same_distribution", store.StatsSummary());
        }
        Assert.Equal("sentinel", File.ReadAllText(memoryPath)); // untouched
        Assert.True(File.Exists(PeClosedLoopStore.DefaultPath(profile)));
    }

    [Fact]
    public void Health_parses_causal_wm_log_counts()
    {
        using var d = new TempDir();
        var logs = d["logs"];
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "app-1.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"causal_wm","preferred":"h0_natural","tags":"wait_same_distribution,track_unstable"}""",
            """{"type":"pe_feature_candidate","candidate_id":1,"tag":"wait_same_distribution","status":"pending"}""",
            """{"type":"pe_closed_loop_summary","summary":"pe_closed_loop tags=[wait_same_distribution:1] preferred=[h0_natural:1] pending=1 enqueued=1 dropped=0","pending":1,"enqueued":1}""",
        });
        using var mem = ExperienceStore.Open(d["mem"], "p", "1", "0.33.8");
        using var app = AppStore.Open(d["app"], "0.33.8");
        var r = HealthReport.Build("0.33.8", mem.Db, app.Db, null, new[] { path }, 2, null);
        var causal = r.Checks.Where(c => c.Area == "因果WM").ToList();
        Assert.NotEmpty(causal);
        Assert.Contains(causal, c => c.Name.Contains("H0") && c.Detail.Contains("h0_natural"));
        Assert.Contains(causal, c => c.Name.Contains("PE") && c.Detail.Contains("wait_same_distribution"));
        Assert.Contains(causal, c => c.Name.Contains("特徴候補") && c.Level is HealthReport.Level.Ok or HealthReport.Level.Info);
        Assert.DoesNotContain(r.Text, "キャラ");
    }

    [Fact]
    public void Contract_mentions_pe_closed_loop_queue()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s3-causal-wm-contract.md"));
        Assert.Contains("PE 閉ループ", text);
        Assert.Contains("pe-closed-loop.sqlite", text);
        Assert.Contains("pending", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.33.8.md")));
    }
}
