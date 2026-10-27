namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// 操作の物理的な種類だけ（身体の構造。U2）: 方向パッドのキー・ボタン・2キー同時押し・経験から見つけた操作の並び（複合の操作の候補）。
/// キーの意味（決定・取消など）は持たない。Wait は「キーを送らずに待つ」中立の観測（v0.15.0。入力なしで画面がどうなるかを学ぶ）
/// </summary>
public enum ActionGroup { Dpad, Button, Chord, Sequence, Wait }

public sealed record ExploreAction(string Name, ActionGroup Group, string[] Keys)
{
    public bool IsDpad => Group == ActionGroup.Dpad;
    public bool IsWait => Group == ActionGroup.Wait;
    /// <summary>キーを送らずに待つ（中立の観測）。名前は中立</summary>
    public static ExploreAction Wait { get; } = new("wait", ActionGroup.Wait, Array.Empty<string>());
    /// <summary>並び（Sequence）のときの、1手ずつの操作。並びは1手ずつ普通の手として送る</summary>
    public IReadOnlyList<ExploreAction>? Steps { get; init; }
    /// <summary>並びを試す画面ノード（見つけたときの始まり）。null ならどのノードでも</summary>
    public string? At { get; init; }
    /// <summary>経験から見つけた並びの番号（ActionSequence #n の n）</summary>
    public int? SequenceId { get; init; }
}

/// <summary>ある画面ノードでの、ある操作の結果の集計</summary>
public sealed class ActionStats
{
    public int Tries;
    public int Changes;
    /// <summary>別のノードへ移った回数</summary>
    public int Left;
    /// <summary>初めて見るノードへ移った回数</summary>
    public int Discoveries;
    public Dictionary<string, int> To { get; } = new(StringComparer.Ordinal);
}

public sealed class NodeStats
{
    public int Visits;
    public Dictionary<string, ActionStats> Actions { get; } = new(StringComparer.Ordinal);
    public ActionStats Of(string a) => Actions.TryGetValue(a, out var s) ? s : Actions[a] = new ActionStats();
}

/// <summary>操作ごとの可逆性の観測（どの画面でも共通に数える）</summary>
public sealed class Reversibility
{
    /// <summary>別の画面へ移ったあと、ReturnWindow 手以内に元の画面へ戻れた回数</summary>
    public int Returned;
    /// <summary>戻れなかった回数</summary>
    public int NotReturned;
    /// <summary>直前の画面へ戻す操作として働いた回数（Y へ来た直後に押して、来る前の画面 X へ戻った）</summary>
    public int UndoHits;
    /// <summary>Laplace 平滑化した「戻れる」確率（観測がなければ 0.5）</summary>
    public double Rate => (Returned + 1.0) / (Returned + NotReturned + 2.0);
}

/// <summary>選んだ操作と理由（ログ・記録用）。Priors は点数に使った「直接の経験ではない」推定の出どころ（借用・親ノード）</summary>
public sealed record ExploreChoice(ExploreAction Action, string Reason, double Score)
{
    public IReadOnlyList<string> Priors { get; init; } = Array.Empty<string>();
    public double Information { get; init; }
    /// <summary>どの規則で選んだか（scored＝点数による抽選・momentum＝同じ操作を続けた・chord_probe＝2キー同時押しのためし・fallback＝候補なし）</summary>
    public string Rule { get; init; } = "scored";
    /// <summary>点数による抽選のとき、この操作が選ばれる確率</summary>
    public double Probability { get; init; } = double.NaN;
    /// <summary>点数の内訳（全候補。判断の理由を構造化した記録。隠れた推論ではなく、点数の式の各項の値）</summary>
    public IReadOnlyList<CandidateScore> Candidates { get; init; } = Array.Empty<CandidateScore>();
}

