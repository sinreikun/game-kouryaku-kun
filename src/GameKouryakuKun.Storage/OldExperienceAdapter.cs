using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameKouryakuKun.Storage;

/// <summary>
/// 旧 memory.sqlite を read-only で読み、Experience vNext 形式へ派生変換する（t113u）。
/// 旧 DB へは一切書かない。行の意味の上書きもしない。
/// </summary>
public sealed class OldExperienceAdapter : IDisposable
{
    public const string AdapterVersion = ExperienceVNextContract.AdapterVersion;

    private readonly SqliteConnection _c;
    private readonly string _path;
    private readonly Func<DateTime> _now;
    private bool _disposed;

    private OldExperienceAdapter(SqliteConnection c, string path, Func<DateTime> now)
    {
        _c = c;
        _path = path;
        _now = now;
    }

    /// <summary>旧 DB を ReadOnly + query_only で開く。存在しなければ例外。</summary>
    public static OldExperienceAdapter OpenReadOnly(string oldDbPath, Func<DateTime>? utcNow = null)
    {
        if (!File.Exists(oldDbPath)) throw new FileNotFoundException("旧 memory.sqlite がありません", oldDbPath);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = oldDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "PRAGMA query_only = ON;";
            q.ExecuteNonQuery();
        }
        return new OldExperienceAdapter(c, oldDbPath, utcNow ?? (() => DateTime.UtcNow));
    }

    public string SourcePath => _path;

    public int ReadSchemaVersion()
    {
        var v = Scalar("PRAGMA user_version");
        return v is null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 直近 limit 件（seq 降順で切ってから昇順）を vNext DTO へ変換。persistTo があれば experience-vnext.sqlite へ書く。
    /// retainRecords=false のとき戻り値の Records は空（件数は Loss.MappedCount。大容量 Replay 用）。
    /// </summary>
    public AdapterResult ConvertRecent(int limit = 100, string? modelBundleId = null, ExperienceVNextStore? persistTo = null, string appVersion = "0.33.1", bool retainRecords = true)
    {
        var bundle = string.IsNullOrWhiteSpace(modelBundleId) ? ExperienceVNextContract.DefaultLegacyBundleId : modelBundleId;
        var loss = new LossReport
        {
            AdapterVersion = AdapterVersion,
            SourcePath = _path,
            SourceSchemaVersion = ReadSchemaVersion(),
        };
        loss.AddInferred("model_bundle_placeholder",
            $"{bundle} (old DB had no model_bundle_id)", 1);
        loss.Notes.Add("Old memory.sqlite is read-only archive; rows are derived into vNext without overwriting source meanings.");

        if (!TableExists("experience_stream"))
        {
            loss.AddDropped("table_missing", "experience_stream");
            loss.MappedCount = 0;
            long? rid0 = PersistLossOnly(persistTo, loss, bundle, appVersion);
            return new AdapterResult(Array.Empty<ExperienceRecord>(), loss, rid0);
        }

        bool hasDecision = TableExists("decision_records");
        bool hasStructured = TableExists("structured_observations");
        bool streamHasSource = ColumnExists("experience_stream", "action_source");
        if (!hasDecision) loss.AddDropped("table_missing", "decision_records");
        if (!hasStructured) loss.AddDropped("table_missing", "structured_observations");

        foreach (var side in new[] { "reaction_maps", "goal_events", "action_sequences", "screen_motion", "growth_snapshots" })
        {
            if (!TableExists(side)) continue;
            var n = Convert.ToInt32(Scalar($"SELECT COUNT(*) FROM \"{side}\"") ?? 0, CultureInfo.InvariantCulture);
            if (n > 0) loss.AddDropped("unmapped_side_table", side, n);
        }

        var take = limit <= 0 ? int.MaxValue : limit;
        var streamSql = streamHasSource
            ? "SELECT seq, run_id, state, action, changed, next_state, change_ratio, surprisal, excess, action_source, created_at FROM experience_stream ORDER BY seq DESC LIMIT $l"
            : "SELECT seq, run_id, state, action, changed, next_state, change_ratio, surprisal, excess, NULL, created_at FROM experience_stream ORDER BY seq DESC LIMIT $l";

        var raw = Query(streamSql,
            r => new StreamRow(
                r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt32(4) != 0,
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetDouble(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10)),
            ("$l", take));
        raw.Reverse();

        long minSeq = raw.Count == 0 ? 0 : raw[0].Seq;
        long maxSeq = raw.Count == 0 ? -1 : raw[^1].Seq;

        // 一括読み（N+1 回避）。同一 seq は最新行を採用し、余剰は Loss に集計。
        var structuredBySeq = new Dictionary<long, (int Ver, string Json)>();
        var structuredDup = 0;
        if (hasStructured && raw.Count > 0)
        {
            foreach (var row in Query(
                         "SELECT seq, schema_version, json FROM structured_observations WHERE seq BETWEEN $a AND $b ORDER BY obs_id ASC",
                         r => (r.GetInt64(0), r.GetInt32(1), r.GetString(2)),
                         ("$a", minSeq), ("$b", maxSeq)))
            {
                if (structuredBySeq.ContainsKey(row.Item1)) structuredDup++;
                structuredBySeq[row.Item1] = (row.Item2, row.Item3);
            }
            if (structuredDup > 0)
                loss.AddDropped("duplicate_structured_dropped", "extra structured_observations rows for same seq (kept latest obs_id)", structuredDup);
        }

        var decisionBySeq = new Dictionary<long, DecisionSnap>();
        var decisionDup = 0;
        if (hasDecision && raw.Count > 0)
        {
            foreach (var d in Query(
                         "SELECT seq, decision_id, predicted_next, p_predicted, p_change, surprisal, excess, candidates_json FROM decision_records WHERE seq BETWEEN $a AND $b ORDER BY decision_id ASC",
                         r => new DecisionSnap(
                             r.GetInt64(0),
                             r.GetInt64(1),
                             r.IsDBNull(2) ? null : r.GetString(2),
                             r.IsDBNull(3) ? null : r.GetDouble(3),
                             r.IsDBNull(4) ? null : r.GetDouble(4),
                             r.IsDBNull(5) ? null : r.GetDouble(5),
                             r.IsDBNull(6) ? null : r.GetDouble(6),
                             r.IsDBNull(7) ? null : r.GetString(7)),
                         ("$a", minSeq), ("$b", maxSeq)))
            {
                if (decisionBySeq.ContainsKey(d.Seq)) decisionDup++;
                decisionBySeq[d.Seq] = d;
            }
            if (decisionDup > 0)
                loss.AddDropped("duplicate_decision_dropped", "extra decision_records rows for same seq (kept latest decision_id)", decisionDup);
        }

        var records = retainRecords ? new List<ExperienceRecord>(raw.Count) : new List<ExperienceRecord>();
        var basename = System.IO.Path.GetFileName(_path);
        var nowIso = SqliteDb.Iso(_now());

        long? lossId = null;
        if (persistTo != null)
        {
            persistTo.EnsureBundle(bundle, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["producer"] = "legacy-adapter",
                ["note"] = "placeholder bundle for adapted legacy rows",
                ["app_version"] = appVersion,
            }));
            // LossReport は後で最終集計を書く。先に experiences を書くため仮 id は provenance に後入れしない簡略：
            // 契約どおり loss_report_id を持つため、空レポートを先に差し、後で UPDATE はしない（旧 DB 方針に合わせ vNext も追記のみ）。
            // → experiences の provenance には loss_report_id を後付けせず、loss_reports に最終 JSON を1行追記する。
        }

        var mapped = 0;
        foreach (var s in raw)
        {
            string actionSource;
            if (!string.IsNullOrEmpty(s.ActionSource))
                actionSource = s.ActionSource!;
            else if (string.Equals(s.Action, "wait", StringComparison.OrdinalIgnoreCase))
            {
                actionSource = "wait";
                loss.AddInferred("action_source_default", "action=wait and no action_source column/value → wait", 1);
            }
            else
            {
                actionSource = "unknown";
                loss.AddInferred("action_source_default", "missing action_source → unknown", 1);
            }

            ObservationKind kind;
            string? obsJson = null;
            int? obsVer = null;
            var joined = new List<string> { "experience_stream" };

            if (hasStructured)
            {
                if (structuredBySeq.TryGetValue(s.Seq, out var st))
                {
                    kind = ObservationKind.Structured;
                    obsVer = st.Ver;
                    obsJson = st.Json;
                    joined.Add("structured_observations");
                }
                else
                {
                    kind = ObservationKind.LegacyCoarse;
                    loss.AddDropped("no_structured_observation", "stream row without structured_observations", 1);
                    if (!string.IsNullOrEmpty(s.State) || !string.IsNullOrEmpty(s.NextState))
                        loss.AddInferred("legacy_state_as_opaque_token", "state/next_state copied as opaque tokens", 1);
                }
            }
            else
            {
                kind = ObservationKind.LegacyCoarse;
                if (!string.IsNullOrEmpty(s.State) || !string.IsNullOrEmpty(s.NextState))
                    loss.AddInferred("legacy_state_as_opaque_token", "state/next_state copied as opaque tokens", 1);
            }

            string? predictionJson = null;
            double? surprisal = s.Surprisal;
            double? excess = s.Excess;
            if (hasDecision)
            {
                if (!decisionBySeq.TryGetValue(s.Seq, out var d))
                    loss.AddDropped("no_decision_record", "stream row without decision_records", 1);
                else
                {
                    joined.Add("decision_records");
                    predictionJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["predicted_next"] = d.PredictedNext,
                        ["p_predicted"] = d.PPredicted,
                        ["p_change"] = d.PChange,
                        ["candidates_json"] = d.CandidatesJson,
                        ["note"] = "opaque copy from legacy decision_records; not reinterpreted",
                    });
                    if (surprisal is null) surprisal = d.Surprisal;
                    if (excess is null) excess = d.Excess;
                }
            }

            var provenance = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["writer"] = "old_experience_adapter",
                ["adapter_version"] = AdapterVersion,
                ["source"] = new Dictionary<string, object?>
                {
                    ["kind"] = "memory.sqlite",
                    ["schema_version"] = loss.SourceSchemaVersion,
                    ["path_basename"] = basename,
                },
                ["joined"] = joined,
                ["legacy_state_before"] = s.State,
            });

            var rec = new ExperienceRecord(
                ExperienceId: null,
                Seq: s.Seq,
                RunId: s.RunId,
                CreatedAt: s.CreatedAt ?? nowIso,
                Action: s.Action,
                ActionSource: NormalizeSource(actionSource),
                ObservationKind: kind,
                ObservationJson: obsJson,
                ObservationSchemaVersion: obsVer,
                ResultChanged: s.Changed,
                ResultNextState: s.NextState,
                ResultChangeRatio: s.ChangeRatio,
                PredictionJson: predictionJson,
                Surprisal: surprisal,
                Excess: excess,
                ModelBundleId: bundle,
                ProvenanceJson: provenance,
                SourceDb: basename,
                SourceSeq: s.Seq);

            if (persistTo != null)
                persistTo.AppendExperience(rec);
            if (retainRecords)
                records.Add(rec);
            mapped++;
        }

        loss.MappedCount = mapped;
        if (persistTo != null)
            lossId = persistTo.AppendLossReport(loss);

        return new AdapterResult(records, loss, lossId);
    }

    private long? PersistLossOnly(ExperienceVNextStore? persistTo, LossReport loss, string bundle, string appVersion)
    {
        if (persistTo is null) return null;
        persistTo.EnsureBundle(bundle, "{\"producer\":\"legacy-adapter\"}");
        return persistTo.AppendLossReport(loss);
    }

    private static string NormalizeSource(string s) => s switch
    {
        "agent" or "human" or "wait" or "unknown" => s,
        _ => "unknown",
    };

    private bool TableExists(string name) =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", ("$n", name)) ?? 0, CultureInfo.InvariantCulture) > 0;

    private bool ColumnExists(string table, string column)
    {
        if (!TableExists(table)) return false;
        using var q = _c.CreateCommand();
        q.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        using var r = q.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private object? Scalar(string sql, params (string, object?)[] ps)
    {
        using var q = _c.CreateCommand();
        q.CommandText = sql;
        foreach (var (k, v) in ps) q.Parameters.AddWithValue(k, v ?? DBNull.Value);
        var r = q.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        using var q = _c.CreateCommand();
        q.CommandText = sql;
        foreach (var (k, v) in ps) q.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = q.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _c.Dispose();
    }

    private sealed record StreamRow(
        long Seq, long? RunId, string? State, string Action, bool? Changed, string? NextState,
        double? ChangeRatio, double? Surprisal, double? Excess, string? ActionSource, string? CreatedAt);

    private sealed record DecisionSnap(
        long Seq, long DecisionId, string? PredictedNext, double? PPredicted, double? PChange,
        double? Surprisal, double? Excess, string? CandidatesJson);
}
