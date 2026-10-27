using GameKouryakuKun.Core.Exploration;

namespace GameKouryakuKun.Core.Goals;

/// <summary>
/// 自己生成の一時的な目的（v0.16.0。設計書 v2.21・src/goals-design.md の安全な部分）。
/// 目的は経験（内部モデルの関係・まだ試していない操作）からだけ作る。ゲームの意味（勝つ・進む・HP など）は持たない。
/// 種類は 2 つだけ:
///   reach  … 「まだ試していない操作が残る、知っている画面へ、知っている関係をたどって行く」
///   retest … 「今の画面で、確かさが揺らいでいる関係（作り直した直後など）を、もう一度確かめる」
/// 価値は「その目的を追った結果、内部モデルの不確かさがどれだけ減ったか」（学習の進み）の移動平均。
/// 「数が多いほど良い」量（画面の数・変化の数・手数）は価値に使わない。成長の計器（表示用）も使わない。
/// 悪用の防止:
///   ・ノイズ追い: 道筋の関係は「入力なしでも同じように起きる」もの（操作の関与が小さい）と、確率の低いものを使わない。
///     確かめても不確かさが減らない関係は、同じ相手への目的の休みが倍々に伸びる（予測できないものを追い続けない）
///   ・知っている所に留まる: 学習の進みのない相手への目的は休む。目的の手は直近の手の一定の割合までに制限する
///   ・待つ手の連打: 待つ手は道筋にも確かめる相手にも入れない
/// v0.20.0（src/planning-design.md）:
///   ・計画の届く手数（Planning Horizon）を固定せず、道筋の予測の確かさ（各手のいちばんありそうな結果の確率の積）と、手数ごとの過去の達成の実績で伸び縮みさせる
///   ・長い道筋は途中の画面を小目的（Subgoal）として数え、外れたら今の画面から引き直す（次の小目的か最終の画面へ）
///   ・目的の達成に実際に繰り返し使った手の区間（2〜3 手）から、並びの候補（スキルの芽）を作る（基準は従来どおり 2 回以上・半分を超える）
///   ・目的の後に「未知の操作・制御の確かさ・使える並び・先の選択肢」がどう変わったかを記録し、目的の種類×手数ごとに集計する（今は記録と集計だけで選択には使わない。数が多いほど良い量を価値にしないため）
/// 計画は「知っている関係を幅優先でたどる最短の道筋」だけ（評価関数・探索木は作らない）。v0.19.0 から、選択の候補に入っている操作の並び（スキル）も 1 本の道として使う（目的とスキルの組み合わせ。並びの中の手数は道筋の長さに数えない）。
/// </summary>
public enum GoalKind { Reach, Retest }
public enum GoalStatus { Active, Achieved, Failed, Abandoned }

public sealed class SelfGoal
{
    public long Id { get; init; }
    public GoalKind Kind { get; init; }
    public string Target { get; init; } = "";
    public string? TargetAction { get; init; }
    /// <summary>出どころ: 目的を作った根拠の経験（直接の観測から作った関係）</summary>
    public string Origin { get; init; } = "";
    public List<(string Node, string Action, string Next)> Plan { get; init; } = new();
    public int Index { get; set; }
    public int Steps { get; set; }
    public int Budget { get; init; }
    public int Replans { get; set; }
    public GoalStatus Status { get; set; } = GoalStatus.Active;
    public string? EndReason { get; set; }
    public double UncBefore { get; init; }
    public double? Progress { get; set; }
    public long CreatedStep { get; init; }
    /// <summary>作ったときの道筋の手数と、その時点の Horizon（v0.20.0）</summary>
    public int PlannedLength { get; init; }
    public int HorizonAtStart { get; init; }
    /// <summary>道筋の予測の確かさ（各手のいちばんありそうな結果の確率の積）</summary>
    public double PlanConfidence { get; init; }
    /// <summary>小目的（途中の画面）。到達した数</summary>
    public List<string> Subgoals { get; init; } = new();
    public int SubgoalsReached { get; set; }
    /// <summary>v0.24.0: この目的に帰属した手（目的の手と、終わった後の ReviewDelay 手）の学習の量の合計と手数</summary>
    public double GainSum { get; set; }
    public int GainSteps { get; set; }
    /// <summary>実際にたどった手（目的の手だけ。並びは 1 手として入る）</summary>
    public List<(string From, string Action, string To)> Trace { get; } = new();
    public GoalSnapshot? Before { get; init; }
    public string Text => Kind == GoalKind.Reach ? $"画面 {Target} へ行く（未試行の操作・揺らいでいる結果が残る）" : $"画面 {Target} で「{TargetAction}」の関係を確かめ直す";
}

public sealed record GoalEvent(long GoalId, string Kind, string Target, string? Action, string Status, string? Reason, int Steps, double? Progress, double ValueAfter, string Origin, string Plan,
    int Horizon = 0, int PlannedLength = 0, int Replans = 0, int Subgoals = 0, int SubgoalsReached = 0, string? Outcome = null);

/// <summary>目的の前後で測る量（v0.20.0。内部モデルの状態だけ）: 未試行の操作の数・相手の画面の制御の確かさ・選択の候補の並びの数・相手から Horizon 内で行ける画面の数</summary>
public sealed record GoalSnapshot(int Unknown, double Control, int Skills, int Options);