/// <summary>
/// 1つの候補の点数の内訳。Score = Reversibility × (0.5 + (Novelty + Rates + InformationWeight×Information + UncertaintyWeight×Uncertainty) × (Stagnant ? 3 : 1))。
/// Novelty は直接の試行回数だけから（未試行 2.0）、Rates は直接の経験と弱い事前の推定を合わせた割合の項
/// </summary>
public sealed record CandidateScore(string Action, double Score, double Probability, int DirectTries, double Novelty, double Rates, double Information,
    double Uncertainty, double Reversibility, bool Stagnant, bool Deferred, IReadOnlyList<string> Priors)
{
    /// <summary>差し引いた「入力なしでも変わる割合」（その画面で待つ手の結果。基準がなければ 0）</summary>
    public double Natural { get; init; }
    /// <summary>v0.25.3: 行き先の地域の進みと今の地域の進みの比（DestinationValue。なければ 1）</summary>
    public double Destination { get; init; } = 1;   // 根拠: 汎用（中立＝掛けない）
}

/// <summary>
/// ほかのノードの経験を、弱い事前の推定として使う（v0.10.0）。コピーはしない: 元のノードの直接の経験の割合を、Strength 回分の仮の観測として足すだけ。
/// Origin は "borrowed"（遷移の特徴が似た別の画面）か "parent"（分割前の粗いノードのうち、分け直せなかった集計）
/// </summary>
public sealed record ExperiencePrior(string Source, string Origin, double Strength);

/// <summary>
/// 事前知識なしの手探り探索（phase2-design §7A の最小版）。
/// 画面ノード（教師なしの種類＋矩形の配置）ごとに、まだ試していない操作・新しいノードに行けた操作・予測の確かでない操作（UncertaintyValue）を優先する。
/// キーの意味は一切使わない（論理キー名は中立の dpad_*・button*。U2）。
/// 安全規則は観測から学ぶ可逆性だけ: 別の画面へ移ったあと元へ戻れた操作を先に試し、戻れなかったことが多い操作は、
/// その画面のほかの操作を一通り試すか、進展がなくなるまで後回しにする（禁止はしない）。
/// 停止の判断はしない（画面が反応しないことは未知の状態の性質として記録するだけ。入力の送信の失敗は Session の InputDeliveryMonitor）。
/// </summary>
public sealed class Explorer
{
    private readonly List<ExploreAction> _actions;
    private readonly List<ExploreAction> _singles;
    private readonly Random _rng;
    private readonly Dictionary<string, NodeStats> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reversibility> _rev = new(StringComparer.Ordinal);
    private readonly HashSet<string> _actionsInStreak = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<(string Source, string Origin), double>> _priors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inactive = new(StringComparer.Ordinal);
    private readonly List<(string Action, string Origin, int Age)> _pendingReturns = new();
    private ExploreAction? _last; private bool _lastChanged; private string? _lastNode; private string? _prevNode;
    private int _stayNoProgress;

    /// <summary>可逆性の判定に使う手数（別の画面へ移ってから、この手数以内に戻れたら「戻れた」）</summary>
    public int ReturnWindow { get; init; } = 6;          // 根拠: 汎用（短い往復＝数手の操作で戻れるかを見る）
    /// <summary>後回しにする操作の条件: 戻れなかった回数の下限</summary>
    public int DeferMinEvidence { get; init; } = 3;      // 根拠: 汎用（少数の偶然で後回しにしない統計上の下限）
    /// <summary>後回しにする操作の条件: 戻れる確率の上限</summary>
    public double DeferRate { get; init; } = 0.35;       // 根拠: 汎用（戻れる確率が平滑化後の事前値 0.5 より明らかに低い）
    /// <summary>後回しの操作を解禁する前に、そのノードのほかの操作を何回ずつ試すか</summary>
    public int DeferUntilOthersTried { get; init; } = 2; // 根拠: 汎用（1回の偶然を避ける最小の繰り返し）
    /// <summary>同じノードで進展なしがこの手数続いたら、試した回数の少ない操作を強く優先する</summary>
    public int StagnationTurns { get; init; } = 8;       // 根拠: 汎用（探索の停滞の検出）
    /// <summary>直前に画面を変えて同じノードに留まった操作を続けて選ぶ確率（同じ操作を続ける汎用の探索の工夫）</summary>
    public double Momentum { get; init; } = 0.6;         // 根拠: 汎用（効いた操作の反復。キーの種類は問わない）
    /// <summary>まだ試していない2キー同時押しを、ためしに選ぶ確率（身体でできる組合せの発見）</summary>
    public double ChordProbeProbability { get; init; } = 0.05; // 根拠: 汎用（単独キーの探索を妨げない小さな割合）

