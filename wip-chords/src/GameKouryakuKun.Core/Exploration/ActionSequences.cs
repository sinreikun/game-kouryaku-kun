namespace GameKouryakuKun.Core.Exploration;

/// <summary>並びの候補の状態。Active＝選択の候補に入っている。Dormant＝休止（記録は残し、条件がそろえば戻る）</summary>
public enum SequenceStatus { Active, Dormant }

/// <summary>見つけた操作の並び（複合の操作の候補）。名前は中立の「ActionSequence #n」だけで、意味も事前の価値も持たない</summary>
public sealed class ActionSequenceCandidate
{
    public int Id { get; init; }
    public string Name => $"ActionSequence #{Id}";
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();
    /// <summary>この並びを試す画面ノード（最も多く始まったノード）</summary>
    public string Start { get; init; } = "";
    /// <summary>最も多く行き着いたノード</summary>
    public string End { get; set; } = "";
    public int Total { get; set; }
    public int AtStart { get; set; }
    /// <summary>始まりの状態の似かより（シンプソン指数: 2回を無作為に選ぶと同じノードから始まっている確率）</summary>
    public double StartSimilarity { get; set; }
    /// <summary>始まりのノードから、同じ行き先に着いた割合（ラプラス平滑化）</summary>
    public double Reproducibility { get; set; }
    public long FoundAtSeq { get; init; }
    /// <summary>候補の出どころ: "stream"（連続履歴の反復から）・"goal"（自己生成の目的を同じ道筋で達成した経験から。v0.18.0）</summary>
    public string Origin { get; init; } = "stream";
    public SequenceStatus Status { get; set; } = SequenceStatus.Active;
    public long StatusSeq { get; set; }
    /// <summary>最後に始まりから同じ行き先に着いた通し番号</summary>
    public long LastReproducedSeq { get; set; }
    /// <summary>始まりからの最近 RecentWindow 回のうち、同じ行き先に着いた割合（ラプラス平滑化）と回数</summary>
    public double RecentReproducibility { get; set; } = 0.5;   // 根拠: 汎用（観測がないときのラプラス平滑化の値）
    public int RecentCount { get; set; }
    /// <summary>実行したときの結果の記録（手の番号, 予測より明らかに驚いたか）。DormantAfterSteps より古いものは数えない</summary>
    public List<(long Seq, bool Surprised)> Executions { get; } = new();
    /// <summary>最近の実行のうち、予測が外れた割合（ラプラス平滑化）と回数</summary>
    public (int N, double Rate) RecentExecutionSurprise(long now, long horizon)
    {
        int n = 0, k = 0;
        foreach (var (q, sur) in Executions) if (now - q <= horizon) { n++; if (sur) k++; }
        return (n, (k + 1.0) / (n + 2.0));
    }
}

/// <summary>並びの候補の状態の変化（記録用）。Kind: promoted・waiting・dormant・reactivated</summary>
public sealed record SequenceEvent(ActionSequenceCandidate Candidate, string Kind, string Reason, long Seq);