public sealed class SelfGoals
{
    public int MaxPlanLength { get; init; } = 6;            // 根拠: 汎用（短い道筋。外れの重なりで計画が無意味にならない長さ）
    public double MinEdgeP { get; init; } = 0.5;            // 根拠: 汎用（いちばんありそうな結果が半分以上＝道筋として使える）
    public double MinActionCaused { get; init; } = 0.3;     // 根拠: 汎用（入力なしの変化と見分けのつく関与）
    public double MaxGoalShare { get; init; } = 0.3;        // 根拠: 汎用（探索を主にし、目的は従。直近の手の 3 割まで）
    public int ShareWindow { get; init; } = 100;            // 根拠: 汎用（数分の手）
    public double MinProgress { get; init; } = 0.02;        // 根拠: 汎用（不確かさの平均の差として、偶然の揺れより大きい最小の量）
    public int ReviewDelay { get; init; } = 3;              // 根拠: 汎用（達成の後に数手、その画面の手を試す余地）
    public double ValueRate { get; init; } = 0.2;           // 根拠: 汎用（直近の数回を重く見る移動平均）
    public int BaseCooldown { get; init; } = 20;            // 根拠: 汎用（短い休み。倍々に伸ばす）
    public double MinRetestUncertainty { get; init; } = 0.5;
    public int SkillMinAchieved { get; init; } = 2;          // 根拠: 汎用（1 回の偶然を除く最小の繰り返し）
    // ---- v0.20.0: Planning Horizon・小目的・引き直し・区間の再利用・目的の結果の記録 ----
    public int HorizonMin { get; init; } = 1;                // 根拠: 汎用（1 手先）
    public int HorizonInitial { get; init; } = 2;            // 根拠: 汎用（確かめ直し＝1 手の一つ先から始め、実績で伸ばす。固定の長さではない）
    public int HorizonTrials { get; init; } = 3;             // 根拠: 汎用（伸び縮みを決める最小の試行数）
    public double HorizonExpandRate { get; init; } = 0.6;    // 根拠: 汎用（今の手数の道筋を過半より十分多く達成できる）
    public double HorizonShrinkRate { get; init; } = 0.3;    // 根拠: 汎用（今の手数の道筋がほとんど達成できない）
    public double MinPlanConfidence { get; init; } = 0.25;   // 根拠: 汎用（道筋全体が 4 回に 1 回も通らないと予測するものは計画にしない）
    public int SubgoalSpan { get; init; } = 2;               // 根拠: 汎用（2 手ごとに途中の画面を確かめる）
    public int SegmentMin { get; init; } = 2;   // 根拠: 並びの候補の長さと同じ（ActionSequenceMiner）
    public int SegmentMax { get; init; } = 3;   // 根拠: 同上
    private int? _horizon;
    public int Horizon => _horizon ?? HorizonInitial;
    public int HorizonChanges { get; private set; }
    public int ReplanCount { get; private set; }
    public int ReplanAchieved { get; private set; }
    public int ReplanGoals { get; private set; }
    public int SubgoalsTotal { get; private set; }
    public int SubgoalsReachedTotal { get; private set; }
    public int MaxAchievedLength { get; private set; }
    private readonly Dictionary<int, (int N, int A)> _byLen = new();
    private readonly Dictionary<string, (string Start, string End, List<string> Steps, int Attempts, int Achieved, bool Proposed)> _segments = new(StringComparer.Ordinal);
    /// <summary>目的の種類×手数の帯ごとの、結果の量の変化の平均（記録と集計だけ。選択には使わない）</summary>
    private readonly Dictionary<string, (int N, double DUnknown, double DControl, double DSkills, double DOptions)> _outcome = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, (int N, double DUnknown, double DControl, double DSkills, double DOptions)> OutcomeModel => _outcome;
    public IReadOnlyDictionary<int, (int N, int A)> LengthStats => _byLen;
    public List<(long Step, int From, int To, string Reason)> HorizonEvents { get; } = new();

    /// <summary>同じ道筋で SkillMinAchieved 回以上達成した到達の目的（並びの候補にする材料。呼び出し側が取り出す）</summary>
    public List<(string Start, string End, IReadOnlyList<string> Steps, int Achieved, int Attempts)> SkillCandidates { get; } = new();// 根拠: 汎用（不確かさが半分を超える＝結果が揺らいでいる）

    private readonly Dictionary<GoalKind, double> _value = new() { [GoalKind.Reach] = 0.5, [GoalKind.Retest] = 0.5 };   // 根拠: 汎用（無情報の事前値）
    // ---- v0.24.0 ブロック D: 目的の結果から、どの種類の目的を選ぶ値打ちがあるかを学ぶ（src/goal-value-design.md） ----
    // 単位は「1 手あたりの学習の量」＝その手で試した関係の予測の不確かさの減り（内部モデルの量。表示の計器・外の報酬ではない）。
    // 目的の種類×手数の帯ごとに、目的に帰属した手の 1 手あたりの量を集め、目的を持たずに探索した手の 1 手あたりの量（機会費用）と比べる。
    // 「大きいほどよい」ではなく、同じ単位の 2 つの率の比で、目的に手を割く値打ちがあるかを決める。
    public double PriorStrength { get; init; } = 2;       // 根拠: 汎用（2 回分の重みの事前値。最初の数回で大きく振れない）
    public double PriorRatio { get; init; } = 0.5;        // 根拠: 汎用（無情報の事前値。従来の価値の初期値と同じ）
    public double MaxRatio { get; init; } = 4;            // 根拠: 汎用（候補の重みの上限。1 つの種類だけに偏らない）
    public double ValueFloor { get; init; } = 0.05;       // 根拠: 汎用（二度と試さないことを避ける下限）
    private readonly Dictionary<string, (int N, double Mean)> _gain = new(StringComparer.Ordinal);
    private (int N, double Mean) _baseline;
    private SelfGoal? _attrib; private int _attribLeft;
    public IReadOnlyDictionary<string, (int N, double Mean)> GainModel => _gain;
    public (int N, double Mean) Baseline => _baseline;
    private static string GainKey(GoalKind k, int len) => $"{(k == GoalKind.Reach ? "reach" : "retest")}|{Band(Math.Max(1, len))}";

