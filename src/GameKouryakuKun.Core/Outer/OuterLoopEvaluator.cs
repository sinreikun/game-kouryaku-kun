using System.Text.Json;

namespace GameKouryakuKun.Core.Outer;

/// <summary>
/// S7 OuterLoopEvaluator: Champion vs Candidate を fixture/replay メトリクスで比較。
/// ライブ入力は増やさない。code_edit は常に not_supported。
/// </summary>
public static class OuterLoopEvaluator
{
    public const string EvaluatorId = "outer_loop_evaluator@1";
    /// <summary>根拠: PE 代理の明確改善の形式床（相対）。</summary>
    public const double MinPeRelativeImprove = 0.05;
    /// <summary>根拠: 校正ギャップ悪化を許さない相対余裕。</summary>
    public const double MaxCalibrationWorsen = 0.02;
    /// <summary>根拠: 比較に必要な最小サンプル（偶発除外）。</summary>
    public const int MinSampleN = 1;

    public static CompareResult Compare(
        ModelBundleSpec champion,
        ModelBundleSpec candidate,
        MetricSnapshot champMetrics,
        MetricSnapshot candMetrics,
        bool rollbackAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(champion);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(champMetrics);
        ArgumentNullException.ThrowIfNull(candMetrics);

        if (!rollbackAvailable)
        {
            return Reject(champion.Id, candidate.Id, "rollback_unavailable",
                "rollback pointer missing; outer loop must not run", champMetrics, candMetrics);
        }

        if (string.Equals(candidate.Kind, ModelBundleKinds.CodeEdit, StringComparison.Ordinal))
        {
            return Reject(champion.Id, candidate.Id, "not_supported",
                "code_edit candidates are forbidden in S7 (appendix default)", champMetrics, candMetrics);
        }

        if (!ModelBundleKinds.AllowedForOuter.Contains(candidate.Kind))
        {
            return Reject(champion.Id, candidate.Id, "rejected",
                $"unknown kind={candidate.Kind}", champMetrics, candMetrics);
        }

        var reasons = new List<string>();
        if (candMetrics.SampleN < MinSampleN || champMetrics.SampleN < MinSampleN)
        {
            return Reject(champion.Id, candidate.Id, "rejected",
                "insufficient_sample_n", champMetrics, candMetrics);
        }

        // Lower PE / calibration is better
        double peImprove = RelativeImproveLowerBetter(champMetrics.PeProxy, candMetrics.PeProxy);
        double calWorsen = RelativeWorsenLowerBetter(champMetrics.CalibrationGapProxy, candMetrics.CalibrationGapProxy);
        double infoDelta = candMetrics.ContributionInfoGain - champMetrics.ContributionInfoGain;

        bool peClearlyBetter = peImprove >= MinPeRelativeImprove;
        bool calNotWorse = calWorsen <= MaxCalibrationWorsen;
        bool infoNotWorse = infoDelta >= -1e-9;

        reasons.Add($"pe_improve={peImprove:0.000}");
        reasons.Add($"cal_worsen={calWorsen:0.000}");
        reasons.Add($"info_delta={infoDelta:0.000}");

        bool canPromote = peClearlyBetter && calNotWorse && infoNotWorse;
        string winner = canPromote ? "candidate"
            : peImprove < -MinPeRelativeImprove ? "champion"
            : "tie";

        if (!canPromote)
            reasons.Add(peClearlyBetter ? "blocked_by_secondary_metric" : "no_clear_pe_improve");

        var prov = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["evaluator"] = EvaluatorId,
            ["champion_id"] = champion.Id,
            ["candidate_id"] = candidate.Id,
            ["candidate_kind"] = candidate.Kind,
            ["winner"] = winner,
            ["can_promote"] = canPromote,
            ["reasons"] = reasons,
            ["champion_metrics"] = champMetrics,
            ["candidate_metrics"] = candMetrics,
            ["live_inputs_increased"] = false,
        });

        return new CompareResult
        {
            ChampionId = champion.Id,
            CandidateId = candidate.Id,
            Winner = winner,
            CanPromote = canPromote,
            Status = "compared",
            Reasons = reasons,
            ProvenanceJson = prov,
            ChampionMetrics = champMetrics,
            CandidateMetrics = candMetrics,
        };
    }

    /// <summary>比較なしの昇格要求を拒否する（Registry 側からも呼ぶ）。</summary>
    public static CompareResult MissingComparison(string championId, string candidateId) =>
        Reject(championId, candidateId, "rejected", "promote_requires_comparison", null, null);

    private static CompareResult Reject(
        string champId, string candId, string status, string reason,
        MetricSnapshot? cm, MetricSnapshot? dm) =>
        new()
        {
            ChampionId = champId,
            CandidateId = candId,
            Winner = "champion",
            CanPromote = false,
            Status = status,
            Reasons = new[] { reason },
            ProvenanceJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["evaluator"] = EvaluatorId,
                ["status"] = status,
                ["reason"] = reason,
                ["live_inputs_increased"] = false,
            }),
            ChampionMetrics = cm,
            CandidateMetrics = dm,
        };

    private static double RelativeImproveLowerBetter(double before, double after)
    {
        double denom = Math.Max(Math.Abs(before), 1e-6);
        return (before - after) / denom;
    }

    private static double RelativeWorsenLowerBetter(double before, double after)
    {
        double denom = Math.Max(Math.Abs(before), 1e-6);
        return (after - before) / denom;
    }
}
