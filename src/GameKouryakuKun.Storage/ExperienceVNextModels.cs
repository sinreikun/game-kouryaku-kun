using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameKouryakuKun.Storage;

/// <summary>
/// S1 Experience vNext の DTO（docs/s1-runtime-contract.md）。ゲーム意味ラベルを持たない。
/// </summary>
public static class ExperienceVNextContract
{
    public const string AdapterVersion = "1";
    public const string DefaultLegacyBundleId = "legacy-adapter/none@0";
    public const string FileName = "experience-vnext.sqlite";
}

/// <summary>観測の質。推測で structured に昇格しない。</summary>
public enum ObservationKind
{
    Structured,
    LegacyCoarse,
    Absent,
}

/// <summary>1 アクション完了サイクル分の経験。</summary>
public sealed record ExperienceRecord(
    long? ExperienceId,
    long Seq,
    long? RunId,
    string CreatedAt,
    string Action,
    string ActionSource,
    ObservationKind ObservationKind,
    string? ObservationJson,
    int? ObservationSchemaVersion,
    bool? ResultChanged,
    string? ResultNextState,
    double? ResultChangeRatio,
    string? PredictionJson,
    double? Surprisal,
    double? Excess,
    string ModelBundleId,
    string ProvenanceJson,
    string? SourceDb,
    long? SourceSeq);

/// <summary>LossReport の 1 項目。</summary>
public sealed record LossItem(string Code, string Detail, int Count = 1);

/// <summary>旧→vNext 変換で落ちた情報と推定箇所の明示。</summary>
public sealed class LossReport
{
    public string AdapterVersion { get; init; } = ExperienceVNextContract.AdapterVersion;
    public string SourcePath { get; init; } = "";
    public int SourceSchemaVersion { get; init; }
    public int MappedCount { get; set; }
    public List<LossItem> Dropped { get; } = new();
    public List<LossItem> Inferred { get; } = new();
    public List<string> Notes { get; } = new();

    public void AddDropped(string code, string detail, int count = 1)
    {
        var i = Dropped.FindIndex(x => x.Code == code && x.Detail == detail);
        if (i >= 0) Dropped[i] = Dropped[i] with { Count = Dropped[i].Count + count };
        else Dropped.Add(new LossItem(code, detail, count));
    }

    public void AddInferred(string code, string detail, int count = 1)
    {
        var i = Inferred.FindIndex(x => x.Code == code && x.Detail == detail);
        if (i >= 0) Inferred[i] = Inferred[i] with { Count = Inferred[i].Count + count };
        else Inferred.Add(new LossItem(code, detail, count));
    }

    public string ToJson()
    {
        var payload = new Dictionary<string, object?>
        {
            ["adapter_version"] = AdapterVersion,
            ["source_path"] = SourcePath,
            ["source_schema_version"] = SourceSchemaVersion,
            ["mapped_count"] = MappedCount,
            ["dropped"] = Dropped.Select(x => new Dictionary<string, object?> { ["code"] = x.Code, ["detail"] = x.Detail, ["count"] = x.Count }).ToList(),
            ["inferred"] = Inferred.Select(x => new Dictionary<string, object?> { ["code"] = x.Code, ["detail"] = x.Detail, ["count"] = x.Count }).ToList(),
            ["notes"] = Notes,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Adapter の戻り値。</summary>
public sealed record AdapterResult(IReadOnlyList<ExperienceRecord> Records, LossReport Loss, long? LossReportId);

/// <summary>
/// S1 切替フラグ。既定 OFF。PlaySession は置換しない（Session 未配線）。
/// ON のとき <see cref="RuntimeKernel.Step"/> が experience-vnext.sqlite へ追記する。
/// </summary>
public static class RuntimeFlags
{
    /// <summary>既定 false。true で RuntimeKernel 空シェルが vNext へ Experience を書く（学習・Sensor は動かない）。</summary>
    public static bool UseExperienceVNextWriter { get; set; }
}