    /// <summary>目的の種類×帯の値打ち: 目的に帰属した手の 1 手あたりの学習の量 ÷ 探索の手の 1 手あたりの学習の量（事前値で縮める）</summary>
    public double Ratio(GoalKind k, int len)
    {
        double b = _baseline.N == 0 ? 0 : _baseline.Mean;
        var g = _gain.GetValueOrDefault(GainKey(k, len));
        double r = b <= 1e-9 ? (g.N == 0 ? PriorRatio : (g.Mean > 0 ? MaxRatio : PriorRatio)) : g.Mean / b;
        double est = (g.N * r + PriorStrength * PriorRatio) / (g.N + PriorStrength);
        return Math.Clamp(est, ValueFloor, MaxRatio);
    }

    /// <summary>1 手ごとの学習の量（呼び出し側が手の前後の不確かさの差を渡す）。今の目的か、終わったばかりの目的に帰属させる。どれでもなければ探索の基準に入れる</summary>
    public void StepGain(double gain, bool goalStep)
    {
        gain = Math.Max(0, gain);
        var g = goalStep ? Active ?? _attrib : (_attribLeft > 0 ? _attrib : null);
        if (g != null) { g.GainSum += gain; g.GainSteps++; if (!goalStep && _attribLeft > 0) _attribLeft--; return; }
        int n = _baseline.N + 1; _baseline = (n, _baseline.Mean + (gain - _baseline.Mean) / Math.Min(n, 1 / ValueRate * 5));
    }
    private readonly Dictionary<string, (long Until, int Cool)> _rest = new(StringComparer.Ordinal);
    private readonly Queue<bool> _recent = new();
    private readonly List<(SelfGoal G, long At)> _review = new();
    private long _nextId = 1;
    public SelfGoal? Active { get; private set; }
    public List<GoalEvent> Events { get; } = new();
    public int Created { get; private set; }
    public int Achieved { get; private set; }
    public int Failed { get; private set; }
    public int Abandoned { get; private set; }
    /// <summary>v0.24.0 実機: 探索の基準は毎手変わるので、値打ちは使うたびに数え直す（保存した値と読み込んだ値がずれない）</summary>
    public double Value(GoalKind k) => _gain.Count == 0 && _baseline.N == 0 ? _value[k] : KindRatio(k);
    /// <summary>種類全体の値打ち（帯ごとの比を、試した回数で重みづけた平均。表示と全体の選ぶ確率に使う）</summary>
    private double KindRatio(GoalKind k)
    {
        var xs = _gain.Where(kv => kv.Key.StartsWith(k == GoalKind.Reach ? "reach|" : "retest|", StringComparison.Ordinal)).ToList();
        if (xs.Count == 0) return Ratio(k, 1);
        int n = xs.Sum(x => x.Value.N);
        return Math.Clamp(xs.Sum(x => x.Value.N * Ratio(k, x.Key.EndsWith("|1") ? 1 : x.Key.EndsWith("|2") ? 2 : x.Key.EndsWith("|3-4") ? 3 : 5)) / Math.Max(1, n), ValueFloor, MaxRatio);
    }
    /// <summary>v0.25.0 ブロック E: 地域の行き直しの値打ち（FrontierRegions.RegionValue）。なければ使わない</summary>
    public Func<string, double>? RegionValue { get; set; }
    private double RegionFactor(string cur, string target) => RegionValue == null ? 1 : Math.Clamp(RegionValue(target) / Math.Max(RegionValue(cur), ValueFloor), ValueFloor, MaxRatio);
    public double Share => _recent.Count == 0 ? 0 : _recent.Count(x => x) / (double)_recent.Count;

    /// <summary>相手の不確かさ: その画面の操作ごとの不確かさの平均（試していない操作は 1）</summary>
    public static double NodeUncertainty(Explorer ex, TransitionModel m, string node) =>
        ex.Actions.Where(a => !a.IsWait && a.Group != ActionGroup.Sequence).Select(a => (ex.StatsOf(node, a.Name)?.Tries ?? 0) == 0 ? 1.0 : m.Uncertainty(node, a.Name)).DefaultIfEmpty(1).Average();

    private static string Key(GoalKind k, string t, string? a) => $"{k}|{t}|{a}";

