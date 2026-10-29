namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// 真の throughput 指標（§9）。単位時間あたりの「因果関係を保った・再利用可能な・将来再解釈可能な」経験量。
/// </summary>
public sealed record ThroughputMetrics(
    double Minutes, int Actions, long CausallyIntact, long Violations, long LateAmended, long Unresolved, long Missing,
    long RawRows, long DecidedWithPending)
{
    public double ActionsPerMinute => Minutes <= 0 ? 0 : Actions / Minutes;
    /// <summary>CIO/min: 因果保持観測/分（主指標）。対応違反が 1 件でもあれば 0（壊れた系列は数えない）</summary>
    public double CausalObservationsPerMinute => Minutes <= 0 || Violations > 0 ? 0 : CausallyIntact / Minutes;
    public double RawRetentionRatio => Actions == 0 ? 1 : Math.Min(1, (double)RawRows / Actions);
    public double LateAmendSuccessRate => LateAmended + Unresolved == 0 ? 1 : (double)LateAmended / (LateAmended + Unresolved);
    public double PendingNeverResolvedRate => Actions == 0 ? 0 : (double)Unresolved / Actions;
    public double DecidedWithPendingRate => Actions == 0 ? 0 : (double)DecidedWithPending / Actions;

    public string Summary() =>
        $"actions/min {ActionsPerMinute:0.0}・CIO/min {CausalObservationsPerMinute:0.0}・raw 保持 {RawRetentionRatio:P0}・late-amend 成功 {LateAmendSuccessRate:P0}・pending 未解決 {PendingNeverResolvedRate:P1}・前の観測未完で決めた手 {DecidedWithPendingRate:P0}・対応違反 {Violations}・missing {Missing}";
}
