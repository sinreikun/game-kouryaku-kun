using GameKouryakuKun.Core.Perception;

namespace GameKouryakuKun.Core.Exploration;

// 予測 → 比較 → 修正 の内部モデル（v0.11.0）。
// 手を送る前に「この画面でこの操作をすると、次にどの画面になるか・画面が変わるか」を予測し、結果と比べて外れを測る。
// ある関係（画面, 操作）の外れ方が持続的に変わったら（関係ごとのベイズ的変化点）、その関係の古い証拠の重みを下げて予測を作り直す（修正）。
// 修正しても DB の直接の記録は消さない（作業用の重みだけ。修正は model_revisions に残し、起動時は連続履歴の流し直しで同じ状態を作り直す）。
// 予測の不確かさ（次の観測で関係について得られる情報の量）は探索の選択に使う（Explorer.UncertaintyValue）。
// 中立の記号（画面ノード・操作の名前）だけを扱い、ゲームの意味は持たない。

/// <summary>手を送る前の予測。Top は確率の高い次の画面（最大 3 個）。DirectN・ReinterpretedN・PriorN は予測に使った証拠の量と出どころ</summary>
public sealed record PredictedOutcome(string Node, string Action, IReadOnlyList<(string Next, double P)> Top, double PChange, double Entropy,
    double DirectN, double ReinterpretedN, double PriorN, IReadOnlyList<string> PriorSources, double Uncertainty)
{
    public string? TopNext => Top.Count > 0 ? Top[0].Next : null;
    public double PTop => Top.Count > 0 ? Top[0].P : 0;
}

/// <summary>予測を作り直した記録（修正）。WeightBefore／WeightAfter は作業用の証拠の重みの合計</summary>
public sealed record ModelRevision(long Seq, string Node, string Action, double WeightBefore, double WeightAfter, int RecentKept, string Reason);

/// <summary>予測と結果の比較。Excess は外れからふだんの不確かさ（エントロピー）を引いた値（ビット）</summary>
/// <summary>
/// 内部モデルの1つの関係（画面, 操作）→ 次の画面 の要約。Evidence は作業用の重み（修正で縮む）、Observations は観測の回数。
/// ChecksSince・HitsSince は最後の修正の後に、材料のある予測と結果を比べた回数と当たった回数
/// </summary>
public sealed record RelationSummary(string Node, string Action, double Evidence, int Observations, string? TopNext, double PTop, double Uncertainty,
    int ChecksSince, int HitsSince, int RevisionCount, long? LastRevisionSeq, long LastSeq);

public sealed record PredictionCheck(PredictedOutcome Prediction, string Actual, bool Changed, double PActual, double Surprisal, double Excess, bool Hit, double ChangeBrier)
{
    public ModelRevision? Revision { get; init; }
}

public sealed class TransitionModel
{
    private sealed class Relation
    {
        public readonly Dictionary<string, double> W = new(StringComparer.Ordinal);
        public double Total, Changed, Reinterpreted;
        public readonly Queue<(string Next, bool Changed, bool Reinterpreted)> Recent = new();
        public BayesianChangePoint Shift = null!;
        public int Observations;
        /// <summary>最後の修正の後に予測と比べた回数・当たった回数（表示だけ。選択と修正には使わない）</summary>
        public int ChecksSince, HitsSince, RevisionCount;
        public long LastRevisionSeq = -1, LastSeq = -1;
    }

    private readonly Dictionary<(string, string), Relation> _r = new();
    private readonly HashSet<string> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExperiencePrior>> _priors = new(StringComparer.Ordinal);

    /// <summary>予測の外れとして数える最小の値（ビット）。系列の学習と同じ定義</summary>
    public double MinExcessBits { get; init; } = 1.0;        // 根拠: 汎用（情報量の 1 ビット＝起きた結果の確率がふだんの半分以下）
    /// <summary>関係ごとの変化点の検出で、これより短い区間からの切り替わりは数えない（関係ごとの観測はまばらなので系列全体の 10 より短い）</summary>
    public int RelationMinRun { get; init; } = 5;            // 根拠: 汎用（偶然の外れの連続と区別できる最小の長さ）
    /// <summary>関係ごとの区間の長さの事前の期待値の逆数</summary>
    public double RelationChangeRate { get; init; } = 1.0 / 100;   // 根拠: 汎用（系列全体の変化点と同じ事前の期待値 100 回）
    /// <summary>修正のとき、古い区間の証拠を何回分の仮の観測として残すか（消さずに弱い事前の推定にする）</summary>
    public double RevisedRetain { get; init; } = 2;          // 根拠: 汎用（分割前のノードの弱い事前の推定と同じ強さ）
    /// <summary>修正のときに新しい区間の証拠として残す、最近の観測の数の上限</summary>
    public int RecentKeep { get; init; } = 10;               // 根拠: 汎用（変化点の検出に要る長さ RelationMinRun の 2 倍）

