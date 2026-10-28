using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Observation;

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
    public string Status { get; init; } = "not_applicable"; // parallel_log | soft_adopt_available | not_applicable | abandon_hint | replan_hint | blocked
    public IReadOnlyList<PlanStep> Steps { get; init; } = Array.Empty<PlanStep>();
    public string? FirstAction { get; init; }
    public int? ExpandedSkillId { get; init; }
    public string? Note { get; init; }
    public bool WmLookaheadUsed { get; init; }
    public string? SoftAdoptBlock { get; init; }
    public string? WmLookaheadStatus { get; init; } // observed | not_observed | partial | unused

    public string SummaryLine() =>
        $"plan status={Status} first={FirstAction} skill={ExpandedSkillId} steps={Steps.Count} wm={WmLookaheadStatus ?? (WmLookaheadUsed ? "yes" : "stub")}";
}

public sealed record PlanProposeInput(
    ObservationGoal? Goal,
    IEnumerable<SkillRecord> Skills,
    string? CoarseCurrent = null,
    string? LegacyNextAction = null,
    ShortHorizonForecast? ShortHorizon = null,
    string? SoftAdoptBlock = null);

/// <summary>
/// S6 Planner: Skill マクロ展開 + PredictiveWmShortHorizon 短時間効果チェック。
/// ライブ差し替えは危険なため並行提案が主。soft adopt は呼び出し側フラグ+予算。
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
        string? legacyNextAction = null,
        ShortHorizonForecast? shortHorizon = null,
        string? softAdoptBlock = null) =>
        Propose(new PlanProposeInput(goal, skills, coarseCurrent, legacyNextAction, shortHorizon, softAdoptBlock));

    public static PlanProposal Propose(PlanProposeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Goal == null)
        {
            return new PlanProposal
            {
                Status = "not_applicable",
                Note = "no_observation_goal",
                WmLookaheadUsed = false,
                WmLookaheadStatus = "unused",
            };
        }

        var (wmUsed, wmStatus, wmNote, wmOk) = ReadWm(input.ShortHorizon, input.LegacyNextAction);

        if (!string.IsNullOrEmpty(input.SoftAdoptBlock))
        {
            // still propose parallel plan but mark blocked for soft adopt
        }

        var skillList = input.Skills.Where(s => s.Status == "active" && s.Body.Count > 0).ToList();
        SkillRecord? best = null;
        double bestScore = 0;
        string? target = input.Goal.Predicate.CoarseNode ?? input.Goal.LegacyTarget;
        foreach (var s in skillList)
        {
            double score = s.EffectConfidence;
            if (input.CoarseCurrent != null && s.PreCondition?.CoarseNode == input.CoarseCurrent) score += 0.15;   // 根拠: 事前条件一致の形式ブースト
            if (target != null && s.PostCondition?.CoarseNode == target) score += 0.2;
            if (wmOk == true) score += 0.05;   // 根拠: 短時間 WM が miss でないときの軽いブースト
            if (wmOk == false) score -= 0.15;  // 根拠: PredictionMiss 時はマクロ採用を抑える
            if (score > bestScore) { bestScore = score; best = s; }
        }

        var steps = new List<PlanStep>();
        if (best != null && best.EffectConfidence >= MinSoftAdoptConfidence
            && (input.CoarseCurrent == null || best.PreCondition?.CoarseNode == null || best.PreCondition.CoarseNode == input.CoarseCurrent)
            && wmOk != false)
        {
            steps.Add(new PlanStep
            {
                Action = best.Body[0],
                CoarseNode = input.CoarseCurrent,
                Kind = "skill_macro",
                SkillId = best.Id,
            });
            for (int i = 1; i < best.Body.Count; i++)
                steps.Add(new PlanStep { Action = best.Body[i], Kind = "skill_macro", SkillId = best.Id });

            bool differs = input.LegacyNextAction != null
                && !string.Equals(input.LegacyNextAction, best.Body[0], StringComparison.Ordinal)
                && !string.Equals(input.LegacyNextAction, best.Name, StringComparison.Ordinal);

            string status = !string.IsNullOrEmpty(input.SoftAdoptBlock) ? "blocked"
                : differs ? "soft_adopt_available" : "parallel_log";

            return new PlanProposal
            {
                Status = status,
                Steps = steps,
                FirstAction = best.Body[0],
                ExpandedSkillId = best.Id,
                Note = $"expand_skill:{best.Name}; {wmNote}; no_supervisor_glue"
                    + (input.SoftAdoptBlock != null ? $"; soft_block={input.SoftAdoptBlock}" : ""),
                WmLookaheadUsed = wmUsed,
                WmLookaheadStatus = wmStatus,
                SoftAdoptBlock = input.SoftAdoptBlock,
            };
        }

        if (!string.IsNullOrEmpty(input.LegacyNextAction))
        {
            steps.Add(new PlanStep { Action = input.LegacyNextAction, CoarseNode = input.CoarseCurrent, Kind = "primitive" });
            return new PlanProposal
            {
                Status = !string.IsNullOrEmpty(input.SoftAdoptBlock) && best != null ? "blocked" : "parallel_log",
                Steps = steps,
                FirstAction = input.LegacyNextAction,
                Note = $"mirror_selfgoals_next; {wmNote}"
                    + (wmOk == false && best != null ? "; skill_suppressed_wm_miss" : ""),
                WmLookaheadUsed = wmUsed,
                WmLookaheadStatus = wmStatus,
                SoftAdoptBlock = input.SoftAdoptBlock,
            };
        }

        return new PlanProposal
        {
            Status = "not_applicable",
            Note = $"no_skill_and_no_legacy_next; {wmNote}",
            WmLookaheadUsed = wmUsed,
            WmLookaheadStatus = wmStatus,
            SoftAdoptBlock = input.SoftAdoptBlock,
        };
    }

    private static (bool Used, string Status, string Note, bool? Ok) ReadWm(ShortHorizonForecast? sh, string? actionHint)
    {
        if (sh == null)
            return (false, "unused", "wm_lookahead=not_available", null);
        if (sh.Status == LayerStatus.NotObserved || sh.Status == LayerStatus.NotImplemented)
            return (false, "not_observed", $"wm_lookahead=partial; sh_status={sh.Status}", null);
        // Observed: use PredictionMiss as effect check for proposed first action
        bool miss = sh.PredictionMiss;
        string note = miss
            ? "wm_lookahead=observed; effect_check=prediction_miss"
            : "wm_lookahead=observed; effect_check=ok_or_unchecked";
        if (sh.ContextHelpEvents > 0) note += $"; ctx_helps={sh.ContextHelpEvents}";
        return (true, miss ? "partial" : "observed", note, miss ? false : true);
    }

    /// <summary>計画失敗ヒント（SelfGoals の abandon/replan と並記。実行区切りにしない）。</summary>
    public static PlanProposal FailureHint(string kind, string reason) => new()
    {
        Status = kind is "abandon" ? "abandon_hint" : "replan_hint",
        Note = $"{kind}:{reason}; plan_not_run_cut; no_supervisor_gap_glue",
        WmLookaheadUsed = false,
        WmLookaheadStatus = "unused",
    };
}
