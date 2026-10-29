namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// T3: 入力と無関係な変化（画素の揺れ・模様の揺らぎ・周期的な動き）の画面ごと・区画ごとのモデル（§3）。
/// 学ぶのは入力なしの区間（手と手の間・待つ手）の区画の変化だけ。入力の手の変化には「自然変化の候補」「入力起因の候補」「基準なし」の注釈を付けるだけで、
/// 観測を消さない・学習の入力を変えない（画面に見える情報は隠さない。時間的に後＝原因とはしない）。ゲームの知識は使わない。
/// </summary>
public sealed class NaturalNoiseModel
{
    public int MinIntervals { get; init; } = 5;          // 根拠: 汎用（区画ごとの率を言うのに少なすぎない入力なし区間の数）
    public double NaturalRate { get; init; } = 0.5;      // 根拠: 汎用（入力なしの区間の半分以上で変わる区画は、変化が入力なしでも起きる方が普通）
    public double PeriodicCv { get; init; } = 0.3;       // 根拠: 汎用（変化の間隔のばらつきが平均の 3 割以下なら周期的とみる）
    public int MaxTimes { get; init; } = 32;             // 根拠: 汎用（周期の推定に使う最近の変化の時刻の数）

    private sealed class Screen
    {
        public int Intervals; public int[] Changes = Array.Empty<int>();
        public List<double>[] Times = Array.Empty<List<double>>();
    }
    private readonly Dictionary<string, Screen> _s = new(StringComparer.Ordinal);
    public long NaturalIntervals, Annotated, ChangedBlocks, NaturalCandidates, InputCandidates, NoBaseline;

    private Screen Get(string key, int n)
    {
        if (!_s.TryGetValue(key, out var s) || s.Changes.Length != n)
            _s[key] = s = new Screen { Changes = new int[n], Times = Enumerable.Range(0, n).Select(_ => new List<double>()).ToArray() };
        return s;
    }

    /// <summary>入力なしの区間の区画の変化（手と手の間・待つ手）</summary>
    public void ObserveNatural(string key, bool[] changed, double atMs)
    {
        var s = Get(key, changed.Length);
        s.Intervals++; NaturalIntervals++;
        for (int i = 0; i < changed.Length; i++)
        {
            if (!changed[i]) continue;
            s.Changes[i]++;
            var t = s.Times[i]; t.Add(atMs); if (t.Count > MaxTimes) t.RemoveAt(0);
        }
    }

    /// <summary>区画の入力なしの変化率（基準が足りなければ null）</summary>
    public double? Rate(string key, int block)
        => _s.TryGetValue(key, out var s) && s.Intervals >= MinIntervals && block < s.Changes.Length ? (double)s.Changes[block] / s.Intervals : null;

    /// <summary>区画の変化が周期的か（変化の間隔の変動係数）。周期（ms）を返す</summary>
    public double? Period(string key, int block)
    {
        if (!_s.TryGetValue(key, out var s) || block >= s.Times.Length || s.Times[block].Count < 4) return null;
        var t = s.Times[block];
        var d = t.Zip(t.Skip(1), (a, b) => b - a).ToArray();
        double m = d.Average(); if (m <= 0) return null;
        double sd = Math.Sqrt(d.Select(x => (x - m) * (x - m)).Average());
        return sd / m <= PeriodicCv ? m : null;
    }

    public sealed record Annotation(int Changed, int Natural, int Input, int NoBaseline, int Periodic);

    /// <summary>入力の手の変化への注釈（観測・学習は変えない）</summary>
    public Annotation Annotate(string key, bool[] changed)
    {
        int c = 0, nat = 0, inp = 0, nb = 0, per = 0;
        for (int i = 0; i < changed.Length; i++)
        {
            if (!changed[i]) continue;
            c++;
            var r = Rate(key, i);
            if (r is null) nb++;
            else if (r >= NaturalRate) { nat++; if (Period(key, i) != null) per++; }
            else inp++;
        }
        Annotated++; ChangedBlocks += c; NaturalCandidates += nat; InputCandidates += inp; NoBaseline += nb;
        return new Annotation(c, nat, inp, nb, per);
    }

    public int ScreensWithBaseline => _s.Values.Count(s => s.Intervals >= MinIntervals);
    public int Screens => _s.Count;

    public string Summary() =>
        $"入力なし区間 {NaturalIntervals}・基準のある画面 {ScreensWithBaseline}/{Screens}・注釈した手 {Annotated}・変わった区画 {ChangedBlocks}（自然変化の候補 {NaturalCandidates}・入力起因の候補 {InputCandidates}・基準なし {NoBaseline}）";
}