    /// <summary>
    /// 試していない関係の情報の価値（0〜1）。区画の反応の対応表から、まだ分かっていない反応の関係が多い操作ほど大きい。
    /// 変化の大きさの価値ではない（反応しないことが分かった観測も不確かさを減らす）
    /// </summary>
    public Func<string, ExploreAction, double>? InformationValue { get; set; }
    public double InformationWeight { get; init; } = 0.5;   // 根拠: 汎用（未試行の重み 2.0 より小さい補助の項）
    /// <summary>
    /// 予測の不確かさ（0〜1）。内部モデル（TransitionModel）で、次の観測でその関係について得られる情報の量。
    /// 予測の外れから関係を修正すると古い証拠の重みが下がり、この値が上がって、その関係をもう一度確かめる手が選ばれやすくなる（予測の外れ→修正→次の選択）
    /// </summary>
    public Func<string, ExploreAction, double>? UncertaintyValue { get; set; }
    /// <summary>v0.25.3 ブロック E: その操作の行き先（予測）の地域の進み ÷ 今の地域の進み（0.05〜4）。未試行・待つ手は 1（行き先が分からない・固定の価値は置かない）</summary>
    public Func<string, ExploreAction, double>? DestinationValue { get; set; }
    public double UncertaintyWeight { get; init; } = 0.5;   // 根拠: 汎用（区画の反応の情報の量と同じ大きさの補助の項）

    /// <summary>
    /// キーを送った手で画面が変わらなかった連続数（観測の記録だけ。v0.15.0 から停止の条件には使わない:
    /// 画面が反応しないのは未知の状態の性質かもしれないため。止まるのは入力の送信そのものの失敗＝InputDeliveryMonitor だけ）
    /// </summary>
    public int NoChangeStreak { get; private set; }
    /// <summary>上の連続のあいだに試した操作の種類の数</summary>
    public int NoChangeDistinct => _actionsInStreak.Count;
    public IReadOnlyDictionary<string, NodeStats> Nodes => _nodes;
    public IReadOnlyDictionary<string, Reversibility> ReversibilityByAction => _rev;
    public IReadOnlyList<ExploreAction> Actions => _actions;
    public int Discoveries { get; private set; }

    public Explorer(IReadOnlyList<ExploreAction> actions, int seed)
    {
        if (actions.Count == 0) throw new ArgumentException("操作がありません", nameof(actions));
        _actions = actions.ToList(); _singles = actions.Where(a => a.Group is not (ActionGroup.Chord or ActionGroup.Wait)).ToList();
        if (_singles.Count == 0) throw new ArgumentException("キーを送る操作がありません", nameof(actions)); _rng = new Random(seed);
    }

    /// <summary>過去の記録（遷移）を読み込んで、続きから学ぶ。2キー同時押しの記録があれば候補に加える</summary>
    public void Load(IEnumerable<(string From, string Action, string To, int N)> transitions)
    {
        foreach (var (from, a, to, n) in transitions)
        {
            if (n <= 0) continue;
            if (Find(a) == null && TryChord(a) is { } ch) Promote(ch);
            if (Find(a) == null) continue;   // 今の Profile にない操作（キー名の変更前の記録など）は使わない
            var st = Node(from).Of(a);
            st.Tries += n; st.Changes += from == to ? 0 : n; if (from != to) st.Left += n;
            st.To[to] = st.To.TryGetValue(to, out var c) ? c + n : n;
            Node(to);
        }
    }

