using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GameKouryakuKun.Storage;

/// <summary>
/// 学習 DB（memory.sqlite）を読むだけの点検の道具（v0.12.0）。読み取り専用で開き、移行もバックアップもしない・何も書かない。
/// 出力は件数・出どころ・判断の記録など構造の点検だけ（ゲームの意味は解釈しない）
/// </summary>
public sealed class DbInspector : IDisposable
{
    private readonly SqliteConnection _c;
    public DbInspector(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("DB がありません", path);
        _c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        _c.Open();
        using var q = _c.CreateCommand(); q.CommandText = "PRAGMA query_only = ON;"; q.ExecuteNonQuery();
    }

    public static readonly string[] Commands = { "summary", "decisions", "revisions", "splits", "sequences", "discoveries", "provenance" };

    /// <summary>コマンドを実行して文字列で返す。args: decisions [件数] / provenance ノード [操作]</summary>
    public string Run(string cmd, IReadOnlyList<string> args) => cmd switch
    {
        "summary" => Summary(),
        "decisions" => Decisions(args.Count > 0 && int.TryParse(args[0], out var n) ? n : 20),
        "revisions" => Revisions(args.Count > 0 && int.TryParse(args[0], out var m) ? m : 20),
        "splits" => Splits(),
        "sequences" => Sequences(),
        "discoveries" => Discoveries(args.Count > 0 && int.TryParse(args[0], out var k) ? k : 30),
        "provenance" when args.Count > 0 => Provenance(args[0], args.Count > 1 ? args[1] : null),
        _ => "使い方: summary | decisions [件数] | revisions [件数] | splits | sequences | discoveries [件数] | provenance ノード [操作]",
    };

