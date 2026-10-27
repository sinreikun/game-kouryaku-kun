namespace GameKouryakuKun.Core.Exploration;

// 連続した経験の系列から、反復・部分系列・循環・大きな変化・予測の外れを統計的に見つけ、パターン P1, P2, … を作る。
// 系列は「状態（画面のノード）・入力・結果（画面が変わったか）」の中立の記号だけ。どこからどこまでが1件かも、ここで経験から見つける。
// 実行単位（runs）・冒険・終了・再開などの区切りは入力に含めない（最上位原則 3、2026-10-18 09:36 のユーザー指示）。

/// <summary>連続した経験の1手（中立の記号）。Seq は全履歴を通した通し番号</summary>
public sealed record StreamStep(long Seq, string State, string Action, bool Changed, string NextState, double ChangeRatio)
{
    /// <summary>分割で読み直した手か（出どころの区別。読み直しは stream_reinterpretations にある）</summary>
    public bool Reinterpreted { get; init; }
    /// <summary>手の始まりの見た目の状態の番号（v6 以降の記録だけ）</summary>
    public int? VisState { get; init; }
}

/// <summary>パターンの種類（見つけ方の区別だけ。意味は持たない）</summary>
public enum SequencePatternKind { Motif, Cycle, Segment }

/// <summary>見つかったパターン1つ。名前は持たない（人向けの名前は説明層の表だけ）</summary>
public sealed class SequencePattern
{
    public int Id { get; init; }
    public SequencePatternKind Kind { get; init; }
    public string Signature { get; init; } = "";
    public int Length { get; init; }
    public int Count { get; set; }
    public long FirstSeq { get; set; }
    public long LastSeq { get; set; }
}

/// <summary>境界の候補の理由（どれも統計の量。意味は持たない）</summary>
public enum BoundaryReason { Surprisal, ChangePoint, BigChange, PredictionShift }

/// <summary>1手を観測した結果。
/// Excess は「その状態・入力でふだんどれだけ予測が当たらないか」（予測分布のエントロピー）を差し引いた外れ。</summary>
public sealed record SequenceObservation(long Seq, double Surprisal, IReadOnlyList<BoundaryReason> Boundary, IReadOnlyList<SequencePattern> NewPatterns, IReadOnlyList<int> Hits)
{
    public double Excess { get; init; }
}

/// <summary>(状態, 入力) → 次の状態 の頻度による予測。外れ（surprisal = −log2 p）を返す</summary>
public sealed class TransitionPredictor
{
    private readonly Dictionary<(string, string), Dictionary<string, int>> _c = new();
    private readonly HashSet<string> _states = new(StringComparer.Ordinal);

    public double Surprisal(string state, string action, string next)
    {
        int v = Math.Max(2, _states.Count + 1);   // 未知の次状態の分も残すラプラス平滑化
        if (!_c.TryGetValue((state, action), out var d)) return Math.Log2(v);
        int total = d.Values.Sum(), n = d.TryGetValue(next, out var k) ? k : 0;
        return -Math.Log2((n + 1.0) / (total + v));
    }

    /// <summary>
    /// その (状態, 入力) での予測分布のエントロピー（ビット）＝ふだんの不確かさ。surprisal の期待値。
    /// 初めての組では一様分布なので surprisal と等しく、差（Excess）は 0 になる（未知は外れとして数えない）
    /// </summary>
    public double Entropy(string state, string action)
    {
        int v = Math.Max(2, _states.Count + 1);
        if (!_c.TryGetValue((state, action), out var d)) return Math.Log2(v);
        int total = d.Values.Sum(); double den = total + v, h = 0;
        foreach (var k in d.Values) { double p = (k + 1.0) / den; h -= p * Math.Log2(p); }
        int unseen = Math.Max(0, v - d.Count);
        if (unseen > 0) { double p = 1.0 / den; h -= unseen * p * Math.Log2(p); }
        return h;
    }