    /// <summary>過去の可逆性の集計を読み込む</summary>
    public void LoadReversibility(IEnumerable<(string Action, int Returned, int NotReturned, int UndoHits)> rows)
    {
        foreach (var (a, r, nr, u) in rows) { var x = Rev(a); x.Returned += r; x.NotReturned += nr; x.UndoHits += u; }
    }

    private ExploreAction? Find(string name) => _actions.FirstOrDefault(a => a.Name == name);
    /// <summary>その画面でこの操作を候補にできるか（並びは始まりの画面でだけ・休止中は不可）</summary>
    public bool CanUse(ExploreAction a, string node) => Applicable(a, node);
    private bool Applicable(ExploreAction a, string node) => (a.At == null || a.At == node) && !_inactive.Contains(a.Name);

    /// <summary>並びの候補を休止にする（記録は残す。選択の候補から外すだけ）</summary>
    public void Deactivate(string name) => _inactive.Add(name);
    /// <summary>休止の並びを候補に戻す</summary>
    public void Activate(string name) => _inactive.Remove(name);
    public bool IsActive(string name) => !_inactive.Contains(name);

    /// <summary>弱い事前の推定を置く（同じ出どころ・種類なら強さを上書き。0 以下なら外す）</summary>
    public void SetPrior(string node, string source, string origin, double strength)
    {
        if (node == source) return;
        if (!_priors.TryGetValue(node, out var d)) _priors[node] = d = new();
        if (strength <= 0) d.Remove((source, origin)); else d[(source, origin)] = strength;
    }

    public IEnumerable<(string Node, ExperiencePrior Prior)> Priors() =>
        _priors.SelectMany(kv => kv.Value.Select(p => (kv.Key, new ExperiencePrior(p.Key.Source, p.Key.Origin, p.Value))));

    /// <summary>
    /// 直接の経験と弱い事前の推定を合わせた割合の分子と分母。分母 = 直接の回数 + 事前の強さ（仮の観測の回数）なので、直接の経験が増えるとすぐに直接の経験が支配する。
    /// 事前の推定は出どころのノードの直接の経験だけから作る（推定の推定はしない）
    /// </summary>
    public (double Changes, double Left, double Discoveries, double N, List<string> Used) Blended(string node, string action)
    {
        var st = _nodes.TryGetValue(node, out var ns) && ns.Actions.TryGetValue(action, out var x) ? x : null;
        double c = st?.Changes ?? 0, l = st?.Left ?? 0, ds = st?.Discoveries ?? 0, n = st?.Tries ?? 0; var used = new List<string>();
        if (_priors.TryGetValue(node, out var ps))
            foreach (var ((src, origin), w) in ps)
            {
                if (!_nodes.TryGetValue(src, out var sn) || !sn.Actions.TryGetValue(action, out var ss) || ss.Tries == 0) continue;
                c += w * ss.Changes / (double)ss.Tries; l += w * ss.Left / (double)ss.Tries; ds += w * ss.Discoveries / (double)ss.Tries; n += w;
                used.Add($"{origin}:{src}");
            }
        return (c, l, ds, n, used);
    }

    /// <summary>
    /// 記録の読み直し（分け直し）: 分割で新しいノードになった観測を、元の (from, to) から新しい (from, to) へ移す。
    /// 継承ではなく、同じ観測をより細かいノードで解釈し直すだけ（回数の合計は変わらない）。移せるのは手元にある回数まで
    /// </summary>
    public void Reassign(string origFrom, string action, string origTo, string newFrom, string newTo, int n, int changed)
    {
        if (n <= 0 || !_nodes.TryGetValue(origFrom, out var on) || !on.Actions.TryGetValue(action, out var os)) return;
        int k = Math.Min(n, os.To.TryGetValue(origTo, out var have) ? have : 0);
        if (k <= 0) return;
        int ch = Math.Min(Math.Min(changed, k), os.Changes);
        os.Tries -= k; os.Changes -= ch; if (origFrom != origTo) os.Left = Math.Max(0, os.Left - k);
        if ((os.To[origTo] -= k) <= 0) os.To.Remove(origTo);
        var ns = Node(newFrom).Of(action); Node(newTo);
        ns.Tries += k; ns.Changes += ch; if (newFrom != newTo) ns.Left += k;
        ns.To[newTo] = ns.To.TryGetValue(newTo, out var c) ? c + k : k;
    }

