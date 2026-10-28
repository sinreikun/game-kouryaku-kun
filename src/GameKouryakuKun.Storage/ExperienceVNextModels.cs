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
    /// <summary>S2: Observation Bundle が正規観測面（docs/s2-sensor-stack-contract.md）。</summary>
    Bundle,
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
/// <summary>
/// S1 Experience vNext dual-append の有効フラグ。
/// 優先順位: 環境変数 GKK_USE_EXPERIENCE_VNEXT=1 → 強制 ON（開発用）。
/// それ以外は ui-settings.json の UI トグル（既定 OFF）。
/// ON のとき Session が memory.sqlite 学習に加え experience-vnext.sqlite へ併記する。
/// </summary>
public static class RuntimeFlags
{
    public const string SettingsKey = "experience_vnext_dual_append";
    public const string EnvName = "GKK_USE_EXPERIENCE_VNEXT";

    /// <summary>実際に dual-append するか（Kernel / VNextDualAppend が参照）。</summary>
    public static bool UseExperienceVNextWriter { get; set; }

    /// <summary>UI が望む状態（環境変数より弱い）。ui-settings から復元。</summary>
    public static bool UiEnabled { get; set; }

    /// <summary>有効化の出どころ: off / ui / env</summary>
    public static string EffectiveSource { get; private set; } = "off";

    public static bool EnvForcesOn =>
        string.Equals(Environment.GetEnvironmentVariable(EnvName), "1", StringComparison.Ordinal);

    /// <summary>env と UI から有効フラグを再計算する。</summary>
    public static void ApplyEffective()
    {
        if (EnvForcesOn)
        {
            UseExperienceVNextWriter = true;
            EffectiveSource = "env";
        }
        else if (UiEnabled)
        {
            UseExperienceVNextWriter = true;
            EffectiveSource = "ui";
        }
        else
        {
            UseExperienceVNextWriter = false;
            EffectiveSource = "off";
        }
    }

    public static void LoadUiPreference(string? raw)
    {
        UiEnabled = raw is "1" or "true" or "True" or "yes" or "on";
        ApplyEffective();
    }

    public static string SerializeUiPreference() => UiEnabled ? "1" : "0";

    /// <summary>診断・Viewer 向けの短い表示。</summary>
    public static string StatusText()
    {
        ApplyEffective();
        return EffectiveSource switch
        {
            "env" => "Experience vNext dual-append: ON（環境変数 GKK_USE_EXPERIENCE_VNEXT=1）",
            "ui" => "Experience vNext dual-append: ON（画面のトグル）",
            _ => "Experience vNext dual-append: OFF（既定）",
        };
    }

    // --- S2 Observation Bundle 診断フラグ（docs/s2-sensor-stack-contract.md §4）---

    /// <summary>環境変数名。=1 で Session が Bundle 要約をログする（既定 OFF・探索挙動は変えない）。</summary>
    public const string BundleEnvName = "GKK_ATTACH_OBSERVATION_BUNDLE";

    /// <summary>Observation Bundle を診断ログへ付けるか。env のみ（本スライスで UI 無し）。</summary>
    public static bool AttachObservationBundle { get; set; }

    public static bool BundleEnvForcesOn =>
        string.Equals(Environment.GetEnvironmentVariable(BundleEnvName), "1", StringComparison.Ordinal);

    /// <summary>Bundle フラグを env から再計算する（dual-append とは独立）。</summary>
    public static void ApplyBundleEffective()
    {
        AttachObservationBundle = BundleEnvForcesOn;
    }

    public static string BundleStatusText()
    {
        ApplyBundleEffective();
        return AttachObservationBundle
            ? "Observation Bundle attach: ON（環境変数 GKK_ATTACH_OBSERVATION_BUNDLE=1）"
            : "Observation Bundle attach: OFF（既定）";
    }

    // --- S3 Causal WM 診断フラグ（docs/s3-causal-wm-contract.md §4）---

    /// <summary>環境変数名。=1 で Session が H0–H4 比較をログする（既定 OFF）。</summary>
    public const string CausalWmEnvName = "GKK_ATTACH_CAUSAL_WM";

    /// <summary>Causal WM 比較を診断へ付けるか。env のみ。</summary>
    public static bool AttachCausalWm { get; set; }

    public static bool CausalWmEnvForcesOn =>
        string.Equals(Environment.GetEnvironmentVariable(CausalWmEnvName), "1", StringComparison.Ordinal);

    public static void ApplyCausalWmEffective()
    {
        AttachCausalWm = CausalWmEnvForcesOn;
    }

    public static string CausalWmStatusText()
    {
        ApplyCausalWmEffective();
        return AttachCausalWm
            ? "Causal WM attach: ON（環境変数 GKK_ATTACH_CAUSAL_WM=1）"
            : "Causal WM attach: OFF（既定）";
    }

    // --- S4 Importance & Attention 診断フラグ（docs/s4-importance-attention-contract.md §5）---

    /// <summary>環境変数名。=1 で Session が重要度・注意重み・PE 推奨をログする（既定 OFF）。</summary>
    public const string ImportanceEnvName = "GKK_ATTACH_IMPORTANCE";

    /// <summary>Importance / Attention を診断へ付けるか。env のみ。</summary>
    public static bool AttachImportance { get; set; }

    public static bool ImportanceEnvForcesOn =>
        string.Equals(Environment.GetEnvironmentVariable(ImportanceEnvName), "1", StringComparison.Ordinal);

    public static void ApplyImportanceEffective()
    {
        AttachImportance = ImportanceEnvForcesOn;
    }

    public static string ImportanceStatusText()
    {
        ApplyImportanceEffective();
        return AttachImportance
            ? "Importance attach: ON（環境変数 GKK_ATTACH_IMPORTANCE=1）"
            : "Importance attach: OFF（既定）";
    }
}