    /// <summary>v0.29.6（Goal/Planning）: 局所の出来事の予測の不確かさ（画面, 手）。あれば手の選び方と同じく 1 − (1 − 画面の不確かさ)(1 − 局所) で合わせる。なければ従来どおり</summary>
    public Func<string, string, double?>? LocalUncertainty { get; set; }
    private double U(TransitionModel m, string node, string action)
    {
        double u = m.Uncertainty(node, action);
        if (LocalUncertainty?.Invoke(node, action) is not double l) return u;
        return 1 - (1 - Math.Clamp(u, 0, 1)) * (1 - Math.Clamp(l, 0, 1));
    }
    private double NodeUnc(Explorer ex, TransitionModel m, string node) =>
        ex.Actions.Where(a => !a.IsWait && a.Group != ActionGroup.Sequence).Select(a => (ex.StatsOf(node, a.Name)?.Tries ?? 0) == 0 ? 1.0 : U(m, node, a.Name)).DefaultIfEmpty(1).Average();

    /// <summary>道筋に使える関係（待つ手・並びは使わない。入力なしでも起きるもの・確率の低いものは使わない）</summary>
    /// <summary>v0.25.2: 地域の計算に使う、道筋に使える関係（自分の操作で確かに動ける関係）の全体</summary>
    public IEnumerable<(string From, string To)> PlanEdges(Explorer ex, TransitionModel m) =>
        ex.Nodes.Keys.ToList().SelectMany(n => Edges(ex, m, n).Select(e => (n, e.Next)));

    private IEnumerable<(string Action, string Next)> Edges(Explorer ex, TransitionModel m, string node)
    {
        foreach (var a in ex.Actions)
        {
            if (a.IsWait || a.Group is ActionGroup.Chord) continue;
            // 簡単な計画（v0.19.0）: 選択の候補に入っている操作の並び（スキル）も、その始まりの画面からの 1 本の道として使う（結果は並びとして直接観測した関係）
            if (a.Group == ActionGroup.Sequence && !ex.CanUse(a, node)) continue;
            if ((ex.StatsOf(node, a.Name)?.Tries ?? 0) == 0) continue;
            var p = m.Predict(node, a.Name);
            if (p.TopNext == null || p.TopNext == node || p.TopNext == EnvironmentLossBook.Terminal || p.PTop < MinEdgeP) continue;
            if (a.Group != ActionGroup.Sequence)
            {
                var at = NaturalChange.Attribute(ex, node, a.Name);
                if (!double.IsNaN(at.PActionCaused) && at.PActionCaused < MinActionCaused) continue;
            }
            yield return (a.Name, p.TopNext);
        }
    }

    public List<(string Node, string Action, string Next)>? PlanPath(Explorer ex, TransitionModel m, string from, string to, int? maxLen = null)
    {
        int lim = maxLen ?? MaxPlanLength;
        var prev = new Dictionary<string, (string Node, string Action)>(StringComparer.Ordinal) { [from] = ("", "") };
        var q = new Queue<(string N, int D)>(); q.Enqueue((from, 0));
        while (q.Count > 0)
        {
            var (n, d) = q.Dequeue();
            if (n == to && d > 0) break;
            if (d >= lim) continue;
            foreach (var (a, nx) in Edges(ex, m, n))
                if (!prev.ContainsKey(nx)) { prev[nx] = (n, a); q.Enqueue((nx, d + 1)); }
        }
        if (!prev.ContainsKey(to) || to == from) return null;
        var path = new List<(string, string, string)>();
        for (var c = to; c != from;) { var (p, a) = prev[c]; path.Add((p, a, c)); c = p; }
        path.Reverse(); return path;
    }

    /// <summary>診断用（v0.22.0）: 今の画面からの到達の目的の候補が、どこで落ちるか（手数の上限 h で）。選択には使わない</summary>
    public IEnumerable<(string Target, int Len, double Conf, string Why)> AuditReach(Explorer ex, TransitionModel m, string cur, int h)
    {
        foreach (var (node, st) in ex.Nodes)
        {
            if (node == cur) continue;
            int untried = ex.Actions.Count(a => !a.IsWait && a.Group is not (ActionGroup.Sequence or ActionGroup.Chord) && (st.Actions.GetValueOrDefault(a.Name)?.Tries ?? 0) == 0);
            double unc = NodeUnc(ex, m, node);
            if (untried == 0 && unc < MinRetestUncertainty) { yield return (node, 0, 0, "not_target"); continue; }
            var plan = PlanPath(ex, m, cur, node, h);
            if (plan == null) { yield return (node, 0, 0, "no_path"); continue; }
            double conf = Confidence(m, plan);
            yield return (node, plan.Count, conf, conf < MinPlanConfidence ? "low_conf" : "ok");
        }
    }

    /// <summary>道筋の予測の確かさ（各手のいちばんありそうな結果の確率の積）</summary>
    public static double Confidence(TransitionModel m, IEnumerable<(string Node, string Action, string Next)> plan) =>
        plan.Aggregate(1.0, (acc, p) => acc * m.Predict(p.Node, p.Action).PTop);

    /// <summary>目的の前後で測る量（内部モデルの状態だけ）</summary>
    public GoalSnapshot Snapshot(Explorer ex, TransitionModel m, string target)
    {
        int unknown = ex.Nodes.Sum(kv => ex.Actions.Count(a => !a.IsWait && a.Group is not (ActionGroup.Sequence or ActionGroup.Chord) && (kv.Value.Actions.GetValueOrDefault(a.Name)?.Tries ?? 0) == 0));
        var tried = ex.Actions.Where(a => !a.IsWait && a.Group != ActionGroup.Sequence && (ex.StatsOf(target, a.Name)?.Tries ?? 0) > 0).ToList();
        double control = tried.Count == 0 ? 0 : tried.Average(a => m.Predict(target, a.Name).PTop);
        int skills = ex.Actions.Count(a => a.Group == ActionGroup.Sequence);
        // 相手から Horizon 内で行ける画面の数（先の選択肢）
        var seen = new HashSet<string>(StringComparer.Ordinal) { target }; var q = new Queue<(string N, int D)>(); q.Enqueue((target, 0));
        while (q.Count > 0) { var (n, d) = q.Dequeue(); if (d >= Horizon) continue; foreach (var (_, nx) in Edges(ex, m, n)) if (seen.Add(nx)) q.Enqueue((nx, d + 1)); }
        return new GoalSnapshot(unknown, control, skills, seen.Count - 1);
    }

