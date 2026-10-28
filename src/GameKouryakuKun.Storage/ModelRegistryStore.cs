using System.Text.Json;
using GameKouryakuKun.Core.Outer;

namespace GameKouryakuKun.Storage;

/// <summary>S7: profile 配下 model-registry.sqlite（Champion/Candidate。memory 非変更）。</summary>
public sealed class ModelRegistryStore : IDisposable
{
    public const string FileName = "model-registry.sqlite";
    public const string ChampionKey = "default";
    public SqliteDb Db { get; }
    public string Path { get; }

    private ModelRegistryStore(SqliteDb db, string path) { Db = db; Path = path; }

    public static string DefaultPath(string profileDataDir) => System.IO.Path.Combine(profileDataDir, FileName);

    public static ModelRegistryStore Open(string profileDataDir, string appVersion = "0.37.0")
    {
        var path = DefaultPath(profileDataDir);
        Directory.CreateDirectory(profileDataDir);
        var db = SqliteDb.Open(path, Schemas.ModelRegistryMetaTable, Schemas.ModelRegistry, appVersion);
        var store = new ModelRegistryStore(db, path);
        store.EnsureBootstrapChampion(appVersion);
        return store;
    }

    private void EnsureBootstrapChampion(string appVersion)
    {
        var cur = ChampionId();
        if (cur != null) return;
        var boot = new ModelBundleSpec
        {
            Id = "champion/bootstrap@0",
            Kind = ModelBundleKinds.Params,
            Hypothesis = "bootstrap champion shell",
            Origin = "outer_shell",
            Note = "S7 initial champion; no live behavior change",
            Parameters = new Dictionary<string, double> { ["shell"] = 1.0 },
        };
        RegisterBundle(boot);
        Db.Exec("INSERT INTO champion_pointer(key, bundle_id, previous_bundle_id, updated_at) VALUES($k,$b,NULL,$t)",
            null, ("$k", ChampionKey), ("$b", boot.Id), ("$t", Now()));
        AppendEvent("bootstrap", boot.Id, "initial champion", "{}");
    }

    private static string Now() => SqliteDb.Iso(DateTime.UtcNow);

    public void RegisterBundle(ModelBundleSpec spec)
    {
        Db.Exec("INSERT OR REPLACE INTO registry_bundles(bundle_id, kind, created_at, json, origin) VALUES($id,$k,$t,$j,$o)",
            null, ("$id", spec.Id), ("$k", spec.Kind), ("$t", Now()), ("$j", spec.ToJson()), ("$o", spec.Origin));
    }

    public string? ChampionId() =>
        Db.Query("SELECT bundle_id FROM champion_pointer WHERE key=$k", r => r.GetString(0), ("$k", ChampionKey)).FirstOrDefault();

    public string? PreviousChampionId() =>
        Db.Query("SELECT previous_bundle_id FROM champion_pointer WHERE key=$k",
            r => r.IsDBNull(0) ? null : r.GetString(0), ("$k", ChampionKey)).FirstOrDefault();

    public bool RollbackAvailable() => !string.IsNullOrEmpty(PreviousChampionId());

    public ModelBundleSpec? GetBundle(string id)
    {
        var json = Db.Query("SELECT json FROM registry_bundles WHERE bundle_id=$id", r => r.GetString(0), ("$id", id)).FirstOrDefault();
        return json == null ? null : ModelBundleSpec.FromJson(json);
    }

    public long AddCandidate(ModelBundleSpec spec)
    {
        RegisterBundle(spec);
        return Db.Insert(
            "INSERT INTO registry_candidates(bundle_id, status, hypothesis, created_at, updated_at) VALUES($b,'proposed',$h,$t,$t)",
            null, ("$b", spec.Id), ("$h", spec.Hypothesis), ("$t", Now()));
    }

    public void SetCandidateStatus(string bundleId, string status) =>
        Db.Exec("UPDATE registry_candidates SET status=$s, updated_at=$t WHERE bundle_id=$b",
            null, ("$s", status), ("$t", Now()), ("$b", bundleId));

