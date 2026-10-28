using System.Text.Json;
using System.Text.Json.Serialization;
using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Observation;

namespace GameKouryakuKun.Core.Importance;

/// <summary>S4: 重要度 method タグ（docs/s4-importance-attention-contract.md）。</summary>
public static class ImportanceMethods
{
    public const string PeTagProxy = "pe_tag_proxy";
    public const string ContextHelpsProxy = "context_helps_proxy";
    public const string LocalSurpriseProxy = "local_surprise_proxy";
    public const string AblationReplay = "ablation_replay";
}

/// <summary>1 特徴の重要度レコード。</summary>
public sealed class FeatureImportanceRecord
{
    public const int CurrentSchema = 1;   // 根拠: S4 契約の最初の版

    public int SchemaVersion { get; init; } = CurrentSchema;
    public string FeatureId { get; init; } = "";
    public double Score { get; init; }
    public string Method { get; init; } = ImportanceMethods.PeTagProxy;
    public string Status { get; init; } = LayerStatus.NotObserved;
    public string? EvidenceSummary { get; init; }
    public long? Step { get; init; }
    public string? CreatedAt { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>観測バケットへの注意重み（advisory。永久削除しない・床あり）。</summary>
public sealed class AttentionWeights
{
    /// <summary>根拠: §6.3 低頻度再確認。高コスト寄与を下げても 0 にしない。</summary>
    public const double RareRecheckFloor = 0.15;
    /// <summary>根拠: 推奨 Accept の代理閾値（強い ContextHelps / 高 PE 文脈）。しきい値緩和ではない。</summary>
    public const double RecommendAcceptMin = 0.7;
    /// <summary>根拠: 推奨 Reject。低寄与の形式スコア。</summary>
    public const double RecommendRejectMax = 0.2;

    public string Status { get; init; } = LayerStatus.Observed;
    public double Regions { get; init; } = 1;   // 根拠: advisory 初期は均一（未学習時は下げない）
    public double Tracks { get; init; } = 1;    // 根拠: advisory 初期は均一（未学習時は下げない）
    public double Ocr { get; init; } = 1;       // 根拠: advisory 初期は均一（未学習時は下げない）
    public double Residual { get; init; } = 1;  // 根拠: advisory 初期は均一（未学習時は下げない）
    public string Policy { get; init; } = "advisory_only";
    public string? Note { get; init; }

    public IReadOnlyDictionary<string, double> AsMap() => new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["regions"] = Regions, ["tracks"] = Tracks, ["ocr"] = Ocr, ["residual"] = Residual,
    };

    public string ToJson() => JsonSerializer.Serialize(this, FeatureImportanceRecord.JsonOpts);
}

/// <summary>1 回分の重要度推定結果。</summary>
public sealed class ImportanceReport
{
    public string Estimator { get; init; } = FeatureImportanceEstimator.EstimatorId;
    public long? Step { get; init; }
    public IReadOnlyList<FeatureImportanceRecord> Features { get; init; } = Array.Empty<FeatureImportanceRecord>();
    public AttentionWeights Attention { get; init; } = new();
    public int RecommendationsWritten { get; init; }
    public string? Note { get; init; }

    public string SummaryLine()
    {
        var top = Features.Where(f => f.Status == "scored").OrderByDescending(f => f.Score).Take(3)
            .Select(f => $"{f.FeatureId}:{f.Score:0.00}({f.Method})");
        var a = Attention;
        return $"importance n={Features.Count} top=[{string.Join(" ", top)}] attn=r{a.Regions:0.00}/t{a.Tracks:0.00}/o{a.Ocr:0.00}/x{a.Residual:0.00} rec={RecommendationsWritten}";
    }
}

/// <summary>
/// S4 FeatureImportanceEstimator（契約 §1–§4）。
/// オンライン代理（PE / ContextHelps / surprise）。ablation_replay は not_implemented。
/// 報酬に使わない。WM を自動拡大しない。
/// </summary>
public static class FeatureImportanceEstimator
{
    public const string EstimatorId = "feature_importance_estimator@1";

