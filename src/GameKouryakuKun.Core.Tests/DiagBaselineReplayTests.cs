using System.Globalization;
using System.Text.Json;
using GameKouryakuKun.Core.Exploration;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.27.0 開発用: 実機の記録（turn・no_input_change）から入力なしの基準を作り直し、従来（待つ手だけ）との差を出す（GKK_DIAG_DIR があるときだけ）。
/// 手と手の間の長さ: 変化した区間は記録の gap_ms。変化しなかった区間は記録にないので「前の手の終わり〜この手の終わり − この手の長さ」で近似する（処理時間を含むので長めになる）。</summary>
public class DiagBaselineReplayTests
{
    private sealed record T(DateTimeOffset At, int Step, string Action, double Ms, bool Changed, string Node, string File, (string from, bool tr, double ms)? Gap);

    [Fact]
    public void DiagReplay_natural_baseline_if_provided()
    {
        var dir = Environment.GetEnvironmentVariable("GKK_DIAG_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var files = Directory.GetFiles(Path.Combine(dir, "logs"), "app-*.jsonl").OrderBy(x => x).ToList();
        var nb = new NaturalBaseline();
        var wait = new Dictionary<string, (int n, int c)>();
        var act = new Dictionary<(string, string), (int n, int c)>();
        var sb = new System.Text.StringBuilder();
        string last = files.Last(f => File.ReadLines(f).Any(l => l.Contains("\"type\":\"turn\"")));
        var lastRun = new List<(string node, bool gapChanged, double gapMs)>();
        foreach (var f in files)
        {
            var turns = new List<T>(); var gaps = new Dictionary<int, (string from, bool tr, double ms)>();   // 同じファイルに複数の実行があるので、入力なしの変化は直後の同じ手番の turn にだけ結びつける
            foreach (var l in File.ReadLines(f))
            {
                if (!l.Contains("\"type\":\"turn\"") && !l.Contains("\"type\":\"no_input_change\"")) continue;
                using var d = JsonDocument.Parse(l); var e = d.RootElement; string ty = e.GetProperty("type").GetString()!;
                if (ty == "turn" && e.TryGetProperty("node", out var nn) && nn.ValueKind == JsonValueKind.String && e.TryGetProperty("boundary_ms", out var bm) && bm.ValueKind == JsonValueKind.Number)
                { if (gaps.Keys.Any(k => k < e.GetProperty("step").GetInt32())) gaps.Clear(); turns.Add(new T(DateTimeOffset.Parse(e.GetProperty("t").GetString()!, CultureInfo.InvariantCulture), e.GetProperty("step").GetInt32(), e.GetProperty("action").GetString()!, bm.GetDouble(), e.TryGetProperty("net_change", out var nc) && nc.GetBoolean(), nn.GetString()!, f, gaps.Remove(e.GetProperty("step").GetInt32(), out var pg) ? pg : null)); }
                else if (ty == "no_input_change" && e.GetProperty("from").ValueKind == JsonValueKind.String) gaps[e.GetProperty("step").GetInt32()] = (e.GetProperty("from").GetString()!, e.GetProperty("transition").GetBoolean(), e.GetProperty("gap_ms").GetDouble());
            }
            for (int i = 0; i < turns.Count; i++)
            {
                var t = turns[i]; if (f == last && i > 0 && t.Step < turns[i - 1].Step) lastRun.Clear();   // 最後の実行だけ
                string cur = i > 0 ? turns[i - 1].Node : t.Node;
                if (i > 0 && turns[i - 1].Step == t.Step - 1)
                {
                    bool ch = t.Gap is { } g0; var g = t.Gap ?? default;
                    double ms = ch ? g.ms : Math.Max(0, (t.At - turns[i - 1].At).TotalMilliseconds - t.Ms);
                    if (ch) cur = g.from;
                    // v0.27.1 と同じ規則: 待つ手の直後の区間だけを基準に。キーを送った手の直後は数えるだけ（記録には IsChanged の判定がないので、記録の変化をそのまま使う近似）
                    if (turns[i - 1].Action == "wait") nb.ObserveNatural(cur, ms, ch, ch && g.tr, fromWait: false); else nb.ObserveAfterAction(cur, ch);
                    if (f == last) lastRun.Add((cur, ch, ms));
                    if (ch) cur = g.tr ? turns[i - 1].Node : cur;   // 遷移した区間の後は記録上の結果の画面から（近似）
                }
                if (t.Action == "wait") { nb.ObserveNatural(cur, t.Ms, t.Changed, t.Node != cur, fromWait: true); var w = wait.GetValueOrDefault(cur); wait[cur] = (w.n + 1, w.c + (t.Changed ? 1 : 0)); }
                else { nb.ObserveActionTurn(cur, t.Ms); var a = act.GetValueOrDefault((cur, t.Action)); act[(cur, t.Action)] = (a.n + 1, a.c + (t.Changed ? 1 : 0)); }
            }
        }
        sb.AppendLine($"logs {files.Count}・最後の実行 {Path.GetFileName(last)}・基準の観測 {nb.TotalObservations}（待つ手の後の手と手の間 {nb.GapObservations}・待つ手 {nb.WaitObservations}）・手の直後の区間（基準に使わない） {nb.AfterActionObservations}");
        var fam = lastRun.Where(x => x.node.StartsWith("m45")).ToList();
        if (fam.Count > 0) sb.AppendLine($"最後の実行の m45 系: 手と手の間 {fam.Count} 区間・入力なしで変わった {fam.Count(x => x.gapChanged)}（{fam.Count(x => x.gapChanged) / (double)fam.Count:P0}）・区間の長さの中央値 {fam.Select(x => x.gapMs).OrderBy(x => x).ElementAt(fam.Count / 2):0} ms");
        var allwait = (n: wait.Values.Sum(x => x.n), c: wait.Values.Sum(x => x.c));
        double OldPw(string n) { var w = wait.TryGetValue(n, out var x) && x.n > 0 ? x : allwait; return (w.c + 0.5) / (w.n + 1.0); }
        int noWaitNodes = 0;
        sb.AppendLine("画面\t手の数\t待つ手\t基準の観測(変化)\t従来の入力なしの割合\t新しい入力なしの割合(1手の長さ)\t操作の効果(従来→新)の中央値\t操作の効果0.3未満の組(従来→新)");
        foreach (var n in act.Keys.Select(k => k.Item1).Distinct().OrderByDescending(n => act.Where(k => k.Key.Item1 == n).Sum(k => k.Value.n)).Take(30))
        {
            var pairs = act.Where(k => k.Key.Item1 == n).ToList(); int steps = pairs.Sum(p => p.Value.n);
            bool hasWait = wait.ContainsKey(n); if (!hasWait) noWaitNodes++;
            double po = OldPw(n); var r = nb.Rates(n); double pn = r?.Change ?? po;
            var co = pairs.Select(p => { double pa = (p.Value.c + 0.5) / (p.Value.n + 1.0); return Math.Max(0, pa - po) / pa; }).OrderBy(x => x).ToList();
            var cn = pairs.Select(p => { double pa = (p.Value.c + 0.5) / (p.Value.n + 1.0); return Math.Max(0, pa - pn) / pa; }).OrderBy(x => x).ToList();
            var c = nb.Counts(n);
            sb.AppendLine($"{n}\t{steps}\t{(hasWait ? wait[n].n : 0)}\t{c.Obs}({c.Changes})\t{po:0.000}{(hasWait ? "" : "(全画面から借用)")}\t{pn:0.000}\t{co[co.Count / 2]:0.00}→{cn[cn.Count / 2]:0.00}\t{co.Count(x => x < 0.3)}→{cn.Count(x => x < 0.3)}/{pairs.Count}");
        }
        // m45 系をまとめて
        var m45 = act.Where(k => k.Key.Item1.StartsWith("m45")).ToList();
        if (m45.Count > 0)
        {
            var co = m45.Select(p => { double pa = (p.Value.c + 0.5) / (p.Value.n + 1.0); double pw = OldPw(p.Key.Item1); return (pa, pw); }).ToList();
            var cn = m45.Select(p => { double pa = (p.Value.c + 0.5) / (p.Value.n + 1.0); double pw = nb.Rates(p.Key.Item1)?.Change ?? OldPw(p.Key.Item1); return (pa, pw); }).ToList();
            double Med(IEnumerable<double> v) { var l = v.OrderBy(x => x).ToList(); return l[l.Count / 2]; }
            sb.AppendLine($"m45 系 {m45.Count} 組: 入力なしの割合の中央値 従来 {Med(co.Select(x => x.pw)):0.000} → 新 {Med(cn.Select(x => x.pw)):0.000}・操作の効果の中央値 従来 {Med(co.Select(x => Math.Max(0, x.pa - x.pw) / x.pa)):0.00} → 新 {Med(cn.Select(x => Math.Max(0, x.pa - x.pw) / x.pa)):0.00}・0.3 未満 従来 {co.Count(x => Math.Max(0, x.pa - x.pw) / x.pa < 0.3)} → 新 {cn.Count(x => Math.Max(0, x.pa - x.pw) / x.pa < 0.3)}");
            foreach (var n in m45.Select(k => k.Key.Item1).Distinct().Where(n => nb.Observations(n) > 0).OrderByDescending(n => nb.Observations(n)).Take(5))
                sb.AppendLine($"  {n}: 待つ手の不確かさ（基準） {nb.Uncertainty(n):0.000}・基準の観測 {nb.Observations(n)}・1 手の長さ {nb.ActionTurnMs(n):0} ms");
        }
        File.WriteAllText(Path.Combine(dir, "..", "baseline-replay.txt"), sb.ToString());
    }
}
