namespace GameKouryakuKun.Core.LongHorizon;

public enum LhLevel { Ok, Warn, Fail, NotTested, Info }

public sealed record LhCheck(string Area, string Name, LhLevel Level, string Detail);

/// <summary>既存ストアから渡す要約（Core は sqlite に直接触れない）。</summary>
public sealed class LongHorizonInput
{
    public string? PeStatsSummary { get; init; }
    public long PeEnqueued { get; init; }
    public long PePending { get; init; }
    public double PeUndecidedRate { get; init; } = -1; // 根拠: 汎用（未計測センチネル。<0 = unknown）
    public double PeCoarseRate { get; init; } = -1; // 根拠: 汎用（未計測センチネル。<0 = unknown）

    public string? IntrinsicStatsSummary { get; init; }
    public long IntrinsicMixSteps { get; init; }
    public long IntrinsicInterrupts { get; init; }
    public long IntrinsicBudget { get; init; } = GameKouryakuKun.Core.Intrinsic.ActiveExperimentBudget.DefaultMaxInterrupts;
    public long IntrinsicBlockedBudget { get; init; }
    public long IntrinsicAmbiguityFell { get; init; }

    public string? RegistrySummary { get; init; }
    public string? ChampionId { get; init; }
    public bool RegistryRollbackAvailable { get; init; }
    public int RegistryCompares { get; init; }
    public int RegistryPromotes { get; init; }
    public int RegistryRollbacks { get; init; }

    public int SoftAdopts { get; init; }
    public int SoftAdoptBudget { get; init; } = GameKouryakuKun.Core.Goals.SoftAdoptBudget.DefaultMaxSoftAdopts;
    public int LiveRoiDeferHints { get; init; } // INFO only
    public int HumanSwitches { get; init; }
    public bool OuterLoopEnabled { get; init; }
    public bool OuterPromoteEnabled { get; init; }
    public bool SupervisorCircuitTripped { get; init; }
    public int SupervisorRecoveries { get; init; }

    public IReadOnlyList<GameKouryakuKun.Core.Growth.GrowthSnapshot>? GrowthSnapshots { get; init; }
    public IReadOnlyList<double>? RecentPeProxies { get; init; } // older → newer
    public bool SaveApiAbsent { get; init; } = true; // contract: agent does not operate saves
}

public sealed class LongHorizonReport
{
    public string BuilderId { get; init; } = LongHorizonHealth.BuilderId;
    public IReadOnlyList<LhCheck> Checks { get; init; } = Array.Empty<LhCheck>();
    public string SummaryLine { get; init; } = "";
    public string? Note { get; init; }
}