    private static string Band(int len) => len <= 1 ? "1" : len == 2 ? "2" : len <= 4 ? "3-4" : "5+";

    private bool Resting(string key, long step) => _rest.TryGetValue(key, out var r) && step < r.Until;

    /// <summary>1手ごとに呼ぶ（目的の手かどうかを数える）</summary>
    public void Tick(bool goalStep)
    {
        _recent.Enqueue(goalStep); while (_recent.Count > ShareWindow) _recent.Dequeue();
    }

    /// <summary>目的がないとき、経験から候補を作り、価値で 1 つ選ぶ。選ばないこともある（目的は探索の従）</summary>
    public SelfGoal? Propose(Explorer ex, TransitionModel m, string cur, long step, Random rng)
    {
        if (Active != null || Share >= MaxGoalShare) return null;
        var cands = new List<(SelfGoal G, double W)>();
        // retest: 今の画面の、確かさが揺らいでいる関係
        foreach (var a in ex.Actions)
        {
            if (a.IsWait || a.Group == ActionGroup.Sequence) continue;
            var s = ex.StatsOf(cur, a.Name); if (s == null || s.Tries < 2) continue;
            double u = U(m, cur, a.Name); if (u < MinRetestUncertainty) continue;
            if (m.Predict(cur, a.Name).Top.Any(t => t.Next == EnvironmentLossBook.Terminal)) continue;   // v0.25.4: 環境が消えたことのある関係は確かめ直しの目的にしない（確かめると先がない）
            var at = NaturalChange.Attribute(ex, cur, a.Name);
            if (!double.IsNaN(at.PActionCaused) && at.PActionCaused < MinActionCaused) continue;
            var k = Key(GoalKind.Retest, cur, a.Name); if (Resting(k, step)) continue;
            cands.Add((new SelfGoal { Kind = GoalKind.Retest, Target = cur, TargetAction = a.Name, Budget = 1, UncBefore = u,
                Origin = $"関係 ({cur}, {a.Name}) の不確かさ {u:0.00}（直接の観測 {s.Tries} 回）", CreatedStep = step,
                Plan = new() { (cur, a.Name, "?") } }, Ratio(GoalKind.Retest, 1) * u));
        }
        // reach: 未試行の操作が残る、知っている画面
        foreach (var (node, st) in ex.Nodes)
        {
            if (node == cur) continue;
            int untried = ex.Actions.Count(a => !a.IsWait && a.Group is not (ActionGroup.Sequence or ActionGroup.Chord) && (st.Actions.GetValueOrDefault(a.Name)?.Tries ?? 0) == 0);
            // 未試行の操作が残る画面に加えて、操作の結果がまだ揺らいでいる画面も行き先にする（v0.19.0。実機 v0.18.0 で未試行の画面がほぼ無く、到達の目的が 55 件中 2 件だった）
            double unc = NodeUnc(ex, m, node);
            if (untried == 0 && unc < MinRetestUncertainty) continue;
            var k = Key(GoalKind.Reach, node, null); if (Resting(k, step)) continue;
            var plan = PlanPath(ex, m, cur, node, Math.Min(Horizon, MaxPlanLength)); if (plan == null) continue;
            double conf = Confidence(m, plan); if (conf < MinPlanConfidence) continue;   // 予測の確かさが足りない道筋は計画にしない（World Model の成熟度）
            var subs = new List<string>(); for (int i = SubgoalSpan - 1; i < plan.Count - 1; i += SubgoalSpan) subs.Add(plan[i].Next);
            cands.Add((new SelfGoal { Kind = GoalKind.Reach, Target = node, Budget = plan.Count + 2 + plan.Count / 2, UncBefore = unc, Plan = plan, CreatedStep = step,
                PlannedLength = plan.Count, HorizonAtStart = Horizon, PlanConfidence = conf, Subgoals = subs,
                Origin = untried > 0 ? $"画面 {node} に未試行の操作 {untried} 個（道筋 {plan.Count} 手は直接の観測の関係）" : $"画面 {node} の操作の結果の不確かさ {unc:0.00}（道筋 {plan.Count} 手は直接の観測の関係）" }, Ratio(GoalKind.Reach, plan.Count) * unc * conf * RegionFactor(cur, node)));   // v0.25.0: 行き先の地域の進みを今の地域の進みと比べる（進みの減った閉じた地域から、進みのある地域へ）   // v0.20.0: 手数で割らず、道筋が通る見込みで重みづけ（遠い目的も、通る見込みが高ければ選ばれる）
        }
        if (cands.Count == 0) return null;
        // 価値に比例して選ぶ。選ぶ確率の全体も価値の平均（低ければ探索に任せる）
        double total = cands.Sum(c => c.W); if (total <= 0) return null;
        // 目的を持つかどうか: 候補の種類の値打ち（探索と比べた比）の平均。1 以上なら探索より学べる見込み（目的の手の割合の上限は別にかかる）
        if (rng.NextDouble() > Math.Min(1, (Value(GoalKind.Reach) + Value(GoalKind.Retest)) / 2)) return null;
        double r = rng.NextDouble() * total; SelfGoal pick = cands[^1].G;
        foreach (var c in cands) { r -= c.W; if (r <= 0) { pick = c.G; break; } }
        pick = new SelfGoal { Id = _nextId++, Kind = pick.Kind, Target = pick.Target, TargetAction = pick.TargetAction, Origin = pick.Origin, Plan = pick.Plan, Budget = pick.Budget, UncBefore = pick.UncBefore, CreatedStep = step,
            PlannedLength = pick.Plan.Count, HorizonAtStart = Horizon, PlanConfidence = pick.PlanConfidence, Subgoals = pick.Subgoals, Before = Snapshot(ex, m, pick.Target) };
        Active = pick; Created++;
        return pick;
    }

