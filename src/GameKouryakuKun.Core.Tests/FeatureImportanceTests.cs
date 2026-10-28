using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Importance;
using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class FeatureImportanceTests : IDisposable
{
    public FeatureImportanceTests()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.ImportanceEnvName, null);
        RuntimeFlags.AttachImportance = false;
        RuntimeFlags.ApplyImportanceEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.ImportanceEnvName, null);
        RuntimeFlags.AttachImportance = false;
        RuntimeFlags.ApplyImportanceEffective();
    }

    [Fact]
    public void Estimator_scores_pe_tag_and_stubs_ablation()
    {
        var report = FeatureImportanceEstimator.Estimate(new ImportanceEstimateInput(
            Step: 1,
            PeTags: new[] { PeRoutingTags.RepresentationCoarse, PeRoutingTags.WaitSameDistribution }));
        Assert.Contains(report.Features, f => f.FeatureId == "meta:ablation_replay" && f.Status == LayerStatus.NotImplemented);
        var coarse = report.Features.Single(f => f.FeatureId == "pe:" + PeRoutingTags.RepresentationCoarse);
        Assert.Equal("scored", coarse.Status);
        Assert.Equal(ImportanceMethods.PeTagProxy, coarse.Method);
        Assert.True(coarse.Score >= AttentionWeights.RecommendAcceptMin);
        Assert.Equal("advisory_only", report.Attention.Policy);
        Assert.True(report.Attention.Regions >= AttentionWeights.RareRecheckFloor);
        Assert.DoesNotContain("キャラ", report.SummaryLine());
    }

    [Fact]
    public void Context_helps_raises_ctx_feature()
    {
        var sh = new ShortHorizonForecast
        {
            Status = LayerStatus.Observed,
            ContextHelpEvents = 2,
            MeanSurprise = 0.2,
            PredictionMiss = false,
        };
        var report = FeatureImportanceEstimator.Estimate(new ImportanceEstimateInput(Step: 2, ShortHorizon: sh));
        var ctx = report.Features.Single(f => f.FeatureId == "ctx:local_context");
        Assert.Equal("scored", ctx.Status);
        Assert.Equal(ImportanceMethods.ContextHelpsProxy, ctx.Method);
        Assert.True(ctx.Score >= 0.55);
    }

    [Fact]
    public void Recommendations_set_recommended_status_not_status()
    {
        var loop = new PeClosedLoopRecorder();
        var rec = new CausalComparisonRecord
        {
            Step = 3,
            Action = "a",
            Preferred = "undecided",
            PeRoutingTags = new[] { PeRoutingTags.RepresentationCoarse, PeRoutingTags.WaitSameDistribution },
            Hypotheses = CausalHypothesisIds.All.Select(id => new HypothesisScore { Id = id, Status = "not_observed" }).ToList(),
        };
        loop.Observe(rec);
        var report = FeatureImportanceEstimator.Estimate(new ImportanceEstimateInput(
            Step: 3, PeTags: rec.PeRoutingTags, Causal: rec), loop);
        Assert.True(report.RecommendationsWritten >= 1);
        var pending = loop.Snapshot().Where(c => c.Status == PeCandidateStatus.Pending).ToList();
        Assert.NotEmpty(pending);
        Assert.All(pending, c => Assert.Equal(PeCandidateStatus.Pending, c.Status)); // WM not auto-accepted
        var coarse = pending.Single(c => c.Tag == PeRoutingTags.RepresentationCoarse);
        Assert.Equal(PeCandidateStatus.Accepted, coarse.RecommendedStatus);
        Assert.True(coarse.Importance >= AttentionWeights.RecommendAcceptMin);
        var wait = pending.Single(c => c.Tag == PeRoutingTags.WaitSameDistribution);
        Assert.True(wait.RecommendedStatus is null or PeCandidateStatus.Rejected or PeCandidateStatus.Accepted);
    }

    [Fact]
    public void Flag_default_off_env_on()
    {
        RuntimeFlags.ApplyImportanceEffective();
        Assert.False(RuntimeFlags.AttachImportance);
        Environment.SetEnvironmentVariable(RuntimeFlags.ImportanceEnvName, "1");
        RuntimeFlags.ApplyImportanceEffective();
        Assert.True(RuntimeFlags.AttachImportance);
        Assert.Contains("ON", RuntimeFlags.ImportanceStatusText());
    }

    [Fact]
    public void Health_parses_importance_logs()
    {
        using var d = new TempDir();
        var logs = d["logs"];
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "app-1.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"feature_importance","feature_id":"pe:representation_coarse","score":0.75,"method":"pe_tag_proxy","status":"scored"}""",
            """{"type":"attention_weights","summary":"importance n=2","regions":0.75,"tracks":0.55,"ocr":0.45,"residual":0.7,"policy":"advisory_only"}""",
            """{"type":"pe_importance_recommend","count":1,"pending":2}""",
        });
        using var mem = ExperienceStore.Open(d["mem"], "p", "1", "0.34.0");
        using var app = AppStore.Open(d["app"], "0.34.0");
        var r = HealthReport.Build("0.34.0", mem.Db, app.Db, null, new[] { path }, 2, null);
        var imp = r.Checks.Where(c => c.Area == "重要度").ToList();
        Assert.NotEmpty(imp);
        Assert.Contains(imp, c => c.Name.Contains("特徴重要度"));
        Assert.Contains(imp, c => c.Name.Contains("注意重み"));
        Assert.DoesNotContain(r.Text, "キャラ");
    }

    [Fact]
    public void Bundle_can_carry_attention_additive()
    {
        var obs = new StructuredObservation(
            StructuredObservation.CurrentSchema, 1, "agent", "a", "b", "c",
            new GlobalScene("n", "m", "1,1", "v", null, LayerStatus.Observed),
            new RegionLayer(LayerStatus.Observed, 0, Array.Empty<ChangeRegion>()),
            new TextLayer(LayerStatus.NotObserved, "none", "unknown", Array.Empty<TextLineObs>(), Array.Empty<NumberObs>(), 0, 0, 0),
            new TemporalLayer(0, false, null, LayerStatus.Observed));
        var attn = new AttentionWeights { Regions = 0.8, Tracks = 0.5, Ocr = 0.3, Residual = 0.7 };
        var bundle = SensorStackAssembler.FromStructured(obs, attentionWeights: attn);
        Assert.Equal(3, bundle.SchemaVersion); // additive, no live ROI
        Assert.NotNull(bundle.Attention);
        Assert.Equal(LayerStatus.Observed, bundle.Layers.Attention);
        Assert.Equal("advisory_only", bundle.Attention!.Policy);
        Assert.DoesNotContain("中央最大", bundle.ToJson());
    }

    [Fact]
    public void Contract_and_audit_exist()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s4-importance-attention-contract.md"));
        Assert.Contains("FeatureImportanceRecord", text);
        Assert.Contains("GKK_ATTACH_IMPORTANCE", text);
        Assert.Contains("報酬", text);
        Assert.Contains("永久削除", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.34.0.md")));
    }
}
