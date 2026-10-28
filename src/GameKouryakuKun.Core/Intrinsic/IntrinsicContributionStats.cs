using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameKouryakuKun.Core.Intrinsic;

/// <summary>
/// S5: ポートフォリオ寄与・能動実験割込みの統計（S7 メタ重みの材料。本スライスは学習しない）。
/// </summary>
public sealed class IntrinsicContributionStats
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _componentChosen = new(StringComparer.Ordinal); // component → times it led when action chosen
    private readonly Dictionary<string, double> _componentSumWhenChosen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _tagBeforeInterrupt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _tagAfterInterrupt = new(StringComparer.Ordinal);
    private long _mixSteps;
    private long _interruptsApplied;
    private long _blockedBudget;
    private long _blockedCooldown;
    private long _blockedGuard;
    private long _blockedWindow;
    private long _aftermathResolved;
    private long _ambiguityFellAfter; // preferred left undecided / coarse tag gone
    private PendingInterrupt? _pending;

    private sealed class PendingInterrupt
    {
        public long Step;
        public string Action = "";
        public string? PreferredBefore;
        public IReadOnlyList<string> TagsBefore = Array.Empty<string>();
    }

    public long MixSteps => _mixSteps;
    public long InterruptsApplied => _interruptsApplied;
    public long BlockedBudget => _blockedBudget;
    public long BlockedCooldown => _blockedCooldown;
    public long BlockedGuard => _blockedGuard;
    public long BlockedWindow => _blockedWindow;
    public long AftermathResolved => _aftermathResolved;
    public long AmbiguityFellAfter => _ambiguityFellAfter;

    public void NoteMixStep()
    {
        lock (_gate) _mixSteps++;
    }

    /// <summary>選ばれた候補の成分寄与（最大成分を 1 票）。</summary>
    public void NoteChosen(IntrinsicActionScore? score)
    {
        if (score == null || score.Components.Count == 0) return;
        lock (_gate)
        {
            var top = score.Components.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
            _componentChosen[top.Key] = _componentChosen.GetValueOrDefault(top.Key) + 1;
            foreach (var (k, v) in score.Components)
                _componentSumWhenChosen[k] = _componentSumWhenChosen.GetValueOrDefault(k) + v;
        }
    }

    public void NoteBlocked(string reason)
    {
        lock (_gate)
        {
            switch (reason)
            {
                case "budget": _blockedBudget++; break;
                case "cooldown": _blockedCooldown++; break;
                case "window": _blockedWindow++; break;
                default: _blockedGuard++; break;
            }
        }
    }

    public void NoteInterruptApplied(long step, string action, string? preferredBefore, IReadOnlyList<string>? tagsBefore)
    {
        lock (_gate)
        {
            _interruptsApplied++;
            _pending = new PendingInterrupt
            {
                Step = step,
                Action = action,
                PreferredBefore = preferredBefore,
                TagsBefore = tagsBefore?.ToList() ?? new List<string>(),
            };
            foreach (var t in _pending.TagsBefore)
                _tagBeforeInterrupt[t] = _tagBeforeInterrupt.GetValueOrDefault(t) + 1;
        }
    }

    /// <summary>割込み後の因果/PE を見て ambiguity が落ちたかを数える（報酬に使わない）。</summary>
    public void ObserveAftermath(long step, string? preferredAfter, IReadOnlyList<string>? tagsAfter, int horizonSteps = ActiveExperimentBudget.AftermathHorizonSteps)
    {
        lock (_gate)
        {
            if (_pending == null) return;
            if (step < _pending.Step + 1) return;
            if (step > _pending.Step + horizonSteps)
            {
                // horizon 超過: 解決扱い（落ちなかった）
                FinishAftermath(preferredAfter, tagsAfter, fell: false);
                return;
            }
            bool fell = preferredAfter is not null and not "undecided"
                && (string.IsNullOrEmpty(_pending.PreferredBefore) || _pending.PreferredBefore == "undecided" || preferredAfter != _pending.PreferredBefore);
            bool coarseGone = _pending.TagsBefore.Contains("representation_coarse")
                && tagsAfter != null && !tagsAfter.Contains("representation_coarse");
            if (fell || coarseGone || step >= _pending.Step + horizonSteps)
                FinishAftermath(preferredAfter, tagsAfter, fell: fell || coarseGone);
        }
    }

    private void FinishAftermath(string? preferredAfter, IReadOnlyList<string>? tagsAfter, bool fell)
    {
        _aftermathResolved++;
        if (fell) _ambiguityFellAfter++;
        if (tagsAfter != null)
            foreach (var t in tagsAfter)
                _tagAfterInterrupt[t] = _tagAfterInterrupt.GetValueOrDefault(t) + 1;
        _pending = null;
        _ = preferredAfter;
    }

    public IReadOnlyDictionary<string, long> ComponentChosenCounts()
    {
        lock (_gate) return new Dictionary<string, long>(_componentChosen);
    }

    public IReadOnlyDictionary<string, double> ComponentSumWhenChosen()
    {
        lock (_gate) return new Dictionary<string, double>(_componentSumWhenChosen);
    }

    public IReadOnlyDictionary<string, long> TagBeforeCounts()
    {
        lock (_gate) return new Dictionary<string, long>(_tagBeforeInterrupt);
    }

    public IReadOnlyDictionary<string, long> TagAfterCounts()
    {
        lock (_gate) return new Dictionary<string, long>(_tagAfterInterrupt);
    }

    public string SummaryLine()
    {
        lock (_gate)
        {
            string comp = string.Join(" ", _componentChosen.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
            return $"intrinsic_stats mix={_mixSteps} interrupts={_interruptsApplied} blocked[b{_blockedBudget}/c{_blockedCooldown}/w{_blockedWindow}/g{_blockedGuard}] " +
                   $"aftermath={_aftermathResolved} fell={_ambiguityFellAfter} top_comp=[{comp}]";
        }
    }

    public string ToJson()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(new
            {
                mix_steps = _mixSteps,
                interrupts_applied = _interruptsApplied,
                blocked_budget = _blockedBudget,
                blocked_cooldown = _blockedCooldown,
                blocked_window = _blockedWindow,
                blocked_guard = _blockedGuard,
                aftermath_resolved = _aftermathResolved,
                ambiguity_fell_after = _ambiguityFellAfter,
                component_chosen = _componentChosen,
                component_sum_when_chosen = _componentSumWhenChosen,
                tag_before = _tagBeforeInterrupt,
                tag_after = _tagAfterInterrupt,
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        }
    }
}

