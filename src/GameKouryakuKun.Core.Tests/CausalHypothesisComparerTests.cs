using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class CausalHypothesisComparerTests : IDisposable
{
    public CausalHypothesisComparerTests()
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

    [Fact]
    public void Missing_baselines_mark_h0_h1_not_observed_h3_not_implemented()
    {
        var r = CausalHypothesisComparer.Compare(new CausalCompareInput(1, "button1", "n1", true, "agent"));
        Assert.Equal("undecided", r.Preferred);
        Assert.Equal(LayerStatus.NotObserved, r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H0Natural).Status);
        Assert.Equal(LayerStatus.NotObserved, r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H1Immediate).Status);
        Assert.Equal(LayerStatus.NotImplemented, r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H3Context).Status);
        Assert.Equal(LayerStatus.NotImplemented, r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H4CommonCause).Status);
        Assert.DoesNotContain("キャラ", r.ToJson());
        Assert.DoesNotContain("enemy", r.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Natural_baseline_scores_h0_without_adopting_h1_from_action_after_alone()
    {
        var nb = new NaturalBaseline();
        for (int i = 0; i < 20; i++) nb.ObserveNatural("n1", 800, changed: i % 5 == 0, left: false, fromWait: true);
        nb.ObserveActionTurn("n1", 800);

        var r = CausalHypothesisComparer.Compare(new CausalCompareInput(
            2, "button1", "n1", Changed: true, Provenance: "agent", NaturalBaseline: nb));
        var h0 = r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H0Natural);
        var h1 = r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H1Immediate);
        Assert.Equal("scored", h0.Status);
        Assert.Equal(LayerStatus.NotObserved, h1.Status); // no region cells / dependence → refuse action-after-alone
        Assert.Equal(CausalHypothesisIds.H0Natural, r.Preferred);
        Assert.Contains(PeRoutingTags.WaitSameDistribution, r.PeRoutingTags); // wait distribution routing when H0 preferred
        Assert.Contains("natural_baseline", r.EvidenceSources);
    }

    [Fact]
    public void Region_causality_can_score_h1_with_dependence_contrast()
    {
        var rc = new RegionCausality(4);
        // Make cell 0 action-dependent across >= RevertMinCommonActions groups
        for (int i = 0; i < 8; i++)
        {
            rc.ObserveAction("n1", "a", new[] { 0 });
            rc.ObserveAction("n1", "b", Array.Empty<int>());
            rc.ObserveAction("n1", "c", Array.Empty<int>());
        }
        for (int i = 0; i < 4; i++) rc.ObserveNatural("n1", Array.Empty<int>());

        var r = CausalHypothesisComparer.Compare(new CausalCompareInput(
            3, "a", "n1", true, "agent", RegionCausality: rc, ChangedCells: new[] { 0 }));
        var h1 = r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H1Immediate);
        Assert.Equal("scored", h1.Status);
        Assert.True(h1.LogBf is double bf && bf > 0, h1.Note);
        Assert.Contains("region_causality", r.EvidenceSources);
        Assert.Equal(CausalHypothesisIds.H1Immediate, r.Preferred);
    }

    [Fact]
    public void Delayed_evidence_scores_h2()
    {
        var r = CausalHypothesisComparer.Compare(new CausalCompareInput(
            4, "button1", "n1", true, "agent", DelayedPresent: true, DelayedCells: 3));
        Assert.Equal("scored", r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H2Delayed).Status);
        Assert.Contains("delayed", r.EvidenceSources);
    }

    [Fact]
    public void Track_unstable_hint_adds_pe_tag()
    {
        var r = CausalHypothesisComparer.Compare(new CausalCompareInput(
            5, "wait", "n1", false, "wait", TrackUnstableHint: true));
        Assert.Contains(PeRoutingTags.TrackUnstable, r.PeRoutingTags);
        Assert.Contains(PeRoutingTags.WaitSameDistribution, r.PeRoutingTags);
    }

    [Fact]
    public void Flag_default_off_env_on()
    {
        RuntimeFlags.ApplyCausalWmEffective();
        Assert.False(RuntimeFlags.AttachCausalWm);
        Environment.SetEnvironmentVariable(RuntimeFlags.CausalWmEnvName, "1");
        RuntimeFlags.ApplyCausalWmEffective();
        Assert.True(RuntimeFlags.AttachCausalWm);
        Assert.Contains("ON", RuntimeFlags.CausalWmStatusText());
    }

    [Fact]
    public void Bundle_can_carry_causal_comparison_schema3()
    {
        var obs = new StructuredObservation(
            StructuredObservation.CurrentSchema, 9, "agent", "button1", "a", "b",
            new GlobalScene("n", "m", "1,1", "v", null, LayerStatus.Observed),
            new RegionLayer(LayerStatus.Observed, 0, Array.Empty<ChangeRegion>()),
            new TextLayer(LayerStatus.NotObserved, "none", "unknown", Array.Empty<TextLineObs>(), Array.Empty<NumberObs>(), 0, 0, 0),
            new TemporalLayer(0, false, null, LayerStatus.Observed));
        var cmp = CausalHypothesisComparer.Compare(new CausalCompareInput(9, "button1", "a", false, "agent"));
        var bundle = SensorStackAssembler.FromStructured(obs, causalComparison: cmp);
        Assert.Equal(3, bundle.SchemaVersion);
        Assert.Equal(LayerStatus.Observed, bundle.Layers.CausalComparison);
        Assert.NotNull(bundle.CausalComparison);
        Assert.Contains("causal_comparison", bundle.ToJson());
        Assert.DoesNotContain("中央最大", bundle.ToJson());
    }

    [Fact]
    public void Contract_and_audit_exist()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        Assert.True(File.Exists(Path.Combine(root, "docs", "s3-causal-wm-contract.md")));
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.33.7.md")));
        var c = File.ReadAllText(Path.Combine(root, "docs", "s3-causal-wm-contract.md"));
        Assert.Contains("H0", c);
        Assert.Contains("GKK_ATTACH_CAUSAL_WM", c);
        Assert.Contains("Action 後に起きたことだけで H1", c);
    }
}
