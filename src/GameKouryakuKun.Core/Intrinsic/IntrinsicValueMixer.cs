using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Observation;

namespace GameKouryakuKun.Core.Intrinsic;

/// <summary>S5 ポートフォリオ成分 ID（docs/s5-intrinsic-active-exp-contract.md）。ゲーム意味禁止。</summary>
public static class IntrinsicComponents
{
    public const string InfoGainPe = "info_gain_pe";
    public const string Controllability = "controllability";
    public const string ReachUnknown = "reach_unknown";
    public const string ReversibilityOptions = "reversibility_options";
    public const string SkillWmLongTerm = "skill_wm_long_term";
    public static readonly string[] All =
    {
        InfoGainPe, Controllability, ReachUnknown, ReversibilityOptions, SkillWmLongTerm,
    };
}

/// <summary>初期均等重み（メタ学習は S7）。</summary>
public static class IntrinsicPortfolioWeights
{
    /// <summary>根拠: §13.1 初期ポートフォリオ。5 成分均等（合計 1）。正解価値ではない。</summary>
    public const double Equal = 0.2;
    public static IReadOnlyDictionary<string, double> Default() =>
        IntrinsicComponents.All.ToDictionary(x => x, _ => Equal, StringComparer.Ordinal);

    /// <summary>S7: Bundle Parameters から正規化。未知キー無視・欠落は残差均等。</summary>
    public static IReadOnlyDictionary<string, double> FromParameters(IReadOnlyDictionary<string, double>? parameters)
    {
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        double known = 0;
        if (parameters != null)
        {
            foreach (var id in IntrinsicComponents.All)
            {
                if (parameters.TryGetValue(id, out var v) && v > 0)
                {
                    raw[id] = v;
                    known += v;
                }
            }
        }
        if (raw.Count == 0) return Default();
        double missing = IntrinsicComponents.All.Count(id => !raw.ContainsKey(id));
        double residual = Math.Max(0, 1.0 - known);
        double fill = missing > 0 ? residual / missing : 0;
        foreach (var id in IntrinsicComponents.All)
            if (!raw.ContainsKey(id)) raw[id] = fill;
        return NormalizeOrDefault(raw);
    }

    public static IReadOnlyDictionary<string, double> NormalizeOrDefault(IReadOnlyDictionary<string, double>? weights)
    {
        if (weights == null || weights.Count == 0) return Default();
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        double sum = 0;
        foreach (var id in IntrinsicComponents.All)
        {
            double v = weights.TryGetValue(id, out var x) ? Math.Max(0, x) : 0;
            d[id] = v;
            sum += v;
        }
        if (sum <= 1e-9) return Default();
        foreach (var id in IntrinsicComponents.All)
            d[id] = d[id] / sum;
        return d;
    }
}

public sealed class IntrinsicActionScore
{
    public string Action { get; init; } = "";
    public double Mixed { get; init; }
    public IReadOnlyDictionary<string, double> Components { get; init; } = new Dictionary<string, double>();
    public string Method { get; init; } = "portfolio_equal_v1";
    public string? Note { get; init; }
}

public sealed class ActiveExperimentProposal
{
    public string Action { get; init; } = "";
    public string Status { get; init; } = "not_applicable"; // proposed | budget_exhausted | blocked | not_applicable
    public double ExpectedInfoGain { get; init; }
    public string? CompeteHypotheses { get; init; }
    public string? Note { get; init; }
}

public sealed class IntrinsicMixerReport
{
    public string Estimator { get; init; } = IntrinsicValueMixer.MixerId;
    public long? Step { get; init; }
    public string Mode { get; init; } = "parallel_log";
    public IReadOnlyList<IntrinsicActionScore> Scores { get; init; } = Array.Empty<IntrinsicActionScore>();
    public string? TopMixedAction { get; init; }
    public ActiveExperimentProposal? Experiment { get; init; }
    public string? Note { get; init; }

    public string SummaryLine()
    {
        var top = Scores.OrderByDescending(s => s.Mixed).Take(3)
            .Select(s => $"{s.Action}:{s.Mixed:0.00}");
        return $"intrinsic_mix mode={Mode} top=[{string.Join(" ", top)}] exp={Experiment?.Status}:{Experiment?.Action}";
    }
}

public sealed record IntrinsicMixInput(
    IReadOnlyList<CandidateScore> Candidates,
    long? Step = null,
    CausalComparisonRecord? Causal = null,
    ShortHorizonForecast? ShortHorizon = null,
    IReadOnlyList<string>? PeTags = null,
    int ExperimentInterruptsUsed = 0,
    int ExperimentBudget = ActiveExperimentBudget.DefaultMaxInterrupts,
    string? SoftInterruptBlock = null,
    IReadOnlyDictionary<string, double>? PortfolioWeights = null);

/// <summary>
/// S5 IntrinsicValueMixer: 既存 CandidateScore を WRAP してポートフォリオ Mixed を付ける。
/// Explorer の点数式は変更しない（並行ログが既定）。
/// </summary>
public static class IntrinsicValueMixer
{
    public const string MixerId = "intrinsic_value_mixer@1";

