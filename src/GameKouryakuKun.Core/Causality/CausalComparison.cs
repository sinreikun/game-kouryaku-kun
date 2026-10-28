using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameKouryakuKun.Core.Causality;

/// <summary>S3: H0–H4 仮説 ID（形式名のみ。ゲーム意味禁止）。</summary>
public static class CausalHypothesisIds
{
    public const string H0Natural = "h0_natural";
    public const string H1Immediate = "h1_immediate";
    public const string H2Delayed = "h2_delayed";
    public const string H3Context = "h3_context";
    public const string H4CommonCause = "h4_common_cause";
    public static readonly string[] All = { H0Natural, H1Immediate, H2Delayed, H3Context, H4CommonCause };
}

/// <summary>§10.2 PE 行き先タグ（記録のみ。能動実験は S5）。</summary>
public static class PeRoutingTags
{
    public const string RepresentationCoarse = "representation_coarse";
    public const string TrackUnstable = "track_unstable";
    public const string WaitSameDistribution = "wait_same_distribution";
    public const string ReducesWithHistory = "reduces_with_history";
    public const string ReducesNowhere = "reduces_nowhere";
}

/// <summary>仮説ごとの証拠要約。</summary>
public sealed record HypothesisScore
{
    public string Id { get; init; } = "";
    /// <summary>scored / not_observed / not_implemented</summary>
    public string Status { get; init; } = "not_implemented";
    public double? LogScore { get; init; }
    public double? LogBf { get; init; }
    public int? SupportN { get; init; }
    public int? ContrastN { get; init; }
    public string? Note { get; init; }
}

/// <summary>1 関連イベントの H0–H4 比較結果（docs/s3-causal-wm-contract.md）。</summary>
public sealed record CausalComparisonRecord
{
    public const int CurrentSchema = 1;   // 根拠: S3 契約の最初の版（docs/s3-causal-wm-contract.md）

    public int SchemaVersion { get; init; } = CurrentSchema;
    public long Step { get; init; }
    public string Action { get; init; } = "";
    public string? Node { get; init; }
    public bool Changed { get; init; }
    public string Provenance { get; init; } = "unknown";
    public IReadOnlyList<HypothesisScore> Hypotheses { get; init; } = Array.Empty<HypothesisScore>();
    public string Preferred { get; init; } = "undecided";
    public IReadOnlyList<string> PeRoutingTags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EvidenceSources { get; init; } = Array.Empty<string>();
    public CausalLayerStatus Layers { get; init; } = new();
    public string Comparer { get; init; } = CausalHypothesisComparer.ComparerId;
    /// <summary>短時間 Predictive WM の読み取り（任意。H3 / PE 接続用）。</summary>
    public ShortHorizonForecast? ShortHorizon { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class CausalLayerStatus
{
    public string Comparer { get; set; } = "observed";
    public string NaturalBaseline { get; set; } = "not_observed";
    public string RegionCausality { get; set; } = "not_observed";
    public string Delayed { get; set; } = "not_observed";
    public string PredictiveWmShortHorizon { get; set; } = "not_observed";
    public string PredictionErrorLink { get; set; } = "not_implemented";
}

/// <summary>PE 閉ループ接続の予約（本スライスはタグ記録のみ）。</summary>
public sealed record PredictionErrorLink(
    string Tag,
    string Status,
    string? Detail = null);

/// <summary>Comparer 入力。意味ラベル禁止。無い源は null。</summary>
public sealed record CausalCompareInput(
    long Step,
    string Action,
    string? Node,
    bool Changed,
    string Provenance,
    GameKouryakuKun.Core.Exploration.NaturalBaseline? NaturalBaseline = null,
    GameKouryakuKun.Core.Exploration.RegionCausality? RegionCausality = null,
    IReadOnlyCollection<int>? ChangedCells = null,
    bool DelayedPresent = false,
    int? DelayedCells = null,
    bool? TrackUnstableHint = null,
    int? ResidualCellCount = null,
    /// <summary>短時間 Predictive WM（LocalEventModel WRAP）。無いと H3 は not_observed。</summary>
    GameKouryakuKun.Core.Observation.LocalEventModel? LocalEvents = null,
    string? LocalContextKey = null,
    IReadOnlyList<int>? ObservedLocalEvents = null,
    IReadOnlyList<double>? LocalSurprise = null,
    GameKouryakuKun.Core.Exploration.TransitionModel? WorldModel = null,
    string? ActualNextNode = null,
    GameKouryakuKun.Core.Observation.EntityExperience? EntityExperience = null,
    /// <summary>事前計算済み短時間予測（Session が付けた場合）。無いと Comparer が Forecast する。</summary>
    ShortHorizonForecast? ShortHorizon = null);
