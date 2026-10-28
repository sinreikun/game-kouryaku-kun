using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Intrinsic;

namespace GameKouryakuKun.Core.Outer;

/// <summary>
/// S7: 同一経験スライスから MetricSnapshot を構築（ライブ入力を増やさない）。
/// PE/校正は PE 閉ループ・因果タグ由来。寄与は IntrinsicStats + ポートフォリオ重みの再配分。
/// </summary>
public static class OuterMetricSnapshotBuilder
{
    public const string MethodExperience = "experience_v1";
    public const string MethodPartial = "experience_partial_v1";
    public const string MethodFixture = "fixture_v1";
    /// <summary>根拠: PE 経験サンプルの形式床（これ未満は partial）。</summary>
    public const int MinPeEventsForFull = 5;
    /// <summary>根拠: mix 手数の形式床。</summary>
    public const int MinMixForFull = 5;

    /// <summary>Unit-test / offline fixture path（既存 OuterLoopTests 互換）。</summary>
    public static MetricSnapshot Fixture(double pe, double cal = 0.2, double info = 0.1, int n = 10, string? note = null) =>
        new()
        {
            PeProxy = pe,
            CalibrationGapProxy = cal,
            ContributionInfoGain = info,
            SampleN = n,
            Method = MethodFixture,
            Note = note ?? "fixture_only",
        };

    /// <summary>
    /// Champion / Candidate を同一経験スライスで比較用に組む。
    /// PE・校正は共通経験から。Candidate の PE 代理は重み差×割込み後 ambiguity 低下の counterfactual（入力増なし）。
    /// </summary>
    public static (MetricSnapshot Champion, MetricSnapshot Candidate, string SliceNote) PairOnSameSlice(
        IntrinsicContributionStats? stats,
        PeClosedLoopRecorder? pe,
        CausalComparisonRecord? latestCausal,
        IReadOnlyDictionary<string, double>? championWeights,
        IReadOnlyDictionary<string, double>? candidateWeights)
    {
        var baseSnap = FromExperienceCore(stats, pe, latestCausal, championWeights ?? IntrinsicPortfolioWeights.Default());
        var champW = IntrinsicPortfolioWeights.NormalizeOrDefault(championWeights);
        var candW = IntrinsicPortfolioWeights.NormalizeOrDefault(candidateWeights);

        double infoChamp = ContributionInfo(stats, champW);
        double infoCand = ContributionInfo(stats, candW);

        double fellRate = 0;
        if (stats != null && stats.InterruptsApplied > 0)
            fellRate = Math.Clamp((double)stats.AmbiguityFellAfter / stats.InterruptsApplied, 0, 1);

        double deltaInfoW =
            candW.GetValueOrDefault(IntrinsicComponents.InfoGainPe)
            - champW.GetValueOrDefault(IntrinsicComponents.InfoGainPe);
        // Counterfactual PE: より info_gain に寄せ、かつ割込み後 ambiguity が落ちた経験があるときだけ PE 代理を下げる
        double peCand = baseSnap.PeProxy;
        if (deltaInfoW > 0 && fellRate > 0)
            peCand = Math.Clamp(baseSnap.PeProxy * (1.0 - Math.Clamp(deltaInfoW, 0, 1) * fellRate * 0.5), 0.05, 1.0);

        string method = baseSnap.Method;
        string note = baseSnap.Note + $";same_slice;delta_info_w={deltaInfoW:0.000};fell_rate={fellRate:0.000}";

        var champ = new MetricSnapshot
        {
            PeProxy = baseSnap.PeProxy,
            CalibrationGapProxy = baseSnap.CalibrationGapProxy,
            ContributionInfoGain = infoChamp,
            SampleN = baseSnap.SampleN,
            Method = method,
            Note = note + ";role=champion",
        };
        var cand = new MetricSnapshot
        {
            PeProxy = Math.Round(peCand, 4),
            CalibrationGapProxy = baseSnap.CalibrationGapProxy, // 同一経験の校正
            ContributionInfoGain = infoCand,
            SampleN = baseSnap.SampleN,
            Method = method,
            Note = note + ";role=candidate;counterfactual_pe_from_weights",
        };
        return (champ, cand, note);
    }

    public static MetricSnapshot FromExperienceCore(
        IntrinsicContributionStats? stats,
        PeClosedLoopRecorder? pe,
        CausalComparisonRecord? latestCausal,
        IReadOnlyDictionary<string, double> weights)
    {
        long peEvents = 0;
        double undecidedRate = 0.5;
        double coarseRate = 0.5;
        if (pe != null)
        {
            var prefs = pe.PreferredCounts();
            long prefTotal = prefs.Values.Sum();
            peEvents = prefTotal;
            if (prefTotal > 0)
                undecidedRate = (double)prefs.GetValueOrDefault("undecided") / prefTotal;
            var tags = pe.TagCounts();
            long tagTotal = tags.Values.Sum();
            if (tagTotal > 0)
                coarseRate = (double)tags.GetValueOrDefault(PeRoutingTags.RepresentationCoarse) / tagTotal;
        }
        else if (latestCausal != null)
        {
            peEvents = 1;
            undecidedRate = latestCausal.Preferred == "undecided" ? 1.0 : 0.0;
            coarseRate = latestCausal.PeRoutingTags.Contains(PeRoutingTags.RepresentationCoarse) ? 1.0 : 0.0;
        }

        long mix = stats?.MixSteps ?? 0;
        // PE 代理: undecided + coarse（小さいほど良い）
        double peProxy = Math.Clamp(0.5 * undecidedRate + 0.5 * coarseRate, 0.05, 1.0);

        // 校正ギャップ代理: pending 比率（未解決が多いほどギャップ大）
        double cal = 0.5;
        if (pe != null && pe.Enqueued > 0)
        {
            double pending = pe.PendingCount;
            cal = Math.Clamp(pending / Math.Max(pe.Enqueued, 1), 0.05, 1.0);
        }

        int sampleN = (int)Math.Max(peEvents, mix);
        bool full = peEvents >= MinPeEventsForFull || mix >= MinMixForFull;
        string method = full ? MethodExperience : MethodPartial;
        string sparse = full ? "ok" : "sparse";

        return new MetricSnapshot
        {
            PeProxy = Math.Round(peProxy, 4),
            CalibrationGapProxy = Math.Round(cal, 4),
            ContributionInfoGain = ContributionInfo(stats, weights),
            SampleN = Math.Max(sampleN, full ? MinMixForFull : 1),
            Method = method,
            Note = $"experience_slice pe_events={peEvents} mix={mix} sparse={sparse}",
        };
    }

    private static double ContributionInfo(IntrinsicContributionStats? stats, IReadOnlyDictionary<string, double> weights)
    {
        if (stats == null) return 0;
        var sums = stats.ComponentSumWhenChosen();
        if (sums.Count == 0) return 0;
        double total = sums.Values.Sum();
        if (total <= 1e-9) return 0;
        double wSum = 0;
        foreach (var id in IntrinsicComponents.All)
        {
            double share = sums.GetValueOrDefault(id) / total;
            wSum += weights.GetValueOrDefault(id) * share;
        }
        return Math.Round(wSum, 4);
    }
}
