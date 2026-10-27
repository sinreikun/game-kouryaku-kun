using System.Diagnostics;

namespace GameKouryakuKun.Core.Diagnostics;

/// <summary>時刻の抽象。状態機械やプローブは IClock 経由で時刻を得るので、テストでは時間を自由に進められる。</summary>
public interface IClock
{
    /// <summary>単調増加のミリ秒（プロセス起動基準）</summary>
    double NowMs { get; }
}

public sealed class MonoClock : IClock
{
    public static readonly MonoClock Instance = new();
    private static readonly long Origin = Stopwatch.GetTimestamp();
    /// <summary>Stopwatch タイムスタンプをこの時計のミリ秒に変換する</summary>
    public static double ToMs(long stopwatchTimestamp) => (stopwatchTimestamp - Origin) * 1000.0 / Stopwatch.Frequency;
    public static double Now => ToMs(Stopwatch.GetTimestamp());
    public double NowMs => Now;
}

/// <summary>テスト用の手動時計</summary>
public sealed class ManualClock : IClock
{
    public double NowMs { get; set; }
    public void Advance(double ms) => NowMs += ms;
}