    public void Observe(string state, string action, string next)
    {
        _states.Add(state); _states.Add(next);
        if (!_c.TryGetValue((state, action), out var d)) _c[(state, action)] = d = new Dictionary<string, int>(StringComparer.Ordinal);
        d[next] = d.TryGetValue(next, out var k) ? k + 1 : 1;
    }
}

/// <summary>標準化した値が、最近の分布から大きく外れたか（平均＋Z×標準偏差。自己調整）</summary>
public sealed class OutlierGate
{
    private readonly Queue<double> _w = new();
    private double _sum, _sq;
    public int Window { get; init; } = 300;     // 根拠: 汎用（最近の分布だけを使う窓）
    public int MinSamples { get; init; } = 30;  // 根拠: 汎用（分布を推定する最小の標本数）
    public double Z { get; init; } = 2.5;       // 根拠: 汎用（正規分布でおよそ上位1%）

    public bool Observe(double x)
    {
        bool outlier = false;
        if (_w.Count >= MinSamples)
        {
            double m = _sum / _w.Count, sd = Math.Sqrt(Math.Max(1e-12, _sq / _w.Count - m * m));
            outlier = x > m + Z * Math.Max(sd, 1e-6);
        }
        _w.Enqueue(x); _sum += x; _sq += x * x;
        if (_w.Count > Window) { var o = _w.Dequeue(); _sum -= o; _sq -= o * o; }
        return outlier;
    }
}

/// <summary>
/// ベイズ的なオンライン変化点検出（Adams &amp; MacKay 2007）。0/1 の系列（ここでは「予測が外れたか」）を「起きる率が一定の区間」の連なりとみなし、
/// 今の区間がどれだけ続いているか（区間の長さ）の事後分布を毎手更新する（区間ごとの率はベータ分布の共役事前分布）。
/// 「区間が MinRun 手より短い」確率が半分を超えたら変化点とする（最も確からしい長さそのものは、長い区間どうしの確率がほぼ等しいと大きく跳ぶため使わない）。
/// 外れの大きさ（連続値）ではなく有無を使うのは、外れの大きさの分布の裾が重く、正規分布の仮定ではまれな大きい外れのたびに変化点が出たため（合成試験）
/// </summary>
public sealed class BayesianChangePoint
{
    public double PriorChangeRate { get; init; } = 1.0 / 100;   // 根拠: 汎用（区間の長さの事前の期待値 100 手。区間が終わる確率は毎手一定）
    public int MaxRunLength { get; init; } = 300;      // 根拠: 汎用（追う区間の長さの上限。計算量との釣り合い）
    public int MinRun { get; init; } = 10;             // 根拠: 汎用（これより短い区間からの切り替わりは数えない）
    private const double A0 = 1, B0 = 1;              // 根拠: 汎用（率の一様な事前分布）

    private List<double> _p = new() { 1.0 };
    private List<(double A, double B)> _st = new() { (A0, B0) };
    private bool _inShort = true; private long _sinceShift;
    /// <summary>今いちばん確からしい区間の長さ</summary>
    public int MapRunLength { get; private set; }

    public bool Observe(bool x)
    {
        int R = _p.Count;
        var np = new List<double>(R + 1) { 0 }; var ns = new List<(double, double)>(R + 1) { (A0, B0) };
        double cp = 0;
        for (int r = 0; r < R; r++)
        {
            var (a, b) = _st[r];
            double pr = x ? a / (a + b) : b / (a + b);
            np.Add(_p[r] * pr * (1 - PriorChangeRate)); cp += _p[r] * pr * PriorChangeRate;
            ns.Add(x ? (a + 1, b) : (a, b + 1));
        }
        np[0] = cp;
        if (np.Count > MaxRunLength) { np[^2] += np[^1]; np.RemoveAt(np.Count - 1); ns.RemoveAt(ns.Count - 1); }
        double sum = np.Sum();
        if (!(sum > 0)) { np = new() { 1.0 }; ns = new() { (A0, B0) }; sum = 1; }
        for (int i = 0; i < np.Count; i++) np[i] /= sum;
        _p = np; _st = ns;
        int map = 0; for (int i = 1; i < _p.Count; i++) if (_p[i] > _p[map]) map = i;
        MapRunLength = map; _sinceShift++;
        double shortMass = 0; for (int i = 0; i < Math.Min(MinRun, _p.Count); i++) shortMass += _p[i];
        bool shift = false;
        if (shortMass > 0.5) { if (!_inShort && _sinceShift >= MinRun) { shift = true; _sinceShift = 0; } _inShort = true; }
        else _inShort = false;
        return shift;
    }
}

