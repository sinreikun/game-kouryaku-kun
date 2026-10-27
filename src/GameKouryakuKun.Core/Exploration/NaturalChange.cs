namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// ある画面・操作の結果が「入力なしでも起きた可能性」と「自分の操作が関与した可能性」の推定（v0.15.0）。
/// 比べるのは同じ画面での待つ手（キーを送らない中立の観測）の結果だけ。ゲームの意味は使わない。
/// PActionCaused = 変化が起きたとき、それが操作によるものである割合の推定 = max(0, pA − pW) / pA（待つ手の観測がなければ NaN＝未知）。
/// NextDivergence = 次の画面の分布の全変動距離（操作と待つ手で行き先がどれだけ違うか。0〜1）
/// </summary>
public sealed record ActionAttribution(string Node, string Action, int ActionTries, double PChangeAction, int WaitTries, double PChangeWait,
    double PActionCaused, double NextDivergence)
{
    public bool Known => WaitTries > 0 && ActionTries > 0;
}

/// <summary>
/// 画面ごとの時間的な変化の性質（v0.15.0）。名前は観測の統計の区分だけ（ゲームの意味の分類ではない）。
/// 元になる観測は直接の記録: 待つ手の結果（experience_stream の action='wait'）、手と手の間の入力なしの変化（no_input_events）、
/// 動き続ける所（screen_motion）。この要約は保存せず、毎回その記録から作り直す（出どころは記録そのもの）
/// </summary>
public sealed record TemporalProfile(string Node, int WaitTries, double WaitChangeRate, double WaitLeaveRate,
    int FirstWaitTries, double FirstWaitChangeRate, int LaterWaitTries, double LaterWaitChangeRate,
    int NoInputEvents, int NoInputTransitions, double MotionCoverage, double MeanMaskedActivity, double PeriodMs = double.NaN, int PeriodIntervals = 0)
{
    /// <summary>観測の統計の区分（複数あり得る）。"still"・"periodic"・"partial_continuous"・"natural_transition"・"stops_after_time"・"unobserved"</summary>
    public IReadOnlyList<string> Traits
    {
        get
        {
            var t = new List<string>();
            if (WaitTries == 0 && NoInputEvents == 0 && MotionCoverage <= 0 && double.IsNaN(PeriodMs)) { t.Add("unobserved"); return t; }
            if (MotionCoverage > 0 || (WaitChangeRate >= 0.5 && WaitLeaveRate < 0.5)) t.Add("partial_continuous");
            if (WaitLeaveRate >= 0.5 || NoInputTransitions > 0) t.Add("natural_transition");
            // 待ち始めは変わるが、待ち続けると変わらなくなる（同じ画面で連続して待った2手目以降の変化の割合が明らかに低い）
            if (FirstWaitTries >= TemporalBook.MinEvidence && LaterWaitTries >= TemporalBook.MinEvidence && FirstWaitChangeRate - LaterWaitChangeRate >= 0.4) t.Add("stops_after_time");
            if (!double.IsNaN(PeriodMs)) t.Add("periodic");
            if (t.Count == 0 && WaitTries > 0 && WaitChangeRate < 0.5) t.Add("still");
            return t;
        }
    }

    public static string TraitText(string k) => k switch
    {
        "still" => "入力なしでは変わらない", "partial_continuous" => "入力なしでも一部が変わり続ける", "natural_transition" => "入力なしで別の画面へ移ることがある",
        "stops_after_time" => "待ち始めは変わるが、待ち続けると止まる", "periodic" => "入力なしの変化がほぼ一定の間隔でくり返す", "unobserved" => "入力なしの観測がまだない", _ => k,
    };
}

public static class NaturalChange
{
    public static ActionAttribution Attribute(Explorer ex, string node, string action)
    {
        var a = ex.StatsOf(node, action); var w = ex.StatsOf(node, ExploreAction.Wait.Name);
        int na = a?.Tries ?? 0, nw = w?.Tries ?? 0;
        double pa = ((a?.Changes ?? 0) + 0.5) / (na + 1.0);
        double pw = ((w?.Changes ?? 0) + 0.5) / (nw + 1.0);
        // v0.27.0: 画面ごとの自然な変化の基準（手と手の間の観測を含む）があれば、それを入力なしの割合にする。観測の数は基準の観測の数
        if (ex.Baseline is { } nb && nb.Rates(node) is { } br) { pw = br.Change; nw = nb.Observations(node); }
        double caused = nw == 0 || na == 0 ? double.NaN : pa <= 0 ? 0 : Math.Max(0, pa - pw) / pa;
        double div = double.NaN;
        int nwt = w?.Tries ?? 0;   // v0.27.1: 次の画面の分布の差は待つ手の実績があるときだけ（基準だけの画面で w が null のまま参照していた実機の NullReference を修正）
        if (na > 0 && nwt > 0)
        {
            var keys = a!.To.Keys.Union(w!.To.Keys, StringComparer.Ordinal);
            div = 0.5 * keys.Sum(k => Math.Abs((a.To.GetValueOrDefault(k)) / (double)na - (w.To.GetValueOrDefault(k)) / (double)nwt));
        }
        return new ActionAttribution(node, action, na, pa, nw, pw, caused, div);
    }
}

