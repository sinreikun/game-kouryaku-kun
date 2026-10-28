using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Observation;

namespace GameKouryakuKun.Core.Causality;

/// <summary>
/// S3 CausalHypothesisComparer（docs/s3-causal-wm-contract.md）。
/// 既存 NaturalBaseline / RegionCausality / delayed から H0–H4 比較を派生する。
/// しきい値・学習・Explorer は変えない。Action 後に起きたことだけで H1 を採用しない。
/// </summary>
public static class CausalHypothesisComparer
{
    public const string ComparerId = "causal_hypothesis_comparer@1";
    public const string ComparerVersion = "2";

    public static CausalComparisonRecord Compare(CausalCompareInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sources = new List<string>();
        var layers = new CausalLayerStatus { Comparer = LayerStatus.Observed };
        var scores = new Dictionary<string, HypothesisScore>(StringComparer.Ordinal);

        // --- H0 natural ---
        scores[CausalHypothesisIds.H0Natural] = ScoreH0(input, sources, layers);

        // --- H1 immediate (needs contrast; not action-after alone) ---
        scores[CausalHypothesisIds.H1Immediate] = ScoreH1(input, sources, layers);

        // --- H2 delayed ---
        scores[CausalHypothesisIds.H2Delayed] = ScoreH2(input, sources, layers);

        // --- H3 contextual (Predictive WM short-horizon WRAP) ---
        var sh = input.ShortHorizon ?? PredictiveWmShortHorizon.Forecast(new ShortHorizonInput(
            input.Node, input.Action, input.LocalEvents, input.LocalContextKey,
            input.ObservedLocalEvents, input.LocalSurprise, input.WorldModel, input.ActualNextNode, input.EntityExperience));
        scores[CausalHypothesisIds.H3Context] = ScoreH3(input, sh, sources, layers);

        // --- H4 stub ---
        scores[CausalHypothesisIds.H4CommonCause] = ScoreH4Hint(input, sources, layers);

        if (sh.Status == LayerStatus.Observed)
        {
            sources.Add("predictive_wm_short_horizon");
            layers.PredictiveWmShortHorizon = LayerStatus.Observed;
            layers.PredictionErrorLink = LayerStatus.Observed;
        }
        else
        {
            layers.PredictiveWmShortHorizon = LayerStatus.NotObserved;
            layers.PredictionErrorLink = LayerStatus.NotObserved;
        }

        var preferred = Prefer(scores);
        var tags = RoutePe(input, scores, preferred, sh);

        return new CausalComparisonRecord
        {
            SchemaVersion = CausalComparisonRecord.CurrentSchema,
            Step = input.Step,
            Action = input.Action,
            Node = input.Node,
            Changed = input.Changed,
            Provenance = input.Provenance,
            Hypotheses = CausalHypothesisIds.All.Select(id => scores[id]).ToList(),
            Preferred = preferred,
            PeRoutingTags = tags,
            EvidenceSources = sources.Distinct(StringComparer.Ordinal).ToList(),
            Layers = layers,
            Comparer = ComparerId,
            ShortHorizon = sh.Status == LayerStatus.Observed ? sh : null,
        };
    }

