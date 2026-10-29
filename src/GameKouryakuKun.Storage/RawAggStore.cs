using GameKouryakuKun.Core.Throughput;

namespace GameKouryakuKun.Storage;

/// <summary>
/// T4 / C2: profile 配下 experience-raw-agg.sqlite。Raw Experience は append-only（UPDATE/DELETE はトリガーで拒否）、
/// 集約（agg_index）は表現の版ごと。Raw からの再集約（Replay）で集約を作り直せる。memory.sqlite は変更しない。
/// </summary>
public sealed class RawAggStore : IDisposable
{
    public const string FileName = "experience-raw-agg.sqlite";
    public SqliteDb Db { get; }
    public string Path { get; }
    public AggregateIndex Index { get; }

    private RawAggStore(SqliteDb db, string path, AggregateIndex ix) { Db = db; Path = path; Index = ix; }

    public static string DefaultPath(string profileDataDir) => System.IO.Path.Combine(profileDataDir, FileName);

    public static RawAggStore Open(string profileDataDir, string appVersion)
    {
        Directory.CreateDirectory(profileDataDir);
        var path = DefaultPath(profileDataDir);
        var db = SqliteDb.Open(path, Schemas.RawAggMetaTable, Schemas.RawAgg, appVersion);
        var st = new RawAggStore(db, path, new AggregateIndex(1));
        // 既存の Raw から現在の表現の集約を復元（集約は Raw から作れるので、Raw が正本）
        foreach (var r in st.LoadRaw()) st.Index.Add(r, out _, out _);
        st.Index.Added = 0; st.Index.Duplicates = 0;   // 今回の実行の重複率は今回足した分だけで数える
        return st;
    }

    /// <summary>Raw に 1 行足し（潰さない）、現在の表現の集約を更新する。返り値: 既出の鍵だったか</summary>
    public bool Append(RawExperience r)
    {
        bool dup = Index.Add(r, out var key, out var stats);
        Db.InTransaction(tx =>
        {
            Db.Exec("INSERT INTO raw_experience(seq, step, memory_turn_id, frame_id, capture_ms, node_before, action, node_after, net_change, kind, evidence_json, rep_version, tier) " +
                    "VALUES($seq,$step,$tid,$fid,$cap,$nb,$a,$na,$nc,$k,$ev,$rv,$tier)", tx,
                ("$seq", r.Seq), ("$step", r.Step), ("$tid", r.TurnId), ("$fid", r.FrameId), ("$cap", r.CaptureMs), ("$nb", r.NodeBefore), ("$a", r.Action),
                ("$na", r.NodeAfter), ("$nc", r.NetChange ? 1 : 0), ("$k", r.Kind), ("$ev", r.EvidenceJson), ("$rv", r.RepVersion), ("$tier", r.Tier));
            Db.Exec("INSERT INTO agg_index(rep_version, state, action, outcome, count, first_seq, last_seq) VALUES($rv,$s,$a,$o,$n,$f,$l) " +
                    "ON CONFLICT(rep_version, state, action, outcome) DO UPDATE SET count=$n, last_seq=$l", tx,
                ("$rv", Index.RepVersion), ("$s", key.State), ("$a", key.Action), ("$o", key.Outcome), ("$n", stats.Count), ("$f", stats.FirstSeq), ("$l", stats.LastSeq));
            return 0;
        });
        return dup;
    }

    public long RawCount() => Convert.ToInt64(Db.Scalar("SELECT COUNT(*) FROM raw_experience") ?? 0L);

    public IReadOnlyList<RawExperience> LoadRaw() => Db.Query(
        "SELECT seq, step, memory_turn_id, frame_id, capture_ms, node_before, action, node_after, net_change, kind, evidence_json, rep_version, tier FROM raw_experience ORDER BY id",
        r => new RawExperience(r.GetInt64(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetInt64(3), r.GetDouble(4), r.GetString(5), r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.GetInt64(8) != 0, r.GetString(9), r.GetString(10), r.GetInt32(11), r.GetString(12)));

    public AggregateIndex LoadAgg(int repVersion)
    {
        var ix = new AggregateIndex(repVersion);
        foreach (var (k, s) in Db.Query("SELECT state, action, outcome, count, first_seq, last_seq FROM agg_index WHERE rep_version=$v",
                     r => (new AggKey(r.GetString(0), r.GetString(1), r.GetString(2)), new AggStats { Count = r.GetInt64(3), FirstSeq = r.GetInt64(4), LastSeq = r.GetInt64(5) }), ("$v", repVersion)))
            ix.Put(k, s);
        return ix;
    }

    /// <summary>Raw をリプレイして集約を作り直し、保存済みの集約と一致するか（再解釈可能性の検証）</summary>
    public bool VerifyReplay() => AggregateIndex.Rebuild(LoadRaw(), 1, AggregateIndex.SignatureV1).SameAs(LoadAgg(1));

    /// <summary>新しい表現で Raw から再集約して別の版として保存（古い版は消さない）</summary>
    public AggregateIndex Reaggregate(int repVersion, AggregateIndex.SignatureFn signature)
    {
        if (repVersion == Index.RepVersion) throw new ArgumentException("新しい表現には新しい版番号を使う（現在の集約を上書きしない）");
        var ix = AggregateIndex.Rebuild(LoadRaw(), repVersion, signature);
        Db.InTransaction(tx =>
        {
            Db.Exec("DELETE FROM agg_index WHERE rep_version=$v", tx, ("$v", repVersion));
            foreach (var (k, s) in ix.Entries)
                Db.Exec("INSERT INTO agg_index(rep_version, state, action, outcome, count, first_seq, last_seq) VALUES($v,$s,$a,$o,$n,$f,$l)", tx,
                    ("$v", repVersion), ("$s", k.State), ("$a", k.Action), ("$o", k.Outcome), ("$n", s.Count), ("$f", s.FirstSeq), ("$l", s.LastSeq));
            return 0;
        });
        return ix;
    }

    public void Dispose() => Db.Dispose();
}
