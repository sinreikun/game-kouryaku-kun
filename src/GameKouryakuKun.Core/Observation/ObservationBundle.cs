using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameKouryakuKun.Core.Observation;

/// <summary>
/// S2 Observation Bundle（docs/s2-sensor-stack-contract.md）。
/// Sensor Stack の単一面。Meaning 欄なし。アルゴリズム本体は持たない（Assembler が既存観測から派生）。
/// </summary>
public sealed record ObservationBundle
{
    public const int CurrentSchema = 1;   // 根拠: Bundle 契約の最初の版（docs/s2-sensor-stack-contract.md）

    public int SchemaVersion { get; init; } = CurrentSchema;
    public long Step { get; init; }
    public RawRef? Raw { get; init; }
    public CoordinateFrameResult? CoordinateFrame { get; init; }
    public ChangeFieldSnapshot? ChangeField { get; init; }
    public IReadOnlyList<CandidateSnapshot> Candidates { get; init; } = Array.Empty<CandidateSnapshot>();
    public IReadOnlyList<TrackSnapshot> Tracks { get; init; } = Array.Empty<TrackSnapshot>();
    public IReadOnlyList<RelationSnapshot> Relations { get; init; } = Array.Empty<RelationSnapshot>();
    public TextSnapshotRef? Text { get; init; }
    public IReadOnlyList<NumberSnapshotRef> Numbers { get; init; } = Array.Empty<NumberSnapshotRef>();
    public TemporalSnapshot? Temporal { get; init; }
    public string Provenance { get; init; } = "unknown";
    public LayerStatusMap Layers { get; init; } = new();
    public BundleSource Source { get; init; } = new("structured_observation", StructuredObservation.CurrentSchema, SensorStackAssembler.AssemblerId);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>画素フル保存はしない。参照または無し。</summary>
public sealed record RawRef(string Kind, string? Hash = null);

/// <summary>Global Transform 結果の契約面コピー（v0.32.9 KEEP）。</summary>
public sealed record CoordinateFrameResult
{
    public string Decision { get; init; } = "none";
    public bool Accepted { get; init; }
    public string Method { get; init; } = "model_comparison";
    public double PField { get; init; }
    public double PTranslation { get; init; }
    public double PZero { get; init; }
    public double PNone { get; init; }
    public double LogOdds { get; init; }
    public double[] Field { get; init; } = new double[8];
    public int Inliers { get; init; }
    public int Points { get; init; }
    public double Coverage { get; init; }
    public double MedianResidual { get; init; }
    public double ErrorReduction { get; init; }
    public double? Continuity { get; init; }
}

public sealed record ChangeFieldSnapshot
{
    public int ChangedCells { get; init; }
    public int? RawChangedCells { get; init; }
    public double[]? CommonShift { get; init; }
    public IReadOnlyList<ChangeRegionSnapshot> Regions { get; init; } = Array.Empty<ChangeRegionSnapshot>();
    public IReadOnlyList<int>? ResidualCells { get; init; }
}

public sealed record ChangeRegionSnapshot(
    int Id, int Track, int X0, int Y0, int X1, int Y1, int Cells, string Causality, double NaturalRate, bool NewTrack);

/// <summary>今フレームの局所仮説。形式名のみ（moved / appearance_changed 等）。ゲーム意味禁止。</summary>
public sealed record CandidateSnapshot
{
    public int Id { get; init; }
    public int Track { get; init; }
    public int X0 { get; init; }
    public int Y0 { get; init; }
    public int X1 { get; init; }
    public int Y1 { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }
    public int Cells { get; init; }
    public string Appearance { get; init; } = "";
    public string AppearanceState { get; init; } = "";
    public double Dx { get; init; }
    public double Dy { get; init; }
    public bool Moved { get; init; }
    public bool AppearanceChanged { get; init; }
    public bool New { get; init; }
    public double Confidence { get; init; }
    public int Age { get; init; }
    public double? IdentityPosterior { get; init; }
    public bool Continuous { get; init; }
    public long Lifetime { get; init; }
    public bool PeriodicInPlace { get; init; }
}

/// <summary>時間的連続性の仮説（ID）。Identity ≠ Appearance。</summary>
public sealed record TrackSnapshot
{
    public int Track { get; init; }
    public bool Present { get; init; }
    public int? CandidateId { get; init; }
    public long Lifetime { get; init; }
    public int Age { get; init; }
    public bool New { get; init; }
    public double? IdentityPosterior { get; init; }
}

public sealed record RelationSnapshot(
    int TrackA, int TrackB, double Dx, double Dy, double Distance, string Change, bool Adjacent, bool Overlap, bool SameMove);

public sealed record TextSnapshotRef
{
    public string Status { get; init; } = LayerStatus.NotObserved;
    public string ReadKind { get; init; } = "";
    public string DiffKind { get; init; } = "";
    public int LineCount { get; init; }
    public int Observed { get; init; }
    public int Inherited { get; init; }
    public double ActionCaused { get; init; }
    /// <summary>行テキストは観測事実として任意で保持（意味付けしない）。巨大化回避のため Assembler 既定は要約のみ。</summary>
    public IReadOnlyList<TextLineObs>? Lines { get; init; }
}

public sealed record NumberSnapshotRef(int Cell, int Index, double Value, double? Previous, IReadOnlyList<double> Series);

public sealed record TemporalSnapshot
{
    public string Status { get; init; } = LayerStatus.NotObserved;
    public double BoundaryMs { get; init; }
    public bool Changed { get; init; }
    public double? NaturalChange { get; init; }
    public DelayedChangeSnapshot? Delayed { get; init; }
}

public sealed record DelayedChangeSnapshot(long FromStep, double AfterMs, int Cells, IReadOnlyList<int> CellList, bool Late);

public sealed record LayerStatusMap
{
    public string Raw { get; init; } = LayerStatus.NotObserved;
    public string CoordinateFrame { get; init; } = LayerStatus.NotImplemented;
    public string ChangeField { get; init; } = LayerStatus.NotImplemented;
    public string Candidates { get; init; } = LayerStatus.NotImplemented;
    public string Tracks { get; init; } = LayerStatus.NotImplemented;
    public string Relations { get; init; } = LayerStatus.NotImplemented;
    public string Text { get; init; } = LayerStatus.NotImplemented;
    public string Numbers { get; init; } = LayerStatus.NotImplemented;
    public string Temporal { get; init; } = LayerStatus.NotImplemented;
}

public sealed record BundleSource(string Kind, int Schema, string Assembler);

/// <summary>
/// EntityExperience 本移植は後続。S2 スライスでは契約上のスタブ印のみ。
/// </summary>
public interface IEntityExperienceStub
{
    /// <summary>将来 Track ごとの予測可能統計を Bundle 付帯する入口。本スライスでは未配線。</summary>
    string StubId { get; }
}

public sealed class EntityExperienceStubPlaceholder : IEntityExperienceStub
{
    public string StubId => "entity_experience/stub@0";
}