    private static HypothesisScore ScoreH3(CausalCompareInput input, ShortHorizonForecast sh, List<string> sources, CausalLayerStatus layers)
    {
        if (input.LocalEvents == null && input.ShortHorizon == null)
        {
            return new HypothesisScore
            {
                Id = CausalHypothesisIds.H3Context,
                Status = LayerStatus.NotObserved,
                Note = "no local_event_model / short_horizon input",
            };
        }

        if (sh.Status != LayerStatus.Observed)
        {
            return new HypothesisScore
            {
                Id = CausalHypothesisIds.H3Context,
                Status = LayerStatus.NotObserved,
                Note = sh.Note ?? "short_horizon not_observed",
            };
        }

        sources.Add("local_event_model");
        int help = sh.ContextHelpEvents;
        double l1 = sh.ContextDivergenceL1;

        // Strong: ContextHelps（既存 ln10 ゲート）が 1 件以上 → LogBf 下限 = RevertMinLogBayesFactor
        if (help > 0)
        {
            double logBf = SplitReview.RevertMinLogBayesFactor;
            return new HypothesisScore
            {
                Id = CausalHypothesisIds.H3Context,
                Status = "scored",
                LogBf = Math.Round(logBf, 4),
                LogScore = Math.Round(logBf + Math.Log(1 + l1), 4),
                SupportN = help,
                ContrastN = LocalEventModel.Events.Length,
                Note = "context_helps_branch; same_action_local_context",
            };
        }

        // Partial / low-confidence: 文脈キーありで pooled と ctx 予測がわずかに違う、または観測はあるが分岐未成立
        if (!string.IsNullOrEmpty(sh.ContextKey) && l1 > 1e-6)
        {
            return new HypothesisScore
            {
                Id = CausalHypothesisIds.H3Context,
                Status = "scored",
                LogScore = Math.Round(Math.Log(1 + l1) - 1.0, 4), // 低信頼: 明示的に弱く
                LogBf = null,
                SupportN = 0,
                ContrastN = LocalEventModel.Events.Length,
                Note = "partial_context_divergence; low_confidence",
            };
        }

        return new HypothesisScore
        {
            Id = CausalHypothesisIds.H3Context,
            Status = "scored",
            LogScore = -1.0,
            SupportN = 0,
            ContrastN = LocalEventModel.Events.Length,
            Note = "short_horizon_observed; context_split_not_supported_yet",
        };
    }

    private static HypothesisScore ScoreH0(CausalCompareInput input, List<string> sources, CausalLayerStatus layers)
    {
        var nb = input.NaturalBaseline;
        if (nb == null || string.IsNullOrEmpty(input.Node))
        {
            layers.NaturalBaseline = LayerStatus.NotObserved;
            return new HypothesisScore { Id = CausalHypothesisIds.H0Natural, Status = LayerStatus.NotObserved, Note = "no natural_baseline or node" };
        }

        sources.Add("natural_baseline");
        layers.NaturalBaseline = LayerStatus.Observed;
        var (obs, changes, _, gap, wait, _) = nb.Counts(input.Node);
        var rates = nb.Rates(input.Node);
        bool isWait = string.Equals(input.Provenance, "wait", StringComparison.Ordinal)
                      || string.Equals(input.Action, "wait", StringComparison.OrdinalIgnoreCase);

        if (obs == 0 || rates == null)
        {
            return new HypothesisScore
            {
                Id = CausalHypothesisIds.H0Natural,
                Status = LayerStatus.NotObserved,
                SupportN = obs,
                ContrastN = wait + gap,
                Note = "natural_baseline has no rates yet",
            };
        }

        // Contrast: natural change rate vs this step's changed bit (not a threshold gate — recorded score).
        double pNat = rates.Value.Change;
        double logScore = input.Changed ? Math.Log(Math.Clamp(pNat, 1e-6, 1 - 1e-6)) : Math.Log(Math.Clamp(1 - pNat, 1e-6, 1 - 1e-6));
        if (isWait && input.Changed)
            logScore += 0.5; // 根拠: wait 手で変化した事実を H0 側にわずかに寄せる形式ボーナス（しきい値採用ではない）

        return new HypothesisScore
        {
            Id = CausalHypothesisIds.H0Natural,
            Status = "scored",
            LogScore = Math.Round(logScore, 4),
            SupportN = changes,
            ContrastN = obs,
            Note = isWait ? "wait_or_no_input_provenance" : "action_turn_natural_rate_contrast",
        };
    }

