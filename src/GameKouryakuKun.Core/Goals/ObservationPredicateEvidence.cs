using GameKouryakuKun.Core.Observation;

namespace GameKouryakuKun.Core.Goals;

/// <summary>
/// S6: Bundle / EntityExperience / Structured から形式述語を採点（アルゴリズム KEEP・ゲーム意味禁止）。
/// </summary>
public static class ObservationPredicateEvidence
{
    /// <summary>根拠: EntityExperience 関係表の最小予測回数（偶発除外の形式床）。</summary>
    public const int MinRelationPredicted = 3;
    /// <summary>根拠: 関係ヒット率の形式床（偶然 0.5 より上）。</summary>
    public const double MinRelationHitRate = 0.55;
    /// <summary>根拠: 数字系列の最小長（Series が変化を語れる長さ）。</summary>
    public const int MinNumberSeriesLen = 2;

    public sealed class EvidenceHit
    {
        public string PredicateId { get; init; } = "";
        public string Status { get; init; } = "not_enough_evidence"; // scored | not_enough_evidence
        public string? CoarseNode { get; init; }
        public string? Action { get; init; }
        public string? Note { get; init; }
        public IReadOnlyDictionary<string, string>? Args { get; init; }
        public double PeProxy { get; init; }
        public double CtrlProxy { get; init; }
        public double OptionsProxy { get; init; }
    }

