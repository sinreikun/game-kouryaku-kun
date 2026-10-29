using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// v0.41.0: 手ごとに設定を切り替える高速化比較（docs/throughput-parallel-sensing-design.md §12）。
/// v0.40.x は 4 分ごとに設定を変えたため、区間ごとに訪れた画面が違い（実機 v0.40.2: D は m48 だけ・A は m44/m1/m42 だけ）、
/// 同じ (画面, 手) の照合が 0 件のまま速さだけで判定していた。ここでは (画面, 手の種類) ごとに A/B/C/D を
/// 並べ替えたブロック（ブロック無作為化）で順に割り当て、同じ (画面, 手) どうしの照合をすべての設定で貯める。
/// 各手には設定のラベルを付け（経験の出どころ）、判定は同じ (画面, 手) どうしの差だけで行う。判定は助言のみ。
/// </summary>
public sealed class InterleavedComparison
{
    public static readonly string[] Labels = { "A", "B", "C", "D" };
    public const double MinWeight = ThroughputComparison.StratMinWeight;   // 根拠: ThroughputComparison.StratMinWeight と同じ（実効 30 件）

    private readonly Random _rng;
    private readonly Dictionary<string, Queue<string>> _blocks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Rec> _steps = new();
    private readonly List<(string Label, string Key, int Half, bool Hit)> _preds = new();
    private readonly object _lock = new();
    public Dictionary<string, string> FailReasons { get; } = new(StringComparer.Ordinal);

    private sealed class Rec { public string Label = "A", Key = ""; public int Half; public double DurMs; public bool Turned, NoChange; public double SettleMs; public bool? Intact; }

    public InterleavedComparison(int seed = 20261029) { _rng = new Random(seed); }   // 根拠: 汎用（再現できる固定の種）

    public IReadOnlyCollection<string> Failed { get { lock (_lock) return _failed.ToList(); } }

    /// <summary>(画面, 手の種類) ごとのブロック無作為化。FAIL の設定は割り当てから外す（基準 A は外さない）</summary>
    public string Assign(string key)
    {
        lock (_lock)
        {
            if (!_blocks.TryGetValue(key, out var q)) _blocks[key] = q = new Queue<string>();
            while (q.Count > 0 && _failed.Contains(q.Peek())) q.Dequeue();
            if (q.Count == 0)
            {
                var b = Labels.Where(l => !_failed.Contains(l)).ToArray();
                for (int i = b.Length - 1; i > 0; i--) { int j = _rng.Next(i + 1); (b[i], b[j]) = (b[j], b[i]); }
                foreach (var l in b) q.Enqueue(l);
            }
            return q.Dequeue();
        }
    }

    public void Fail(string label, string reason) { if (label == "A") return; lock (_lock) { _failed.Add(label); FailReasons.TryAdd(label, reason); } }

    public void Begin(int step, string label, string key, int half) { lock (_lock) _steps[step] = new Rec { Label = label, Key = key, Half = half }; }
    public string? LabelOf(int step) { lock (_lock) return _steps.TryGetValue(step, out var r) ? r.Label : null; }
    public void AddDuration(int step, double ms) { lock (_lock) if (_steps.TryGetValue(step, out var r)) r.DurMs += ms; }
    public void Turn(int step, string actionKey, bool noChange, double settleMs) { lock (_lock) if (_steps.TryGetValue(step, out var r)) { r.Turned = true; r.Key = actionKey; r.NoChange = noChange; r.SettleMs = settleMs; } }
    public void Predict(int step, string key, bool hit) { lock (_lock) if (_steps.TryGetValue(step, out var r)) _preds.Add((r.Label, key, r.Half, hit)); }
    public void SetIntact(Func<int, bool?> f) { lock (_lock) foreach (var (s, r) in _steps) r.Intact = f(s); }

    public int Count(string label) { lock (_lock) return _steps.Values.Count(r => r.Turned && r.Label == label); }

    // ---- 同じ (画面, 手) どうしの比較 ----
    public sealed record Measure(double Diff, double Se, double Weight);
    public sealed record Result(string Label, Verdict Verdict, string Reason, Measure[] Gain, Measure StepMs, Measure NoChange, Measure Hit, Measure Intact, int Actions, IReadOnlyList<string> Regressions);
    public enum Verdict { Adopt, Reject, Inconclusive }

    private IEnumerable<Rec> Turned(string l, int half) => _steps.Values.Where(r => r.Turned && r.DurMs > 0 && r.Label == l && (half == 0 || r.Half == half));