    /// <summary>
    /// 経験から見つけた操作の並び（ActionSequence #n）を、その始まりのノードで試せる複合の操作の候補に加える。
    /// 事前の価値は単独の操作の未試行と同じ（上乗せしない）。並びの中の操作が今の候補にないときは加えない
    /// </summary>
    public ExploreAction? AddSequence(ActionSequenceCandidate c)
    {
        if (Find(c.Name) is { } have) return have;
        var steps = c.Steps.Select(n => Find(n) ?? TryChord(n)).ToList();
        if (steps.Count < 2 || steps.Any(x => x == null || x.Group == ActionGroup.Sequence)) return null;
        var a = new ExploreAction(c.Name, ActionGroup.Sequence, Array.Empty<string>()) { Steps = steps!, At = c.Start, SequenceId = c.Id };
        _actions.Add(a);
        return a;
    }

    /// <summary>並びを最後まで送り終えたときの結果を、並びの操作の集計に足す（1手ずつの結果は Observe で数え済み）</summary>
    public void ObserveComposite(string from, ExploreAction a, string to, bool changed, bool discovered)
    {
        var st = Node(from).Of(a.Name);
        Node(to);
        st.Tries++; if (changed) st.Changes++;
        if (to != from) st.Left++;
        if (discovered) st.Discoveries++;
        st.To[to] = st.To.TryGetValue(to, out var c) ? c + 1 : 1;
    }
    private NodeStats Node(string n) => _nodes.TryGetValue(n, out var s) ? s : _nodes[n] = new NodeStats();
    private Reversibility Rev(string a) => _rev.TryGetValue(a, out var r) ? r : _rev[a] = new Reversibility();

    private ExploreAction? TryChord(string name)
    {
        var parts = name.Split('+');
        if (parts.Length != 2) return null;
        var a = _singles.FirstOrDefault(x => x.Name == parts[0]); var b = _singles.FirstOrDefault(x => x.Name == parts[1]);
        return a == null || b == null || a == b ? null : MakeChord(a, b);
    }

    public static ExploreAction MakeChord(ExploreAction a, ExploreAction b)
    {
        var keys = a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        return new ExploreAction(string.Join("+", keys), ActionGroup.Chord, keys);
    }

    private void Promote(ExploreAction chord) { if (Find(chord.Name) == null) _actions.Add(chord); }

    private bool Deferred(string action) { var r = Rev(action); return r.NotReturned >= DeferMinEvidence && r.Rate < DeferRate; }

    private bool SinglesTried(NodeStats ns) => _singles.All(x => ns.Of(x.Name).Tries >= DeferUntilOthersTried);

