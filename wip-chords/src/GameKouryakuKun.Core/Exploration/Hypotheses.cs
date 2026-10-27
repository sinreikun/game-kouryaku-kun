using System.Globalization;
using System.Text;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// 仮説の板（v0.13.0。説明層・表示だけ）。AI が今もっている「〜すると〜になる」という見込みを、内部モデル・分割・操作の並びの記録から
/// 構造化した記録として並べる。新しい判断はしない（状態の名前は記録の数値の区分だけ）。選択・修正・記録からは参照しない（静的検査）
/// </summary>
public enum HypothesisKind { Relation, Split, Sequence, Motion }

/// <summary>画面の種類ごとの、入力なしで動き続ける所の性質（v0.15.0。MotionMaskBook の要約）</summary>
public sealed record MotionSummary(string Screen, int Timeouts, double Coverage, bool Learned, bool TooWide, int ActivityTurns, double MeanActivity);
public enum HypothesisStatus
{
    /// <summary>予測の材料がない（まだ比べていない）</summary>
    Untested,
    /// <summary>予測の不確かさが大きい（次の観測で多くが分かる）</summary>
    Testing,
    /// <summary>最後の修正の後、比べた回数が少ない</summary>
    Revised,
    /// <summary>不確かさが小さく、比べた結果が多くそろっている</summary>
    Settled,
    /// <summary>取り消した・休止した</summary>
    Withdrawn,
}

public sealed record Hypothesis(HypothesisKind Kind, string Subject, string Claim, HypothesisStatus Status, double Evidence, int Checks, int Hits, double Uncertainty, long? SinceSeq, string Source);

public static class HypothesisBoard
{
    /// <summary>「確かめ中」とする予測の不確かさの下限（0〜1。何も観測していない関係が 1）</summary>
    public const double TestingUncertainty = 0.5;   // 根拠: 汎用（何も観測していない関係の情報量の半分）
    /// <summary>修正の後「作り直した直後」とする、比べた回数の上限</summary>
    public const int RevisedChecks = 5;              // 根拠: 汎用（関係ごとの変化点の最小の長さ RelationMinRun と同じ）
    /// <summary>並びを「確か」とする、最近の実行で予測が外れた割合の上限</summary>
    public const double SettledSurpriseRate = 0.5;   // 根拠: 汎用（偶然（半々）より外れが少ない）

    public static HypothesisStatus StatusOf(RelationSummary r)
    {
        if (r.TopNext == null || r.Observations == 0) return HypothesisStatus.Untested;
        if (r.RevisionCount > 0 && r.ChecksSince < RevisedChecks) return HypothesisStatus.Revised;
        return r.Uncertainty >= TestingUncertainty ? HypothesisStatus.Testing : HypothesisStatus.Settled;
    }

