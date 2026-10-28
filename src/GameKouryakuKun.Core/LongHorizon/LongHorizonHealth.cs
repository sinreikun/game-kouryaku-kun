using GameKouryakuKun.Core.Growth;
using GameKouryakuKun.Core.Intrinsic;
using GameKouryakuKun.Core.Goals;

namespace GameKouryakuKun.Core.LongHorizon;

/// <summary>
/// S8: 長期自律の横断 Health（表示・診断のみ。探索・報酬に使わない）。
/// </summary>
public static class LongHorizonHealth
{
    public const string BuilderId = "long_horizon_health@1";
    public const string Area = "長期自律";

    public static LongHorizonReport Build(LongHorizonInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var checks = new List<LhCheck>();

        // --- 学習 ---
        if (input.PeEnqueued <= 0 && string.IsNullOrEmpty(input.PeStatsSummary))
            checks.Add(new(Area, "PE閉ループ", LhLevel.NotTested, "pe-closed-loop データなし（フラグ OFF か未 enqueue）"));
        else
        {
            string peDetail = input.PeStatsSummary ?? $"enqueued={input.PeEnqueued} pending={input.PePending}";
            if (input.PeUndecidedRate >= 0) peDetail += $" undecided={input.PeUndecidedRate:0.00}";
            if (input.PeCoarseRate >= 0) peDetail += $" coarse={input.PeCoarseRate:0.00}";
            checks.Add(new(Area, "PE閉ループ", LhLevel.Info, peDetail));
        }

        if (input.IntrinsicMixSteps <= 0 && string.IsNullOrEmpty(input.IntrinsicStatsSummary))
            checks.Add(new(Area, "Intrinsic寄与", LhLevel.NotTested, "intrinsic-stats なし（Mixer OFF か未 mix）"));
        else
            checks.Add(new(Area, "Intrinsic寄与", LhLevel.Info,
                input.IntrinsicStatsSummary ?? $"mix={input.IntrinsicMixSteps} interrupts={input.IntrinsicInterrupts} fell={input.IntrinsicAmbiguityFell}"));

        // --- Outer ---
        if (string.IsNullOrEmpty(input.ChampionId) && string.IsNullOrEmpty(input.RegistrySummary))
            checks.Add(new(Area, "Outer/Registry", LhLevel.NotTested, "model-registry なし（Outer OFF か未 open）"));
        else
        {
            checks.Add(new(Area, "Outer/Registry", LhLevel.Info,
                input.RegistrySummary ?? $"champ={input.ChampionId} rollback={(input.RegistryRollbackAvailable ? "yes" : "no")} compares={input.RegistryCompares} promotes={input.RegistryPromotes}"));
            if (input.RegistryPromotes > 0 && !input.RegistryRollbackAvailable)
                checks.Add(new(Area, "Champion rollback", LhLevel.Warn, "promote 後に previous が無い（rollback 不能）"));
            else if (input.RegistryRollbackAvailable)
                checks.Add(new(Area, "Champion rollback", LhLevel.Ok, "previous ポインタあり"));
        }

        // --- 安全予算 §29 ---
        checks.AddRange(SafetyBudgetChecks.Evaluate(input));

        // --- 成長スイート ---
        checks.AddRange(GrowthSuite.Evaluate(input));

        // --- システム参照要約 ---
        if (input.SupervisorCircuitTripped)
            checks.Add(new(Area, "Supervisor回路", LhLevel.Warn, $"回路遮断あり・復旧記録 {input.SupervisorRecoveries}"));
        else if (input.SupervisorRecoveries > 0)
            checks.Add(new(Area, "Supervisor回路", LhLevel.Info, $"復旧 {input.SupervisorRecoveries}（遮断なし）"));
        else
            checks.Add(new(Area, "Supervisor回路", LhLevel.NotTested, "この入力に Supervisor 復旧なし"));

        string summary =
            $"long_horizon checks={checks.Count} warn={checks.Count(c => c.Level == LhLevel.Warn)} fail={checks.Count(c => c.Level == LhLevel.Fail)} " +
            $"pe={input.PeEnqueued} mix={input.IntrinsicMixSteps} champ={input.ChampionId ?? "(none)"}";
        return new LongHorizonReport
        {
            Checks = checks,
            SummaryLine = summary,
            Note = "display_only; not_reward; stupid_play_not_fail",
        };
    }
}
