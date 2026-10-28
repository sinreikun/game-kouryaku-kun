using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameKouryakuKun.Core.Outer;

/// <summary>S7 Model Bundle 種類（docs/s7-outer-self-improvement-contract.md）。code_edit は常に非対応。</summary>
public static class ModelBundleKinds
{
    public const string Params = "params";
    public const string Weights = "weights";
    public const string Roi = "roi";
    public const string ModelSelect = "model_select";
    public const string CodeEdit = "code_edit"; // not_supported
    public static readonly string[] AllowedForOuter = { Params, Weights, Roi, ModelSelect };
}

public sealed class ModelBundleSpec
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = ModelBundleKinds.Params;
    public string? ParentId { get; init; }
    public string Hypothesis { get; init; } = "";
    public string Origin { get; init; } = "outer_shell"; // outer_shell | intrinsic_stats | pe_accept | fixture
    public IReadOnlyDictionary<string, double>? Parameters { get; init; }
    public string? Note { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static ModelBundleSpec? FromJson(string json) =>
        JsonSerializer.Deserialize<ModelBundleSpec>(json, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>比較用メトリクス（ゲーム意味禁止。低い PE / 校正ギャップが良い）。</summary>
public sealed class MetricSnapshot
{
    /// <summary>根拠: PE / surprisal 代理（小さいほど良い）。</summary>
    public double PeProxy { get; init; } = 1.0;
    /// <summary>根拠: 校正ギャップ代理（小さいほど良い）。</summary>
    public double CalibrationGapProxy { get; init; } = 1.0;
    /// <summary>根拠: 寄与統計から得た情報利得成分の相対（大きいほど良い・任意）。</summary>
    public double ContributionInfoGain { get; init; }
    public int SampleN { get; init; }
    public string Method { get; init; } = "fixture_v1";
    public string? Note { get; init; }
}

public sealed class CompareResult
{
    public string ChampionId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public string Winner { get; init; } = "champion"; // champion | candidate | tie
    public bool CanPromote { get; init; }
    public string Status { get; init; } = "compared"; // compared | rejected | not_supported | rollback_unavailable
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public string ProvenanceJson { get; init; } = "{}";
    public MetricSnapshot? ChampionMetrics { get; init; }
    public MetricSnapshot? CandidateMetrics { get; init; }

    public string SummaryLine() =>
        $"outer_compare champ={ChampionId} cand={CandidateId} winner={Winner} promote={CanPromote} status={Status}";
}