    public static IReadOnlyList<Hypothesis> Build(IEnumerable<RelationSummary> relations,
        IEnumerable<(int Vis, string Coarse, string Node, bool Active)> splits,
        IEnumerable<ActionSequenceCandidate> sequences, long now, long sequenceHorizon, IEnumerable<MotionSummary>? motions = null)
    {
        var l = new List<Hypothesis>();
        foreach (var m in motions ?? Array.Empty<MotionSummary>())
            l.Add(new Hypothesis(HypothesisKind.Motion, m.Screen,
                m.TooWide ? $"入力なしでも広い範囲（画面の {m.Coverage.ToString("P0", CultureInfo.InvariantCulture)}）が動き続ける"
                          : $"入力なしでも画面の {m.Coverage.ToString("P1", CultureInfo.InvariantCulture)} が動き続ける" + (m.ActivityTurns > 0 ? $"（その中の変化の割合の平均 {m.MeanActivity.ToString("P0", CultureInfo.InvariantCulture)}・{m.ActivityTurns} 手）" : ""),
                m.TooWide ? HypothesisStatus.Withdrawn : m.Learned ? HypothesisStatus.Settled : m.Timeouts > 0 ? HypothesisStatus.Testing : HypothesisStatus.Untested,
                m.Timeouts, m.Timeouts, 0, double.NaN, null, "画面ごとの動き続ける所"));
        foreach (var r in relations)
            l.Add(new Hypothesis(HypothesisKind.Relation, $"{r.Node}「{r.Action}」",
                r.TopNext == null ? "次の画面は未知" : $"次の画面は {r.TopNext}（{r.PTop.ToString("P0", CultureInfo.InvariantCulture)}）",
                StatusOf(r), r.Evidence, r.ChecksSince, r.HitsSince, r.Uncertainty, r.LastRevisionSeq, "内部モデル"));
        foreach (var (vis, coarse, node, active) in splits)
            l.Add(new Hypothesis(HypothesisKind.Split, node, $"{coarse} の中の見た目の状態 {vis} は別の画面", active ? HypothesisStatus.Settled : HypothesisStatus.Withdrawn,
                double.NaN, 0, 0, double.NaN, null, "見た目の状態の分割"));
        foreach (var c in sequences)
        {
            var recent = c.Executions.Where(e => now - e.Seq <= sequenceHorizon).ToList();
            int en = recent.Count, ok = recent.Count(e => !e.Surprised);
            double er = c.RecentExecutionSurprise(now, sequenceHorizon).Rate;
            var st = c.Status == SequenceStatus.Dormant ? HypothesisStatus.Withdrawn
                : en == 0 ? HypothesisStatus.Untested
                : en >= RevisedChecks && er < SettledSurpriseRate ? HypothesisStatus.Settled : HypothesisStatus.Testing;
            l.Add(new Hypothesis(HypothesisKind.Sequence, c.Name, $"{c.Start} から {string.Join(" → ", c.Steps)} で {c.End} に着く（再現性 {c.Reproducibility.ToString("P0", CultureInfo.InvariantCulture)}）",
                st, c.AtStart, en, ok, double.NaN, c.StatusSeq > 0 ? c.StatusSeq : null, "操作の並び"));
        }
        return l;
    }

    private static string StatusText(HypothesisStatus s) => s switch
    {
        HypothesisStatus.Untested => "未検証", HypothesisStatus.Testing => "確かめ中", HypothesisStatus.Revised => "作り直した直後",
        HypothesisStatus.Settled => "確か", HypothesisStatus.Withdrawn => "取り消し・休止", _ => s.ToString(),
    };