    /// <summary>目的の次の手（今の画面が道筋から外れていれば 1 回だけ引き直し、だめなら放棄）</summary>
    public string? NextAction(Explorer ex, TransitionModel m, string cur, long step)
    {
        var g = Active; if (g == null) return null;
        if (g.Steps >= g.Budget) { End(GoalStatus.Failed, "手数の上限", step); return null; }
        if (g.Kind == GoalKind.Reach && cur == g.Target) { End(GoalStatus.Achieved, "到達", step); return null; }
        if (g.Index < g.Plan.Count && g.Plan[g.Index].Node == cur) return g.Plan[g.Index].Action;
        // 予測外れのとき、今の画面から引き直す（v0.20.0: 小目的の数に応じて数回まで。最終の画面へ、届かなければ次の小目的へ）
        if (g.Kind == GoalKind.Reach && g.Replans < 1 + g.Subgoals.Count)
        {
            var p = PlanPath(ex, m, cur, g.Target, Math.Min(Horizon, MaxPlanLength));
            if (p == null)
            {
                var nextSub = g.Subgoals.Skip(g.SubgoalsReached).FirstOrDefault();
                if (nextSub != null && PlanPath(ex, m, cur, nextSub, Math.Min(Horizon, MaxPlanLength)) is { } ps && PlanPath(ex, m, nextSub, g.Target, MaxPlanLength) is { } rest) p = ps.Concat(rest).ToList();
            }
            if (p != null && g.Steps + p.Count <= g.Budget + 1) { g.Replans++; ReplanCount++; g.Plan.Clear(); g.Plan.AddRange(p); g.Index = 0; return p[0].Action; }
        }
        End(GoalStatus.Abandoned, "道筋から外れた", step); return null;
    }

    /// <summary>目的の手の結果</summary>
    public void Observed(string from, string action, string to, bool hit, long step)
    {
        var g = Active; if (g == null) return;
        g.Steps++; g.Index++;
        g.Trace.Add((from, action, to));
        if (g.SubgoalsReached < g.Subgoals.Count && to == g.Subgoals[g.SubgoalsReached]) g.SubgoalsReached++;
        if (g.Kind == GoalKind.Retest) { End(hit ? GoalStatus.Achieved : GoalStatus.Failed, hit ? "予測どおり" : "予測と違った", step); return; }
        if (to == g.Target) End(GoalStatus.Achieved, "到達", step);
    }

    /// <summary>人間の操作への切替など、外から目的をやめる</summary>
    public void Abandon(string reason, long step) { if (Active != null) End(GoalStatus.Abandoned, reason, step); }

