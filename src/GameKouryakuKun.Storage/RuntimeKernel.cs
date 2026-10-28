using System.Text.Json;

namespace GameKouryakuKun.Storage;

/// <summary>
/// S1 Runtime Kernel 空シェル（docs/s1-runtime-contract.md §5）。
/// 観測・入力・学習は行わない。呼び出し側が渡した 1 手サイクルの結果を、
/// <see cref="RuntimeFlags.UseExperienceVNextWriter"/> が ON のときだけ
/// <c>experience-vnext.sqlite</c> へ追記する。旧 memory.sqlite は開かない・書かない。
/// PlaySession / Explorer / GT / Goal は置換しない。
/// </summary>
public sealed class RuntimeKernel : IDisposable
{
    public const string KernelVersion = "1";
    public const string ShellBundleId = "runtime-kernel/shell@0";

    private readonly ExperienceVNextStore _store;
    private readonly string _appVersion;
    private readonly Func<DateTime> _now;
    private bool _disposed;
    private long _nextSeq;

    public ExperienceVNextStore Store => _store;
    public string VNextPath => _store.Path;

    public RuntimeKernel(ExperienceVNextStore store, string appVersion = "0.33.6", Func<DateTime>? utcNow = null)
    {
        _store = store;
        _appVersion = appVersion;
        _now = utcNow ?? (() => DateTime.UtcNow);
        _nextSeq = Convert.ToInt64(_store.Db.Scalar("SELECT COALESCE(MAX(seq), 0) FROM experiences") ?? 0L) + 1;
        _store.EnsureBundle(ShellBundleId, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["producer"] = "runtime-kernel",
            ["kind"] = "empty_shell",
            ["kernel_version"] = KernelVersion,
            ["app_version"] = _appVersion,
            ["note"] = "S2 shell: Bundle-canonical observation when present; no learning / sense / act",
        }));
    }

    /// <summary>profileDataDir 配下の experience-vnext.sqlite を開く（memory.sqlite には触れない）。</summary>
    public static RuntimeKernel Open(string profileDataDir, string appVersion = "0.33.6", Func<DateTime>? utcNow = null)
    {
        var path = ExperienceVNextStore.DefaultPath(profileDataDir);
        var store = ExperienceVNextStore.Open(path, appVersion, utcNow);
        return new RuntimeKernel(store, appVersion, utcNow);
    }

    /// <summary>
    /// 契約の Step API。フラグ OFF なら追記せず Skipped。ON なら vNext へ 1 Experience を追記。
    /// sense/act/learn は実行しない（空シェル）。
    /// ObservationBundleJson があるとき、それを正規観測面として ObservationKind=Bundle で書く（Structured は provenance に残す）。
    /// </summary>
    public StepResult Step(ActionSpec action, StepContext ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!RuntimeFlags.UseExperienceVNextWriter)
            return new StepResult(Wrote: false, SkippedBecauseFlagOff: true, ExperienceId: null, Record: null, CanonicalSurface: null);

        var resolved = ResolveObservation(ctx);
        var seq = ctx.Seq ?? _nextSeq;
        var created = SqliteDb.Iso(_now());
        var source = NormalizeSource(action.ActionSource, action.LogicalKey);
        var modelBundle = string.IsNullOrWhiteSpace(ctx.ModelBundleId) ? ShellBundleId : ctx.ModelBundleId!;

        var inferred = new List<Dictionary<string, object?>>();
        if (modelBundle == ShellBundleId)
            inferred.Add(new Dictionary<string, object?> { ["code"] = "model_bundle_placeholder", ["detail"] = $"{ShellBundleId} (S2 empty shell)" });
        if (resolved.PromotedFromStructured)
            inferred.Add(new Dictionary<string, object?> { ["code"] = "observation_surface_bundle", ["detail"] = "ObservationBundleJson used as canonical; structured kept in provenance" });
        if (ctx.Inferred != null)
            foreach (var (code, detail) in ctx.Inferred)
                inferred.Add(new Dictionary<string, object?> { ["code"] = code, ["detail"] = detail });

        var provenance = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["writer"] = "runtime_kernel",
            ["kernel_version"] = KernelVersion,
            ["model_bundle_id"] = modelBundle,
            ["shell"] = true,
            ["call_site"] = ctx.CallSite ?? "direct",
            ["observation_surface"] = resolved.Surface,
            ["source"] = new Dictionary<string, object?>
            {
                ["kind"] = ctx.CallSite == "session_dual_append" ? "session_dual_append" : "live_step_shell",
                ["note"] = "empty shell: caller-supplied observation/result; learning remains on memory.sqlite",
            },
            ["legacy_state_before"] = ctx.LegacyStateBefore,
            ["legacy_structured_json"] = resolved.LegacyStructuredJson,
            ["inferred"] = inferred,
        });

        var rec = new ExperienceRecord(
            ExperienceId: null,
            Seq: seq,
            RunId: ctx.RunId,
            CreatedAt: created,
            Action: action.LogicalKey,
            ActionSource: source,
            ObservationKind: resolved.Kind,
            ObservationJson: resolved.ObservationJson,
            ObservationSchemaVersion: resolved.SchemaVersion,
            ResultChanged: ctx.ResultChanged,
            ResultNextState: ctx.ResultNextState,
            ResultChangeRatio: ctx.ResultChangeRatio,
            PredictionJson: ctx.PredictionJson,
            Surprisal: ctx.Surprisal,
            Excess: ctx.Excess,
            ModelBundleId: modelBundle,
            ProvenanceJson: provenance,
            SourceDb: null,
            SourceSeq: null);

        var id = _store.AppendExperience(rec);
        if (seq >= _nextSeq) _nextSeq = seq + 1;
        return new StepResult(Wrote: true, SkippedBecauseFlagOff: false, ExperienceId: id, Record: rec with { ExperienceId = id }, CanonicalSurface: resolved.Surface);
    }

    /// <summary>
    /// Bundle JSON があれば正規観測面。無ければ呼び出し側の Structured / Legacy / Absent を維持。
    /// Session 学習経路（memory.sqlite）は触らない。
    /// </summary>
    public static ResolvedObservation ResolveObservation(StepContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.ObservationBundleJson))
        {
            int? schema = TryReadBundleSchema(ctx.ObservationBundleJson);
            string? legacy = null;
            bool promoted = false;
            if (!string.IsNullOrWhiteSpace(ctx.ObservationJson)
                && ctx.ObservationKind is ObservationKind.Structured or ObservationKind.LegacyCoarse)
            {
                legacy = ctx.ObservationJson;
                promoted = ctx.ObservationKind == ObservationKind.Structured;
            }
            return new ResolvedObservation(
                ObservationKind.Bundle,
                ctx.ObservationBundleJson,
                schema ?? ctx.ObservationSchemaVersion,
                "bundle",
                legacy,
                promoted);
        }

        return new ResolvedObservation(
            ctx.ObservationKind,
            ctx.ObservationJson,
            ctx.ObservationSchemaVersion,
            ctx.ObservationKind switch
            {
                ObservationKind.Structured => "structured",
                ObservationKind.LegacyCoarse => "legacy_coarse",
                ObservationKind.Bundle => "bundle",
                _ => "absent",
            },
            null,
            false);
    }

    private static int? TryReadBundleSchema(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("schema_version", out var v) && v.TryGetInt32(out var n))
                return n;
        }
        catch { /* ignore malformed; caller still stores raw */ }
        return null;
    }

    private static string NormalizeSource(string? s, string action)
    {
        if (!string.IsNullOrWhiteSpace(s) && s is "agent" or "human" or "wait" or "unknown") return s;
        if (string.Equals(action, "wait", StringComparison.OrdinalIgnoreCase)) return "wait";
        return "unknown";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
    }
}

