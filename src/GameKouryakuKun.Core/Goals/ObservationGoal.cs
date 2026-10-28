namespace GameKouryakuKun.Core.Goals;

/// <summary>S6: 観測言語の形式述語 ID（docs/s6-goal-plan-skill-contract.md）。ゲーム意味禁止。</summary>
public static class ObservationPredicateIds
{
    public const string TrackRelationReproduce = "track_relation_reproduce";
    public const string NumberSeriesChangeTest = "number_series_change_test";
    public const string HighUncertaintyRetest = "high_uncertainty_retest";
    public const string FrontierReach = "frontier_reach";
    public static readonly string[] All =
    {
        TrackRelationReproduce, NumberSeriesChangeTest, HighUncertaintyRetest, FrontierReach,
    };
}

/// <summary>観測述語（達成条件の原子）。State ID は coarse_index 任意。</summary>
public sealed class ObservationPredicate
{
    public string Id { get; init; } = "";
    /// <summary>scored / stub / not_enough_evidence</summary>
    public string Status { get; init; } = "stub";
    public string? CoarseNode { get; init; }
    public string? Action { get; init; }
    public string? Note { get; init; }
    public IReadOnlyDictionary<string, string>? Args { get; init; }
}

/// <summary>成功時に期待する学習/到達価値代理（達成カウントではない）。</summary>
public sealed class GoalExpectedValue
{
    /// <summary>根拠: §14.3 / §13 — PE 減少代理（0..1 形式スケール）。</summary>
    public double PeReduction { get; init; }
    /// <summary>根拠: 制御可能性代理（既存 Unc/Control の WRAP）。</summary>
    public double Controllability { get; init; }
    /// <summary>根拠: 到達後 option 増加代理。</summary>
    public double OptionsGain { get; init; }
    public double Combined => Math.Clamp(0.5 * PeReduction + 0.3 * Controllability + 0.2 * OptionsGain, 0, 1);
    public string Method { get; init; } = "expected_value_proxy_v1";
}

/// <summary>S6 ObservationGoal（SelfGoal と並存。旧経路は削除しない）。</summary>
public sealed class ObservationGoal
{
    public long Id { get; init; }
    public ObservationPredicate Predicate { get; init; } = new();
    public GoalExpectedValue ExpectedValue { get; init; } = new();
    public string Origin { get; init; } = "";
    public string? LegacyKind { get; init; } // reach | retest | null
    public string? LegacyTarget { get; init; }
    public string Status { get; init; } = "proposed"; // proposed | active | achieved | failed | abandoned | parallel_log
    public string? Note { get; init; }

    public string SummaryLine() =>
        $"obs_goal id={Id} pred={Predicate.Id}:{Predicate.Status} value={ExpectedValue.Combined:0.00} legacy={LegacyKind}:{LegacyTarget} status={Status}";
}

/// <summary>SelfGoal → ObservationGoal の WRAP（破壊的置換ではない）。</summary>
public static class ObservationGoalBridge
{
    public static ObservationGoal FromSelfGoal(SelfGoal g, long? idOverride = null)
    {
        ArgumentNullException.ThrowIfNull(g);
        bool retest = g.Kind == GoalKind.Retest;
        var pred = new ObservationPredicate
        {
            Id = retest ? ObservationPredicateIds.HighUncertaintyRetest : ObservationPredicateIds.FrontierReach,
            Status = "scored",
            CoarseNode = g.Target,
            Action = g.TargetAction,
            Note = retest
                ? "wrap_selfgoal_retest; state_id=coarse_index"
                : "wrap_selfgoal_reach; state_id=coarse_index; prefer_bundle_later",
            Args = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["legacy_kind"] = retest ? "retest" : "reach",
                ["budget"] = g.Budget.ToString(),
            },
        };
        // 価値は不確かさ減りの見込み（達成数ではない）
        double pe = Math.Clamp(g.UncBefore, 0, 1);
        double ctrl = Math.Clamp(g.PlanConfidence, 0, 1);
        double opt = Math.Clamp(g.Subgoals.Count / 4.0, 0, 1);   // 根拠: 小目的数の粗い option 代理（形式床）
        return new ObservationGoal
        {
            Id = idOverride ?? g.Id,
            Predicate = pred,
            ExpectedValue = new GoalExpectedValue
            {
                PeReduction = Math.Round(pe, 4),
                Controllability = Math.Round(ctrl, 4),
                OptionsGain = Math.Round(opt, 4),
            },
            Origin = g.Origin,
            LegacyKind = retest ? "retest" : "reach",
            LegacyTarget = g.Target,
            Status = "parallel_log",
            Note = "bridge_from_selfgoal; not_reward_counts",
        };
    }

    /// <summary>形式述語のスタブ提案（証拠が無いときは not_enough_evidence）。</summary>
    public static ObservationGoal StubFormal(string predicateId, string? coarseNode = null, string? note = null) =>
        new()
        {
            Id = 0,
            Predicate = new ObservationPredicate
            {
                Id = predicateId,
                Status = "not_enough_evidence",
                CoarseNode = coarseNode,
                Note = note ?? "formal_stub; await_bundle_evidence",
            },
            ExpectedValue = new GoalExpectedValue(),
            Origin = "formal_stub",
            Status = "proposed",
            Note = "track/number_series need Structured/Bundle evidence",
        };
}