    public long RecordComparison(CompareResult result)
    {
        var id = Db.Insert(
            "INSERT INTO registry_comparisons(champion_id, candidate_id, winner, can_promote, status, provenance_json, created_at) " +
            "VALUES($c,$a,$w,$p,$s,$j,$t)", null,
            ("$c", result.ChampionId), ("$a", result.CandidateId), ("$w", result.Winner),
            ("$p", result.CanPromote ? 1 : 0), ("$s", result.Status), ("$j", result.ProvenanceJson), ("$t", Now()));
        SetCandidateStatus(result.CandidateId, result.Status == "compared" ? "compared" : result.Status);
        AppendEvent("compare", result.CandidateId, result.SummaryLine(), result.ProvenanceJson);
        return id;
    }

    public CompareResult? LatestComparison(string candidateId) =>
        Db.Query(
            "SELECT champion_id, candidate_id, winner, can_promote, status, provenance_json FROM registry_comparisons " +
            "WHERE candidate_id=$a ORDER BY comparison_id DESC LIMIT 1",
            r => new CompareResult
            {
                ChampionId = r.GetString(0),
                CandidateId = r.GetString(1),
                Winner = r.GetString(2),
                CanPromote = r.GetInt64(3) != 0,
                Status = r.GetString(4),
                ProvenanceJson = r.GetString(5),
            }, ("$a", candidateId)).FirstOrDefault();

    /// <summary>比較合格かつ第二フラグ許可時のみ。比較なしは拒否。</summary>
    public (bool Ok, string Detail) TryPromote(string candidateId, bool promoteFlagEnabled, string? reason = null)
    {
        if (!promoteFlagEnabled)
            return (false, "promote_flag_off");
        var cmp = LatestComparison(candidateId);
        if (cmp == null)
            return (false, "promote_requires_comparison");
        if (!cmp.CanPromote || cmp.Status != "compared")
            return (false, "comparison_not_promotable:" + cmp.Status);
        var champ = ChampionId();
        if (champ == null) return (false, "no_champion");
        if (!RollbackAvailable() && champ != cmp.ChampionId)
        {
            // still allow first promote from bootstrap (previous may be null) — record previous=champ
        }
        Db.Exec("UPDATE champion_pointer SET previous_bundle_id=bundle_id, bundle_id=$b, updated_at=$t WHERE key=$k",
            null, ("$b", candidateId), ("$t", Now()), ("$k", ChampionKey));
        SetCandidateStatus(candidateId, "promoted");
        var prov = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = "promote",
            ["from"] = champ,
            ["to"] = candidateId,
            ["reason"] = reason ?? "outer_compare_can_promote",
            ["comparison_winner"] = cmp.Winner,
        });
        AppendEvent("promote", candidateId, $"promoted from {champ}", prov);
        return (true, "promoted:" + candidateId);
    }

    public (bool Ok, string Detail) TryRollback(string? reason = null)
    {
        var prev = PreviousChampionId();
        if (string.IsNullOrEmpty(prev))
            return (false, "rollback_unavailable");
        var cur = ChampionId();
        Db.Exec("UPDATE champion_pointer SET previous_bundle_id=bundle_id, bundle_id=$b, updated_at=$t WHERE key=$k",
            null, ("$b", prev), ("$t", Now()), ("$k", ChampionKey));
        if (cur != null) SetCandidateStatus(cur, "rolled_back");
        var prov = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = "rollback",
            ["from"] = cur,
            ["to"] = prev,
            ["reason"] = reason ?? "explicit_rollback",
        });
        AppendEvent("rollback", prev, $"rollback to {prev}", prov);
        return (true, "rolled_back:" + prev);
    }

    public void AppendEvent(string kind, string? bundleId, string? detail, string provenanceJson) =>
        Db.Exec("INSERT INTO registry_events(kind, bundle_id, detail, provenance_json, created_at) VALUES($k,$b,$d,$p,$t)",
            null, ("$k", kind), ("$b", bundleId), ("$d", detail), ("$p", provenanceJson), ("$t", Now()));

    public int CountEvents(string? kind = null) => kind == null
        ? Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM registry_events") ?? 0)
        : Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM registry_events WHERE kind=$k", null, ("$k", kind)) ?? 0);

    public string StatsSummary()
    {
        var champ = ChampionId() ?? "(none)";
        var prev = PreviousChampionId() ?? "(none)";
        var comps = Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM registry_comparisons") ?? 0);
        var promotes = CountEvents("promote");
        var rolls = CountEvents("rollback");
        return $"registry champ={champ} prev={prev} rollback={(RollbackAvailable() ? "yes" : "no")} compares={comps} promotes={promotes} rollbacks={rolls}";
    }

    public void Dispose() => Db.Dispose();
}