    public ExploreChoice Choose(string node)
    {
        var ns = Node(node);
        bool stagnant = _stayNoProgress >= StagnationTurns;
        // 直前に画面を変えて同じノードに留まった操作を続ける（キーの種類は問わない）
        if (_last != null && !_last.IsWait && _lastChanged && _lastNode == node && _prevNode == node && _rng.NextDouble() < Momentum)
            return new ExploreChoice(_last, "直前の操作で画面が変わり同じ種類の画面に留まったので、同じ操作を続ける", 0) { Rule = "momentum" };
        // 2キー同時押しの試行（まだ候補に入っていない組を、ためしに押してみる）
        if (_singles.Count >= 2 && _rng.NextDouble() < (stagnant ? 4 : 1) * ChordProbeProbability)
        {
            var a = _singles[_rng.Next(_singles.Count)]; var b = _singles[_rng.Next(_singles.Count)];
            if (a != b)
            {
                var ch = MakeChord(a, b);
                if (Find(ch.Name) == null) return new ExploreChoice(ch, "2キー同時押しをためしに押す（身体でできる組合せの発見）", 0) { Rule = "chord_probe" };
            }
        }
        // 戻せることが分かっていない操作のうち、後回しにする条件を満たすもの
        bool othersTried = _actions.Where(a => Applicable(a, node) && !Deferred(a.Name)).All(a => ns.Of(a.Name).Tries >= DeferUntilOthersTried);
        var scored = new List<(ExploreAction A, double S, string Why, CandidateScore C)>();
        // 待つ手には「戻せたか」が定義できないので、可逆性の倍率はこの画面のほかの候補の平均（中立。固定の 1.0 で不利にしない）
        var keyB = _actions.Where(x => !x.IsWait && Applicable(x, node)).Select(x => 0.5 + Rev(x.Name).Rate).DefaultIfEmpty(1.0).Average();
        foreach (var a in _actions)
        {
            if (!Applicable(a, node)) continue;   // 並びは見つけた始まりのノードでだけ試す
            // v0.26.0（t54u）: 同時押しは、その画面の単独の操作を一通り（DeferUntilOthersTried 回）試した後、またはその画面で既に試した同時押しだけを候補にする。
            // 実機の記録で、同時押しの関係の 87% は構成するキーの 1 つと同じ結果で、手の 82% が同時押しのため単独の操作の 58% が未試行だった
            if (a.Group == ActionGroup.Chord && ns.Of(a.Name).Tries == 0 && !stagnant && !SinglesTried(ns)) continue;
            var st = ns.Of(a.Name);
            var rev = Rev(a.Name);
            string why = ""; bool deferred = false;
            if (Deferred(a.Name) && st.Left == 0)
            {
                if (!othersTried && !stagnant) continue;   // 可逆性の安全規則: 戻れなかったことの多い操作は後回し
                why = othersTried ? "戻れなかったことが多い操作だが、この画面のほかの操作は一通り試した" : "同じ画面で進展がない";
                deferred = true;
            }
            // 事前の重みは置かない（観測がなければどの操作も 1）。戻れた操作ほど少し優先する（0.5〜1.5 倍）
            double b = a.IsWait ? keyB : 0.5 + rev.Rate;
            // 未試行かどうかは直接の経験だけで決める（推定では「試した」ことにしない）
            double novelty = st.Tries == 0 ? 2.0 : 1.0 / (1 + st.Tries);
            // 好奇心: 新しい画面に行けた・別の画面へ移れた・画面が変わった操作を好む（効果は観測から学ぶ）。
            // 割合は直接の経験に、弱い事前の推定（似た画面・分割前のノード）を仮の観測として足したもの。事前の推定がなければ直接の経験だけ（v0.9.0 と同じ）
            var (bc, bl, bd, bn, used) = Blended(node, a.Name);
            // 入力なしでも起きる変化（同じ画面で待つ手の割合）は、その操作の効果として数えない（v0.15.0）。待つ手そのものは基準と同じなので、
            // 変化・移動の項は 0 になり、待つ手が「変化が多い」ことで有利にならない（Wait の固定の価値は置かない。未試行・新しい画面・不確かさは同じ規則）
            var (wc, wl) = NaturalRates(node);
            double rates = bn > 0 ? 1.5 * bd / bn + 0.5 * Math.Max(0, bl / bn - wl) + 1.0 * Math.Max(0, bc / bn - wc) : 0;
            double info = Math.Clamp(InformationValue?.Invoke(node, a) ?? 0, 0, 1);
            double unc = Math.Clamp(UncertaintyValue?.Invoke(node, a) ?? 0, 0, 1);
            double sum = novelty + rates + InformationWeight * info + UncertaintyWeight * unc;
            double dest = a.IsWait || st.Tries == 0 ? 1 : Math.Clamp(DestinationValue?.Invoke(node, a) ?? 1, 0.05, 4);
            sum *= dest;
            if (stagnant) sum *= 3;
            double s = b * (0.5 + sum);
            if (why.Length == 0) why = st.Tries == 0 ? "この画面でまだ試していない" : $"この画面で {st.Tries} 回試した（別の画面へ {st.Left} 回・戻れた割合 {rev.Rate:P0}）";
            if (used.Count > 0) why += $"／ほかの画面の経験を弱い推定として使用（{string.Join(", ", used)}）";
            if (info > 0) why += $"／まだ分かっていない反応の関係 {info:F2}";
            if (unc > 0) why += $"／次の画面の予測の不確かさ {unc:F2}";
            if (wc > 0 && !a.IsWait) why += $"／入力なしでも変わる割合 {wc:P0} を差し引き";
            if (Math.Abs(dest - 1) > 0.01) why += $"／行き先の地域の進みの比 {dest:F2}";
            scored.Add((a, s, why, new CandidateScore(a.Name, s, 0, st.Tries, novelty, rates, info, unc, b, stagnant, deferred, used) { Natural = wc, Destination = dest }));
        }
        string rule = "scored";
        if (scored.Count == 0)
        {
            var fb = _singles[_rng.Next(_singles.Count)]; rule = "fallback";
            scored.Add((fb, 1, "候補なし", new CandidateScore(fb.Name, 1, 1, ns.Of(fb.Name).Tries, 0, 0, 0, 0, 1, stagnant, false, Array.Empty<string>())));
        }
        double total = scored.Sum(x => x.S * x.S);
        var cands = scored.Select(x => x.C with { Probability = total > 0 ? x.S * x.S / total : 1.0 / scored.Count }).ToList();
        double r = _rng.NextDouble() * total; int pick = scored.Count - 1;
        for (int i = 0; i < scored.Count; i++) { r -= scored[i].S * scored[i].S; if (r <= 0) { pick = i; break; } }
        var x0 = scored[pick];
        return new ExploreChoice(x0.A, x0.Why, x0.S) { Priors = x0.C.Priors, Information = x0.C.Information, Rule = rule, Probability = cands[pick].Probability, Candidates = cands };
    }