    private static HypothesisScore ScoreH1(CausalCompareInput input, List<string> sources, CausalLayerStatus layers)
    {
        var rc = input.RegionCausality;
        if (rc == null || string.IsNullOrEmpty(input.Node))
        {
            layers.RegionCausality = LayerStatus.NotObserved;
            return new HypothesisScore { Id = CausalHypothesisIds.H1Immediate, Status = LayerStatus.NotObserved, Note = "no region_causality or node" };
        }

        sources.Add("region_causality");
        layers.RegionCausality = LayerStatus.Observed;

        // Action-after alone is insufficient: require action-dependence evidence on some changed cell.
        var cells = input.ChangedCells?.Where(i => i >= 0 && i < rc.Cells).Distinct().ToList() ?? new List<int>();
        if (cells.Count == 0)
        {
            return new HypothesisScore
            {
                Id = CausalHypothesisIds.H1Immediate,
                Status = LayerStatus.NotObserved,
                Note = "no changed_cells for contrast; refuse action-after-alone",
            };
        }

        double bestBf = double.NegativeInfinity;
        int support = 0, contrast = 0;
        foreach (var cell in cells)
        {
            var (lbf, groups) = rc.ActionDependence(input.Node, cell);
            var (n, c) = rc.ActionCounts(input.Node, input.Action, cell);
            support += c; contrast += n;
            if (groups > 0 && lbf > bestBf) bestBf = lbf;
        }

        double caused = rc.DiffCaused(input.Node, input.Action, cells);
        if (double.IsNegativeInfinity(bestBf))
        {
            return new HypothesisScore
            {
                Id = CausalHypothesisIds.H1Immediate,
                Status = LayerStatus.NotObserved,
                SupportN = support,
                ContrastN = contrast,
                Note = "insufficient action groups for dependence; refuse H1",
            };
        }

        // Score records dependence; Prefer() will not pick H1 unless LogBf beats H0 and meets existing RevertMinLogBayesFactor spirit via recorded bf.
        return new HypothesisScore
        {
            Id = CausalHypothesisIds.H1Immediate,
            Status = "scored",
            LogBf = Math.Round(bestBf, 4),
            LogScore = Math.Round(bestBf + Math.Log(Math.Clamp(caused + 1e-6, 1e-6, 1.0)), 4),
            SupportN = support,
            ContrastN = contrast,
            Note = "action_dependence_contrast",
        };
    }

    private static HypothesisScore ScoreH2(CausalCompareInput input, List<string> sources, CausalLayerStatus layers)
    {
        if (!input.DelayedPresent)
        {
            layers.Delayed = LayerStatus.NotObserved;
            // AfterAction counts are delayed-ish evidence without a DelayedChange object
            if (input.NaturalBaseline != null && !string.IsNullOrEmpty(input.Node))
            {
                var (ao, ac) = input.NaturalBaseline.AfterAction(input.Node);
                if (ao > 0)
                {
                    sources.Add("natural_baseline_after_action");
                    layers.Delayed = LayerStatus.Observed;
                    return new HypothesisScore
                    {
                        Id = CausalHypothesisIds.H2Delayed,
                        Status = "scored",
                        SupportN = ac,
                        ContrastN = ao,
                        LogScore = Math.Round(Math.Log((ac + 0.5) / (ao + 1.0)), 4),
                        Note = "after_action_delay_counts",
                    };
                }
            }
            return new HypothesisScore { Id = CausalHypothesisIds.H2Delayed, Status = LayerStatus.NotObserved, Note = "no delayed evidence" };
        }

        sources.Add("delayed");
        layers.Delayed = LayerStatus.Observed;
        return new HypothesisScore
        {
            Id = CausalHypothesisIds.H2Delayed,
            Status = "scored",
            SupportN = input.DelayedCells ?? 1,
            LogScore = Math.Round(Math.Log(1 + (input.DelayedCells ?? 1)), 4),
            Note = "delayed_change_present",
        };
    }