    private void End(GoalStatus st, string reason, long step)
    {
        var g = Active!; g.Status = st; g.EndReason = reason; Active = null;
        if (g.Kind == GoalKind.Reach && reason != "自動と人間の切替")
        {
            // 目的の達成に実際に使った手の区間（並びを含まないもの）を数える。区間は実際にたどった手から作る（計画ではなく観測）
            var tr = g.Trace;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int len = SegmentMin; len <= SegmentMax; len++)
                for (int i = 0; i + len <= tr.Count; i++)
                {
                    var seg = tr.Skip(i).Take(len).ToList();
                    if (seg.Any(x => x.Action.StartsWith("ActionSequence", StringComparison.Ordinal))) continue;   // 並びの入れ子は作らない
                    bool chain = true; for (int j = 1; j < seg.Count; j++) if (seg[j].From != seg[j - 1].To) chain = false;
                    if (!chain || seg[0].From == seg[^1].To) continue;
                    var steps = seg.Select(x => x.Action).ToList(); var k = seg[0].From + "|" + string.Join(">", steps);
                    if (!keys.Add(k)) continue;
                    var e = _segments.TryGetValue(k, out var x0) ? x0 : (Start: seg[0].From, End: seg[^1].To, Steps: steps, Attempts: 0, Achieved: 0, Proposed: false);
                    e.Attempts++; if (st == GoalStatus.Achieved && seg[^1].To == e.End) e.Achieved++;
                    if (!e.Proposed && e.Achieved >= SkillMinAchieved && e.Achieved * 2 > e.Attempts) { e.Proposed = true; SkillCandidates.Add((e.Start, e.End, e.Steps, e.Achieved, e.Attempts)); }
                    _segments[k] = e;
                }
            // 手数ごとの達成の実績と Horizon の伸び縮み
            if (g.PlannedLength > 0)
            {
                var ls = _byLen.GetValueOrDefault(g.PlannedLength); ls.N++; if (st == GoalStatus.Achieved) ls.A++; _byLen[g.PlannedLength] = ls;
                if (st == GoalStatus.Achieved) MaxAchievedLength = Math.Max(MaxAchievedLength, g.PlannedLength);
                AdaptHorizon(step);
            }
            if (g.Replans > 0) { ReplanGoals++; if (st == GoalStatus.Achieved) ReplanAchieved++; }
            SubgoalsTotal += g.Subgoals.Count; SubgoalsReachedTotal += g.SubgoalsReached;
        }
        switch (st) { case GoalStatus.Achieved: Achieved++; break; case GoalStatus.Failed: Failed++; break; default: Abandoned++; break; }
        _review.Add((g, step + ReviewDelay));
        _attrib = g; _attribLeft = ReviewDelay;   // 終わった後の数手（その画面で試す余地）の学習もこの目的に帰属させる
        Events.Add(Ev(g, double.NaN));
    }

    /// <summary>今の Horizon の手数の道筋の実績で伸び縮みさせる（予測の確かさの条件は計画のときに別にかかる）</summary>
    private void AdaptHorizon(long step)
    {
        var (n, a) = _byLen.GetValueOrDefault(Horizon);
        // 今の Horizon より短い道筋の実績も合わせて見る（その手数の目的が少ないうちは、届いている範囲の実績で判断する）
        int nAll = 0, aAll = 0; foreach (var (len, v) in _byLen) if (len <= Horizon && len >= Horizon - 1) { nAll += v.N; aAll += v.A; }
        if (n >= HorizonTrials && (a + 0.5) / (n + 1.0) < HorizonShrinkRate && Horizon > HorizonMin) { SetHorizon(Horizon - 1, step, $"{Horizon} 手の道筋の達成 {a}/{n}"); return; }
        if (nAll >= HorizonTrials && (aAll + 0.5) / (nAll + 1.0) >= HorizonExpandRate && Horizon < MaxPlanLength && MaxAchievedLength >= Horizon - 1)
            SetHorizon(Horizon + 1, step, $"{Horizon - 1}〜{Horizon} 手の道筋の達成 {aAll}/{nAll}");
    }

    private void SetHorizon(int h, long step, string reason)
    {
        HorizonEvents.Add((step, Horizon, h, reason)); _horizon = h; HorizonChanges++;
        // 伸び縮みの判断に使った実績は、新しい手数で数え直す（同じ実績で何度も伸ばさない）
        foreach (var k in _byLen.Keys.ToList()) if (k >= Math.Min(h, Horizon) - 1) _byLen[k] = (0, 0);
    }

    /// <summary>数手後に学習の進み（相手の不確かさの減り）を測り、価値と休みを更新する</summary>
    public IReadOnlyList<GoalEvent> Review(Explorer ex, TransitionModel m, long step)
    {
        var done = new List<GoalEvent>();
        for (int i = _review.Count - 1; i >= 0; i--)
        {
            var (g, at) = _review[i]; if (step < at) continue;
            _review.RemoveAt(i);
            double after = g.Kind == GoalKind.Retest ? U(m, g.Target, g.TargetAction!) : NodeUnc(ex, m, g.Target);
            double prog = Math.Clamp(g.UncBefore - after, 0, 1);
            if (g.Status == GoalStatus.Abandoned) prog = 0;   // 放棄は進みなし（価値は少し下がる）
            g.Progress = prog;
            // v0.24.0: 目的に帰属した手の 1 手あたりの学習の量を、種類×帯ごとに集める（放棄も、使った手の分だけそのまま数える）
            if (g.GainSteps > 0)
            {
                var gk = GainKey(g.Kind, g.PlannedLength); var gv = _gain.GetValueOrDefault(gk); int gn = gv.N + 1;
                double per = g.GainSum / g.GainSteps;
                _gain[gk] = (gn, gv.Mean + (per - gv.Mean) / Math.Min(gn, 1 / ValueRate));
            }
            if (ReferenceEquals(_attrib, g)) { _attrib = null; _attribLeft = 0; }
            _value[g.Kind] = KindRatio(g.Kind);
            var k = Key(g.Kind, g.Target, g.TargetAction);
            if (prog < MinProgress) { int c = _rest.TryGetValue(k, out var r) ? Math.Min(r.Cool * 2, 1280) : BaseCooldown; _rest[k] = (step + c, c); }
            else _rest.Remove(k);
            string? outcome = null;
            if (g.Before is { } b0)
            {
                var a1 = Snapshot(ex, m, g.Target);
                double du = b0.Unknown - a1.Unknown, dc = a1.Control - b0.Control, ds = a1.Skills - b0.Skills, dop = a1.Options - b0.Options;
                var key = $"{(g.Kind == GoalKind.Reach ? "reach" : "retest")}|{Band(Math.Max(1, g.PlannedLength))}";
                var o = _outcome.GetValueOrDefault(key); int n1 = o.N + 1;
                _outcome[key] = (n1, o.DUnknown + (du - o.DUnknown) / n1, o.DControl + (dc - o.DControl) / n1, o.DSkills + (ds - o.DSkills) / n1, o.DOptions + (dop - o.DOptions) / n1);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                outcome = string.Create(ci, $"{{\"unknown_reduced\":{du},\"control_gain\":{Math.Round(dc, 4)},\"skills_gain\":{ds},\"options_gain\":{dop},\"key\":\"{key}\"}}");
            }
            var e = Ev(g, _value[g.Kind]) with { Outcome = outcome }; Events.Add(e); done.Add(e);
        }
        return done;
    }

    private static GoalEvent Ev(SelfGoal g, double v) => new(g.Id, g.Kind == GoalKind.Reach ? "reach" : "retest", g.Target, g.TargetAction,
        g.Progress.HasValue ? "reviewed" : g.Status.ToString().ToLowerInvariant(), g.EndReason, g.Steps, g.Progress, v, g.Origin,
        string.Join(" → ", g.Plan.Select(p => $"{p.Node}:{p.Action}")), g.HorizonAtStart, g.PlannedLength, g.Replans, g.Subgoals.Count, g.SubgoalsReached);

    // ---- 起動をまたいで引き継ぐ状態（v0.20.0。価値・休み・Horizon・手数ごとの実績・区間の実績・結果の集計） ----
    public string ExportState()
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1); w.WriteNumber("horizon", Horizon); w.WriteNumber("max_achieved_length", MaxAchievedLength);
            w.WriteStartObject("value"); foreach (var (k, v) in _value) w.WriteNumber(k.ToString(), v); w.WriteEndObject();
            w.WriteStartObject("by_len"); foreach (var (k, v) in _byLen) { w.WriteStartArray(k.ToString(System.Globalization.CultureInfo.InvariantCulture)); w.WriteNumberValue(v.N); w.WriteNumberValue(v.A); w.WriteEndArray(); } w.WriteEndObject();
            w.WriteStartArray("segments");
            foreach (var (k, v) in _segments) { w.WriteStartObject(); w.WriteString("start", v.Start); w.WriteString("end", v.End); w.WriteStartArray("steps"); foreach (var x in v.Steps) w.WriteStringValue(x); w.WriteEndArray(); w.WriteNumber("attempts", v.Attempts); w.WriteNumber("achieved", v.Achieved); w.WriteBoolean("proposed", v.Proposed); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartObject("gain"); foreach (var (k, v) in _gain) { w.WriteStartArray(k); w.WriteNumberValue(v.N); w.WriteNumberValue(v.Mean); w.WriteEndArray(); } w.WriteEndObject();
            w.WriteStartArray("baseline"); w.WriteNumberValue(_baseline.N); w.WriteNumberValue(_baseline.Mean); w.WriteEndArray();
            w.WriteStartObject("outcome"); foreach (var (k, v) in _outcome) { w.WriteStartArray(k); w.WriteNumberValue(v.N); w.WriteNumberValue(v.DUnknown); w.WriteNumberValue(v.DControl); w.WriteNumberValue(v.DSkills); w.WriteNumberValue(v.DOptions); w.WriteEndArray(); } w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public void ImportState(string json)
    {
        using var d = System.Text.Json.JsonDocument.Parse(json); var r = d.RootElement;
        if (r.TryGetProperty("horizon", out var h)) _horizon = Math.Clamp(h.GetInt32(), HorizonMin, MaxPlanLength);
        if (r.TryGetProperty("max_achieved_length", out var mx)) MaxAchievedLength = mx.GetInt32();
        if (r.TryGetProperty("value", out var v)) foreach (var p in v.EnumerateObject()) if (Enum.TryParse<GoalKind>(p.Name, out var k)) _value[k] = Math.Clamp(p.Value.GetDouble(), 0.05, 1);
        if (r.TryGetProperty("by_len", out var bl)) foreach (var p in bl.EnumerateObject()) _byLen[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = (p.Value[0].GetInt32(), p.Value[1].GetInt32());
        if (r.TryGetProperty("segments", out var sg)) foreach (var e in sg.EnumerateArray())
        {
            var steps = e.GetProperty("steps").EnumerateArray().Select(x => x.GetString()!).ToList(); var start = e.GetProperty("start").GetString()!;
            _segments[start + "|" + string.Join(">", steps)] = (start, e.GetProperty("end").GetString()!, steps, e.GetProperty("attempts").GetInt32(), e.GetProperty("achieved").GetInt32(), e.GetProperty("proposed").GetBoolean());
        }
        if (r.TryGetProperty("gain", out var gn0)) foreach (var p in gn0.EnumerateObject()) _gain[p.Name] = (p.Value[0].GetInt32(), p.Value[1].GetDouble());
        if (r.TryGetProperty("baseline", out var bl0) && bl0.ValueKind == System.Text.Json.JsonValueKind.Array) _baseline = (bl0[0].GetInt32(), bl0[1].GetDouble());
        // v0.24.0: 古い価値（不確かさの減りの割合。到達は 0.05 に張り付いていた）は引き継がず、学習の量の比で数え直す
        if (r.TryGetProperty("gain", out _)) { _value[GoalKind.Reach] = KindRatio(GoalKind.Reach); _value[GoalKind.Retest] = KindRatio(GoalKind.Retest); }
        else { _value[GoalKind.Reach] = PriorRatio; _value[GoalKind.Retest] = PriorRatio; }
        if (r.TryGetProperty("outcome", out var oc)) foreach (var p in oc.EnumerateObject()) _outcome[p.Name] = (p.Value[0].GetInt32(), p.Value[1].GetDouble(), p.Value[2].GetDouble(), p.Value[3].GetDouble(), p.Value[4].GetDouble());
    }
}