    /// <summary>Viewer 向けの文: 状態ごとの件数と、作り直した直後・確かめ中（不確かさの大きい順）の関係、分割・並び</summary>
    public static string Text(IReadOnlyList<Hypothesis> hs, int k = 4)
    {
        if (hs.Count == 0) return "まだありません（操作の結果を予測できる経験がたまると、ここに「〜すると〜になる」という見込みを並べます）";
        var sb = new StringBuilder();
        foreach (var g in hs.GroupBy(h => h.Kind).OrderBy(g => g.Key))
        {
            string name = g.Key switch { HypothesisKind.Relation => "関係（画面・操作 → 次の画面）", HypothesisKind.Split => "画面の分割", HypothesisKind.Motion => "入力なしで動き続ける所", _ => "操作の並び" };
            sb.Append(sb.Length > 0 ? "\n" : "").Append($"{name} {g.Count()}: ")
              .Append(string.Join("・", g.GroupBy(h => h.Status).OrderBy(x => x.Key).Select(x => $"{StatusText(x.Key)} {x.Count()}")));
        }
        var rel = hs.Where(h => h.Kind == HypothesisKind.Relation).ToList();
        foreach (var h in rel.Where(h => h.Status == HypothesisStatus.Revised).OrderByDescending(h => h.SinceSeq).Take(k))
            sb.Append($"\n[作り直した直後] {h.Subject}: {h.Claim}・修正の後に比べた {h.Checks} 回中 当たり {h.Hits}");
        foreach (var h in rel.Where(h => h.Status == HypothesisStatus.Testing).OrderByDescending(h => h.Uncertainty).ThenBy(h => h.Subject, StringComparer.Ordinal).Take(k))
            sb.Append($"\n[確かめ中] {h.Subject}: {h.Claim}・不確かさ {h.Uncertainty.ToString("0.00", CultureInfo.InvariantCulture)}・証拠 {h.Evidence.ToString("0.#", CultureInfo.InvariantCulture)} 回分");
        foreach (var h in hs.Where(h => h.Kind == HypothesisKind.Sequence && h.Status != HypothesisStatus.Withdrawn).OrderByDescending(h => h.Checks).ThenBy(h => h.Subject, StringComparer.Ordinal).Take(2))
            sb.Append($"\n[{StatusText(h.Status)}] {h.Subject}: {h.Claim}・最近の実行 {h.Checks} 回（予測どおり {h.Hits}）");
        foreach (var h in hs.Where(h => h.Kind == HypothesisKind.Motion && h.Status != HypothesisStatus.Untested).OrderBy(h => h.Subject, StringComparer.Ordinal).Take(2))
            sb.Append($"\n[{StatusText(h.Status)}] 画面 {h.Subject}: {h.Claim}・時間切れ {h.Checks} 回から");
        return sb.ToString();
    }
}

/// <summary>新発見の種類（記録の出どころ）</summary>
public enum DiscoveryKind { ScreenMode, Node, Split, SplitReverted, Sequence, Pattern, Revision, Motion, NoInputTransition, FlowReset }

/// <summary>新発見の1件（説明層・表示だけ）。それぞれの出どころの表（画面の種類・分割の出来事・並びの出来事・系列のパターン・修正）にも記録がある</summary>
public sealed record Discovery(long Seq, DiscoveryKind Kind, string Subject, string Detail);

/// <summary>新発見の一覧（新しい順・上限つき）。表示だけで、選択・学習からは参照しない</summary>
public sealed class DiscoveryLog
{
    public const int Capacity = 200;   // 根拠: 汎用（表示用に覚える件数。記録そのものは各表に残る）
    private readonly LinkedList<Discovery> _l = new();
    private readonly Dictionary<DiscoveryKind, int> _counts = new();
    private readonly object _gate = new();

    public void Add(Discovery d)
    {
        lock (_gate)
        {
            _l.AddFirst(d); while (_l.Count > Capacity) _l.RemoveLast();
            _counts[d.Kind] = (_counts.TryGetValue(d.Kind, out var n) ? n : 0) + 1;
        }
    }
    public IReadOnlyList<Discovery> Recent(int k) { lock (_gate) return _l.Take(k).ToList(); }
    public IReadOnlyDictionary<DiscoveryKind, int> Counts() { lock (_gate) return new Dictionary<DiscoveryKind, int>(_counts); }

    public static string KindText(DiscoveryKind k) => k switch
    {
        DiscoveryKind.ScreenMode => "画面の種類", DiscoveryKind.Node => "初めて着いた画面", DiscoveryKind.Split => "画面の分割", DiscoveryKind.SplitReverted => "分割の取り消し",
        DiscoveryKind.Sequence => "操作の並び", DiscoveryKind.Pattern => "系列のパターン", DiscoveryKind.Revision => "予測の作り直し", DiscoveryKind.Motion => "入力なしで動き続ける所", DiscoveryKind.NoInputTransition => "入力なしの遷移", DiscoveryKind.FlowReset => "エピソードの区切りの候補", _ => k.ToString(),
    };

    public string Text(int k = 10)
    {
        var c = Counts(); var r = Recent(k);
        if (r.Count == 0) return "この回はまだありません（新しい画面の種類・初めて着いた画面・分割・操作の並び・系列のパターン・予測の作り直しを、見つけた順に並べます）";
        var sb = new StringBuilder("この回: " + string.Join("・", c.OrderBy(kv => kv.Key).Select(kv => $"{KindText(kv.Key)} {kv.Value}")));
        foreach (var d in r) sb.Append($"\n#{d.Seq} [{KindText(d.Kind)}] {d.Subject}{(d.Detail.Length > 0 ? "（" + d.Detail + "）" : "")}");
        return sb.ToString();
    }
}
