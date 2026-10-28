using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class PredictiveWmShortHorizonTests : IDisposable
{
    private static readonly EntityLayer Empty =
        new(LayerStatus.Observed, Array.Empty<EntityCandidate>(), Array.Empty<int>(), Array.Empty<RelationObs>());
    private static readonly int Mv = Array.IndexOf(LocalEventModel.Events, "entity_moved");

    public PredictiveWmShortHorizonTests()
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
    public void Forecast_wraps_local_event_predict_without_rewrite()
    {
        var m = new LocalEventModel();
        for (int i = 0; i < 5; i++)
            m.Observe("n1", "button1", false, new[] { Mv }, Empty);

        var f = PredictiveWmShortHorizon.Forecast(new ShortHorizonInput("n1", "button1", m));
        Assert.Equal(LayerStatus.Observed, f.Status);
        Assert.Equal(LocalEventModel.Events.Length, f.Factors.Count);
        Assert.Contains(f.Factors, x => x.Name == "entity_moved" && x.P > 0.5);
        Assert.DoesNotContain("キャラ", f.Note ?? "");
        Assert.Equal(PredictiveWmShortHorizon.SurfaceId, f.Surface);
    }

    [Fact]
    public void Forecast_marks_miss_from_high_surprise()
    {
        var m = new LocalEventModel();
        m.Observe("n1", "a", false, new[] { Mv }, Empty);
        var surp = Enumerable.Repeat(1.5, LocalEventModel.Events.Length).ToArray(); // > ln2
        var f = PredictiveWmShortHorizon.Forecast(new ShortHorizonInput(
            "n1", "a", m, OccurredEvents: new[] { Mv }, Surprise: surp));
        Assert.True(f.PredictionMiss);
        Assert.True(f.MeanSurprise > PredictiveWmShortHorizon.SurpriseMissThreshold);
    }

    [Fact]
    public void H3_scores_when_short_horizon_observed()
    {
        var m = new LocalEventModel();
        for (int i = 0; i < 3; i++) m.Observe("n1", "x", false, new[] { Mv }, Empty, "0,0");
        for (int i = 0; i < 3; i++) m.Observe("n1", "x", false, Array.Empty<int>(), Empty, "1,1");

        var r = CausalHypothesisComparer.Compare(new CausalCompareInput(
            9, "x", "n1", true, "agent",
            LocalEvents: m, LocalContextKey: "0,0", ObservedLocalEvents: new[] { Mv }));
        var h3 = r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H3Context);
        Assert.Equal("scored", h3.Status);
        Assert.Contains("local_event_model", r.EvidenceSources);
        Assert.Contains("predictive_wm_short_horizon", r.EvidenceSources);
        Assert.Equal(LayerStatus.Observed, r.Layers.PredictiveWmShortHorizon);
        Assert.Equal(LayerStatus.Observed, r.Layers.PredictionErrorLink);
        Assert.NotNull(r.ShortHorizon);
        Assert.DoesNotContain("中央最大", r.ToJson());
    }

    [Fact]
    public void H3_strong_when_ContextHelps_and_can_be_preferred()
    {
        var m = new LocalEventModel();
        // ContextHelps needs ContextMinN + BF + LOO — mirror BlockV0309 scale
        for (int i = 0; i < 40; i++)
        {
            m.Observe("n1", "x", false, new[] { Mv }, Empty, "A");
            m.Observe("n1", "x", false, Array.Empty<int>(), Empty, "B");
        }
        Assert.True(m.ContextHelps("n1", "x", Mv), "fixture must meet ContextHelps gate");

        var r = CausalHypothesisComparer.Compare(new CausalCompareInput(
            10, "x", "n1", true, "agent",
            LocalEvents: m, LocalContextKey: "A", ObservedLocalEvents: new[] { Mv }));
        var h3 = r.Hypotheses.Single(h => h.Id == CausalHypothesisIds.H3Context);
        Assert.Equal("scored", h3.Status);
        Assert.True(h3.LogBf is double bf && bf >= SplitReview.RevertMinLogBayesFactor, h3.Note);
        Assert.Equal(CausalHypothesisIds.H3Context, r.Preferred);
        Assert.Contains("context_helps", h3.Note!);
    }

    [Fact]
    public void Prediction_miss_routes_pe_tag_into_closed_loop()
    {
        var m = new LocalEventModel();
        for (int i = 0; i < 4; i++) m.Observe("n1", "x", false, new[] { Mv }, Empty);
        var surp = Enumerable.Repeat(2.0, LocalEventModel.Events.Length).ToArray();
        var r = CausalHypothesisComparer.Compare(new CausalCompareInput(
            11, "x", "n1", true, "agent",
            LocalEvents: m, LocalContextKey: "none", ObservedLocalEvents: Array.Empty<int>(), LocalSurprise: surp,
            ResidualCellCount: 3));
        Assert.True(r.ShortHorizon!.PredictionMiss);
        Assert.Contains(PeRoutingTags.RepresentationCoarse, r.PeRoutingTags);
        var loop = new PeClosedLoopRecorder();
        var cands = loop.Observe(r, runId: 1);
        Assert.True(cands.Count >= 1);
        Assert.DoesNotContain(cands, c => c.Tag.Contains("キャラ"));
    }

    [Fact]
    public void Contract_and_audit_v0339_exist()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s3-causal-wm-contract.md"));
        Assert.True(text.Contains("Predictive WM", StringComparison.Ordinal) || text.Contains("短時間"));
        Assert.Contains("H3", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.33.9.md")));
    }
}
