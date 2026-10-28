namespace GameKouryakuKun.Core.Ocr;

/// <summary>v0.27.2: 毎手の文字の観測の集計（速さ・読まなかった手・古い文字・読み取り中の手の時間）。スレッドをまたぐので lock する</summary>
public sealed class TextStepStats
{
    private readonly object _l = new();
    private readonly List<double> _ms = new();
    private long _lastArrivedStep = long.MinValue;
    public long Requested, Skipped, Full, Region, Reused, Cache, Deferred, Failed, Fresh, Stale, Pending, Pairs, Unpaired, TextChanged, Suppressed;
    public long BusyTurns, IdleTurns; public double BusyBoundary, IdleBoundary, BusyGap, IdleGap; public long BusyGapN, IdleGapN;
    public double AreaSum;

    public void Arrived(long step, string kind, double ms, double area)
    {
        lock (_l)
        {
            _lastArrivedStep = Math.Max(_lastArrivedStep, step);
            switch (kind) { case "full": Full++; _ms.Add(ms); AreaSum += area; break; case "region": Region++; _ms.Add(ms); AreaSum += area; break; case "reuse": Reused++; break; case "cache": Cache++; break; case "deferred": Deferred++; break; }
        }
    }

    /// <summary>この手を選んだ時点の文字: fresh＝直前の手の結果の文字が届いていた・stale＝もっと前の文字だけ・pending＝まだ一度も届いていない</summary>
    public string StateAt(long step)
    {
        lock (_l)
        {
            string s = _lastArrivedStep == long.MinValue ? "pending" : _lastArrivedStep >= step - 1 ? "fresh" : "stale";
            if (s == "fresh") Fresh++; else if (s == "stale") Stale++; else Pending++;
            return s;
        }
    }

    public void TurnTiming(bool busy, double boundaryMs, double gapMs)
    {
        lock (_l)
        {
            if (busy) { BusyTurns++; BusyBoundary += boundaryMs; if (double.IsFinite(gapMs)) { BusyGap += gapMs; BusyGapN++; } }
            else { IdleTurns++; IdleBoundary += boundaryMs; if (double.IsFinite(gapMs)) { IdleGap += gapMs; IdleGapN++; } }
        }
    }

    public (double Median, double P90, double Max, int N) Latency()
    {
        lock (_l) { if (_ms.Count == 0) return (double.NaN, double.NaN, double.NaN, 0); var v = _ms.OrderBy(x => x).ToList(); return (v[v.Count / 2], v[Math.Min(v.Count - 1, (int)(v.Count * 0.9))], v[^1], v.Count); }
    }

    public (double Busy, double Idle) MeanBoundary() { lock (_l) return (BusyTurns > 0 ? BusyBoundary / BusyTurns : double.NaN, IdleTurns > 0 ? IdleBoundary / IdleTurns : double.NaN); }
    public (double Busy, double Idle) MeanGap() { lock (_l) return (BusyGapN > 0 ? BusyGap / BusyGapN : double.NaN, IdleGapN > 0 ? IdleGap / IdleGapN : double.NaN); }
}