/// <summary>Page-Hinkley 法による変化点の検出（平均の持続的な変化。閾値は標準偏差の倍数で自己調整）</summary>
public sealed class ChangePointDetector
{
    private double _mean, _m2, _cumUp, _minUp, _cumDn, _maxDn; private long _n;
    public double Lambda { get; init; } = 8;     // 根拠: 汎用（標準偏差の 8 倍の累積の偏りで変化点とする）
    public double Delta { get; init; } = 0.5;    // 根拠: 汎用（標準偏差の半分より小さい揺れは無視）
    public int MinSamples { get; init; } = 30;   // 根拠: 汎用（分布を推定する最小の標本数）

    public bool Observe(double x)
    {
        _n++; double d = x - _mean; _mean += d / _n; _m2 += d * (x - _mean);
        if (_n < MinSamples) return false;
        double sd = Math.Sqrt(Math.Max(1e-12, _m2 / (_n - 1))), z = (x - _mean) / Math.Max(sd, 1e-6);
        _cumUp += z - Delta; _minUp = Math.Min(_minUp, _cumUp);
        _cumDn += z + Delta; _maxDn = Math.Max(_maxDn, _cumDn);
        if (_cumUp - _minUp > Lambda || _maxDn - _cumDn > Lambda)
        {
            _n = 0; _mean = 0; _m2 = 0; _cumUp = _minUp = _cumDn = _maxDn = 0;   // 新しい区間として統計をやり直す
            return true;
        }
        return false;
    }
}

/// <summary>
/// 連続経験の系列学習。
///  - 反復系列: 記号（状態|入力|結果）の n-gram（2〜MaxN）を数え、MinSupport 回以上現れ、かつ同じ回数のより長い系列に含まれないもの（閉じた頻出系列）を Motif とする
///  - 循環: 同じ状態に MaxCycle 手以内に戻ったとき、その間の状態の並び（連続の重複を除き、回転を正規化）を数え、MinSupport 回で Cycle とする
///  - 区切り: 予測の外れ（surprisal から、その状態・入力でのふだんの不確かさ＝エントロピーを引いた値の外れ値）・
///    予測の外れ方の変化点（MinExcessBits 以上の外れが起きる率のベイズ的変化点）・変化点（画面の変化量の平均の持続的な変化）・大きな変化（変化量の外れ値）を境界の候補とし、
///    境界と境界の間（MinSegment〜MaxSegment 手）の状態の並びが SegmentSupport 回現れたら Segment とする（「1件」の範囲を経験から見つける）
/// 実行単位・冒険の区切りは使わない。
/// </summary>
public sealed class SequenceLearner
{
    public int MaxN { get; init; } = 6;            // 根拠: 汎用（数え上げる部分系列の最大の長さ。記憶量との釣り合い）
    public int MinSupport { get; init; } = 3;      // 根拠: 汎用（偶然の一致を除く最小の反復回数）
    public int MaxCycle { get; init; } = 30;       // 根拠: 汎用（循環として数える最大の長さ）
    public int MinSegment { get; init; } = 2;      // 根拠: 汎用（1手だけの区間は数えない）
    public int MaxSegment { get; init; } = 60;     // 根拠: 汎用（署名にする区間の最大の長さ）
    public int SegmentSupport { get; init; } = 2;  // 根拠: 汎用（同じ区間が2回現れたら反復とみなす最小の回数）
    public int MaxTracked { get; init; } = 200_000; // 根拠: 汎用（数える候補の上限。超えたら1回しか現れていない候補から捨てる）
    // 予測の外れは「surprisal − その (状態, 入力) の予測分布のエントロピー」で測る（v0.9.0）。
    // v0.8.1 の「直前の文脈の平均との差」は、予測できない組が多い所では差が小さく境界がほとんど出なかった（実機診断）。
    // ふだんから結果がばらつく組では外れても値は小さく、いつも同じ結果になる組での外れだけが大きくなる。初めての組は 0（未知は外れではない）

