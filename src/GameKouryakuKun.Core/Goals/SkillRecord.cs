using GameKouryakuKun.Core.Exploration;

namespace GameKouryakuKun.Core.Goals;

/// <summary>S6 Skill（§16）。浅い階層のみ。</summary>
public sealed class SkillRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public ObservationPredicate? PreCondition { get; init; }
    public IReadOnlyList<string> Body { get; init; } = Array.Empty<string>();
    public ObservationPredicate? PostCondition { get; init; }
    /// <summary>効果モデルの信頼度 0..1（再現性 WRAP）。</summary>
    public double EffectConfidence { get; init; }
    public string EffectSummary { get; init; } = "";
    public string Origin { get; init; } = "sequence"; // sequence | goal | active_experiment | human_transition
    public string Status { get; init; } = "proposed"; // proposed | active | dormant
    public string? Note { get; init; }
    /// <summary>対応する ActionSequence ID（並び学習 KEEP）。</summary>
    public int? SequenceId { get; init; }

    public string SummaryLine() =>
        $"skill #{Id} {Name} body={Body.Count} conf={EffectConfidence:0.00} origin={Origin} status={Status}";
}

/// <summary>ActionSequence / Goal 区間を SkillRecord 表面へ昇格（並び学習ロジックは触らない）。</summary>
public sealed class SkillMiner
{
    /// <summary>根拠: ActionSequenceMiner.MinReproducibility と同型の昇格床。</summary>
    public const double MinEffectConfidence = 0.6;
    /// <summary>根拠: 浅いライブラリ上限（同時表示・並行ログ用。記憶上限ではない）。</summary>
    public const int MaxTracked = 64;

    private readonly Dictionary<string, SkillRecord> _byKey = new(StringComparer.Ordinal);
    private int _nextId = 1;

    public IReadOnlyCollection<SkillRecord> Skills => _byKey.Values;
    public IEnumerable<SkillRecord> Active => _byKey.Values.Where(s => s.Status == "active");

    public static string Key(IEnumerable<string> body) => string.Join(" > ", body);

    public SkillRecord? PromoteFromSequence(ActionSequenceCandidate seq)
    {
        ArgumentNullException.ThrowIfNull(seq);
        if (seq.Steps.Count == 0) return null;
        string key = Key(seq.Steps);
        if (_byKey.TryGetValue(key, out var existing))
        {
            // refresh confidence
            var refreshed = Clone(existing, seq.Reproducibility, seq.Status == SequenceStatus.Active ? "active" : "dormant", seq.Origin);
            _byKey[key] = refreshed;
            return refreshed;
        }
        if (_byKey.Count >= MaxTracked) return null;
        if (seq.Reproducibility < MinEffectConfidence && seq.Origin != "goal") return null;

        var skill = new SkillRecord
        {
            Id = _nextId++,
            Name = $"Skill #{_nextId - 1}",
            PreCondition = new ObservationPredicate
            {
                Id = ObservationPredicateIds.FrontierReach,
                Status = string.IsNullOrEmpty(seq.Start) ? "not_enough_evidence" : "scored",
                CoarseNode = seq.Start,
                Note = "pre_from_sequence_start; coarse_index",
            },
            Body = seq.Steps.ToList(),
            PostCondition = new ObservationPredicate
            {
                Id = ObservationPredicateIds.FrontierReach,
                Status = string.IsNullOrEmpty(seq.End) ? "not_enough_evidence" : "scored",
                CoarseNode = seq.End,
                Note = "post_from_sequence_end; coarse_index",
            },
            EffectConfidence = Math.Round(Math.Clamp(seq.Reproducibility, 0, 1), 4),
            EffectSummary = $"start={seq.Start} end={seq.End} repro={seq.Reproducibility:0.00} recent={seq.RecentReproducibility:0.00}",
            Origin = MapOrigin(seq.Origin),
            Status = seq.Status == SequenceStatus.Active ? "active" : "dormant",
            SequenceId = seq.Id,
            Note = "promote_action_sequence; no_human_imitation_reward; shallow_only",
        };
        _byKey[key] = skill;
        return skill;
    }

    public SkillRecord? PromoteFromGoalSegment(IReadOnlyList<string> steps, string start, string end, int achieved, int attempts, string origin = "goal")
    {
        if (steps.Count == 0) return null;
        string key = Key(steps);
        double conf = attempts <= 0 ? 0 : (achieved + 1.0) / (attempts + 2.0);   // 根拠: ラプラス平滑の再現代理
        if (_byKey.TryGetValue(key, out var existing))
        {
            var refreshed = Clone(existing, conf, "active", origin);
            _byKey[key] = refreshed;
            return refreshed;
        }
        if (_byKey.Count >= MaxTracked) return null;
        if (conf < MinEffectConfidence) return null;
        var skill = new SkillRecord
        {
            Id = _nextId++,
            Name = $"Skill #{_nextId - 1}",
            PreCondition = new ObservationPredicate
            {
                Id = ObservationPredicateIds.FrontierReach,
                Status = "scored",
                CoarseNode = start,
                Note = "pre_from_goal_segment",
            },
            Body = steps.ToList(),
            PostCondition = new ObservationPredicate
            {
                Id = ObservationPredicateIds.FrontierReach,
                Status = "scored",
                CoarseNode = end,
                Note = "post_from_goal_segment",
            },
            EffectConfidence = Math.Round(Math.Clamp(conf, 0, 1), 4),
            EffectSummary = $"goal_segment achieved={achieved}/{attempts}",
            Origin = MapOrigin(origin),
            Status = "active",
            Note = "promote_goal_segment; sequence_learning_kept_behind",
        };
        _byKey[key] = skill;
        return skill;
    }

    public IReadOnlyList<SkillRecord> SyncFromMiner(ActionSequenceMiner miner)
    {
        var added = new List<SkillRecord>();
        foreach (var c in miner.Candidates)
        {
            string key = Key(c.Steps);
            bool wasNew = !_byKey.ContainsKey(key);
            var s = PromoteFromSequence(c);
            if (s != null && wasNew) added.Add(s);
        }
        return added;
    }

    public string SummaryLine() =>
        $"skill_lib n={_byKey.Count} active={Active.Count()} top=[{string.Join(" ", Active.Take(3).Select(s => s.Name + ":" + s.EffectConfidence.ToString("0.00")))}]";

    private static string MapOrigin(string o) => o switch
    {
        "goal" => "goal",
        "human" => "human_transition",
        "active_experiment" => "active_experiment",
        _ => "sequence",
    };

    private static SkillRecord Clone(SkillRecord e, double conf, string status, string origin) => new()
    {
        Id = e.Id,
        Name = e.Name,
        PreCondition = e.PreCondition,
        Body = e.Body,
        PostCondition = e.PostCondition,
        EffectConfidence = Math.Round(Math.Clamp(conf, 0, 1), 4),
        EffectSummary = e.EffectSummary,
        Origin = MapOrigin(origin),
        Status = status,
        SequenceId = e.SequenceId,
        Note = e.Note,
    };
}
