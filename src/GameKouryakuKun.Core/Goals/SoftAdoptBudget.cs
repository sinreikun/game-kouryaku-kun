namespace GameKouryakuKun.Core.Goals;

/// <summary>S6 soft adopt 予算・冷却・復旧ギャップ（Human 上書き禁止は呼び出し側）。</summary>
public static class SoftAdoptBudget
{
    /// <summary>根拠: 診断セッション規模の soft adopt 上限（S5 interrupt より控えめ）。</summary>
    public const int DefaultMaxSoftAdopts = 20;
    /// <summary>根拠: 直近窓内の soft adopt 上限（連打防止）。</summary>
    public const int MaxSoftAdoptsPerWindow = 2;
    /// <summary>根拠: 窓の手数。</summary>
    public const int WindowSteps = 50;
    /// <summary>根拠: soft adopt 後の冷却手数。</summary>
    public const int CooldownSteps = 10;
    /// <summary>根拠: 復旧観測の後、計画をまたがない冷却手数。</summary>
    public const int RecoveryGapCooldownSteps = 15;

    public static string? BlockReason(
        int sessionSoftAdopts,
        int sessionBudget,
        long step,
        long? lastSoftAdoptStep,
        IReadOnlyList<long> softAdoptSteps,
        long? lastRecoveryStep,
        bool humanControl)
    {
        if (humanControl) return "human";
        if (sessionSoftAdopts >= sessionBudget) return "budget";
        if (lastRecoveryStep is long rec && step - rec < RecoveryGapCooldownSteps)
            return "recovery_gap";
        if (lastSoftAdoptStep is long last && step - last < CooldownSteps) return "cooldown";
        int inWindow = softAdoptSteps.Count(s => step - s < WindowSteps);
        if (inWindow >= MaxSoftAdoptsPerWindow) return "window";
        return null;
    }
}