    public long Revisions { get; private set; }
    public long Checks { get; private set; }
    /// <summary>観測が不確かなため変化点の検出に入れなかった手の数</summary>
    public long AmbiguousSkipped { get; private set; }
    public int Relations => _r.Count;

    /// <summary>弱い事前の推定（Explorer と同じもの）を置き直す。推定の推定はしない（出どころの直接の重みだけを使う）</summary>
    public void SetPriors(IEnumerable<(string Node, ExperiencePrior Prior)> priors)
    {
        _priors.Clear();
        foreach (var (n, p) in priors) { if (!_priors.TryGetValue(n, out var l)) _priors[n] = l = new(); l.Add(p); }
    }

    private int V => Math.Max(2, _states.Count + 1);   // 次の画面になりうるものの数（既知の画面＋まだ見ていない画面 1 つ）。系列の学習の TransitionPredictor はラプラス平滑化のまま（v0.14.0 で変えたのはこちらだけ）
    /// <summary>まだ見ていない結果の出やすさ（経験から推定）</summary>
    public NoveltyConcentration Novelty { get; } = new();
    private int Unseen(Dictionary<string, double> c) => Math.Max(1, V - c.Count(kv => kv.Value > 0));

    /// <summary>予測に使う疑似回数（直接の重み＋弱い事前の推定）。平滑化の前</summary>
    private (Dictionary<string, double> C, double Total, double ChangedRate, double DirectN, double Reint, double PriorN, List<string> Sources) Counts(string node, string action)
    {
        var c = new Dictionary<string, double>(StringComparer.Ordinal);
        double total = 0, ch = 0, directN = 0, reint = 0, priorN = 0; var src = new List<string>();
        if (_r.TryGetValue((node, action), out var r))
        {
            foreach (var (k, w) in r.W) c[k] = w;
            total = r.Total; ch = r.Changed; directN = r.Total; reint = r.Reinterpreted;
        }
        if (_priors.TryGetValue(node, out var ps))
            foreach (var p in ps)
            {
                if (!_r.TryGetValue((p.Source, action), out var sr) || sr.Total <= 0) continue;
                foreach (var (k, w) in sr.W) c[k] = (c.TryGetValue(k, out var x) ? x : 0) + p.Strength * w / sr.Total;
                total += p.Strength; ch += p.Strength * sr.Changed / sr.Total; priorN += p.Strength;
                src.Add($"{p.Origin}:{p.Source}");
            }
        return (c, total, (ch + 1) / (total + 2), directN, reint, priorN, src);
    }

    public PredictedOutcome Predict(string node, string action)
    {
        var (c, total, pch, dn, ri, pn, src) = Counts(node, action);
        var top = c.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(3)
            .Select(kv => (kv.Key, Novelty.PSeen(kv.Value, total))).ToList();
        double h = Novelty.Entropy(c.Values, total, Unseen(c));
        return new PredictedOutcome(node, action, top, pch, h, dn, ri, pn, src, Uncertainty(c.Values, total));
    }

    /// <summary>
    /// 次の観測で、この関係（次の画面の分布）について得られる情報の量（ディリクレ分布の相互情報量）。
    /// 何も観測していない関係（2つの結果が同じ確からしさ）の値で割って 0〜1 にする。修正で古い証拠の重みが下がると大きくなる
    /// </summary>
    /// <summary>
    /// v0.28.2: 次の画面の分布の不確かさ。+1 の擬似観測は直接観測した次の画面と「まだ見ていない次の画面」にだけ置き、弱い事前の推定（借りた・分割前）の重みは
    /// その次の画面を直接見ていなければ「まだ見ていない」の側に足す（実機 #105: 重み 0.04 の推定でも次の画面の種類ごとに +1 が付き、直接の観測が増えても不確かさが 1 に張り付いた）
    /// </summary>
    public double Uncertainty(string node, string action)
    {
        var (c, total, _, _, _, _, _) = Counts(node, action);
        var direct = _r.TryGetValue((node, action), out var r) ? r.W : null;
        var a = new List<double>(); double unseen = 1;
        foreach (var (k, w) in c) { if (direct != null && direct.TryGetValue(k, out var dw) && dw > 0) a.Add(w + 1); else unseen += w; }
        a.Add(unseen);
        if (a.Count < 2) return 1;   // 直接の観測がなければ、従来どおり最大（未知）
        return Math.Clamp(DirichletInformation(a) / ZeroInfo, 0, 1);
    }