    private bool Has(string table) => Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", ("$n", table)), CultureInfo.InvariantCulture) > 0;
    private object? Scalar(string sql, params (string, object?)[] ps)
    {
        using var q = _c.CreateCommand(); q.CommandText = sql;
        foreach (var (k, v) in ps) q.Parameters.AddWithValue(k, v ?? DBNull.Value);
        var r = q.ExecuteScalar(); return r is DBNull ? null : r;
    }
    private void Table(StringBuilder sb, string sql, params (string, object?)[] ps)
    {
        using var q = _c.CreateCommand(); q.CommandText = sql;
        foreach (var (k, v) in ps) q.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = q.ExecuteReader();
        var head = Enumerable.Range(0, r.FieldCount).Select(r.GetName);
        sb.AppendLine(string.Join("\t", head));
        int rows = 0;
        while (r.Read())
        {
            sb.AppendLine(string.Join("\t", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "-" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
            rows++;
        }
        if (rows == 0) sb.AppendLine("（なし）");
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"schema v{Scalar("SELECT COALESCE(MAX(version),0) FROM migrations")}");
        sb.AppendLine("表\t行数");
        using (var q = _c.CreateCommand())
        {
            q.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var r = q.ExecuteReader();
            var names = new List<string>(); while (r.Read()) names.Add(r.GetString(0));
            r.Close();
            foreach (var n in names) sb.AppendLine($"{n}\t{Scalar($"SELECT COUNT(*) FROM \"{n.Replace("\"", "\"\"")}\"")}");
        }
        return sb.ToString();
    }

    public string Decisions(int n)
    {
        var sb = new StringBuilder();
        if (!Has("decision_records")) return "判断の記録の表がありません（schema v8 より前）";
        bool v9 = HasColumn("decision_records", "obs_ambiguous");
        Table(sb, "SELECT decision_id, seq, node, chosen, COALESCE(composite,'-') AS composite, rule, ROUND(probability,4) AS p_choice, predicted_next, ROUND(p_predicted,4) AS p_pred, actual_next, hit, ROUND(excess,3) AS excess, revision_id" +
                  (v9 ? ", ROUND(obs_vis_ratio,3) AS vis_ratio, ROUND(obs_mode_ratio,3) AS mode_ratio, ROUND(frame_age_ms,1) AS frame_age_ms, obs_ambiguous" : "") +
                  " FROM decision_records ORDER BY decision_id DESC LIMIT $n", ("$n", Math.Max(1, n)));
        return sb.ToString();
    }

    public string Revisions(int n)
    {
        var sb = new StringBuilder();
        if (!Has("model_revisions")) return "内部モデルの修正の表がありません（schema v8 より前）";
        Table(sb, "SELECT revision_id, seq, node, logical_key, ROUND(weight_before,2) AS before, ROUND(weight_after,2) AS after, recent_kept, reason FROM model_revisions ORDER BY revision_id DESC LIMIT $n", ("$n", Math.Max(1, n)));
        return sb.ToString();
    }

    public string Splits()
    {
        var sb = new StringBuilder();
        bool v9 = HasColumn("state_splits", "status");
        Table(sb, "SELECT vis_id, coarse_node, split_node, ROUND(distance,3) AS distance, decided_at" + (v9 ? ", status, reclassified_at, status_at" : "") + " FROM state_splits ORDER BY vis_id");
        if (Has("state_split_events"))
        {
            sb.AppendLine();
            Table(sb, "SELECT event_id, vis_id, coarse_node, split_node, kind, evidence_kind, ROUND(log_bayes_factor,3) AS log_bf, moved, reason, created_at FROM state_split_events ORDER BY event_id");
        }
        return sb.ToString();
    }

    public string Sequences()
    {
        var sb = new StringBuilder();
        Table(sb, "SELECT sequence_id, steps, start_node, end_node, status, status_seq FROM action_sequences ORDER BY sequence_id");
        if (Has("sequence_executions"))
        {
            sb.AppendLine();
            Table(sb, "SELECT sequence_id, COUNT(*) AS executions, SUM(informed) AS with_prediction, SUM(surprised) AS surprised, MAX(seq_end) AS last_seq FROM sequence_executions GROUP BY sequence_id ORDER BY sequence_id");
        }
        return sb.ToString();
    }

    /// <summary>新発見（Viewer の「新発見」と同じ種類）を、それぞれの出どころの表から通し番号の新しい順に並べる</summary>
    public string Discoveries(int n)
    {
        var parts = new List<string>();
        if (Has("sequence_patterns")) parts.Add("SELECT first_seq AS seq, 'pattern' AS kind, 'P' || pattern_id AS subject, kind || ' len ' || length AS detail FROM sequence_patterns");
        if (Has("action_sequence_events")) parts.Add("SELECT seq, 'sequence', 'ActionSequence #' || sequence_id, kind FROM action_sequence_events WHERE kind='promoted'");
        if (Has("state_split_events")) parts.Add("SELECT NULL, 'split_' || kind, split_node, coarse_node || ' ' || COALESCE(evidence_kind,'-') FROM state_split_events");
        if (Has("model_revisions")) parts.Add("SELECT seq, 'revision', node || ' ' || logical_key, ROUND(weight_before,1) || ' -> ' || ROUND(weight_after,1) FROM model_revisions");
        if (parts.Count == 0) return "（なし）";
        var sb = new StringBuilder();
        Table(sb, "SELECT * FROM (" + string.Join(" UNION ALL ", parts) + ") ORDER BY seq IS NULL, seq DESC LIMIT $n", ("$n", Math.Max(1, n)));
        return sb.ToString();
    }

    /// <summary>ノード（と操作）の経験の出どころ: 直接（mode_transitions）・読み直し（derived_transitions）・弱い事前の推定（experience_priors）を分けて並べる</summary>
    public string Provenance(string node, string? action)
    {
        var sb = new StringBuilder();
        string a = action == null ? "" : " AND logical_key=$a";
        sb.AppendLine("[直接の経験]");
        Table(sb, "SELECT from_node, logical_key, to_node, n FROM mode_transitions WHERE from_node=$f" + a + " ORDER BY logical_key, n DESC", ("$f", node), ("$a", action));
        if (Has("derived_transitions"))
        {
            sb.AppendLine("[分割・取り消しによる読み直し]");
            bool cause = HasColumn("derived_transitions", "cause");
            Table(sb, "SELECT from_node, logical_key, to_node, n, orig_from, orig_to, split_vis" + (cause ? ", cause" : "") + " FROM derived_transitions WHERE from_node=$f" + a + " ORDER BY derived_id", ("$f", node), ("$a", action));
        }
        if (Has("experience_priors"))
        {
            sb.AppendLine("[弱い事前の推定（借りたもの）]");
            Table(sb, "SELECT node, source_node, origin, ROUND(strength,3) AS strength, updated_at FROM experience_priors WHERE node=$f ORDER BY source_node", ("$f", node));
        }
        return sb.ToString();
    }

    private bool HasColumn(string table, string col)
    {
        if (!Has(table)) return false;
        using var q = _c.CreateCommand(); q.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$c";
        q.Parameters.AddWithValue("$c", col);
        return Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public void Dispose() => _c.Dispose();
}
