using System.Text.Json;
using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>S2 Sensor Stack Assembler（v0.33.5）。既存観測からの派生のみ。意味ラベル禁止。</summary>
[Collection("RuntimeFlagsSerial")]
public class SensorStackAssemblerTests : IDisposable
{
    private readonly string? _prevBundleEnv;

    public SensorStackAssemblerTests()
    {
        _prevBundleEnv = Environment.GetEnvironmentVariable(RuntimeFlags.BundleEnvName);
        Environment.SetEnvironmentVariable(RuntimeFlags.BundleEnvName, null);
        RuntimeFlags.AttachObservationBundle = false;
        RuntimeFlags.ApplyBundleEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.BundleEnvName, _prevBundleEnv);
        RuntimeFlags.AttachObservationBundle = false;
        RuntimeFlags.ApplyBundleEffective();
    }

    private static StructuredObservation MinimalObs(EntityLayer? ent = null, GlobalTransformConfidence? gt = null)
    {
        EntityLayer? e = ent;
        if (gt != null)
        {
            e = (ent ?? new EntityLayer(LayerStatus.Observed, Array.Empty<EntityCandidate>(), Array.Empty<int>(), Array.Empty<RelationObs>()))
                with { GlobalTransform = gt };
        }
        var o = new StructuredObservation(
            StructuredObservation.CurrentSchema,
            Step: 7,
            Provenance: "agent",
            Action: "button1",
            Before: "a",
            After: "b",
            Global: new GlobalScene("n", "m", "1,1", "v", null, LayerStatus.Observed),
            Regions: new RegionLayer(LayerStatus.Observed, 3, new[]
            {
                new ChangeRegion(0, 1, 0, 0, 2, 2, 3, "unknown", -1, true),
            }) { RawChangedCells = 5, CommonShift = new[] { 0.0, 1.5 } },
            Text: new TextLayer(LayerStatus.Observed, "full", "unpaired", Array.Empty<TextLineObs>(), Array.Empty<NumberObs>(), 0, 0, 0),
            Temporal: new TemporalLayer(1200, true, 0.1, LayerStatus.Observed));
        return o with { EntityLayer = e };
    }

    [Fact]
    public void Assembles_coordinate_frame_roundtrip_from_gt()
    {
        var gt = new GlobalTransformConfidence
        {
            Decision = "field",
            Accepted = true,
            Method = "model_comparison",
            PField = 0.7,
            PTranslation = 0.2,
            PZero = 0.05,
            PNone = 0.05,
            LogOdds = 1.2,
            Field = new[] { 0, 0, 0, 0, 0, 0, 0.4, 0 },
            Inliers = 10,
            Points = 12,
            Coverage = 0.75,
            MedianResidual = 0.2,
            ErrorReduction = 0.6,
            Continuity = 0.1,
        };
        var bundle = SensorStackAssembler.FromStructured(MinimalObs(gt: gt));
        Assert.Equal(ObservationBundle.CurrentSchema, bundle.SchemaVersion);
        Assert.Equal(7, bundle.Step);
        Assert.Equal("agent", bundle.Provenance);
        Assert.Equal(LayerStatus.Observed, bundle.Layers.CoordinateFrame);
        var f = Assert.IsType<CoordinateFrameResult>(bundle.CoordinateFrame);
        Assert.Equal("field", f.Decision);
        Assert.True(f.Accepted);
        Assert.Equal(0.7, f.PField);
        Assert.Equal(0.4, f.Field[6]);
        Assert.Equal(10, f.Inliers);
        Assert.Equal(0.1, f.Continuity);
        Assert.Equal(SensorStackAssembler.AssemblerId, bundle.Source.Assembler);
    }

    [Fact]
    public void Missing_entity_layer_is_explicit_not_implemented()
    {
        var bundle = SensorStackAssembler.FromStructured(MinimalObs(ent: null));
        Assert.Equal(LayerStatus.NotImplemented, bundle.Layers.CoordinateFrame);
        Assert.Equal(LayerStatus.NotImplemented, bundle.Layers.Candidates);
        Assert.Equal(LayerStatus.NotImplemented, bundle.Layers.Tracks);
        Assert.Equal(LayerStatus.NotImplemented, bundle.Layers.Relations);
        Assert.Null(bundle.CoordinateFrame);
        Assert.Empty(bundle.Candidates);
        Assert.Empty(bundle.Tracks);
        Assert.Equal(LayerStatus.Observed, bundle.Layers.ChangeField);
        Assert.NotNull(bundle.ChangeField);
        Assert.Equal(3, bundle.ChangeField!.ChangedCells);
        Assert.Equal(5, bundle.ChangeField.RawChangedCells);
    }

    [Fact]
    public void Entity_without_gt_marks_coordinate_frame_not_observed()
    {
        var ent = new EntityLayer(LayerStatus.Observed,
            new[]
            {
                new EntityCandidate(1, 9, 0, 0, 1, 1, 0.5, 0.5, 2, "h", "A", 1, 0, true, false, false, 0.9, 3)
                {
                    Continuous = true, IdentityPosterior = 1.5, Lifetime = 4,
                },
            },
            new[] { 2 },
            new[] { new RelationObs(9, 3, 1, 0, 1, "same", true, false, true) });
        var bundle = SensorStackAssembler.FromStructured(MinimalObs(ent));
        Assert.Equal(LayerStatus.NotObserved, bundle.Layers.CoordinateFrame);
        Assert.Null(bundle.CoordinateFrame);
        Assert.Equal(LayerStatus.Observed, bundle.Layers.Candidates);
        Assert.Single(bundle.Candidates);
        Assert.Equal(9, bundle.Candidates[0].Track);
        Assert.Equal(2, bundle.Tracks.Count); // present + disappeared
        Assert.Contains(bundle.Tracks, t => t.Track == 9 && t.Present);
        Assert.Contains(bundle.Tracks, t => t.Track == 2 && !t.Present);
        Assert.Single(bundle.Relations);
    }

    [Fact]
    public void Bundle_json_has_no_game_meaning_or_character_center_label()
    {
        var gt = new GlobalTransformConfidence { Decision = "translation", Accepted = true, Method = "translation_check", Field = new double[8] };
        var json = SensorStackAssembler.FromStructured(MinimalObs(gt: gt)).ToJson();
        Assert.Contains("coordinate_frame", json);
        Assert.Contains("change_field", json);
        Assert.DoesNotContain("キャラ", json);
        Assert.DoesNotContain("中央最大", json);
        Assert.DoesNotContain("character", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enemy", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("player", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hero", json, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(ObservationBundle.CurrentSchema, doc.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void Bundle_attach_flag_default_off_env_on()
    {
        RuntimeFlags.ApplyBundleEffective();
        Assert.False(RuntimeFlags.AttachObservationBundle);
        Assert.Contains("OFF", RuntimeFlags.BundleStatusText());
        Environment.SetEnvironmentVariable(RuntimeFlags.BundleEnvName, "1");
        RuntimeFlags.ApplyBundleEffective();
        Assert.True(RuntimeFlags.AttachObservationBundle);
        Assert.Contains("ON", RuntimeFlags.BundleStatusText());
    }

    [Fact]
    public void Contract_and_audit_files_exist()
    {
        var root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "docs", "s2-sensor-stack-contract.md")));
        Assert.True(File.Exists(Path.Combine(SrcRoot(), "audit", "v0.33.5.md")));
        Assert.True(File.Exists(Path.Combine(SrcRoot(), "audit", "v0.33.6.md")));
        Assert.True(File.Exists(Path.Combine(SrcRoot(), "audit", "v0.33.7.md")));
        Assert.True(File.Exists(Path.Combine(SrcRoot(), "audit", "v0.33.8.md")));
        Assert.True(File.Exists(Path.Combine(SrcRoot(), "audit", "v0.33.9.md")));
        var contract = File.ReadAllText(Path.Combine(root, "docs", "s2-sensor-stack-contract.md"));
        Assert.Contains("Observation Bundle", contract);
        Assert.Contains("中央最大＝キャラ", contract);
        Assert.Contains("GKK_ATTACH_OBSERVATION_BUNDLE", contract);
        Assert.Contains("EntityExperienceSnapshot", contract);
        Assert.Contains("ObservationKind.Bundle", contract);
        Assert.DoesNotContain("しきい値を緩める", contract);
    }

    [Fact]
    public void Entity_experience_snapshot_attaches_when_provided()
    {
        var ee = new EntityExperience();
        var prev = new EntityLayer(LayerStatus.Observed,
            new[] { new EntityCandidate(0, 7, 1, 1, 3, 3, 2, 2, 9, "a", "A", 0, 0, false, false, false, 1, 3) { Continuous = true, IdentityPosterior = 5 } },
            Array.Empty<int>(), Array.Empty<RelationObs>());
        var cur = new EntityLayer(LayerStatus.Observed,
            new[] { new EntityCandidate(0, 7, 1, 1, 3, 3, 2, 2, 9, "b", "A", 0, 0, false, true, false, 1, 3) { Continuous = true, IdentityPosterior = 5 } },
            Array.Empty<int>(), Array.Empty<RelationObs>());
        for (int i = 0; i < 5; i++) ee.Observe(prev, cur, "x", false, false);

        var bundle = SensorStackAssembler.FromStructured(MinimalObs(), entityExperience: ee);
        Assert.Equal(LayerStatus.Observed, bundle.Layers.EntityExperience);
        Assert.NotNull(bundle.EntityExperience);
        Assert.True(bundle.EntityExperience!.Observed >= 5);
        Assert.Contains(bundle.EntityExperience.Tables, tb => tb.Table == "appearance");
        Assert.Equal("b", ee.Predict(0, 7, "a", "x")); // algorithm unchanged
        var json = bundle.ToJson();
        Assert.Contains("entity_experience", json);
        Assert.DoesNotContain("キャラ", json);
        Assert.DoesNotContain("1-2 区画", json); // TrackQuality bins stay human-only
    }

    [Fact]
    public void Entity_experience_missing_is_not_observed_not_invented()
    {
        var bundle = SensorStackAssembler.FromStructured(MinimalObs());
        Assert.Equal(LayerStatus.NotObserved, bundle.Layers.EntityExperience);
        Assert.Null(bundle.EntityExperience);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        if (d == null) throw new DirectoryNotFoundException("GameKouryakuKun.sln が見つかりません");
        // sln は src/ 配下。契約はリポジトリ根、監査は src/audit
        return d.Parent!.FullName;
    }

    private static string SrcRoot() => Path.Combine(RepoRoot(), "src");
}