    public static IntrinsicMixerReport Mix(IntrinsicMixInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var weights = IntrinsicPortfolioWeights.NormalizeOrDefault(input.PortfolioWeights);
        bool custom = input.PortfolioWeights != null;
        var peBoost = input.ShortHorizon?.PredictionMiss == true ? 0.15 : 0.0;   // 根拠: PE miss 時の情報利得代理ブースト（小さい形式補正）
        if (input.PeTags?.Contains(PeRoutingTags.RepresentationCoarse) == true) peBoost = Math.Max(peBoost, 0.1);

        var scores = new List<IntrinsicActionScore>();
        foreach (var c in input.Candidates.Where(x => !x.ExcludedByUser))
        {
            var comp = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [IntrinsicComponents.InfoGainPe] = Clamp01(c.InfoTerm + c.UncTerm + peBoost),
                [IntrinsicComponents.Controllability] = Clamp01(c.Discovery + c.Leave + c.Change + c.Text),
                [IntrinsicComponents.ReachUnknown] = Clamp01(c.Novelty * 0.5 + c.Discovery),
                [IntrinsicComponents.ReversibilityOptions] = Clamp01(c.Reversibility),
                [IntrinsicComponents.SkillWmLongTerm] = 0, // stub
            };
            double mixed = 0;
            foreach (var id in IntrinsicComponents.All)
                mixed += weights[id] * comp[id];
            scores.Add(new IntrinsicActionScore
            {
                Action = c.Action,
                Mixed = Math.Round(mixed, 4),
                Components = comp.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4)),
                Method = custom ? "portfolio_applied_v1" : "portfolio_equal_v1",
                Note = "skill_wm_long_term=not_implemented; not_reward",
            });
        }

        var top = scores.OrderByDescending(s => s.Mixed).ThenBy(s => s.Action, StringComparer.Ordinal).FirstOrDefault();
        var exp = ActiveExperimentPlanner.Propose(input, scores);
        string mode = exp is { Status: "proposed" } ? "soft_experiment_available" : "parallel_log";

        return new IntrinsicMixerReport
        {
            Step = input.Step,
            Mode = mode,
            Scores = scores,
            TopMixedAction = top?.Action,
            Experiment = exp,
            Note = "wrap_candidate_score; explorer_formula_unchanged",
        };
    }

    private static double Clamp01(double x) => Math.Clamp(x, 0, 1);
}

/// <summary>競合仮説を区別する Action を提案（固定キー禁止）。</summary>
public static class ActiveExperimentPlanner
{
    public const string PlannerId = "active_experiment_planner@1";
    /// <summary>根拠: ソフト割込みの最小期待利得代理（低すぎる割込みを避ける形式床）。</summary>
    public const double MinExpectedInfoGain = 0.12;

    public static ActiveExperimentProposal Propose(IntrinsicMixInput input, IReadOnlyList<IntrinsicActionScore> mixed)
    {
        if (!string.IsNullOrEmpty(input.SoftInterruptBlock))
        {
            return new ActiveExperimentProposal
            {
                Status = "blocked",
                Note = $"soft_interrupt_block={input.SoftInterruptBlock}",
            };
        }
        if (input.ExperimentInterruptsUsed >= input.ExperimentBudget)
        {
            return new ActiveExperimentProposal
            {
                Status = "budget_exhausted",
                Note = $"interrupts_used={input.ExperimentInterruptsUsed}/budget={input.ExperimentBudget}",
            };
        }

        var causal = input.Causal;
        bool ambiguous = causal == null
            || causal.Preferred == "undecided"
            || (causal.PeRoutingTags.Contains(PeRoutingTags.RepresentationCoarse));
        if (!ambiguous && causal != null)
        {
            var h0 = causal.Hypotheses.FirstOrDefault(h => h.Id == CausalHypothesisIds.H0Natural);
            var h1 = causal.Hypotheses.FirstOrDefault(h => h.Id == CausalHypothesisIds.H1Immediate);
            if (h0?.Status == "scored" && h1?.Status == "scored"
                && Math.Abs((h0.LogScore ?? 0) - (h1.LogScore ?? 0)) < 0.5)   // 根拠: 対数スコア差が小さい＝競合（形式）
                ambiguous = true;
        }
        if (!ambiguous)
        {
            return new ActiveExperimentProposal
            {
                Status = "not_applicable",
                Note = "causal_not_ambiguous",
            };
        }

        // Pick non-wait candidate maximizing controllability * novelty-ish (discrimination proxy)
        IntrinsicActionScore? best = null;
        double bestGain = 0;
        foreach (var s in mixed)
        {
            if (string.Equals(s.Action, "wait", StringComparison.OrdinalIgnoreCase)) continue;
            var cand = input.Candidates.FirstOrDefault(c => c.Action == s.Action);
            if (cand == null || cand.ExcludedByUser) continue;
            double noveltyFactor = 1.0 / (1 + cand.DirectTries);
            double unc = cand.UncTerm + cand.InfoTerm;
            double ctrl = s.Components.GetValueOrDefault(IntrinsicComponents.Controllability);
            double gain = unc * noveltyFactor * (0.5 + 0.5 * ctrl);
            if (gain > bestGain)
            {
                bestGain = gain;
                best = s;
            }
        }
        if (best == null || bestGain < MinExpectedInfoGain)
        {
            return new ActiveExperimentProposal
            {
                Status = "not_applicable",
                ExpectedInfoGain = Math.Round(bestGain, 4),
                Note = "no_candidate_above_min_gain",
            };
        }

        string compete = causal == null ? "undecided" : $"{causal.Preferred}|tags={string.Join(",", causal.PeRoutingTags)}";
        return new ActiveExperimentProposal
        {
            Action = best.Action,
            Status = "proposed",
            ExpectedInfoGain = Math.Round(bestGain, 4),
            CompeteHypotheses = compete,
            Note = "discriminate_causal_pe; no_fixed_key",
        };
    }
}
