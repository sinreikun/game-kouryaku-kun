namespace GameKouryakuKun.Core.Observation;

/// <summary>
/// S2 Sensor Stack Assembler（docs/s2-sensor-stack-contract.md）。
/// 既存 StructuredObservation / GlobalTransformConfidence / EntityLayer / EntityExperience を読み、Observation Bundle を派生する。
/// GT・Track・しきい値・EntityExperience 学習の再計算はしない。欠測層は捏造せず layers で明示する。
/// </summary>
public static class SensorStackAssembler
{
    public const string AssemblerId = "sensor_stack_assembler@3";
    public const string AssemblerVersion = "3";

    /// <summary>
    /// 主経路: 現行 StructuredObservation から Bundle を作る（純粋・副作用なし）。
    /// entityExperience を渡すと集計スナップショットを付帯。null なら layers.entity_experience = not_observed。
    /// </summary>
    public static ObservationBundle FromStructured(
        StructuredObservation obs,
        RawRef? raw = null,
        bool includeTextLines = false,
        EntityExperience? entityExperience = null,
        GameKouryakuKun.Core.Causality.CausalComparisonRecord? causalComparison = null)
    {
        ArgumentNullException.ThrowIfNull(obs);

        var ent = obs.EntityLayer;
        var gt = ent?.GlobalTransform;
        var eeSnap = entityExperience?.Snapshot();
        var layers = new LayerStatusMap
        {
            Raw = raw == null ? LayerStatus.NotObserved : LayerStatus.Observed,
            CoordinateFrame = FrameStatus(ent, gt),
            ChangeField = RegionStatus(obs.Regions),
            Candidates = EntityStatus(ent),
            Tracks = EntityStatus(ent),
            Relations = EntityStatus(ent),
            Text = obs.Text.Status.Contains(LayerStatus.Observed, StringComparison.Ordinal)
                ? (obs.Text.Status.Contains(LayerStatus.Inherited, StringComparison.Ordinal)
                    ? LayerStatus.Observed + "+" + LayerStatus.Inherited
                    : LayerStatus.Observed)
                : obs.Text.Status,
            Numbers = obs.Text.Numbers.Count > 0 ? LayerStatus.Observed : (
                obs.Text.Status is LayerStatus.NotObserved or LayerStatus.NotImplemented
                    ? obs.Text.Status
                    : LayerStatus.Observed),
            Temporal = string.IsNullOrEmpty(obs.Temporal.Status) ? LayerStatus.Observed : obs.Temporal.Status,
            EntityExperience = eeSnap == null ? LayerStatus.NotObserved : LayerStatus.Observed,
            CausalComparison = causalComparison == null ? LayerStatus.NotObserved : LayerStatus.Observed,
        };

        CoordinateFrameResult? frame = gt == null ? null : CopyFrame(gt);
        ChangeFieldSnapshot? change = CopyChange(obs.Regions, ent);

        var candidates = ent?.Candidates.Select(CopyCandidate).ToList()
            ?? new List<CandidateSnapshot>();
        var tracks = BuildTracks(ent);
        var relations = ent?.Relations.Select(r =>
                new RelationSnapshot(r.TrackA, r.TrackB, r.Dx, r.Dy, r.Distance, r.Change, r.Adjacent, r.Overlap, r.SameMove)).ToList()
            ?? new List<RelationSnapshot>();

        var text = new TextSnapshotRef
        {
            Status = obs.Text.Status,
            ReadKind = obs.Text.ReadKind,
            DiffKind = obs.Text.DiffKind,
            LineCount = obs.Text.Lines.Count,
            Observed = obs.Text.Observed,
            Inherited = obs.Text.Inherited,
            ActionCaused = obs.Text.ActionCaused,
            Lines = includeTextLines ? obs.Text.Lines : null,
        };
        var numbers = obs.Text.Numbers
            .Select(n => new NumberSnapshotRef(n.Cell, n.Index, n.Value, n.Previous, n.Series))
            .ToList();

        var temporal = new TemporalSnapshot
        {
            Status = obs.Temporal.Status,
            BoundaryMs = obs.Temporal.BoundaryMs,
            Changed = obs.Temporal.Changed,
            NaturalChange = obs.Temporal.NaturalChange,
            Delayed = obs.Temporal.Delayed is { } d
                ? new DelayedChangeSnapshot(d.FromStep, d.AfterMs, d.Cells, d.CellList, d.Late)
                : null,
        };

        return new ObservationBundle
        {
            SchemaVersion = ObservationBundle.CurrentSchema,
            Step = obs.Step,
            Raw = raw ?? new RawRef("none"),
            CoordinateFrame = frame,
            ChangeField = change,
            Candidates = candidates,
            Tracks = tracks,
            Relations = relations,
            Text = text,
            Numbers = numbers,
            Temporal = temporal,
            EntityExperience = eeSnap,
            CausalComparison = causalComparison,
            Provenance = obs.Provenance,
            Layers = layers,
            Source = new BundleSource("structured_observation", obs.Schema, AssemblerId),
        };
    }