    private static HypothesisScore ScoreH4Hint(CausalCompareInput input, List<string> sources, CausalLayerStatus layers)
    {
        var rc = input.RegionCausality;
        if (rc == null)
            return new HypothesisScore { Id = CausalHypothesisIds.H4CommonCause, Status = LayerStatus.NotImplemented, Note = "common_cause WM not wired; no region_causality" };

        // Soft hint only: many independent cells suggest shared natural driver — still not a full H4 model.
        var (a, ind, u) = rc.ClassCounts();
        if (a + ind + u == 0)
            return new HypothesisScore { Id = CausalHypothesisIds.H4CommonCause, Status = LayerStatus.NotImplemented, Note = "no classified cells" };

        sources.Add("region_causality_class_counts");
        return new HypothesisScore
        {
            Id = CausalHypothesisIds.H4CommonCause,
            Status = LayerStatus.NotImplemented,
            SupportN = ind,
            ContrastN = a + ind + u,
            Note = $"hint_independent={ind}/action={a}/unknown={u}; full H4 not implemented",
        };
    }

    private static string Prefer(IReadOnlyDictionary<string, HypothesisScore> scores)
    {
        // Never adopt H1 solely from action-after: require scored H1 with LogBf >= existing RevertMinLogBayesFactor.
        var h1 = scores[CausalHypothesisIds.H1Immediate];
        var h0 = scores[CausalHypothesisIds.H0Natural];
        var h2 = scores[CausalHypothesisIds.H2Delayed];
        var h3 = scores[CausalHypothesisIds.H3Context];

        bool h1Ok = h1.Status == "scored" && h1.LogBf is double bf && bf >= SplitReview.RevertMinLogBayesFactor;
        bool h0Ok = h0.Status == "scored";
        bool h2Ok = h2.Status == "scored" && h2.SupportN is > 0;
        // H3: only prefer when ContextHelps-strength LogBf is present (no fake certainty from partial)
        bool h3Ok = h3.Status == "scored" && h3.LogBf is double bf3 && bf3 >= SplitReview.RevertMinLogBayesFactor;

        if (h3Ok && (!h1Ok || (h3.LogScore ?? double.NegativeInfinity) >= (h1.LogScore ?? double.NegativeInfinity))
            && (!h0Ok || (h3.LogScore ?? double.NegativeInfinity) >= (h0.LogScore ?? double.NegativeInfinity)))
            return CausalHypothesisIds.H3Context;
        if (h1Ok && (!h0Ok || (h1.LogScore ?? double.NegativeInfinity) >= (h0.LogScore ?? double.NegativeInfinity)))
            return CausalHypothesisIds.H1Immediate;
        if (h2Ok && !h1Ok && (h2.LogScore ?? 0) > (h0.LogScore ?? double.NegativeInfinity))
            return CausalHypothesisIds.H2Delayed;
        if (h0Ok)
            return CausalHypothesisIds.H0Natural;
        return "undecided";
    }

    private static IReadOnlyList<string> RoutePe(CausalCompareInput input, IReadOnlyDictionary<string, HypothesisScore> scores, string preferred, ShortHorizonForecast sh)
    {
        var tags = new List<string>();
        if (preferred == CausalHypothesisIds.H0Natural
            || string.Equals(input.Provenance, "wait", StringComparison.Ordinal))
            tags.Add(PeRoutingTags.WaitSameDistribution);

        if (input.TrackUnstableHint == true)
            tags.Add(PeRoutingTags.TrackUnstable);

        if (input.ResidualCellCount is > 0
            && preferred is "undecided" or CausalHypothesisIds.H0Natural
            && scores[CausalHypothesisIds.H1Immediate].Status != "scored")
            tags.Add(PeRoutingTags.RepresentationCoarse);

        // Short-horizon PE → closed-loop tags（能動実験はしない）
        if (sh.Status == LayerStatus.Observed && sh.PredictionMiss)
        {
            if (sh.ContextHelpEvents > 0)
                tags.Add(PeRoutingTags.ReducesWithHistory);
            else if (!tags.Contains(PeRoutingTags.RepresentationCoarse))
                tags.Add(PeRoutingTags.RepresentationCoarse);
        }

        // reduces_nowhere: 明示スタブのまま（S5）。勝手に能動探索しない。
        return tags.Distinct(StringComparer.Ordinal).ToList();
    }
}
