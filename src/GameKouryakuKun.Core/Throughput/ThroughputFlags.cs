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
    /// <summary>v0.42.0: 既定の高速モード C（OCR パイプライン＋適応的静止判定＋入力前の間）。=0 で切る（利用者の承認 2026-10-29・実機 v0.41.0 の交互比較で採用）</summary>
    public const string ModeCEnv = "GKK_THROUGHPUT_C";
    /// <summary>画面の「高速モード（C）」の切り替え（false で C を切る）。保存はアプリ側</summary>
    public static bool UserModeC { get; set; } = true;
    public static bool ModeC { get; private set; }

    public static bool StepTiming { get; set; }
    public static bool PipelineSensing { get; set; }
    public static bool AdaptiveSettle { get; set; }
    public static bool NaturalNoise { get; set; }
    public static bool RawAggSplit { get; set; }
    public static bool ParallelObservers { get; set; }

    private static bool Off(string env) => string.Equals(Environment.GetEnvironmentVariable(env), "0", StringComparison.Ordinal);
    private static bool On(string env) => string.Equals(Environment.GetEnvironmentVariable(env), "1", StringComparison.Ordinal);

    /// <summary>
    /// v0.40.0: アプリ内の自動比較（高速化比較）が区間ごとに設定するフラグの組。設定中は環境変数を見ない（人が環境変数を設定する必要はない）。
    /// MeasureLedger は計測専用（台帳の記帳だけで挙動を変えない。全区間で因果保持観測/分を同じ物差しで測るため）。
    /// </summary>
    public sealed record FlagSet(string Label, bool StepTiming, bool PipelineSensing, bool AdaptiveSettle, bool NaturalNoise, bool RawAggSplit, bool ParallelObservers, bool MeasureLedger = true);

    public static FlagSet? Override { get; set; }
    /// <summary>v0.41.0: 手ごとに設定を切り替える比較（設定中は Override をすべて ON にして部品を用意し、各手は割り当てた設定だけを使う）</summary>
    public static InterleavedComparison? Interleave { get; set; }
    public static bool MeasureLedger { get; private set; }
    public static string? SegmentLabel => Override?.Label;

    public static void ApplyEffective()
    {
        if (Override is { } o)
        {
            StepTiming = o.StepTiming; PipelineSensing = o.PipelineSensing; AdaptiveSettle = o.AdaptiveSettle;
            NaturalNoise = o.NaturalNoise; RawAggSplit = o.RawAggSplit; ParallelObservers = o.ParallelObservers; MeasureLedger = o.MeasureLedger;
            return;
        }
        MeasureLedger = false;
        ModeC = UserModeC && !Off(ModeCEnv);
        StepTiming = On(StepTimingEnv) || (ModeC && !Off(StepTimingEnv));            // 入力前の間の学習に区間計測が要る
        PipelineSensing = On(PipelineSensingEnv) || (ModeC && !Off(PipelineSensingEnv));
        AdaptiveSettle = On(AdaptiveSettleEnv) || (ModeC && !Off(AdaptiveSettleEnv));
        NaturalNoise = On(NaturalNoiseEnv);
        RawAggSplit = On(RawAggSplitEnv);
        ParallelObservers = On(ParallelObserversEnv);
    }

    /// <summary>台帳（ObservationLedger）を使うか: パイプラインか並列観測器のどちらかが ON</summary>
    public static bool LedgerActive => PipelineSensing || ParallelObservers || MeasureLedger;

    public static string StatusText()
    {
        ApplyEffective();
        string F(string n, bool v) => $"{n}={(v ? "ON" : "OFF")}";
        bool any = StepTiming || PipelineSensing || AdaptiveSettle || NaturalNoise || RawAggSplit || ParallelObservers;
        return "Throughput: " + (Override is { } ov ? $"[自動比較 {ov.Label}] " : ModeC ? "[既定 C 高速モード ON（GKK_THROUGHPUT_C=0 か画面の切り替えで OFF）] " : "[C 高速モード OFF] ") + (any ? "" : "OFF（基準・v0.38.0 と同一経路） ") + string.Join(" ", F("step_timing", StepTiming), F("pipeline_sensing", PipelineSensing),
            F("adaptive_settle", AdaptiveSettle), F("natural_noise", NaturalNoise), F("raw_agg_split", RawAggSplit), F("parallel_observers", ParallelObservers));
    }
}