/// <summary>
/// 連続経験の系列から、複合の操作の候補になる「操作の並び」を見つける（v0.9.0、入れ替えは v0.10.0）。
/// ただ多く現れた並びは候補にしない。次をすべて満たすものだけを候補の条件を満たすとする:
///  1. 同じ始まりのノードからの反復の回数が MinRepeats 以上
///  2. 始まりのノードの似かより（シンプソン指数）が MinStartSimilarity 以上
///  3. その始まりから同じ行き先に着く割合（ラプラス平滑化）が MinReproducibility 以上で、行き先が始まりと違う
///  4. 最近の再現性（最近 RecentWindow 回）が下がっておらず、最後に再現してから DormantAfterSteps 手以内
/// MaxActive は選択の候補に同時に入れる数の上限で、記憶の上限ではない。長く再現しない・最近の再現性が下がった候補は休止にし、
/// 空いた枠に条件を満たす候補（新しいもの・条件を再び満たした休止中のもの）を入れる。入れ替えの基準は一般の統計（再現性・証拠の量・新しさ）だけで、
/// ゲーム上の価値は使わない。候補の記録は消さない。名前は中立（ActionSequence #n）で、事前の価値は上乗せしない。
/// 区切り（実行単位など）は与えない。
/// </summary>
public sealed class ActionSequenceMiner
{
    public int MinLength { get; init; } = 2;                 // 根拠: 汎用（1手は単独の操作）
    public int MaxLength { get; init; } = 3;                 // 根拠: 汎用（数える並びの最大の長さ。組合せの数との釣り合い）
    public int MinRepeats { get; init; } = 3;                // 根拠: 汎用（偶然の一致を除く最小の反復回数。系列の学習と同じ）
    public double MinStartSimilarity { get; init; } = 0.25;  // 根拠: 汎用（始まりがほぼ4か所以内に集まっている）
    public double MinReproducibility { get; init; } = 0.6;   // 根拠: 汎用（偶然（0.5）より明らかに高い割合で同じ行き先）
    public int MaxActive { get; init; } = 16;                // 根拠: 汎用（同時に選択の候補に入れる数。単独の操作の数と同じ程度に抑える）
    public int RecentWindow { get; init; } = 10;             // 根拠: 汎用（最近の再現性を見る回数）
    public int DormantAfterSteps { get; init; } = 2000;      // 根拠: 汎用（この手数のあいだ再現しなければ休止。起動時に流し直す履歴の 1/10）
    public int MaxTracked { get; init; } = 50_000;           // 根拠: 汎用（数える並びの上限。超えたら1回しか現れていないものから捨てる。候補の記録は消さない）

