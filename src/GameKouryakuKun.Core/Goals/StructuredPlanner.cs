namespace GameKouryakuKun.Core.Goals;

/// <summary>計画 1 ステップ（粗い node 索引可）。</summary>
public sealed class PlanStep
{
    public string Action { get; init; } = "";
    public string? CoarseNode { get; init; }
    public string Kind { get; init; } = "primitive"; // primitive | skill_macro
    public int? SkillId { get; init; }
}

public sealed class PlanProposal
{
    public string Status { get; init; } = "not_applicable"; // parallel_log | soft_adopt_available | not_applicable | abandon_hint | replan_hint
    public IReadOnlyList<PlanStep> Steps { get; init; } = Array.Empty<PlanStep>();
    public string? FirstAction { get; init; }
    public int? ExpandedSkillId { get; init; }
    public string? Note { get; init; }
    public bool WmLookaheadUsed { get; init; }

    public string SummaryLine() =>
        $"plan status={Status} first={FirstAction} skill={ExpandedSkillId} steps={Steps.Count} wm={(WmLookaheadUsed ? "yes" : "stub")}";
}

/// <summary>
/// S6 薄い Planner: Skill マクロ展開 + WM lookahead stub。
/// ライブ差し替えは危険なため並行提案が主。soft adopt は呼び出し側フラグ判断。
/// </summary>
public static class StructuredPlanner
{
    public const string PlannerId = "structured_planner@1";
    /// <summary>根拠: soft adopt 候補にする Skill 効果信頼度の形式床。</summary>
    public const double MinSoftAdoptConfidence = 0.7;

    public static PlanProposal Propose(
        ObservationGoal? goal,
        IEnumerable<SkillRecord> skills,
        string? coarseCurrent,
        string? legacyNextAction = null)
    {
        if (goal == null)
        {
            return new PlanProposal
            {
                Status = "not_applicable",
                Note = "no_observation_goal",
                WmLookaheadUsed = false,
            };
        }

        var skillList = skills.Where(s => s.Status == "active" && s.Body.Count > 0).ToList();
        // Prefer skill whose pre matches coarse current and post toward goal target
        SkillRecord? best = null;
        double bestScore = 0;
        string? target = goal.Predicate.CoarseNode ?? goal.LegacyTarget;
        foreach (var s in skillList)
        {
            double score = s.EffectConfidence;
            if (coarseCurrent != null && s.PreCondition?.CoarseNode == coarseCurrent) score += 0.15;   // 根拠: 事前条件一致の形式ブースト
            if (target != null && s.PostCondition?.CoarseNode == target) score += 0.2;
            if (score > bestScore) { bestScore = score; best = s; }
        }

        var steps = new List<PlanStep>();
        if (best != null && best.EffectConfidence >= MinSoftAdoptConfidence
            && (coarseCurrent == null || best.PreCondition?.CoarseNode == null || best.PreCondition.CoarseNode == coarseCurrent))
        {
            // Expand skill as one macro then (optionally) remainder stub
            steps.Add(new PlanStep
            {
                Action = best.Body[0],
                CoarseNode = coarseCurrent,
                Kind = "skill_macro",
                SkillId = best.Id,
            });
            for (int i = 1; i < best.Body.Count; i++)
                steps.Add(new PlanStep { Action = best.Body[i], Kind = "skill_macro", SkillId = best.Id });

            bool differs = legacyNextAction != null
                && !string.Equals(legacyNextAction, best.Body[0], StringComparison.Ordinal)
                && !string.Equals(legacyNextAction, best.Name, StringComparison.Ordinal);

            return new PlanProposal
            {
                Status = differs ? "soft_adopt_available" : "parallel_log",
                Steps = steps,
                FirstAction = best.Body[0],
                ExpandedSkillId = best.Id,
                Note = $"expand_skill:{best.Name}; wm_lookahead=not_implemented; no_supervisor_glue",
                WmLookaheadUsed = false,
            };
        }

        // Fallback: mirror legacy next action as parallel log
        if (!string.IsNullOrEmpty(legacyNextAction))
        {
            steps.Add(new PlanStep { Action = legacyNextAction, CoarseNode = coarseCurrent, Kind = "primitive" });
            return new PlanProposal
            {
                Status = "parallel_log",
                Steps = steps,
                FirstAction = legacyNextAction,
                Note = "mirror_selfgoals_next; wm_lookahead=not_implemented",
                WmLookaheadUsed = false,
            };
        }

        return new PlanProposal
        {
            Status = "not_applicable",
            Note = "no_skill_and_no_legacy_next; wm_lookahead=not_implemented",
            WmLookaheadUsed = false,
        };
    }

    /// <summary>計画失敗ヒント（SelfGoals の abandon/replan と並記。実行区切りにしない）。</summary>
    public static PlanProposal FailureHint(string kind, string reason) => new()
    {
        Status = kind is "abandon" ? "abandon_hint" : "replan_hint",
        Note = $"{kind}:{reason}; plan_not_run_cut; no_supervisor_gap_glue",
        WmLookaheadUsed = false,
    };
}