/// <summary>ソフト割込みの予算・冷却・窓（馬鹿プレイ禁止ではない・スパム防止）。</summary>
public static class ActiveExperimentBudget
{
    /// <summary>根拠: 診断セッション規模のソフト割込み上限（§12.3）。</summary>
    public const int DefaultMaxInterrupts = 40;
    /// <summary>根拠: 直近窓内の割込み上限（同一失敗ループの連打防止）。</summary>
    public const int MaxInterruptsPerWindow = 3;
    /// <summary>根拠: 窓の手数（〜数十手の局所スパム検知）。</summary>
    public const int WindowSteps = 50;
    /// <summary>根拠: 割込み後の冷却手数（連続上書き防止。Human/Goal とは独立）。</summary>
    public const int CooldownSteps = 8;
    /// <summary>根拠: 割込み後に因果/PE の変化を見る待ち手数。</summary>
    public const int AftermathHorizonSteps = 12;

    public static string? BlockReason(
        int sessionInterrupts,
        int sessionBudget,
        long step,
        long? lastInterruptStep,
        IReadOnlyList<long> interruptStepsInSession,
        string? lastInterruptAction,
        string? proposedAction)
    {
        if (sessionInterrupts >= sessionBudget) return "budget";
        if (lastInterruptStep is long last && step - last < CooldownSteps) return "cooldown";
        if (lastInterruptAction != null && proposedAction != null
            && string.Equals(lastInterruptAction, proposedAction, StringComparison.Ordinal)
            && lastInterruptStep is long last2 && step - last2 < CooldownSteps * 2)
            return "cooldown"; // same-action spam
        int inWindow = interruptStepsInSession.Count(s => step - s < WindowSteps);
        if (inWindow >= MaxInterruptsPerWindow) return "window";
        return null;
    }
}