    private static readonly double ZeroInfo = DirichletInformation(new[] { 1.0, 1.0 });
    /// <summary>v0.27.0: 2 値（起きた・起きなかった）の観測の不確かさ。Uncertainty と同じ Dirichlet 情報量の規則（観測 0 なら 1）</summary>
    public static double BinaryUncertainty(int yes, int no) => Math.Clamp(DirichletInformation(new[] { Math.Max(0, yes) + 1.0, Math.Max(0, no) + 1.0 }) / ZeroInfo, 0, 1);
    private static double Uncertainty(IEnumerable<double> counts, double total)
    {
        var a = counts.Select(w => w + 1).Append(1.0).ToArray();   // 観測した次の画面＋まだ見ていない次の画面
        if (a.Length < 2) a = new[] { 1.0, 1.0 };
        return Math.Clamp(DirichletInformation(a) / ZeroInfo, 0, 1);
    }

    /// <summary>I(X; θ) = H(E[θ]) − E[H(X|θ)]（ナット）。θ ~ Dir(a)</summary>
    public static double DirichletInformation(IReadOnlyList<double> a)
    {
        double s = a.Sum(), h = 0, eh = ReactionMap.Digamma(s + 1);
        foreach (var x in a) { double m = x / s; if (m > 0) h -= m * Math.Log(m); eh -= m * ReactionMap.Digamma(x + 1); }
        return Math.Max(0, h - eh);
    }

    /// <summary>手の結果を受け取る: 先に予測と比べ、それから関係を更新する。外れ方が持続的に変わったら修正する</summary>
    public PredictionCheck Observe(long seq, string node, string action, string next, bool changed, bool reinterpreted = false, bool ambiguous = false)
    {
        var pred = Predict(node, action);
        var (c, total, _, _, _, _, _) = Counts(node, action);
        bool novel = !(c.TryGetValue(next, out var w) && w > 0);
        double pa = novel ? Novelty.PUnseen(total, Unseen(c)) : Novelty.PSeen(w, total);
        double sup = -Math.Log2(pa), excess = sup - pred.Entropy;
        double pc = pred.PChange, brier = (changed ? 1 - pc : pc) * (changed ? 1 - pc : pc);
        bool hit = pred.TopNext == next && (pred.DirectN + pred.PriorN) > 0;
        Checks++;
        Novelty.Observe(total, novel);

        _states.Add(node); _states.Add(next);
        if (!_r.TryGetValue((node, action), out var r))
            _r[(node, action)] = r = new Relation { Shift = new BayesianChangePoint { MinRun = RelationMinRun, PriorChangeRate = RelationChangeRate, MaxRunLength = 100 } };
        r.W[next] = (r.W.TryGetValue(next, out var x) ? x : 0) + 1; r.Total++; if (changed) r.Changed++; if (reinterpreted) r.Reinterpreted++;
        r.Observations++; r.LastSeq = seq;
        if (informed0(pred)) { r.ChecksSince++; if (hit) r.HitsSince++; }
        r.Recent.Enqueue((next, changed, reinterpreted)); while (r.Recent.Count > RecentKeep) r.Recent.Dequeue();
        ModelRevision? rev = null;
        // 初めての関係は外れとして数えない（予測できる材料がない）。材料がある予測の外れだけで変化点を見る
        bool informed = pred.DirectN > 0;
        // 観測が境目の近く（画面の割り当てが不確か）の手は、変化点の検出に入れない（見間違いで関係を作り直さない）
        if (!ambiguous && r.Shift.Observe(informed && excess >= MinExcessBits) && r.Total > RevisedRetain + 1)
            rev = Revise(seq, node, action, r);
        if (ambiguous) AmbiguousSkipped++;
        return new PredictionCheck(pred, next, changed, pa, sup, excess, hit, brier) { Revision = rev };
    }