    private sealed class Stat
    {
        public int Total; public long Last;
        public Dictionary<string, Dictionary<string, int>> ByStart { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Queue<string>> Recent { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, Stat> _stats = new(StringComparer.Ordinal);
    private readonly List<StreamStep> _window = new();
    private readonly Dictionary<string, ActionSequenceCandidate> _cands = new(StringComparer.Ordinal);
    private int _nextId = 1;
    /// <summary>これまでに候補になったすべての並び（休止中を含む）</summary>
    public IReadOnlyCollection<ActionSequenceCandidate> Candidates => _cands.Values;
    public IEnumerable<ActionSequenceCandidate> Active => _cands.Values.Where(c => c.Status == SequenceStatus.Active);

    public static string Key(IEnumerable<string> steps) => string.Join(" > ", steps);

    /// <summary>実行した結果を記録する（予測の確かめは呼び出し側）。休止の判断は次の Observe で行う</summary>
    public void ObserveExecution(int id, long seq, bool surprised)
    {
        var c = _cands.Values.FirstOrDefault(x => x.Id == id);
        if (c == null) return;
        c.Executions.Add((seq, surprised));
        // 数えない古い実行は捨てる（記録そのものは保存先に残る）
        c.Executions.RemoveAll(e => seq - e.Seq > DormantAfterSteps);
    }

    /// <summary>
    /// 自己生成の目的を同じ道筋で繰り返し達成した経験から、並びの候補を足す（v0.18.0。スキルの芽）。
    /// 連続履歴から見つけた候補と同じく「待ち（waiting）」として入れ、枠に入るか・休止するかは同じ規則（再現性・実行の結果）で決める。事前の価値は上乗せしない。
    /// 長さは数える並びの範囲（MinLength〜MaxLength）だけ（統計が数え直せるもの）。同じ並びが既にあれば何もしない
    /// </summary>
    /// <summary>同じ並びの候補がすでにあるか（目的の区間が既存の候補を裏づけたかを数える。状態は変えない）</summary>
    public bool Has(IReadOnlyList<string> steps) => _cands.ContainsKey(Key(steps));

    public SequenceEvent? AddFromGoal(IReadOnlyList<string> steps, string start, string end, int achieved, int attempts, long seq)
    {
        if (steps.Count < MinLength || steps.Count > MaxLength || start == end) return null;
        var key = Key(steps);
        if (_cands.ContainsKey(key)) return null;
        double repro = (achieved + 1.0) / (attempts + 2.0);   // ラプラス平滑化（目的の達成の割合）
        var c = new ActionSequenceCandidate
        {
            Id = _nextId++, Steps = steps.ToList(), Start = start, End = end, Total = achieved, AtStart = achieved, StartSimilarity = 1, Reproducibility = repro,
            FoundAtSeq = seq, LastReproducedSeq = seq, RecentCount = attempts, RecentReproducibility = repro, Status = SequenceStatus.Dormant, StatusSeq = seq, Origin = "goal",
        };
        _cands[key] = c;
        return new SequenceEvent(c, "waiting", $"目的を同じ道筋で {achieved}/{attempts} 回達成した経験から", seq);
    }

    /// <summary>保存してあった候補（番号・並び・状態）を引き継ぐ。統計は Replay で数え直す</summary>
    public void Load(IEnumerable<ActionSequenceCandidate> saved)
    {
        foreach (var c in saved)
        {
            _cands[Key(c.Steps)] = c;
            _nextId = Math.Max(_nextId, c.Id + 1);
        }
    }

    /// <summary>過去の履歴を流し直す。状態の変化は返さない（最後の状態だけが残る）</summary>
    public void Replay(IEnumerable<StreamStep> history) { foreach (var s in history) Observe(s); }

    /// <summary>1手を足す。候補の状態の変化（新しい候補・休止・再開）を返す</summary>
    public IReadOnlyList<SequenceEvent> Observe(StreamStep s)
    {
        if (_window.Count > 0 && (_window[^1].Seq + 1 != s.Seq || _window[^1].NextState != s.State)) _window.Clear();
        _window.Add(s);
        if (_window.Count > MaxLength) _window.RemoveAt(0);
        var events = new List<SequenceEvent>();
        for (int n = MinLength; n <= _window.Count; n++)
        {
            var part = _window.Skip(_window.Count - n).ToList();
            var key = Key(part.Select(x => x.Action));
            if (!_stats.TryGetValue(key, out var st)) _stats[key] = st = new Stat();
            st.Total++; st.Last = s.Seq;
            var start = part[0].State; var end = part[^1].NextState;
            if (!st.ByStart.TryGetValue(start, out var ends)) st.ByStart[start] = ends = new Dictionary<string, int>(StringComparer.Ordinal);
            ends[end] = ends.TryGetValue(end, out var k) ? k + 1 : 1;
            if (!st.Recent.TryGetValue(start, out var rq)) st.Recent[start] = rq = new Queue<string>();
            rq.Enqueue(end); if (rq.Count > RecentWindow) rq.Dequeue();
            var e = Evaluate(st);
            if (_cands.TryGetValue(key, out var c))
            {
                c.Total = st.Total; c.AtStart = e.AtStart; c.StartSimilarity = e.Similarity; c.Reproducibility = e.Repro; c.End = e.End;
                if (start == c.Start)
                {
                    if (end == c.End) c.LastReproducedSeq = s.Seq;
                    if (st.Recent.TryGetValue(c.Start, out var q)) { c.RecentCount = q.Count; c.RecentReproducibility = (q.Count(x => x == c.End) + 1.0) / (q.Count + 2.0); }
                }
                continue;
            }
            if (e.AtStart >= MinRepeats && e.Similarity >= MinStartSimilarity && e.Repro >= MinReproducibility && e.End != e.Start)
            {
                var q = st.Recent[e.Start];
                c = new ActionSequenceCandidate
                {
                    Id = _nextId++, Steps = part.Select(x => x.Action).ToList(), Start = e.Start, End = e.End, Total = st.Total, AtStart = e.AtStart,
                    StartSimilarity = e.Similarity, Reproducibility = e.Repro, FoundAtSeq = s.Seq, LastReproducedSeq = s.Seq,
                    RecentCount = q.Count, RecentReproducibility = (q.Count(x => x == e.End) + 1.0) / (q.Count + 2.0),
                    Status = SequenceStatus.Dormant, StatusSeq = s.Seq,
                };
                _cands[key] = c;
                events.Add(new SequenceEvent(c, "waiting", "候補の条件を満たした", s.Seq));
            }
        }
        Maintain(s.Seq, events);
        if (_stats.Count > MaxTracked)
            foreach (var k in _stats.Where(kv => kv.Value.Total <= 1 && !_cands.ContainsKey(kv.Key)).OrderBy(kv => kv.Value.Last).Take(_stats.Count / 2).Select(kv => kv.Key).ToList()) _stats.Remove(k);
        // 同じ手で候補になり枠にも入った並びは「promoted」の1件にまとめる
        var result = new List<SequenceEvent>();
        foreach (var g in events.GroupBy(x => x.Candidate.Id))
        {
            if (g.Any(x => x.Kind == "waiting") && g.Any(x => x.Kind == "reactivated")) result.Add(new SequenceEvent(g.First().Candidate, "promoted", "候補の条件を満たし、枠に入った", s.Seq));
            else result.AddRange(g);
        }
        return result;
    }

    /// <summary>休止の理由（なければ null）。一般の統計だけ</summary>
    public string? DormantReason(ActionSequenceCandidate c, long now)
    {
        // 実行した結果が予測から外れ続けたら休止（古い実行は数えないので、時間がたてば再び候補になれる）
        var (en, er) = c.RecentExecutionSurprise(now, DormantAfterSteps);
        if (en >= MinRepeats && er > 1 - MinReproducibility) return $"最近 {en} 回の実行で予測が外れた割合 {er:F2}";
        if (now - c.LastReproducedSeq > DormantAfterSteps) return $"{now - c.LastReproducedSeq} 手のあいだ再現していない";
        if (c.RecentCount >= MinRepeats && c.RecentReproducibility < MinReproducibility) return $"最近 {c.RecentCount} 回の再現性 {c.RecentReproducibility:F2} が下がった";
        if (c.Reproducibility < MinReproducibility) return $"再現性 {c.Reproducibility:F2} が下がった";
        return null;
    }

    private bool Eligible(ActionSequenceCandidate c, long now) =>
        DormantReason(c, now) == null && c.AtStart >= MinRepeats && c.StartSimilarity >= MinStartSimilarity && c.End != c.Start;

    private void Maintain(long now, List<SequenceEvent> events)
    {
        foreach (var c in _cands.Values.Where(c => c.Status == SequenceStatus.Active).ToList())
            if (DormantReason(c, now) is string why) { c.Status = SequenceStatus.Dormant; c.StatusSeq = now; events.Add(new SequenceEvent(c, "dormant", why, now)); }
        int free = MaxActive - _cands.Values.Count(c => c.Status == SequenceStatus.Active);
        if (free <= 0) return;
        // 空いた枠には、条件を満たす休止中の候補を、最近の再現性・証拠の量・新しさの順に入れる
        foreach (var c in _cands.Values.Where(c => c.Status == SequenceStatus.Dormant && Eligible(c, now))
                     .OrderByDescending(c => c.RecentReproducibility).ThenByDescending(c => c.AtStart).ThenByDescending(c => c.LastReproducedSeq).ThenBy(c => c.Id).Take(free).ToList())
        {
            c.Status = SequenceStatus.Active; c.StatusSeq = now;
            events.Add(new SequenceEvent(c, "reactivated", "条件を満たし、枠が空いていた", now));
        }
    }

    private static (string Start, int AtStart, double Similarity, string End, double Repro) Evaluate(Stat st)
    {
        string start = ""; int at = -1; double sim = 0;
        foreach (var (s0, ends) in st.ByStart)
        {
            int c = ends.Values.Sum();
            double f = c / (double)st.Total; sim += f * f;
            if (c > at || (c == at && string.CompareOrdinal(s0, start) < 0)) { at = c; start = s0; }
        }
        var best = st.ByStart[start].OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
        return (start, at, sim, best.Key, (best.Value + 1.0) / (at + 2.0));
    }
}