    private readonly TransitionPredictor _pred = new();
    private readonly OutlierGate _surprise = new(), _big = new() { Z = 3 };
    /// <summary>予測の外れとして数える最小の値（ビット）。1 ビット＝起きた結果の確率が、ふだんの結果の確率の半分以下</summary>
    public double MinExcessBits { get; init; } = 1.0;   // 根拠: 汎用（情報量の 1 ビット）
    private readonly ChangePointDetector _cp = new();
    private readonly BayesianChangePoint _shift = new();
    private readonly LinkedList<string> _recentTokens = new();
    private readonly Dictionary<string, (int Count, long Last)> _ngrams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, long Last)> _cycles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, long Last)> _segments = new(StringComparer.Ordinal);
    private readonly List<(string State, long Seq)> _states = new();
    private readonly List<string> _segment = new();
    private readonly Dictionary<(SequencePatternKind, string), SequencePattern> _patterns = new();
    private int _nextId = 1;
    public long Steps { get; private set; }
    public int Boundaries { get; private set; }
    public IReadOnlyCollection<SequencePattern> Patterns => _patterns.Values;

    public static string Token(StreamStep s) => $"{s.State}|{s.Action}|{(s.Changed ? '+' : '=')}";

    /// <summary>保存してあったパターンの番号と回数を引き継ぐ（数え直しは Replay で行い、回数は大きい方を残す。Replay の窓より前の分を失わないため）</summary>
    public void LoadPatterns(IEnumerable<SequencePattern> saved)
    {
        foreach (var p in saved)
        {
            _patterns[(p.Kind, p.Signature)] = new SequencePattern { Id = p.Id, Kind = p.Kind, Signature = p.Signature, Length = p.Length, Count = p.Count, FirstSeq = p.FirstSeq, LastSeq = p.LastSeq };
            _nextId = Math.Max(_nextId, p.Id + 1);
        }
    }

    /// <summary>過去の連続履歴を流し直して統計を作り直す（出来事は返さない）</summary>
    public void Replay(IEnumerable<StreamStep> history) { foreach (var s in history) Observe(s); }

    public SequenceObservation Observe(StreamStep s)
    {
        Steps++;
        var created = new List<SequencePattern>(); var hits = new List<int>();
        // 予測の外れ
        double sup = _pred.Surprisal(s.State, s.Action, s.NextState);
        double excess = sup - _pred.Entropy(s.State, s.Action);
        _pred.Observe(s.State, s.Action, s.NextState);
        var reasons = new List<BoundaryReason>();
        if (_surprise.Observe(excess) && excess >= MinExcessBits) reasons.Add(BoundaryReason.Surprisal);
        if (_shift.Observe(excess >= MinExcessBits)) reasons.Add(BoundaryReason.PredictionShift);   // 外れの起きる率の変化
        if (_cp.Observe(s.ChangeRatio)) reasons.Add(BoundaryReason.ChangePoint);
        if (_big.Observe(s.ChangeRatio)) reasons.Add(BoundaryReason.BigChange);

        // 反復系列（n-gram）
        _recentTokens.AddLast(Token(s));
        if (_recentTokens.Count > MaxN) _recentTokens.RemoveFirst();
        var toks = _recentTokens.ToArray();
        var keys = new string[toks.Length + 1]; var counts = new int[toks.Length + 2];
        for (int n = 2; n <= toks.Length; n++) { keys[n] = string.Join(" ", toks, toks.Length - n, n); counts[n] = Bump(_ngrams, keys[n], s.Seq); }
        for (int n = 2; n <= toks.Length; n++)
        {
            if (counts[n] < MinSupport) continue;
            // 閉じた頻出系列: 1つ前の記号まで含めた長い系列が同じ回数なら、短い方は長い方の一部にすぎない（新しくは作らない）
            bool subsumed = n < toks.Length && counts[n + 1] >= counts[n];
            if (subsumed && !_patterns.ContainsKey((SequencePatternKind.Motif, keys[n]))) continue;
            Hit(SequencePatternKind.Motif, keys[n], n, counts[n], s.Seq, created, hits);
        }

        // 循環（状態の並び）
        if (_states.Count == 0 || _states[^1].State != s.State) _states.Add((s.State, s.Seq));
        if (s.NextState != s.State)
        {
            int back = -1;
            for (int i = _states.Count - 1, k = 0; i >= 0 && k < MaxCycle; i--, k++)
                if (_states[i].State == s.NextState) { back = i; break; }
            if (back >= 0 && _states.Count - back >= 2)
            {
                var cyc = _states.Skip(back).Select(x => x.State).ToList();
                var key = CanonicalRotation(cyc);
                var c = Bump(_cycles, key, s.Seq);
                if (c >= MinSupport) Hit(SequencePatternKind.Cycle, key, cyc.Count, c, s.Seq, created, hits);
            }
            if (_states.Count > MaxCycle * 4) _states.RemoveRange(0, _states.Count - MaxCycle * 2);
        }

        // 区切りと区間
        _segment.Add(s.State);
        if (reasons.Count > 0)
        {
            Boundaries++;
            var seg = Compress(_segment);
            if (seg.Count >= MinSegment && _segment.Count <= MaxSegment)
            {
                var key = string.Join(" > ", seg);
                var c = Bump(_segments, key, s.Seq);
                if (c >= SegmentSupport) Hit(SequencePatternKind.Segment, key, seg.Count, c, s.Seq, created, hits);
            }
            _segment.Clear();
        }
        else if (_segment.Count > MaxSegment) _segment.RemoveAt(0);

        if (_ngrams.Count > MaxTracked) Prune(_ngrams);
        if (_cycles.Count > MaxTracked) Prune(_cycles);
        if (_segments.Count > MaxTracked) Prune(_segments);
        return new SequenceObservation(s.Seq, sup, reasons, created, hits) { Excess = excess };
    }

    private void Hit(SequencePatternKind kind, string key, int len, int count, long seq, List<SequencePattern> created, List<int> hits)
    {
        if (!_patterns.TryGetValue((kind, key), out var p))
        {
            p = new SequencePattern { Id = _nextId++, Kind = kind, Signature = key, Length = len, FirstSeq = seq };
            _patterns[(kind, key)] = p;
            created.Add(p);
        }
        p.Count = Math.Max(p.Count, count); p.LastSeq = Math.Max(p.LastSeq, seq);
        hits.Add(p.Id);
    }

    private static int Bump(Dictionary<string, (int Count, long Last)> d, string key, long seq)
    {
        var c = d.TryGetValue(key, out var v) ? v.Count + 1 : 1;
        d[key] = (c, seq);
        return c;
    }

    private static void Prune(Dictionary<string, (int Count, long Last)> d)
    {
        foreach (var k in d.Where(kv => kv.Value.Count <= 1).OrderBy(kv => kv.Value.Last).Take(d.Count / 2).Select(kv => kv.Key).ToList()) d.Remove(k);
    }

    private static List<string> Compress(List<string> xs)
    {
        var r = new List<string>();
        foreach (var x in xs) if (r.Count == 0 || r[^1] != x) r.Add(x);
        return r;
    }

    /// <summary>循環の並びを、辞書順で最小の回転にそろえる（どこから回り始めても同じ循環）</summary>
    public static string CanonicalRotation(IReadOnlyList<string> cyc)
    {
        string best = "";
        for (int i = 0; i < cyc.Count; i++)
        {
            var r = string.Join(" > ", cyc.Skip(i).Concat(cyc.Take(i)));
            if (i == 0 || string.CompareOrdinal(r, best) < 0) best = r;
        }
        return best;
    }
}