    /// <summary>
    /// 入力なしの基準（その画面で待つ手のときの、画面が変わった割合・別の画面へ移った割合）。待つ手をまだ試していなければ 0（基準なし）。
    /// 平滑化は Jeffreys（+0.5/+1）。この画面で試行 0 ならほかの画面の待つ手の集計（なければ 0.5）
    /// </summary>
    public (double Change, double Leave) NaturalRates(string node)
    {
        if (!_nodes.TryGetValue(node, out var ns) || !ns.Actions.TryGetValue(ExploreAction.Wait.Name, out var w) || w.Tries == 0)
        {
            // v0.16.0（実機 v0.15.1: 待つ手が 262 手中 1 手）: この画面で待っていなければ、ほかの画面で待ったときの割合を弱い推定として使う（出どころ: borrowed）。
            // どこでも待っていなければ Jeffreys の無情報の値 0.5。基準が分からないうちは、キーの手の「変化」をその操作の効果として満額には数えない
            int n = 0, c = 0, l = 0;
            foreach (var x in _nodes.Values) if (x.Actions.TryGetValue(ExploreAction.Wait.Name, out var y)) { n += y.Tries; c += y.Changes; l += y.Left; }
            return ((c + 0.5) / (n + 1.0), (l + 0.5) / (n + 1.0));
        }
        return ((w.Changes + 0.5) / (w.Tries + 1.0), (w.Left + 0.5) / (w.Tries + 1.0));
    }

    /// <summary>その画面でのある操作の直接の集計（無ければ null）</summary>
    public ActionStats? StatsOf(string node, string action) =>
        _nodes.TryGetValue(node, out var ns) && ns.Actions.TryGetValue(action, out var x) ? x : null;