    public static ImportanceReport Estimate(ImportanceEstimateInput input, PeClosedLoopRecorder? peLoop = null, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var now = (utcNow ?? (() => DateTime.UtcNow))().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var list = new List<FeatureImportanceRecord>();

        // ablation_replay: explicit stub row
        list.Add(new FeatureImportanceRecord
        {
            FeatureId = "meta:ablation_replay",
            Score = 0,
            Method = ImportanceMethods.AblationReplay,
            Status = LayerStatus.NotImplemented,
            EvidenceSummary = "replay_harness_missing",
            Step = input.Step,
            CreatedAt = now,
        });

        // PE tag proxies
        var tags = input.PeTags ?? Array.Empty<string>();
        foreach (var tag in tags.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            if (tag.Contains("キャ", StringComparison.Ordinal) || tag.Contains("character", StringComparison.OrdinalIgnoreCase))
                continue;
            double score = tag switch
            {
                PeRoutingTags.RepresentationCoarse => 0.75,
                PeRoutingTags.ReducesWithHistory => 0.85,
                PeRoutingTags.TrackUnstable => 0.65,
                PeRoutingTags.WaitSameDistribution => 0.25,
                PeRoutingTags.ReducesNowhere => 0.55, // experiment candidate later; not auto-accept
                _ => 0.4,
            };
            list.Add(new FeatureImportanceRecord
            {
                FeatureId = "pe:" + tag,
                Score = score,
                Method = ImportanceMethods.PeTagProxy,
                Status = "scored",
                EvidenceSummary = "from_pe_routing_tag",
                Step = input.Step,
                CreatedAt = now,
            });
        }

        // ContextHelps / H3
        var sh = input.ShortHorizon;
        var h3 = input.Causal?.Hypotheses.FirstOrDefault(h => h.Id == CausalHypothesisIds.H3Context);
        if (sh is { Status: LayerStatus.Observed } || h3?.Status == "scored")
        {
            double ctx = 0.3;
            string note = "short_horizon_or_h3";
            if (sh != null && sh.ContextHelpEvents > 0)
            {
                ctx = Math.Clamp(0.55 + 0.1 * sh.ContextHelpEvents, 0, 1);
                note = "context_helps=" + sh.ContextHelpEvents;
            }
            if (h3?.LogBf is double bf && bf >= SplitReview.RevertMinLogBayesFactor)
            {
                ctx = Math.Max(ctx, AttentionWeights.RecommendAcceptMin);
                note += ";h3_logbf";
            }
            list.Add(new FeatureImportanceRecord
            {
                FeatureId = "ctx:local_context",
                Score = Math.Round(ctx, 4),
                Method = ImportanceMethods.ContextHelpsProxy,
                Status = "scored",
                EvidenceSummary = note,
                Step = input.Step,
                CreatedAt = now,
            });
        }

        // Local surprise proxy
        if (sh?.MeanSurprise is double ms)
        {
            // High surprise → higher importance of better features / residual attention
            double s = Math.Clamp(ms / (PredictiveWmShortHorizon.SurpriseMissThreshold * 2), 0, 1);
            list.Add(new FeatureImportanceRecord
            {
                FeatureId = "pe:local_surprise",
                Score = Math.Round(s, 4),
                Method = ImportanceMethods.LocalSurpriseProxy,
                Status = "scored",
                EvidenceSummary = "mean_surprise=" + ms.ToString("0.000"),
                Step = input.Step,
                CreatedAt = now,
            });
        }

        var attn = BuildAttention(list, sh, input);
        int rec = 0;
        if (peLoop != null)
            rec = peLoop.ApplyImportanceRecommendations(list, AttentionWeights.RecommendAcceptMin, AttentionWeights.RecommendRejectMax);

        return new ImportanceReport
        {
            Step = input.Step,
            Features = list,
            Attention = attn,
            RecommendationsWritten = rec,
            Note = "advisory_only; not_reward; no_wm_autowiden",
        };
    }

    private static AttentionWeights BuildAttention(List<FeatureImportanceRecord> feats, ShortHorizonForecast? sh, ImportanceEstimateInput input)
    {
        double floor = AttentionWeights.RareRecheckFloor;
        double regions = 0.55, tracks = 0.55, ocr = 0.45, residual = 0.5;
        foreach (var f in feats.Where(x => x.Status == "scored"))
        {
            if (f.FeatureId.Contains("track", StringComparison.Ordinal)) tracks = Math.Max(tracks, f.Score);
            if (f.FeatureId.Contains("representation", StringComparison.Ordinal) || f.FeatureId.Contains("residual", StringComparison.Ordinal))
                residual = Math.Max(residual, f.Score);
            if (f.FeatureId.Contains("context", StringComparison.Ordinal) || f.FeatureId.Contains("history", StringComparison.Ordinal))
                regions = Math.Max(regions, f.Score);
            if (f.FeatureId.Contains("wait", StringComparison.Ordinal))
            {
                // wait-same → lower action-driven OCR chase
                ocr = Math.Min(ocr, Math.Max(floor, 1 - f.Score));
            }
        }
        if (sh?.PredictionMiss == true) residual = Math.Max(residual, 0.7);
        if (input.TrackUnstableHint == true) tracks = Math.Max(tracks, 0.7);
        if (input.ResidualCellCount is > 0) residual = Math.Max(residual, 0.65);

        static double ClampFloor(double v, double fl) => Math.Clamp(Math.Max(v, fl), fl, 1.0);
        return new AttentionWeights
        {
            Status = LayerStatus.Observed,
            Regions = Math.Round(ClampFloor(regions, floor), 4),
            Tracks = Math.Round(ClampFloor(tracks, floor), 4),
            Ocr = Math.Round(ClampFloor(ocr, floor), 4),
            Residual = Math.Round(ClampFloor(residual, floor), 4),
            Policy = "advisory_only",
            Note = "rare_recheck_floor=" + floor,
        };
    }
}

/// <summary>Estimator 入力。意味ラベル禁止。</summary>
public sealed record ImportanceEstimateInput(
    long? Step = null,
    IReadOnlyList<string>? PeTags = null,
    CausalComparisonRecord? Causal = null,
    ShortHorizonForecast? ShortHorizon = null,
    bool? TrackUnstableHint = null,
    int? ResidualCellCount = null);
