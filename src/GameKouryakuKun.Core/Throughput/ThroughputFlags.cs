namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// 高速経験収集（docs/throughput-parallel-sensing-design.md §6）のフラグ。すべて既定 OFF。環境変数 =1 で ON。
/// OFF のとき探索の経路は v0.38.0 と同一（呼び出し側は各フラグを見てから新しい部品に触れる）。
/// </summary>
public static class ThroughputFlags
{
    public const string StepTimingEnv = "GKK_STEP_TIMING";               // T0
    public const string PipelineSensingEnv = "GKK_PIPELINE_SENSING";     // T1
    public const string AdaptiveSettleEnv = "GKK_ADAPTIVE_SETTLE";       // T2
    public const string NaturalNoiseEnv = "GKK_NATURAL_NOISE_MODEL";     // T3
    public const string RawAggSplitEnv = "GKK_RAW_AGG_SPLIT";            // T4
    public const string ParallelObserversEnv = "GKK_PARALLEL_OBSERVERS"; // T5

    public static bool StepTiming { get; set; }
    public static bool PipelineSensing { get; set; }
    public static bool AdaptiveSettle { get; set; }
    public static bool NaturalNoise { get; set; }
    public static bool RawAggSplit { get; set; }
    public static bool ParallelObservers { get; set; }

    private static bool On(string env) => string.Equals(Environment.GetEnvironmentVariable(env), "1", StringComparison.Ordinal);

    public static void ApplyEffective()
    {
        StepTiming = On(StepTimingEnv);
        PipelineSensing = On(PipelineSensingEnv);
        AdaptiveSettle = On(AdaptiveSettleEnv);
        NaturalNoise = On(NaturalNoiseEnv);
        RawAggSplit = On(RawAggSplitEnv);
        ParallelObservers = On(ParallelObserversEnv);
    }

    /// <summary>台帳（ObservationLedger）を使うか: パイプラインか並列観測器のどちらかが ON</summary>
    public static bool LedgerActive => PipelineSensing || ParallelObservers;

    public static string StatusText()
    {
        ApplyEffective();
        string F(string n, bool v) => $"{n}={(v ? "ON" : "OFF")}";
        bool any = StepTiming || PipelineSensing || AdaptiveSettle || NaturalNoise || RawAggSplit || ParallelObservers;
        return "Throughput: " + (any ? "" : "OFF（既定・v0.38.0 と同一経路） ") + string.Join(" ", F("step_timing", StepTiming), F("pipeline_sensing", PipelineSensing),
            F("adaptive_settle", AdaptiveSettle), F("natural_noise", NaturalNoise), F("raw_agg_split", RawAggSplit), F("parallel_observers", ParallelObservers));
    }
}