    /// <summary>
    /// 修正: 最近の区間（変化点の後と推定した最近の観測）はそのまま残し、それより前の証拠は合計 RevisedRetain 回分の仮の観測に縮める（割合は保つ）。
    /// 何も捨てない（DB の記録はそのまま。作業用の重みだけを変える）
    /// </summary>
    private ModelRevision Revise(long seq, string node, string action, Relation r)
    {
        double before = r.Total;
        int keep = Math.Clamp(r.Shift.MapRunLength, 1, r.Recent.Count);
        var recent = r.Recent.Skip(r.Recent.Count - keep).ToList();
        var old = new Dictionary<string, double>(r.W, StringComparer.Ordinal);
        double oldCh = r.Changed, oldRe = r.Reinterpreted;
        foreach (var (n, ch, re) in recent) { old[n] -= 1; if (ch) oldCh -= 1; if (re) oldRe -= 1; }
        double oldTotal = Math.Max(0, before - recent.Count), f = oldTotal > RevisedRetain ? RevisedRetain / oldTotal : 1;
        r.W.Clear();
        foreach (var (n, v) in old) if (v * f > 1e-9) r.W[n] = v * f;
        foreach (var (n, ch, re) in recent) r.W[n] = (r.W.TryGetValue(n, out var v) ? v : 0) + 1;
        r.Total = oldTotal * f + recent.Count; r.Changed = Math.Max(0, oldCh) * f + recent.Count(z => z.Changed); r.Reinterpreted = Math.Max(0, oldRe) * f + recent.Count(z => z.Reinterpreted);
        Revisions++; r.RevisionCount++; r.LastRevisionSeq = seq; r.ChecksSince = 0; r.HitsSince = 0;
        return new ModelRevision(seq, node, action, before, r.Total, recent.Count,
            $"予測の外れ方が持続的に変わった（関係ごとの変化点）。最近の {recent.Count} 回を残し、それより前の {oldTotal:0} 回分の証拠を {Math.Min(oldTotal, RevisedRetain):0.#} 回分の仮の観測に縮めた");
    }

    private static bool informed0(PredictedOutcome p) => p.DirectN + p.PriorN > 0;

    /// <summary>関係ごとの要約（読むだけ。Viewer の「仮説」と点検に使う）。作業用の重みはそのまま</summary>
    public IReadOnlyList<RelationSummary> Summaries()
    {
        var l = new List<RelationSummary>(_r.Count);
        foreach (var ((n, a), r) in _r)
        {
            var p = Predict(n, a);
            l.Add(new RelationSummary(n, a, r.Total, r.Observations, p.TopNext, p.PTop, p.Uncertainty, r.ChecksSince, r.HitsSince, r.RevisionCount,
                r.LastRevisionSeq >= 0 ? r.LastRevisionSeq : null, r.LastSeq));
        }
        return l;
    }

    /// <summary>過去の連続履歴を流し直して作り直す（修正も同じ規則で起きる。記録はしない）</summary>
    public void Replay(IEnumerable<StreamStep> history) { foreach (var s in history) Observe(s.Seq, s.State, s.Action, s.NextState, s.Changed, s.Reinterpreted); }

    /// <summary>連続履歴にない集計（操作の並びの結果など）を読み込む</summary>
    public void Seed(string node, string action, string next, int n, int changed)
    {
        if (n <= 0) return;
        _states.Add(node); _states.Add(next);
        if (!_r.TryGetValue((node, action), out var r))
            _r[(node, action)] = r = new Relation { Shift = new BayesianChangePoint { MinRun = RelationMinRun, PriorChangeRate = RelationChangeRate, MaxRunLength = 100 } };
        r.W[next] = (r.W.TryGetValue(next, out var x) ? x : 0) + n; r.Total += n; r.Changed += changed;
    }
}

/// <summary>
/// 1手の判断の記録（Viewer の「なぜこの操作か」）。点数の式の各項の値と、手を送る前の予測・結果との比較だけで、隠れた推論は含まない。
/// Rule: scored・momentum・chord_probe・fallback（Explorer）と sequence_step（操作の並びの途中の手）
/// </summary>
public sealed record DecisionRecord(long? Seq, long? TurnId, string Node, string Chosen, string? Composite, string Rule, double Probability, double Score,
    IReadOnlyList<CandidateScore> Candidates, PredictedOutcome? Prediction, PredictionCheck? Check, long? RevisionId)
{
    /// <summary>観測の確かさ（v0.12.0）: 見た目の状態・画面の種類の割り当ての距離 ÷ 新しく作る距離、判定に使ったフレームの古さ、境目の近くか</summary>
    public double ObsVisRatio { get; init; } = double.NaN;
    public double ObsModeRatio { get; init; } = double.NaN;
    public double FrameAgeMs { get; init; } = double.NaN;
    public bool Ambiguous { get; init; }
    /// <summary>行動の出どころ（v0.15.0）: "agent"（攻略くんが選んだ）・"human"（人間が送った。世界の観測としてだけ使う）</summary>
    public string ActionSource { get; init; } = "agent";
    /// <summary>入力なしの基準と比べた、この画面・操作の変化が操作によるものである割合の推定（手を選ぶ前の経験から）</summary>
    public ActionAttribution? Attribution { get; init; }
}
