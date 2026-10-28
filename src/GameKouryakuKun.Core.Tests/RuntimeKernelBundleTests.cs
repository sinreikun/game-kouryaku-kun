using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class RuntimeKernelBundleTests : IDisposable
{
    public RuntimeKernelBundleTests()
    {
        RuntimeFlags.UiEnabled = false;
        RuntimeFlags.UseExperienceVNextWriter = false;
        Environment.SetEnvironmentVariable(RuntimeFlags.EnvName, null);
        Environment.SetEnvironmentVariable(RuntimeFlags.BundleEnvName, null);
        RuntimeFlags.ApplyEffective();
        RuntimeFlags.ApplyBundleEffective();
    }

    public void Dispose()
    {
        RuntimeFlags.UiEnabled = false;
        RuntimeFlags.UseExperienceVNextWriter = false;
        RuntimeFlags.AttachObservationBundle = false;
        Environment.SetEnvironmentVariable(RuntimeFlags.EnvName, null);
        Environment.SetEnvironmentVariable(RuntimeFlags.BundleEnvName, null);
        RuntimeFlags.ApplyEffective();
        RuntimeFlags.ApplyBundleEffective();
    }

    [Fact]
    public void ResolveObservation_prefers_bundle_as_canonical()
    {
        var bundleJson = """{"schema_version":2,"step":1,"provenance":"agent","candidates":[],"tracks":[],"relations":[],"numbers":[],"layers":{},"source":{"kind":"structured_observation","schema":1,"assembler":"sensor_stack_assembler@2"}}""";
        var ctx = new StepContext(
            ObservationKind.Structured,
            ObservationJson: """{"schema":1,"step":1}""",
            ObservationSchemaVersion: 1,
            ObservationBundleJson: bundleJson);
        var r = RuntimeKernel.ResolveObservation(ctx);
        Assert.Equal(ObservationKind.Bundle, r.Kind);
        Assert.Equal("bundle", r.Surface);
        Assert.Equal(bundleJson, r.ObservationJson);
        Assert.Equal(2, r.SchemaVersion);
        Assert.True(r.PromotedFromStructured);
        Assert.Equal("""{"schema":1,"step":1}""", r.LegacyStructuredJson);
    }

    [Fact]
    public void ResolveObservation_without_bundle_keeps_structured()
    {
        var ctx = new StepContext(ObservationKind.Structured, ObservationJson: "{}", ObservationSchemaVersion: 1);
        var r = RuntimeKernel.ResolveObservation(ctx);
        Assert.Equal(ObservationKind.Structured, r.Kind);
        Assert.Equal("structured", r.Surface);
        Assert.False(r.PromotedFromStructured);
    }

    [Fact]
    public void Step_writes_bundle_kind_when_bundle_json_present()
    {
        RuntimeFlags.UseExperienceVNextWriter = true;
        using var d = new TempDir();
        var profile = d["p"];
        Directory.CreateDirectory(profile);
        var obs = new StructuredObservation(
            StructuredObservation.CurrentSchema, 3, "agent", "button1", "a", "b",
            new GlobalScene("n", "m", "1,1", "v", null, LayerStatus.Observed),
            new RegionLayer(LayerStatus.Observed, 0, Array.Empty<ChangeRegion>()),
            new TextLayer(LayerStatus.NotObserved, "none", "unknown", Array.Empty<TextLineObs>(), Array.Empty<NumberObs>(), 0, 0, 0),
            new TemporalLayer(0, false, null, LayerStatus.Observed));
        var bundle = SensorStackAssembler.FromStructured(obs);
        using var k = RuntimeKernel.Open(profile, "0.33.6");
        var result = k.Step(
            new ActionSpec("button1"),
            new StepContext(
                ObservationKind.Structured,
                Seq: 1,
                ObservationJson: obs.ToJson(),
                ObservationSchemaVersion: obs.Schema,
                ObservationBundleJson: bundle.ToJson(),
                CallSite: "test"));
        Assert.True(result.Wrote);
        Assert.Equal("bundle", result.CanonicalSurface);
        Assert.NotNull(result.Record);
        Assert.Equal(ObservationKind.Bundle, result.Record!.ObservationKind);
        Assert.Contains("schema_version", result.Record.ObservationJson!);
        Assert.Contains("observation_surface", result.Record.ProvenanceJson);
        Assert.Contains("legacy_structured_json", result.Record.ProvenanceJson);
        Assert.DoesNotContain("キャラ", result.Record.ObservationJson!);
        Assert.DoesNotContain("中央最大", result.Record.ObservationJson!);
    }
}