    private static Measure Strat<T>(Dictionary<string, List<T>> x, Dictionary<string, List<T>> a, Func<List<T>, (double Mean, double Var)> f)
    {
        double num = 0, w = 0, v = 0;
        foreach (var (k, xs) in x)
        {
            if (!a.TryGetValue(k, out var aas) || xs.Count == 0 || aas.Count == 0) continue;
            var (mx, vx) = f(xs); var (ma, va) = f(aas);
            double wt = (double)xs.Count * aas.Count / (xs.Count + aas.Count);
            num += wt * (mx - ma); w += wt; v += wt * wt * (vx / xs.Count + va / aas.Count);
        }
        return w == 0 ? new(double.NaN, double.NaN, 0) : new(num / w, Math.Sqrt(v) / w, w);
    }
    private static (double, double) MeanVar(List<double> d) { double m = d.Average(); return (m, d.Count > 1 ? d.Sum(t => (t - m) * (t - m)) / (d.Count - 1) : 0); }
    private static (double, double) Prop(List<bool> d) { double p = d.Count(t => t) / (double)d.Count; return (p, p * (1 - p)); }

    private Dictionary<string, List<T>> By<T>(IEnumerable<Rec> rs, Func<Rec, T> f) => rs.GroupBy(r => r.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(f).ToList(), StringComparer.Ordinal);

