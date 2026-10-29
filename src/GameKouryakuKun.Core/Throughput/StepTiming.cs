namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// T0: 1 手の区間計測（挙動は変えない。時刻を記録するだけ）。区間は単調時計のミリ秒。
/// decide: 前の手の後処理の終わり → 入力送出の直前（決定・入力なし区間の確認を含む）
/// send: 入力送出（Send の呼び出し）/ settle: 静止判定 / commit: 分類と memory への記録
/// obs_wait: 観測（OCR を含む）の完成を待った時間（パイプライン時は前の手の観測の回収待ち）/ post: その後の後処理（dual-append・目的など）
/// </summary>
public sealed class StepTiming
{
    public static readonly string[] Phases = { "decide", "send", "settle", "commit", "obs_wait", "post" };
    private readonly double[] _cur = new double[Phases.Length];
    private double _mark = double.NaN;
    private readonly List<double[]> _rows = new();
    private readonly List<double> _intervals = new();
    private double _lastSendAt = double.NaN;

    /// <summary>直前の印からの経過を区間 phase に足して印を進める（印がなければ印を置くだけ）</summary>
    public void Mark(string phase, double nowMs)
    {
        int i = Array.IndexOf(Phases, phase);
        if (i < 0) throw new ArgumentException("unknown phase " + phase);
        if (!double.IsNaN(_mark)) _cur[i] += Math.Max(0, nowMs - _mark);
        _mark = nowMs;
    }

    /// <summary>入力送出の開始時刻（手の間隔の計測用）</summary>
    public void SendStarted(double nowMs)
    {
        if (!double.IsNaN(_lastSendAt)) _intervals.Add(nowMs - _lastSendAt);
        _lastSendAt = nowMs;
    }

    /// <summary>1 手の区切り。今回の区間を返して次の手へ（印は残す: 次の decide は今から数える）</summary>
    public double[] EndStep()
    {
        var r = (double[])_cur.Clone();
        _rows.Add(r);
        Array.Clear(_cur);
        return r;
    }

    public int Steps => _rows.Count;
    public IReadOnlyList<double> Intervals => _intervals;

    public static double Median(IEnumerable<double> xs)
    {
        var s = xs.OrderBy(x => x).ToArray();
        return s.Length == 0 ? 0 : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>区間ごとの中央値</summary>
    public IReadOnlyDictionary<string, double> MedianByPhase()
        => Phases.Select((p, i) => (p, m: Median(_rows.Select(r => r[i])))).ToDictionary(x => x.p, x => Math.Round(x.m, 1));

    /// <summary>区間の合計の中央値と手の間隔の中央値の比（±5% なら計測が手の時間を説明している）</summary>
    public double CoverageRatio()
    {
        if (_intervals.Count == 0 || _rows.Count == 0) return double.NaN;
        double sum = Median(_rows.Select(r => r.Sum()));
        double iv = Median(_intervals);
        return iv <= 0 ? double.NaN : sum / iv;
    }

    /// <summary>手/分（手の間隔の平均から）</summary>
    public double ActionsPerMinute() => _intervals.Count == 0 ? 0 : 60000.0 / _intervals.Average();
}