    /// <summary>
    /// 直前の文脈（同じ操作を続ける・戻せたかの追跡）を捨てる。自動と人間の操作を切り替えたとき、古い画面を前提に続けないため（v0.15.0）
    /// </summary>
    public void ResetContext()
    {
        _last = null; _lastChanged = false; _lastNode = null; _prevNode = null; _stayNoProgress = 0; _pendingReturns.Clear();
        NoChangeStreak = 0; _actionsInStreak.Clear();
    }

    /// <summary>操作の結果を記録する。changed は画面が変わったか（時間切れ＝動き続けた画面も「変わった」とする）</summary>
    /// <summary>v0.25.4: 操作の後に環境が消えた（先のない状態へ）。試した回数と行き先だけを数え、変化・移動・新しい画面としては数えない（好奇心の項を上げない）</summary>
    public void ObserveTerminal(string from, string action)
    {
        var st = Node(from).Of(action);
        st.Tries++;
        st.To[EnvironmentLossBook.Terminal] = st.To.GetValueOrDefault(EnvironmentLossBook.Terminal) + 1;
    }

    public void Observe(string from, ExploreAction a, string to, bool changed)
    {
        if (a.Group == ActionGroup.Chord && Find(a.Name) == null && changed) Promote(a);   // 画面を変えた同時押しは候補に加える
        var st = Node(from).Of(a.Name);
        Node(from).Visits++;
        bool isNew = !_nodes.ContainsKey(to);
        Node(to);
        st.Tries++; if (changed) st.Changes++;
        if (to != from) st.Left++;
        if (isNew) { st.Discoveries++; Discoveries++; }
        st.To[to] = st.To.TryGetValue(to, out var c) ? c + 1 : 1;
        // 入力が届いているかの手がかりは、キーを送った手だけ（待つ手の変化は入力の効果ではない）
        if (!a.IsWait)
        {
            if (changed) { NoChangeStreak = 0; _actionsInStreak.Clear(); }
            else { NoChangeStreak++; _actionsInStreak.Add(a.Name); }
        }
        _stayNoProgress = to == from && !isNew ? _stayNoProgress + 1 : 0;

        // 可逆性: 直前に別の画面から来たところで押して、その画面へ戻ったら「戻す操作」
        if (!a.IsWait && to != from && _prevNode != null && _lastNode == from && _prevNode != from && to == _prevNode) Rev(a.Name).UndoHits++;
        for (int i = _pendingReturns.Count - 1; i >= 0; i--)
        {
            var p = _pendingReturns[i];
            if (to == p.Origin) { Rev(p.Action).Returned++; _pendingReturns.RemoveAt(i); }
            else if (p.Age + 1 >= ReturnWindow) { Rev(p.Action).NotReturned++; _pendingReturns.RemoveAt(i); }
            else _pendingReturns[i] = (p.Action, p.Origin, p.Age + 1);
        }
        if (to != from && !a.IsWait) _pendingReturns.Add((a.Name, from, 0));   // 待つ手は「戻す操作」の候補にしない

        _prevNode = from;
        _last = a; _lastChanged = changed; _lastNode = to;
    }

    public IEnumerable<(string From, string Action, string To, int N)> Transitions() =>
        _nodes.SelectMany(n => n.Value.Actions.Where(a => a.Value.Tries > 0).SelectMany(a => a.Value.To.Select(t => (n.Key, a.Key, t.Key, t.Value))));

    /// <summary>
    /// Profile の論理キーから探索の操作を作る。キーは全部使う（どれが有用かは観測で学ぶ）。
    /// 名前は中立（dpad_*・button*）で、方向パッドかボタンかという身体の構造だけを区別する。2キー同時押しは探索の中で見つける。
    /// </summary>
    public static List<ExploreAction> FromLogicalKeys(IEnumerable<string> logicalKeys) =>
        logicalKeys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new ExploreAction(k, k.StartsWith("dpad_", StringComparison.Ordinal) ? ActionGroup.Dpad : ActionGroup.Button, new[] { k })).ToList();
}