    private static string FrameStatus(EntityLayer? ent, GlobalTransformConfidence? gt)
    {
        if (ent == null) return LayerStatus.NotImplemented;
        if (gt == null) return LayerStatus.NotObserved;
        return LayerStatus.Observed;
    }

    private static string RegionStatus(RegionLayer regions)
    {
        if (string.IsNullOrEmpty(regions.Status)) return LayerStatus.Observed;
        return regions.Status;
    }

    private static string EntityStatus(EntityLayer? ent)
    {
        if (ent == null) return LayerStatus.NotImplemented;
        return string.IsNullOrEmpty(ent.Status) ? LayerStatus.Observed : ent.Status;
    }

    private static CoordinateFrameResult CopyFrame(GlobalTransformConfidence g) => new()
    {
        Decision = g.Decision,
        Accepted = g.Accepted,
        Method = g.Method,
        PField = g.PField,
        PTranslation = g.PTranslation,
        PZero = g.PZero,
        PNone = g.PNone,
        LogOdds = g.LogOdds,
        Field = (double[])g.Field.Clone(),
        Inliers = g.Inliers,
        Points = g.Points,
        Coverage = g.Coverage,
        MedianResidual = g.MedianResidual,
        ErrorReduction = g.ErrorReduction,
        Continuity = g.Continuity,
    };

    private static ChangeFieldSnapshot CopyChange(RegionLayer regions, EntityLayer? ent) => new()
    {
        ChangedCells = regions.ChangedCells,
        RawChangedCells = regions.RawChangedCells,
        CommonShift = regions.CommonShift ?? ent?.CommonShift,
        Regions = regions.Regions.Select(r =>
            new ChangeRegionSnapshot(r.Id, r.Track, r.X0, r.Y0, r.X1, r.Y1, r.Cells, r.Causality, r.NaturalRate, r.NewTrack)).ToList(),
        ResidualCells = ent?.ResidualCells,
    };

    private static CandidateSnapshot CopyCandidate(EntityCandidate c) => new()
    {
        Id = c.Id,
        Track = c.Track,
        X0 = c.X0,
        Y0 = c.Y0,
        X1 = c.X1,
        Y1 = c.Y1,
        Cx = c.Cx,
        Cy = c.Cy,
        Cells = c.Cells,
        Appearance = c.Appearance,
        AppearanceState = c.AppearanceState,
        Dx = c.Dx,
        Dy = c.Dy,
        Moved = c.Moved,
        AppearanceChanged = c.AppearanceChanged,
        New = c.New,
        Confidence = c.Confidence,
        Age = c.Age,
        IdentityPosterior = c.IdentityPosterior,
        Continuous = c.Continuous,
        Lifetime = c.Lifetime,
        PeriodicInPlace = c.PeriodicInPlace,
    };

    private static IReadOnlyList<TrackSnapshot> BuildTracks(EntityLayer? ent)
    {
        if (ent == null) return Array.Empty<TrackSnapshot>();
        var list = new List<TrackSnapshot>();
        foreach (var c in ent.Candidates)
        {
            list.Add(new TrackSnapshot
            {
                Track = c.Track,
                Present = true,
                CandidateId = c.Id,
                Lifetime = c.Lifetime,
                Age = c.Age,
                New = c.New,
                IdentityPosterior = c.IdentityPosterior,
            });
        }
        foreach (var id in ent.Disappeared)
        {
            if (list.Any(t => t.Track == id)) continue;
            list.Add(new TrackSnapshot { Track = id, Present = false, New = false });
        }
        return list;
    }
}
