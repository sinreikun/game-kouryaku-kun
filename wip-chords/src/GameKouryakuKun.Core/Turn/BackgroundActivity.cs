using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Turn;

/// <summary>
/// 画面ごとの「ふだんの動きの量」（v0.17.0。実機 v0.16.0: ある画面で時間切れの手が 160 手中 72 手。動き続ける所の位置が一定しないため、
/// 位置のマスク（MotionMaskBook）では画面の 3% しか隠せず、落ち着きの判定が最後まで満たされなかった）。
/// 時間切れの手の終わりの区間（入力への応答が終わった後）に、連続するフレームの間の変化の量（変化率・最大連結領域）を観測し、
/// その画面の静止の基準を「ふだんの動きの量」まで緩める。入力に応じた大きな変化（基準画面からの変化）の判定は変えない。
/// 位置は問わない。意味（何が動いているか）は持たない。観測の出どころ: 直接（その画面の時間切れの手）
/// </summary>
public sealed class BackgroundActivityBook
{
    public int MinTimeouts { get; init; } = 2;          // 根拠: 汎用（1 回の偶然で基準を変えない最小の繰り返し。MotionMaskBook と同じ）
    public double Quantile { get; init; } = 0.9;        // 根拠: 汎用（ふだんの動きのほとんどを含む高い分位）
    public double Margin { get; init; } = 1.25;         // 根拠: 汎用（計測の揺れの余裕）
    public double MaxStillRatio { get; init; } = 0.03;  // 根拠: 汎用（画面の 3% を超える動きは「静止」と呼ばない。入力による画面の切り替わりは通常これより大きい）
    public double MaxStillComponent { get; init; } = 0.03;   // 根拠: 同上
    public int Keep { get; init; } = 400;               // 根拠: 汎用（直近の観測だけ。画面の性質が変わったら追従する）

    private sealed class E { public int Timeouts; public readonly List<(double R, double C)> S = new(); public readonly List<double> Resp = new(); }
    public double MaxStillRatioWide { get; init; } = 0.10;   // 根拠: 汎用（上限を広げても画面の 1 割まで）
    public int MinResponses { get; init; } = 5;               // 根拠: 汎用（応答の大きさの中央値を言う最小の回数）
    public double ResponseFraction { get; init; } = 0.5;      // 根拠: 汎用（入力への応答の大きさの半分未満の動きなら、応答と取り違えにくい）

    /// <summary>
    /// 入力に応じて画面が落ち着いた手（Normal）の、基準画面からの変化の大きさ（v0.19.0）。この画面の応答がふだんの動きより十分大きいと分かれば、
    /// 静止の基準の上限を「応答の大きさの中央値の半分」まで広げる（画面の 3% を超えて動き続ける画面のため。上限 10%）
    /// </summary>
    public void ObserveResponse(string screen, double ratio)
    {
        var e = _e.TryGetValue(screen, out var x) ? x : _e[screen] = new E();
        e.Resp.Add(ratio); if (e.Resp.Count > 50) e.Resp.RemoveAt(0);   // 根拠: 汎用（直近の応答だけ）
    }

    private (double R, double C) Caps(E e)
    {
        if (e.Resp.Count < MinResponses) return (MaxStillRatio, MaxStillComponent);
        var v = e.Resp.OrderBy(z => z).ToList(); double med = v[v.Count / 2];
        double cap = Math.Clamp(ResponseFraction * med, MaxStillRatio, MaxStillRatioWide);
        return (cap, Math.Max(MaxStillComponent, cap));
    }
    private readonly Dictionary<string, E> _e = new(StringComparer.Ordinal);

    public sealed record Update(bool Learned, int Timeouts, double StillRatio, double StillComponent, bool Capped);

    /// <summary>時間切れの手の終わりの区間の、連続するフレームの間の変化の量</summary>
    public Update Observe(string screen, IReadOnlyList<(double Ratio, double Component)>? late, ChangeCriteria baseCriteria)
    {
        if (late == null || late.Count == 0) return new Update(false, _e.GetValueOrDefault(screen)?.Timeouts ?? 0, baseCriteria.StillRatio, baseCriteria.StillComponent, false);
        var e = _e.TryGetValue(screen, out var x) ? x : _e[screen] = new E();
        bool before = e.Timeouts >= MinTimeouts;
        e.Timeouts++; e.S.AddRange(late);
        if (e.S.Count > Keep) e.S.RemoveRange(0, e.S.Count - Keep);
        var c = CriteriaFor(screen, baseCriteria, out bool capped);
        return new Update(!before && e.Timeouts >= MinTimeouts, e.Timeouts, c.StillRatio, c.StillComponent, capped);
    }

    public ChangeCriteria CriteriaFor(string screen, ChangeCriteria baseCriteria) => CriteriaFor(screen, baseCriteria, out _);

    public ChangeCriteria CriteriaFor(string screen, ChangeCriteria baseCriteria, out bool capped)
    {
        capped = false;
        if (!_e.TryGetValue(screen, out var e) || e.Timeouts < MinTimeouts || e.S.Count == 0) return baseCriteria;
        double q(Func<(double R, double C), double> f) { var v = e.S.Select(f).OrderBy(z => z).ToList(); return v[Math.Min(v.Count - 1, (int)Math.Floor(Quantile * (v.Count - 1)))]; }
        double r = q(s => s.R) * Margin, comp = q(s => s.C) * Margin;
        var (capR, capC) = Caps(e);
        if (r > capR || comp > capC) capped = true;
        r = Math.Min(capR, Math.Max(baseCriteria.StillRatio, r));
        comp = Math.Min(capC, Math.Max(baseCriteria.StillComponent, comp));
        return baseCriteria with { StillRatio = r, StillComponent = comp };
    }

    /// <summary>保存用（画面・時間切れの回数・観測の量の列）。起動をまたいで引き継ぐ（v0.18.0。出どころは同じ直接の観測）</summary>
    public sealed record Stored(string Screen, int Timeouts, IReadOnlyList<(double R, double C)> Samples);
    public IEnumerable<Stored> Export(string? screen = null) =>
        _e.Where(kv => screen == null || kv.Key == screen).Select(kv => new Stored(kv.Key, kv.Value.Timeouts, kv.Value.S.ToList()));
    public int Import(IEnumerable<Stored> rows)
    {
        int n = 0;
        foreach (var r in rows) { var e = new E { Timeouts = r.Timeouts }; e.S.AddRange(r.Samples.TakeLast(Keep)); _e[r.Screen] = e; n++; }
        return n;
    }

    public int Screens => _e.Count(kv => kv.Value.Timeouts >= MinTimeouts);
}