    /// <summary>真の指標: 同じ (画面, 手) で重み付けた因果保持観測/分の増加（x/a − 1）。率 = Σw·p / Σw·d</summary>
    public Measure CioGain(string x, int half)
    {
        lock (_lock)
        {
            var X = Turned(x, half).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.ToList());
            var A = Turned("A", half).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.ToList());
            double w = 0, px = 0, dx = 0, pa = 0, da = 0;
            foreach (var (k, xs) in X)
            {
                if (!A.TryGetValue(k, out var aas)) continue;
                double wt = (double)xs.Count * aas.Count / (xs.Count + aas.Count); w += wt;
                px += wt * xs.Average(r => r.Intact == true ? 1.0 : 0); dx += wt * xs.Average(r => r.DurMs);
                pa += wt * aas.Average(r => r.Intact == true ? 1.0 : 0); da += wt * aas.Average(r => r.DurMs);
            }
            if (w == 0 || pa == 0 || dx == 0) return new(double.NaN, double.NaN, w);
            return new((px / dx) / (pa / da) - 1, double.NaN, w);
        }
    }

    public Measure StepMsDiff(string x, int half = 0) { lock (_lock) return Strat(By(Turned(x, half), r => r.DurMs), By(Turned("A", half), r => r.DurMs), MeanVar); }
    public Measure NoChangeDiff(string x, int half = 0) { lock (_lock) return Strat(By(Turned(x, half), r => r.NoChange), By(Turned("A", half), r => r.NoChange), Prop); }
    public Measure IntactDiff(string x, int half = 0) { lock (_lock) return Strat(By(Turned(x, half), r => r.Intact == true), By(Turned("A", half), r => r.Intact == true), Prop); }
    public Measure HitDiff(string x, int half = 0)
    {
        lock (_lock)
        {
            Dictionary<string, List<bool>> G(string l) => _preds.Where(p => p.Label == l && (half == 0 || p.Half == half)).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(p => p.Hit).ToList(), StringComparer.Ordinal);
            return Strat(G(x), G("A"), Prop);
        }
    }

    public IReadOnlyList<Result> Judge()
    {
        var res = new List<Result>();
        foreach (var x in new[] { "B", "C", "D" })
        {
            var g = new[] { CioGain(x, 1), CioGain(x, 2) };
            var sm = StepMsDiff(x); var nc = NoChangeDiff(x); var hit = HitDiff(x); var it = IntactDiff(x);
            var regs = new List<string>();
            double tol = ThroughputComparison.QualityDropTolerance, k = ThroughputComparison.HitDropSe;
            if (nc.Weight >= MinWeight && nc.Diff > tol && nc.Diff > k * Math.Max(nc.Se, 1e-6)) regs.Add($"同じ (画面, 手) で変化なしが {nc.Diff:+0.0%;-0.0%}（2SE 超・実効 {nc.Weight:0}。入力が受け付けられない手が増える）");
            if (hit.Weight >= MinWeight && -hit.Diff > k * Math.Max(hit.Se, 1e-6)) regs.Add($"同じ (画面, 手) で予測の当たりが {hit.Diff:+0.0%;-0.0%}（2SE 超の低下・実効 {hit.Weight:0}）");
            if (it.Weight >= MinWeight && -it.Diff > tol && -it.Diff > k * Math.Max(it.Se, 1e-6)) regs.Add($"同じ (画面, 手) で因果を保った観測の割合が {it.Diff:+0.0%;-0.0%}（2SE 超の低下）");
            int n; lock (_lock) n = Count(x);
            Verdict v; string why;
            bool failed; string? fr; lock (_lock) { failed = _failed.Contains(x); FailReasons.TryGetValue(x, out fr); }
            double minW = new[] { sm.Weight, nc.Weight, hit.Weight, it.Weight, g[0].Weight, g[1].Weight }.Min();
            if (failed) { v = Verdict.Reject; why = "FAIL: " + fr; }
            else if (regs.Count > 0) { v = Verdict.Reject; why = "品質の後退"; }
            else if (minW < MinWeight) { v = Verdict.Inconclusive; why = $"同じ (画面, 手) の照合が足りない（最小の実効 {minW:0} 件 < {MinWeight:0}。速さ・変化なし・予測・因果保持・前半・後半のすべてで必要）"; }
            else if (g.All(t => !double.IsNaN(t.Diff) && t.Diff >= ThroughputComparison.AdoptMinGain)) { v = Verdict.Adopt; why = $"同じ (画面, 手) で因果保持観測/分が前半・後半とも {ThroughputComparison.AdoptMinGain:P0} 以上増加・品質の後退なし"; }
            else { v = Verdict.Inconclusive; why = $"増加が前半・後半とも {ThroughputComparison.AdoptMinGain:P0} に届かない／向きがそろわない"; }
            res.Add(new(x, v, why, g, sm, nc, hit, it, n, regs));
        }
        return res;
    }

    private static string P(Measure m) => double.IsNaN(m.Diff) ? "-" : $"{m.Diff:+0.0%;-0.0%}±{m.Se:0.0%}";
    private static string G(Measure m) => double.IsNaN(m.Diff) ? "-" : $"{m.Diff:+0%;-0%}（w{m.Weight:0}）";
    private static string V(Verdict v) => v switch { Verdict.Adopt => "採用（推奨）", Verdict.Reject => "不採用", _ => "判定保留" };

    public string Markdown(string version, string started, double minutes, string extra)
    {
        var js = Judge();
        var sb = new StringBuilder();
        sb.AppendLine($"# 高速化比較（自動実行・手ごとに交互） v{version}");
        sb.AppendLine($"- 開始: {started}（PC の現地時刻）・{minutes:0.#} 分・(画面, 手の種類) ごとに A/B/C/D を並べ替えたブロックで割り当て（同じ画面どうしで比べる）");
        sb.AppendLine("- 主指標: 同じ (画面, 手) で重み付けた因果保持観測/分（Action→Observation→Result の対応が壊れずに完成した手/分）。判定は助言のみ。既定値（すべて OFF）は変えていない");
        sb.AppendLine();
        sb.AppendLine("| 設定 | 手 | 因果保持観測/分の増加 前半 | 後半 | 1 手の時間の差 ms | 変化なしの差 | 予測の当たりの差 | 因果保持の割合の差 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        sb.AppendLine($"| A | {Count("A")} | 基準 | 基準 | - | - | - | - |");
        foreach (var r in js)
            sb.AppendLine($"| {r.Label} | {r.Actions} | {G(r.Gain[0])} | {G(r.Gain[1])} | {(double.IsNaN(r.StepMs.Diff) ? "-" : $"{r.StepMs.Diff:+0;-0}±{r.StepMs.Se:0}（w{r.StepMs.Weight:0}）")} | {P(r.NoChange)}（w{r.NoChange.Weight:0}） | {P(r.Hit)}（w{r.Hit.Weight:0}） | {P(r.Intact)}（w{r.Intact.Weight:0}） |");
        sb.AppendLine();
        if (extra.Length > 0) sb.AppendLine(extra);
        sb.AppendLine("## 判定");
        foreach (var r in js)
        {
            sb.AppendLine($"- **{ThroughputComparison.Describe(r.Label)}: {V(r.Verdict)}** — {r.Reason}");
            foreach (var t in r.Regressions) sb.AppendLine($"  - {t}");
        }
        return sb.ToString();
    }

    public string Json(string version, string started, double minutes)
    {
        var js = Judge();
        object M(Measure m) => new { diff = double.IsNaN(m.Diff) ? (double?)null : Math.Round(m.Diff, 4), se = double.IsNaN(m.Se) ? (double?)null : Math.Round(m.Se, 4), weight = Math.Round(m.Weight, 1) };
        return JsonSerializer.Serialize(new
        {
            version, started, minutes, design = "interleaved-per-(screen,action-kind) blocked randomization",
            counts = Labels.ToDictionary(l => l, Count),
            verdicts = js.Select(r => new { r.Label, verdict = r.Verdict.ToString(), r.Reason, gain_half1 = M(r.Gain[0]), gain_half2 = M(r.Gain[1]), step_ms = M(r.StepMs), no_change = M(r.NoChange), hit = M(r.Hit), intact = M(r.Intact), r.Actions, r.Regressions }),
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
