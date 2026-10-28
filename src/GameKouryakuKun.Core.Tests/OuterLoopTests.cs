using GameKouryakuKun.Core.Outer;
using GameKouryakuKun.Core.Intrinsic;
using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class OuterLoopTests : IDisposable
{
    public OuterLoopTests()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.OuterLoopEnvName, null);
        Environment.SetEnvironmentVariable(RuntimeFlags.OuterLoopPromoteEnvName, null);
        RuntimeFlags.EnableOuterLoop = false;
        RuntimeFlags.OuterLoopPromoteEnabled = false;
        RuntimeFlags.ApplyOuterLoopEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.OuterLoopEnvName, null);
        Environment.SetEnvironmentVariable(RuntimeFlags.OuterLoopPromoteEnvName, null);
        RuntimeFlags.EnableOuterLoop = false;
        RuntimeFlags.OuterLoopPromoteEnabled = false;
        RuntimeFlags.ApplyOuterLoopEffective();
    }

    private static ModelBundleSpec Champ() => new()
    {
        Id = "champion/test@0", Kind = ModelBundleKinds.Params, Hypothesis = "champ", Origin = "fixture",
        Parameters = new Dictionary<string, double> { ["a"] = 1 },
    };

    private static ModelBundleSpec Cand(string id = "candidate/test@1", string kind = ModelBundleKinds.Weights) => new()
    {
        Id = id, Kind = kind, ParentId = "champion/test@0", Hypothesis = "better pe", Origin = "fixture",
        Parameters = new Dictionary<string, double> { ["a"] = 0.9 },
    };

    private static MetricSnapshot M(double pe, double cal = 0.2, double info = 0.1, int n = 10) => new()
    {
        PeProxy = pe, CalibrationGapProxy = cal, ContributionInfoGain = info, SampleN = n,
    };

    [Fact]
    public void Compare_promotes_only_on_clear_pe_improve()
    {
        var r = OuterLoopEvaluator.Compare(Champ(), Cand(), M(0.5), M(0.4)); // 20% PE improve
        Assert.Equal("compared", r.Status);
        Assert.True(r.CanPromote);
        Assert.Equal("candidate", r.Winner);
        Assert.Contains("pe_improve", r.Reasons[0]);
        Assert.Contains("live_inputs_increased\":false", r.ProvenanceJson.Replace(" ", ""));
    }

    [Fact]
    public void Compare_rejects_without_clear_improve()
    {
        var r = OuterLoopEvaluator.Compare(Champ(), Cand(), M(0.5), M(0.49)); // <5%
        Assert.False(r.CanPromote);
        Assert.NotEqual("candidate", r.Winner);
    }

    [Fact]
    public void Code_edit_candidate_not_supported()
    {
        var r = OuterLoopEvaluator.Compare(Champ(), Cand(kind: ModelBundleKinds.CodeEdit), M(0.5), M(0.1));
        Assert.Equal("not_supported", r.Status);
        Assert.False(r.CanPromote);
        Assert.Contains("code_edit", r.Reasons[0]);
    }

    [Fact]
    public void Rollback_unavailable_blocks_outer()
    {
        var r = OuterLoopEvaluator.Compare(Champ(), Cand(), M(0.5), M(0.1), rollbackAvailable: false);
        Assert.Equal("rollback_unavailable", r.Status);
        Assert.False(r.CanPromote);
    }

    [Fact]
    public void Registry_cannot_promote_without_comparison()
    {
        using var d = new TempDir();
        using var reg = ModelRegistryStore.Open(d["p"], "0.37.0");
        var cand = Cand("candidate/no-compare@1");
        reg.AddCandidate(cand);
        var (ok, detail) = reg.TryPromote(cand.Id, promoteFlagEnabled: true);
        Assert.False(ok);
        Assert.Contains("promote_requires_comparison", detail);
    }

    [Fact]
    public void Registry_promote_requires_second_flag()
    {
        using var d = new TempDir();
        using var reg = ModelRegistryStore.Open(d["p"], "0.37.0");
        var champ = reg.GetBundle(reg.ChampionId()!)!;
        var cand = Cand("candidate/flag@1");
        reg.AddCandidate(cand);
        var cmp = OuterLoopEvaluator.Compare(champ, cand, M(0.5), M(0.35));
        Assert.True(cmp.CanPromote);
        reg.RecordComparison(cmp);
        var (okOff, detailOff) = reg.TryPromote(cand.Id, promoteFlagEnabled: false);
        Assert.False(okOff);
        Assert.Equal("promote_flag_off", detailOff);
        var (ok, detail) = reg.TryPromote(cand.Id, promoteFlagEnabled: true);
        Assert.True(ok);
        Assert.Equal(cand.Id, reg.ChampionId());
        Assert.True(reg.RollbackAvailable());
    }

    [Fact]
    public void Registry_rollback_restores_champion_pointer()
    {
        using var d = new TempDir();
        using var reg = ModelRegistryStore.Open(d["p"], "0.37.0");
        var before = reg.ChampionId()!;
        var cand = Cand("candidate/roll@1");
        reg.AddCandidate(cand);
        var cmp = OuterLoopEvaluator.Compare(reg.GetBundle(before)!, cand, M(0.5), M(0.3));
        reg.RecordComparison(cmp);
        Assert.True(reg.TryPromote(cand.Id, true).Ok);
        Assert.Equal(cand.Id, reg.ChampionId());
        var (ok, detail) = reg.TryRollback("test_rollback");
        Assert.True(ok);
        Assert.Equal(before, reg.ChampionId());
        Assert.Contains("rolled_back", detail);
        Assert.True(reg.CountEvents("rollback") >= 1);
    }

    [Fact]
    public void Flags_default_off()
    {
        RuntimeFlags.ApplyOuterLoopEffective();
        Assert.False(RuntimeFlags.EnableOuterLoop);
        Assert.False(RuntimeFlags.OuterLoopPromoteEnabled);
        Environment.SetEnvironmentVariable(RuntimeFlags.OuterLoopEnvName, "1");
        RuntimeFlags.ApplyOuterLoopEffective();
        Assert.True(RuntimeFlags.EnableOuterLoop);
        Assert.False(RuntimeFlags.OuterLoopPromoteEnabled);
        Assert.Contains("evaluate-only", RuntimeFlags.OuterLoopStatusText());
        Environment.SetEnvironmentVariable(RuntimeFlags.OuterLoopPromoteEnvName, "1");
        RuntimeFlags.ApplyOuterLoopEffective();
        Assert.True(RuntimeFlags.OuterLoopPromoteEnabled);
        Assert.Contains("PROMOTE", RuntimeFlags.OuterLoopStatusText());
    }

    [Fact]
    public void Health_parses_outer_logs()
    {
        using var d = new TempDir();
        var logs = d["logs"];
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "app-1.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"outer_registry","summary":"registry champ=champion/bootstrap@0 prev=(none) rollback=no compares=0 promotes=0 rollbacks=0"}""",
            """{"type":"outer_compare","summary":"outer_compare champ=a cand=b winner=candidate promote=True","can_promote":true,"status":"compared"}""",
            """{"type":"outer_promote","ok":true,"detail":"promoted:b"}""",
        });
        using var mem = ExperienceStore.Open(d["mem"], "p", "1", "0.37.0");
        using var app = AppStore.Open(d["app"], "0.37.0");
        var r = HealthReport.Build("0.37.0", mem.Db, app.Db, null, new[] { path }, 2, null);
        var area = r.Checks.Where(c => c.Area == "Outer/Registry").ToList();
        Assert.NotEmpty(area);
        Assert.Contains(area, c => c.Name.Contains("Champion"));
        Assert.Contains(area, c => c.Name.Contains("Compare") && c.Detail.Contains("can_promote"));
        Assert.Contains(area, c => c.Name.Contains("Promote"));
    }

    [Fact]
    public void Contract_and_audit_exist()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s7-outer-self-improvement-contract.md"));
        Assert.Contains("Model Registry", text);
        Assert.Contains("GKK_ENABLE_OUTER_LOOP", text);
        Assert.Contains("GKK_OUTER_LOOP_PROMOTE", text);
        Assert.Contains("code_edit", text);
        Assert.Contains("Rollback", text);
        Assert.DoesNotContain("本番コードの自動書き換えはする", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.37.0.md")));
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.37.1.md")));
        Assert.Contains("experience", text.ToLowerInvariant());
        Assert.Contains("weight", text.ToLowerInvariant());
        var fa = File.ReadAllText(Path.Combine(root, "future-architecture.md"));
        Assert.Contains("v0.37.1", fa);
        Assert.Contains("S7 Outer", fa);
        Assert.Contains("S7 クローズ可", fa);
    }

    [Fact]
    public void Session_wires_outer_under_flag()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var ses = File.ReadAllText(Path.Combine(d!.Parent!.FullName, "src", "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("TryRunOuterLoopShell", ses);
        Assert.Contains("ModelRegistryStore", ses);
        Assert.Contains("OuterLoopEvaluator.Compare", ses);
        Assert.Contains("OuterLoopPromoteEnabled", ses);
        Assert.Contains("ApplyOuterLoopEffective", ses);
        Assert.Contains("OuterMetricSnapshotBuilder", ses);
        Assert.Contains("PortfolioWeights", ses);
        Assert.Contains("TryRollbackOuter", ses);
        Assert.DoesNotContain("intrinsic_fixture_v1", ses);
    }

    [Fact]
    public void Fixture_metric_path_still_works()
    {
        var m = OuterMetricSnapshotBuilder.Fixture(0.4, 0.2, 0.2, 12);
        Assert.Equal(OuterMetricSnapshotBuilder.MethodFixture, m.Method);
        var r = OuterLoopEvaluator.Compare(Champ(), Cand(), OuterMetricSnapshotBuilder.Fixture(0.5), m);
        Assert.True(r.CanPromote);
    }

    [Fact]
    public void Experience_pair_uses_same_slice_and_partial_when_sparse()
    {
        var stats = new IntrinsicContributionStats();
        stats.NoteMixStep();
        var pe = new PeClosedLoopRecorder();
        var (c, d, note) = OuterMetricSnapshotBuilder.PairOnSameSlice(
            stats, pe, null,
            IntrinsicPortfolioWeights.Default(),
            new Dictionary<string, double> { ["info_gain_pe"] = 0.5, ["controllability"] = 0.125, ["reach_unknown"] = 0.125, ["reversibility_options"] = 0.125, ["skill_wm_long_term"] = 0.125 });
        Assert.Equal(c.Method, d.Method);
        Assert.Equal(OuterMetricSnapshotBuilder.MethodPartial, c.Method);
        Assert.Contains("same_slice", note);
        Assert.Equal(c.CalibrationGapProxy, d.CalibrationGapProxy);
        Assert.Equal(c.SampleN, d.SampleN);
    }

    [Fact]
    public void Experience_pair_full_when_enough_mix_or_pe()
    {
        var stats = new IntrinsicContributionStats();
        for (int i = 0; i < 8; i++) stats.NoteMixStep();
        var pe = new PeClosedLoopRecorder();
        var (c, d, _) = OuterMetricSnapshotBuilder.PairOnSameSlice(
            stats, pe, null, IntrinsicPortfolioWeights.Default(), IntrinsicPortfolioWeights.Default());
        Assert.Equal(OuterMetricSnapshotBuilder.MethodExperience, c.Method);
        Assert.Equal(c.PeProxy, d.PeProxy); // no weight delta / no fell → same PE
    }

    [Fact]
    public void Promote_applies_weights_and_rollback_restores()
    {
        using var d = new TempDir();
        using var reg = ModelRegistryStore.Open(d["p"], "0.37.1");
        var state = new PortfolioWeightState();
        var before = state.Current[IntrinsicComponents.InfoGainPe];
        Assert.Equal(IntrinsicPortfolioWeights.Equal, before);

        var champ = reg.GetBundle(reg.ChampionId()!)!;
        var cand = new ModelBundleSpec
        {
            Id = "candidate/weights-apply@1",
            Kind = ModelBundleKinds.Weights,
            ParentId = champ.Id,
            Hypothesis = "boost info",
            Origin = "fixture",
            Parameters = new Dictionary<string, double>
            {
                ["info_gain_pe"] = 0.5,
                ["controllability"] = 0.125,
                ["reach_unknown"] = 0.125,
                ["reversibility_options"] = 0.125,
                ["skill_wm_long_term"] = 0.125,
            },
        };
        reg.AddCandidate(cand);
        var cmp = OuterLoopEvaluator.Compare(champ, cand, OuterMetricSnapshotBuilder.Fixture(0.5), OuterMetricSnapshotBuilder.Fixture(0.3, info: 0.2));
        Assert.True(cmp.CanPromote);
        reg.RecordComparison(cmp);

        var applied = state.ApplyFromBundle(cand);
        Assert.True(applied.Ok);
        Assert.True(state.Current[IntrinsicComponents.InfoGainPe] > before);

        var (ok, _) = reg.TryPromote(cand.Id, true);
        Assert.True(ok);
        Assert.Equal(cand.Id, reg.ChampionId());

        var report = IntrinsicValueMixer.Mix(new IntrinsicMixInput(
            new[] { FakeCand("A", info: 0.5, unc: 0.2) }, PortfolioWeights: state.Current));
        Assert.Equal("portfolio_applied_v1", report.Scores[0].Method);

        Assert.True(reg.TryRollback("test").Ok);
        Assert.True(state.Rollback().Ok);
        Assert.Equal(IntrinsicPortfolioWeights.Equal, state.Current[IntrinsicComponents.InfoGainPe], 3);
        Assert.Equal(champ.Id, reg.ChampionId());
    }

    [Fact]
    public void Code_edit_weights_apply_rejected()
    {
        var state = new PortfolioWeightState();
        var bad = Cand(kind: ModelBundleKinds.CodeEdit);
        var (ok, detail) = state.ApplyFromBundle(bad);
        Assert.False(ok);
        Assert.Contains("kind_not_weights", detail);
    }

    [Fact]
    public void Mixer_uses_applied_portfolio_weights()
    {
        var c = FakeCand("x", info: 0.8, unc: 0.1);
        var equal = IntrinsicValueMixer.Mix(new IntrinsicMixInput(new[] { c }));
        var heavy = IntrinsicPortfolioWeights.FromParameters(new Dictionary<string, double>
        {
            ["info_gain_pe"] = 0.8,
            ["controllability"] = 0.05,
            ["reach_unknown"] = 0.05,
            ["reversibility_options"] = 0.05,
            ["skill_wm_long_term"] = 0.05,
        });
        var applied = IntrinsicValueMixer.Mix(new IntrinsicMixInput(new[] { c }, PortfolioWeights: heavy));
        Assert.Equal("portfolio_equal_v1", equal.Scores[0].Method);
        Assert.Equal("portfolio_applied_v1", applied.Scores[0].Method);
        Assert.True(applied.Scores[0].Mixed >= equal.Scores[0].Mixed - 1e-9);
    }

    private static GameKouryakuKun.Core.Exploration.CandidateScore FakeCand(string action, double info = 0, double unc = 0) =>
        new(action, info + unc, 0, 0, 0, 0, info, unc, 1.0, false, false, Array.Empty<string>())
        {
            InfoTerm = info, UncTerm = unc, Discovery = 0, Leave = 0, Change = 0, Text = 0,
        };
}

