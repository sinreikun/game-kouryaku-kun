using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>開発用: 実機の診断データ（展開したフォルダ）で健全性レポートを作り直す。GKK_DIAG_DIR が無ければ何もしない（回帰の確認だけ。学習には使わない）</summary>
public class DiagReplayTests
{
    [Fact]
    public void Replay_health_report_on_real_diag_if_provided()
    {
        var dir = Environment.GetEnvironmentVariable("GKK_DIAG_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var tmp = Path.Combine(Path.GetTempPath(), "gkk-replay-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        foreach (var f in new[] { "memory.sqlite", "app.sqlite" }) File.Copy(Path.Combine(dir, f), Path.Combine(tmp, f));
        using var mem = SqliteDb.Open(Path.Combine(tmp, "memory.sqlite"), "schema_meta", Schemas.Memory, "replay");
        using var app = SqliteDb.Open(Path.Combine(tmp, "app.sqlite"), "meta", Schemas.App, "replay");
        var logs = Directory.GetFiles(Path.Combine(dir, "logs"), "app-*.jsonl").OrderByDescending(x => x).ToList();
        var r = HealthReport.Build("replay", mem, app, null, logs, 2, null);
        File.WriteAllText(Path.Combine(dir, "..", "health-replay.txt"), r.Text);
    }
}

/// <summary>開発用: 実機の記録から、到達の目的の候補が道筋の手数ごとにどこで落ちるか（道筋なし・確かさ不足）を数える（GKK_DIAG_DIR があるときだけ）</summary>
public class DiagPlanAuditTests
{
    [Fact]
    public void DiagReplay_plan_audit_if_provided()
    {
        var dir = Environment.GetEnvironmentVariable("GKK_DIAG_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var tmp = Path.Combine(Path.GetTempPath(), "gkk-plan-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        File.Copy(Path.Combine(dir, "memory.sqlite"), Path.Combine(tmp, "memory.sqlite"));
        using var db = SqliteDb.Open(Path.Combine(tmp, "memory.sqlite"), "schema_meta", Schemas.Memory, "replay");
        var stream = db.Query("SELECT seq, state, action, changed, next_state, COALESCE(change_ratio,0), run_id FROM experience_stream ORDER BY seq",
            r => (new Core.Exploration.StreamStep(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetString(4), r.GetDouble(5)), r.GetInt64(6))).ToList();
        long lastRun = stream.Max(x => x.Item2);
        var keys = stream.Select(x => x.Item1.Action).Where(a => a != "wait" && !a.StartsWith("ActionSequence") && !a.Contains('+')).Distinct().ToList();
        var ex = new Core.Exploration.Explorer(Core.Exploration.Explorer.FromLogicalKeys(keys).Append(Core.Exploration.ExploreAction.Wait).ToList(), 1);
        var steps = stream.Select(x => x.Item1).TakeLast(20000).ToList();
        foreach (var s in steps) { var a = ex.Actions.FirstOrDefault(x => x.Name == s.Action); if (a != null) ex.Observe(s.State, a, s.NextState, s.Changed); }
        var m = new Core.Exploration.TransitionModel(); m.Replay(steps);
        var g = new Core.Goals.SelfGoals();
        var sb = new System.Text.StringBuilder();
        var starts = stream.Where(x => x.Item2 == lastRun).Select(x => x.Item1.State).Distinct().ToList();
        sb.AppendLine($"steps {steps.Count} nodes {ex.Nodes.Count} last-run states {starts.Count}");
        var agg = new SortedDictionary<string, int>();
        foreach (var cur in starts)
            foreach (var (t, len, conf, why) in g.AuditReach(ex, m, cur, 6))
            { var k = $"{why} len={len}"; agg[k] = agg.GetValueOrDefault(k) + 1; }
        foreach (var (k, v) in agg) sb.AppendLine($"{k}: {v}");
        var ef = new SortedDictionary<string, int>();
        foreach (var (node, ns) in ex.Nodes) foreach (var a in ex.Actions)
        {
            if (a.IsWait || a.Group != Core.Exploration.ActionGroup.Dpad && a.Group != Core.Exploration.ActionGroup.Button) continue;
            if ((ex.StatsOf(node, a.Name)?.Tries ?? 0) == 0) { ef["untried"] = ef.GetValueOrDefault("untried") + 1; continue; }
            var p = m.Predict(node, a.Name);
            string k = p.TopNext == null ? "no_pred" : p.TopNext == node ? "self" : p.PTop < 0.5 ? "ptop<0.5" : "";
            if (k == "") { var at = Core.Exploration.NaturalChange.Attribute(ex, node, a.Name); k = !double.IsNaN(at.PActionCaused) && at.PActionCaused < 0.3 ? "caused<0.3" : "edge_ok"; }
            ef[k] = ef.GetValueOrDefault(k) + 1;
        }
        foreach (var (k, v) in ef) sb.AppendLine($"edge {k}: {v}");
        File.WriteAllText(Path.Combine(dir, "..", "plan-audit.txt"), sb.ToString());
    }
}

public class DiagEpisodeTests
{
    [Fact]
    public void DiagReplay_episode_candidates_if_provided()
    {
        var dir = Environment.GetEnvironmentVariable("GKK_DIAG_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var tmp = Path.Combine(Path.GetTempPath(), "gkk-ep-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(tmp, "p"));
        File.Copy(Path.Combine(dir, "memory.sqlite"), Path.Combine(tmp, "p", "memory.sqlite"));
        using var mem = ExperienceStore.Open(Path.Combine(tmp, "p"), "torneko-remaster", "0", "replay");
        var r = new Core.Boundaries.FlowResetMiner().Mine(mem.LoadStream(20000), mem.LoadStreamBreaks());
        var sb = new System.Text.StringBuilder($"steps {r.Steps} breaks {r.Breaks} large>={r.LargeThreshold:0.0000} accepted {r.Accepted.Count}\n");
        foreach (var c in r.Accepted.Concat(r.Examined.Take(12))) sb.AppendLine($"{(c.Accepted ? "★" : " ")} {c.State} n={c.Arrivals} preds={c.Predecessors} large={c.LargeShare:0.00} rec={c.Recurrence:0.00} pwf={c.PredecessorsWithFlow} dep={c.ContextDependence:0.00} flow={c.Flow} — {c.Why}");
        File.WriteAllText(Path.Combine(dir, "..", "episode-replay.txt"), sb.ToString());
    }
}

public class DiagFrontierTests
{
    /// <summary>v0.25.0: 実機の経験の流れを地域の進みに流し直す（診断用。GKK_DIAG_DIR があるときだけ）</summary>
    [Fact]
    public void DiagReplay_frontier_if_provided()
    {
        var dir = Environment.GetEnvironmentVariable("GKK_DIAG_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var tmp = Path.Combine(Path.GetTempPath(), "gkk-fr-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        File.Copy(Path.Combine(dir, "memory.sqlite"), Path.Combine(tmp, "memory.sqlite"));
        using var db = SqliteDb.Open(Path.Combine(tmp, "memory.sqlite"), "schema_meta", Schemas.Memory, "replay");
        var steps = db.Query("SELECT seq, state, action, changed, next_state, COALESCE(change_ratio,0) FROM experience_stream ORDER BY seq",
            r => new Core.Exploration.StreamStep(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetString(4), r.GetDouble(5))).ToList();
        var keys = steps.Select(x => x.Action).Where(a => a != "wait" && !a.StartsWith("ActionSequence") && !a.Contains('+')).Distinct().ToList();
        var ex = new Core.Exploration.Explorer(Core.Exploration.Explorer.FromLogicalKeys(keys).Append(Core.Exploration.ExploreAction.Wait).ToList(), 1);
        var m = new Core.Exploration.TransitionModel(); var f = new Core.Exploration.FrontierRegions();
        var sb = new System.Text.StringBuilder(); int i = 0;
        var top = new Dictionary<string, int>();
        foreach (var s in steps)
        {
            var a = ex.Actions.FirstOrDefault(x => x.Name == s.Action); if (a == null || a.IsWait) continue;
            int d0 = ex.Discoveries; ex.Observe(s.State, a, s.NextState, s.Changed);
            double u0 = m.Uncertainty(s.State, s.Action); m.Observe(s.Seq, s.State, s.Action, s.NextState, s.Changed); double g = Math.Max(0, u0 - m.Uncertainty(s.State, s.Action));
            int untried = ex.Nodes.TryGetValue(s.NextState, out var ns) ? ex.Actions.Count(x => !x.IsWait && x.Group is not (Core.Exploration.ActionGroup.Sequence or Core.Exploration.ActionGroup.Chord) && (ns.Actions.GetValueOrDefault(x.Name)?.Tries ?? 0) == 0) : 0;
            f.Observe(s.State, s.Action, s.NextState, ex.Discoveries > d0, untried, g);
            if (f.Steps % f.RecomputeEvery == 0) f.Recompute(new Core.Goals.SelfGoals().PlanEdges(ex, m));
            if (++i % 2000 == 0) sb.AppendLine($"{i}: {f.Summary()}");
        }
        sb.AppendLine("final: " + f.Summary());
        foreach (var r in f.Regions().Take(12)) sb.AppendLine($"  {r.Region} nodes={r.Nodes} steps={r.Steps:0} value={r.Value:0.000}");
        sb.AppendLine("baseline: " + string.Join(" ", f.Baseline.Select(x => x.ToString("0.0000"))));
        // 実行ごと（最後の地域の分け方で）: 始まりの地域を初めて出るまでの手数・いちばん多い地域の手の割合（意味は付けない。診断の表示だけ）
        var runs = db.Query("SELECT seq, run_id FROM experience_stream", r => (r.GetInt64(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
        foreach (var g in steps.Where(x => x.Action != "wait").GroupBy(x => runs.GetValueOrDefault(x.Seq)).Where(g => g.Count() >= 50))
        {
            var l = g.ToList(); var r0 = f.RegionOf(l[0].State);
            int leave = l.FindIndex(x => f.RegionOf(x.NextState) != r0);
            var topR = l.GroupBy(x => f.RegionOf(x.State)).OrderByDescending(x => x.Count()).First();
            sb.AppendLine($"run {g.Key}: steps {l.Count} start-region {r0} leave-at {(leave < 0 ? "never" : (leave + 1).ToString())} top-region {topR.Key} share {topR.Count() / (double)l.Count:P0} regions {l.Select(x => f.RegionOf(x.State)).Distinct().Count()}");
        }
        File.WriteAllText(Path.Combine(dir, "..", "frontier-replay.txt"), sb.ToString());
        File.WriteAllLines(Path.Combine(dir, "..", "frontier-regions.tsv"), ex.Nodes.Keys.Select(n => $"{n}\t{f.RegionOf(n)}\t{f.RegionValue(n):0.000}"));
    }
}