/// <summary>画面ごとの時間的な変化の観測を数える（記録の流し直しでも、実行中でも同じ規則）</summary>
public sealed class TemporalBook
{
    public const int MinEvidence = 3;   // 根拠: 汎用（割合の差を言うのに少なすぎない回数）
    private sealed class E { public int W, WC, WL, FW, FWC, LW, LWC, NI, NIT; }
    private readonly Dictionary<string, E> _e = new(StringComparer.Ordinal);
    private string? _lastNode; private bool _lastWasWait;
    // 周期の検出（v0.16.0）: 同じ画面に居続ける間の、入力なしの変化の時刻。間隔のばらつきが小さければ周期とみなす
    public const int PeriodMinIntervals = 4;        // 根拠: 汎用（偶然そろった 2〜3 回と区別できる最小の回数）
    public const double PeriodMaxCv = 0.2;          // 根拠: 汎用（間隔の変動係数。計測の揺れ（フレーム間隔）より大きく、不規則な変化より小さい）
    private string? _runNode; private readonly List<double> _runTimes = new();
    private readonly Dictionary<string, (double Period, int N)> _period = new(StringComparer.Ordinal);

    /// <summary>入力なしの変化の時刻（待つ手で変わった・手と手の間に変わった）。キーを送った手・画面の移動・切替で連続が切れる</summary>
    public void ObserveNaturalChange(string node, double tMs)
    {
        if (_runNode != node) { _runNode = node; _runTimes.Clear(); }
        _runTimes.Add(tMs);
        if (_runTimes.Count > 64) _runTimes.RemoveAt(0);   // 根拠: 汎用（直近だけで判定する）
        var iv = new List<double>(); for (int i = 1; i < _runTimes.Count; i++) iv.Add(_runTimes[i] - _runTimes[i - 1]);
        if (iv.Count < PeriodMinIntervals) return;
        double mean = iv.Average(); if (mean <= 0) return;
        double sd = Math.Sqrt(iv.Sum(x => (x - mean) * (x - mean)) / iv.Count);
        if (sd / mean <= PeriodMaxCv) _period[node] = (mean, iv.Count);
        else if (_period.TryGetValue(node, out var p) && iv.Count >= 2 * PeriodMinIntervals) _period.Remove(node);   // 長く見て周期でなくなったら取り消す
    }

    /// <summary>入力を送った・画面が移った: 周期を見る連続を切る</summary>
    public void BreakRun() { _runNode = null; _runTimes.Clear(); }
    public (double PeriodMs, int Intervals)? Period(string node) => _period.TryGetValue(node, out var p) ? p : null;

    private E Of(string n) => _e.TryGetValue(n, out var x) ? x : _e[n] = new E();

    /// <summary>1手を数える（待つ手だけが入力なしの観測。キーを送った手は「連続した待ち」を切るだけ）</summary>
    public void ObserveStep(string from, string action, bool changed, string to)
    {
        if (action == ExploreAction.Wait.Name)
        {
            var e = Of(from); e.W++; if (changed) e.WC++; if (to != from) e.WL++;
            bool continued = _lastWasWait && _lastNode == from;
            if (continued) { e.LW++; if (changed) e.LWC++; } else { e.FW++; if (changed) e.FWC++; }
            _lastWasWait = true;
        }
        else _lastWasWait = false;
        _lastNode = to;
    }

    /// <summary>手と手の間の入力なしの変化（次の手の結果には混ぜない観測）</summary>
    public void ObserveGap(string from, bool transition) { var e = Of(from); e.NI++; if (transition) e.NIT++; _lastWasWait = false; }

    /// <summary>自動と人間の切替などで、連続した待ちの文脈を切る</summary>
    public void Break() { _lastWasWait = false; _lastNode = null; BreakRun(); }

    public TemporalProfile Profile(string node, double motionCoverage = 0, double meanMasked = 0)
    {
        var e = _e.GetValueOrDefault(node) ?? new E();
        static double R(int k, int n) => n == 0 ? 0 : (double)k / n;
        var per = Period(node);
        return new TemporalProfile(node, e.W, R(e.WC, e.W), R(e.WL, e.W), e.FW, R(e.FWC, e.FW), e.LW, R(e.LWC, e.LW), e.NI, e.NIT, motionCoverage, meanMasked,
            per?.PeriodMs ?? double.NaN, per?.Intervals ?? 0);
    }

    public IEnumerable<string> Nodes => _e.Keys;
    public int WaitObservations => _e.Values.Sum(x => x.W);
    public int GapObservations => _e.Values.Sum(x => x.NI);
}
