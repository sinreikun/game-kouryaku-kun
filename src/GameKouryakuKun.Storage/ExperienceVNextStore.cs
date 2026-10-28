namespace GameKouryakuKun.Storage;

/// <summary>
/// experience-vnext.sqlite の開閉（旧 memory.sqlite とは別ファイル）。
/// </summary>
public sealed class ExperienceVNextStore : IDisposable
{
    public const string FileName = ExperienceVNextContract.FileName;
    public SqliteDb Db { get; }
    public string Path { get; }
    private readonly Func<DateTime> _now;

    private ExperienceVNextStore(SqliteDb db, string path, Func<DateTime> now)
    {
        Db = db;
        Path = path;
        _now = now;
    }

    public static string DefaultPath(string profileDataDir) =>
        System.IO.Path.Combine(profileDataDir, FileName);

    /// <summary>新規または既存の vNext DB を ReadWrite で開く（旧 DB には触れない）。</summary>
    public static ExperienceVNextStore Open(string path, string appVersion, Func<DateTime>? utcNow = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var db = SqliteDb.Open(path, Schemas.ExperienceVNextMetaTable, Schemas.ExperienceVNext, appVersion,
            new Dictionary<string, string> { ["created_by_app"] = appVersion, ["contract"] = "s1-runtime-contract-0.1" },
            backupInterval: null, utcNow: now);
        return new ExperienceVNextStore(db, path, now);
    }

    private string Now() => SqliteDb.Iso(_now());

    public void EnsureBundle(string modelBundleId, string jsonMemo)
    {
        Db.Exec("INSERT OR IGNORE INTO model_bundles(model_bundle_id, created_at, json) VALUES($id,$t,$j)", null,
            ("$id", modelBundleId), ("$t", Now()), ("$j", jsonMemo));
    }

    public long AppendLossReport(LossReport report)
    {
        return Db.Insert("INSERT INTO loss_reports(created_at, source_path, adapter_version, json) VALUES($t,$p,$a,$j)", null,
            ("$t", Now()), ("$p", report.SourcePath), ("$a", report.AdapterVersion), ("$j", report.ToJson()));
    }

    public long AppendExperience(ExperienceRecord r)
    {
        return Db.Insert(
            "INSERT INTO experiences(seq, run_id, created_at, action, action_source, observation_kind, observation_json, observation_schema_version, " +
            "result_changed, result_next_state, result_change_ratio, prediction_json, surprisal, excess, model_bundle_id, provenance_json, source_db, source_seq) " +
            "VALUES($q,$r,$t,$a,$as,$ok,$oj,$ov,$rc,$rn,$rr,$pj,$su,$ex,$mb,$pv,$sd,$ss)", null,
            ("$q", r.Seq), ("$r", r.RunId), ("$t", r.CreatedAt), ("$a", r.Action), ("$as", r.ActionSource),
            ("$ok", KindToDb(r.ObservationKind)), ("$oj", r.ObservationJson), ("$ov", r.ObservationSchemaVersion),
            ("$rc", r.ResultChanged is null ? null : r.ResultChanged.Value ? 1 : 0),
            ("$rn", r.ResultNextState), ("$rr", r.ResultChangeRatio), ("$pj", r.PredictionJson),
            ("$su", r.Surprisal), ("$ex", r.Excess), ("$mb", r.ModelBundleId), ("$pv", r.ProvenanceJson),
            ("$sd", r.SourceDb), ("$ss", r.SourceSeq));
    }

    public int CountExperiences() => Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM experiences") ?? 0);

    public static string KindToDb(ObservationKind k) => k switch
    {
        ObservationKind.Structured => "structured",
        ObservationKind.LegacyCoarse => "legacy_coarse",
        ObservationKind.Absent => "absent",
        ObservationKind.Bundle => "bundle",
        _ => "absent",
    };

    public void Dispose() => Db.Dispose();
}