    /// <summary>Track 関係再現: Bundle Relations / EntityExperience relation 表。</summary>
    public static EvidenceHit TrackRelation(
        ObservationBundle? bundle,
        EntityExperienceSnapshot? ee,
        StructuredObservation? structured,
        string? coarseNode = null)
    {
        ee ??= bundle?.EntityExperience;
        var rels = bundle?.Relations;
        if (rels == null || rels.Count == 0)
            rels = structured?.EntityLayer?.Relations?.Select(r => new RelationSnapshot(
                r.TrackA, r.TrackB, r.Dx, r.Dy, r.Distance, r.Change, r.Adjacent, r.Overlap, r.SameMove)).ToList();

        var relTable = ee?.Tables.FirstOrDefault(t => t.Table == "relation");
        long pred = (relTable?.PredictedStable ?? 0) + (relTable?.PredictedUnstable ?? 0);
        long hits = (relTable?.HitsStable ?? 0) + (relTable?.HitsUnstable ?? 0);
        double hitRate = pred > 0 ? (double)hits / pred : 0;

        bool hasLive = rels != null && rels.Count > 0
            && rels.Any(r => r.Change is not ("new" or "") && (r.Adjacent || r.Overlap || r.SameMove || r.Change != "same"));
        bool hasFollow = ee?.Follows is { Count: > 0 };

        if (!hasLive && pred < MinRelationPredicted && !hasFollow)
        {
            return new EvidenceHit
            {
                PredicateId = ObservationPredicateIds.TrackRelationReproduce,
                Status = "not_enough_evidence",
                CoarseNode = coarseNode,
                Note = "no_bundle_relations_and_ee_relation_sparse",
            };
        }

        string? pair = null;
        if (rels is { Count: > 0 })
        {
            var top = rels.OrderByDescending(r => (r.Adjacent ? 2 : 0) + (r.Overlap ? 2 : 0) + (r.SameMove ? 1 : 0))
                .ThenBy(r => r.TrackA).First();
            pair = $"{top.TrackA}:{top.TrackB}:{top.Change}";
        }
        else if (hasFollow)
        {
            var f = ee!.Follows[0];
            pair = $"follow:{f.TrackA}->{f.TrackB}";
        }

        double pe = hasLive ? 0.4 : Math.Clamp(1.0 - hitRate, 0, 1) * 0.5;   // 根拠: 関係が観測できるほど再試験価値の代理
        if (pred >= MinRelationPredicted && hitRate < MinRelationHitRate) pe = Math.Max(pe, 0.55);
        double ctrl = pred >= MinRelationPredicted ? Math.Clamp(hitRate, 0, 1) : (hasLive ? 0.35 : 0.1);
        double opt = hasFollow ? 0.3 : (rels?.Count >= 2 ? 0.2 : 0.05);

        return new EvidenceHit
        {
            PredicateId = ObservationPredicateIds.TrackRelationReproduce,
            Status = "scored",
            CoarseNode = coarseNode,
            Note = "from_bundle_or_entity_experience; formal_relation_only",
            Args = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pair"] = pair ?? "",
                ["ee_rel_pred"] = pred.ToString(),
                ["ee_rel_hit"] = hitRate.ToString("0.00"),
                ["live_rels"] = (rels?.Count ?? 0).ToString(),
            },
            PeProxy = Math.Round(pe, 4),
            CtrlProxy = Math.Round(ctrl, 4),
            OptionsProxy = Math.Round(opt, 4),
        };
    }

    /// <summary>数字系列変化試験: Structured Text.Numbers / Bundle 経由の NumberObs。</summary>
    public static EvidenceHit NumberSeries(
        ObservationBundle? bundle,
        StructuredObservation? structured,
        string? coarseNode = null,
        string? action = null)
    {
        var nums = structured?.Text?.Numbers;
        // Bundle には Number 専用層が無いので Structured を主源にする（Assembler KEEP）
        if (nums == null || nums.Count == 0)
        {
            return new EvidenceHit
            {
                PredicateId = ObservationPredicateIds.NumberSeriesChangeTest,
                Status = "not_enough_evidence",
                CoarseNode = coarseNode,
                Action = action,
                Note = "no_structured_numbers",
            };
        }

        var changing = nums.Where(n => n.Series != null && n.Series.Count >= MinNumberSeriesLen
            && (n.Previous is double p && Math.Abs(p - n.Value) > 1e-9
                || n.Series.Distinct().Count() >= 2)).ToList();
        if (changing.Count == 0)
        {
            // 数字はあるが変化証拠が薄い
            var anySeries = nums.Any(n => n.Series != null && n.Series.Count >= MinNumberSeriesLen);
            return new EvidenceHit
            {
                PredicateId = ObservationPredicateIds.NumberSeriesChangeTest,
                Status = anySeries ? "scored" : "not_enough_evidence",
                CoarseNode = coarseNode,
                Action = action,
                Note = anySeries ? "numbers_present_series_stable; retest_candidate" : "numbers_without_series",
                Args = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["numbers"] = nums.Count.ToString(),
                    ["changing"] = "0",
                },
                PeProxy = anySeries ? 0.35 : 0,
                CtrlProxy = 0.2,
                OptionsProxy = 0.05,
            };
        }

        var top = changing.OrderByDescending(n => n.Series.Count).First();
        return new EvidenceHit
        {
            PredicateId = ObservationPredicateIds.NumberSeriesChangeTest,
            Status = "scored",
            CoarseNode = coarseNode,
            Action = action,
            Note = "from_structured_number_series; formal_value_only",
            Args = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cell"] = top.Cell.ToString(),
                ["index"] = top.Index.ToString(),
                ["series_len"] = top.Series.Count.ToString(),
                ["changing"] = changing.Count.ToString(),
            },
            PeProxy = 0.5,
            CtrlProxy = 0.4,
            OptionsProxy = Math.Clamp(changing.Count / 5.0, 0, 1),
        };
    }

    public static ObservationGoal ToGoal(EvidenceHit hit, long id = 0) => new()
    {
        Id = id,
        Predicate = new ObservationPredicate
        {
            Id = hit.PredicateId,
            Status = hit.Status,
            CoarseNode = hit.CoarseNode,
            Action = hit.Action,
            Note = hit.Note,
            Args = hit.Args,
        },
        ExpectedValue = new GoalExpectedValue
        {
            PeReduction = hit.PeProxy,
            Controllability = hit.CtrlProxy,
            OptionsGain = hit.OptionsProxy,
        },
        Origin = "bundle_entity_structured",
        Status = hit.Status == "scored" ? "parallel_log" : "proposed",
        Note = "predicate_from_evidence; not_reward_counts",
    };
}
