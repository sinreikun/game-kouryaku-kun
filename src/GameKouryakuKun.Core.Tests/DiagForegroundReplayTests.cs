using System.Text.Json;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.27.2 開発用: 実機の経験の流れと OCR の記録から、前景の鍵の組（背景違いの同じ前景）を作り直す（GKK_DIAG_DIR があるときだけ）</summary>
public class DiagForegroundReplayTests
{
    [Fact]
    public void DiagReplay_foreground_if_provided()
    {
        var dir = Environment.GetEnvironmentVariable("GKK_DIAG_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var tmp = Path.Combine(Path.GetTempPath(), "gkk-fg-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        File.Copy(Path.Combine(dir, "memory.sqlite"), Path.Combine(tmp, "memory.sqlite"));
        using var db = SqliteDb.Open(Path.Combine(tmp, "memory.sqlite"), "schema_meta", Schemas.Memory, "replay");
        var steps = db.Query("SELECT state, action, changed, next_state FROM experience_stream WHERE action != 'wait' ORDER BY seq",
            r => (r.GetString(0), r.GetString(1), r.GetInt32(2) != 0, r.GetString(3))).ToList();
        var ex = new Explorer(Explorer.FromLogicalKeys(steps.Select(x => x.Item2).Where(a => !a.StartsWith("ActionSequence")).SelectMany(a => a.Split('+')).Distinct()).ToList()
            .Concat(Explorer.FromProfile(Array.Empty<string>(), steps.Select(x => x.Item2).Where(a => a.Contains('+')).Distinct().Select(a => (IReadOnlyList<string>)a.Split('+')).ToList())).GroupBy(a => a.Name).Select(g => g.First()).ToList(), 1);
        foreach (var (s, a, c, n) in steps) { var act = ex.Actions.FirstOrDefault(x => x.Name == a); if (act != null) ex.Observe(s, act, n, c); }
        // OCR: 状態ごとにいちばん多い配置のハッシュ
        var lay = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal); var chars = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(Path.Combine(dir, "logs"), "app-*.jsonl"))
            foreach (var l in File.ReadLines(f))
            {
                if (!l.Contains("\"ocr_observation\"")) continue;
                using var d = JsonDocument.Parse(l); var e = d.RootElement;
                var st = e.GetProperty("state").GetString()!; var h = e.GetProperty("chars").GetInt32() > 0 ? e.GetProperty("layout_hash").GetString()! : "none";
                if (!lay.TryGetValue(st, out var m)) lay[st] = m = new(); m[h] = m.GetValueOrDefault(h) + 1;
            }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"経験 {steps.Count} 手・ノード {ex.Nodes.Count}・背景の種類 {ex.Nodes.Keys.Select(ForegroundStates.Background).Distinct().Count()}・OCR のある状態 {lay.Count}");
        foreach (var (label, withText) in new[] { ("文字あり（v0.27.2 の規則）", true), ("参考: 文字を使わない（前景の配置だけ）", false) })
        {
            var fg = new ForegroundStates();
            foreach (var n in ex.Nodes.Keys)
                if (!withText) fg.ObserveText(n, 0, true);
                else if (lay.TryGetValue(n, out var m)) { var top = m.OrderByDescending(x => x.Value).First().Key; fg.ObserveText(n, top == "none" ? 0 : Convert.ToUInt64(top, 16), top != "none"); }
            fg.Review(ex.Nodes, 3);
            sb.AppendLine($"== {label}: {fg.Summary()}");
            var merged = fg.Pairs.Where(p => p.Value.Status == "merged").ToList();
            int groups = ex.Nodes.Keys.Count(n => fg.Peers(n).Any());
            sb.AppendLine($"  まとめに入ったノード {groups}（ノード数 {ex.Nodes.Count} → 見方の上の状態 {ex.Nodes.Count - groups + ex.Nodes.Keys.Where(n => fg.Peers(n).Any()).Select(n => string.Join(",", fg.Peers(n).Append(n).OrderBy(x => x))).Distinct().Count()}）");
            foreach (var p in fg.Pairs.OrderBy(p => p.Value.Status).ThenBy(p => p.Value.LogBf).Where(p => p.Value.Status is "merged" or "separate" || p.Key.Item1.StartsWith("m2|") || p.Key.Item2.StartsWith("m2|") || p.Key.Item1.StartsWith("m44") || p.Key.Item2.StartsWith("m46")).Take(40))
                sb.AppendLine($"  {p.Value.Status}\t{p.Key.Item1}\t{p.Key.Item2}\tlnBF {p.Value.LogBf:0.0}\t比べた手 {p.Value.Common}");
        }
        File.WriteAllText(Path.Combine(dir, "..", "foreground-replay.txt"), sb.ToString());
    }
}
