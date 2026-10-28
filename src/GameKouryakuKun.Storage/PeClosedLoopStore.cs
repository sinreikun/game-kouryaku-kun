using GameKouryakuKun.Core.Causality;

namespace GameKouryakuKun.Storage;

/// <summary>
/// S3: PE 特徴候補の永続化（profile 配下 pe-closed-loop.sqlite）。
/// memory.sqlite は触らない。Accept は status 更新のみ（WM 自動拡大なし）。
/// </summary>
public sealed class PeClosedLoopStore : IDisposable
{
    public const string FileName = "pe-closed-loop.sqlite";
    public SqliteDb Db { get; }
    public string Path { get; }

    private PeClosedLoopStore(SqliteDb db, string path)
    {
        Db = db;
        Path = path;
    }

    public static string DefaultPath(string profileDataDir) => System.IO.Path.Combine(profileDataDir, FileName);

    public static PeClosedLoopStore Open(string profileDataDir, string appVersion = "0.34.0")
    {
        var path = DefaultPath(profileDataDir);
        Directory.CreateDirectory(profileDataDir);
        var db = SqliteDb.Open(path, Schemas.PeClosedLoopMetaTable, Schemas.PeClosedLoop, appVersion);
        return new PeClosedLoopStore(db, path);
    }

    public void AppendCandidate(PeFeatureCandidate c)
    {
        Db.Insert(
            "INSERT INTO pe_feature_candidates(candidate_id, created_at, tag, evidence_json, status, step, preferred_hypothesis, run_id, detail) " +
            "VALUES($id,$t,$tag,$ev,$st,$step,$pref,$run,$d)", null,
            ("$id", c.CandidateId), ("$t", c.CreatedAt), ("$tag", c.Tag), ("$ev", c.EvidenceJson),
            ("$st", c.Status), ("$step", c.Step), ("$pref", c.PreferredHypothesis), ("$run", c.RunId), ("$d", c.Detail));
    }

    public void UpsertTagCount(string tag, long count) =>
        Db.Exec("INSERT INTO pe_routing_stats(tag, count) VALUES($t,$c) ON CONFLICT(tag) DO UPDATE SET count=$c", null,
            ("$t", tag), ("$c", count));

    public void UpsertPreferredCount(string hyp, long count) =>
        Db.Exec("INSERT INTO causal_preferred_stats(hypothesis, count) VALUES($h,$c) ON CONFLICT(hypothesis) DO UPDATE SET count=$c", null,
            ("$h", hyp), ("$c", count));

    public void PersistSnapshot(PeClosedLoopRecorder rec)
    {
        foreach (var (tag, n) in rec.TagCounts()) UpsertTagCount(tag, n);
        foreach (var (h, n) in rec.PreferredCounts()) UpsertPreferredCount(h, n);
        long maxId = Convert.ToInt64(Db.Scalar("SELECT COALESCE(MAX(candidate_id), 0) FROM pe_feature_candidates") ?? 0L);
        foreach (var c in rec.Snapshot(PeClosedLoopRecorder.MaxPending).Where(x => x.CandidateId > maxId))
            AppendCandidate(c);
    }

    public int CountCandidates() => Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM pe_feature_candidates") ?? 0);

    public string StatsSummary()
    {
        var tags = Db.Query("SELECT tag, count FROM pe_routing_stats ORDER BY tag",
            r => $"{r.GetString(0)}:{r.GetInt64(1)}");
        var pref = Db.Query("SELECT hypothesis, count FROM causal_preferred_stats ORDER BY hypothesis",
            r => $"{r.GetString(0)}:{r.GetInt64(1)}");
        return $"pe_store tags=[{string.Join(" ", tags)}] preferred=[{string.Join(" ", pref)}] candidates={CountCandidates()}";
    }

    public void Dispose() => Db.Dispose();
}