/// <summary>送る論理キー（身体）。ゲーム意味ラベル禁止。</summary>
public sealed record ActionSpec(string LogicalKey, string ActionSource = "agent");

/// <summary>
/// 1 手完了後に呼び出し側が渡す観測・結果。Kernel は sense/act しない。
/// Seq 省略時は vNext 内の次番号を採番。
/// </summary>
public sealed record StepContext(
    ObservationKind ObservationKind,
    long? Seq = null,
    long? RunId = null,
    string? ObservationJson = null,
    int? ObservationSchemaVersion = null,
    bool? ResultChanged = null,
    string? ResultNextState = null,
    double? ResultChangeRatio = null,
    string? PredictionJson = null,
    double? Surprisal = null,
    double? Excess = null,
    string? LegacyStateBefore = null,
    string? ModelBundleId = null,
    /// <summary>変換時の推定・欠落の明示（ゲーム意味禁止）。provenance に載せる。</summary>
    IReadOnlyList<(string Code, string Detail)>? Inferred = null,
    /// <summary>呼び出し経路（例 session_dual_append）。</summary>
    string? CallSite = null,
    /// <summary>
    /// S2: Observation Bundle JSON。あれば Kernel の正規観測面（ObservationKind=Bundle）。
    /// 学習は Session の StructuredObservation / memory.sqlite のまま。必須にしない（既定 dual-append OFF）。
    /// </summary>
    string? ObservationBundleJson = null);

/// <summary>Step の結果。フラグ OFF なら Wrote=false。CanonicalSurface は bundle/structured/legacy_coarse/absent。</summary>
public sealed record StepResult(bool Wrote, bool SkippedBecauseFlagOff, long? ExperienceId, ExperienceRecord? Record, string? CanonicalSurface = null);

/// <summary>Kernel が正規化した観測面。</summary>
public sealed record ResolvedObservation(
    ObservationKind Kind,
    string? ObservationJson,
    int? SchemaVersion,
    string Surface,
    string? LegacyStructuredJson,
    bool PromotedFromStructured);
