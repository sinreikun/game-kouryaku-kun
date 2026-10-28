using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameKouryakuKun.Core.Causality;

/// <summary>
/// S3 PE 閉ループ（docs/s3-causal-wm-contract.md）: タグ→文脈特徴候補キュー。
/// WM 条件付けへの自動採用はしない。S4 で RecommendedStatus（重要度推奨）を記録可。
/// </summary>
public sealed class PeClosedLoopRecorder
{
    public const int MaxPending = 500;   // 根拠: 診断キュー肥大化防止（古い pending は Dropped に数える）

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _tagCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _preferredCounts = new(StringComparer.Ordinal);
    private readonly List<PeFeatureCandidate> _pending = new();
    private long _nextId = 1;
    private long _enqueued;
    private long _dropped;

    public long Enqueued => _enqueued;
    public long Dropped => _dropped;
    public int PendingCount { get { lock (_gate) return _pending.Count(c => c.Status == PeCandidateStatus.Pending); } }

    /// <summary>比較結果を観測: カウンタ更新 + タグごとの候補 enqueue。</summary>
    public IReadOnlyList<PeFeatureCandidate> Observe(CausalComparisonRecord rec, long? runId = null, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(rec);
        var now = (utcNow ?? (() => DateTime.UtcNow))().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var created = new List<PeFeatureCandidate>();
        lock (_gate)
        {
            var pref = string.IsNullOrEmpty(rec.Preferred) ? "undecided" : rec.Preferred;
            _preferredCounts[pref] = _preferredCounts.GetValueOrDefault(pref) + 1;

            foreach (var tag in rec.PeRoutingTags.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (tag.Contains("キャ", StringComparison.Ordinal) || tag.Contains("character", StringComparison.OrdinalIgnoreCase))
                    continue; // belt: never enqueue meaning-ish tags
                _tagCounts[tag] = _tagCounts.GetValueOrDefault(tag) + 1;

                var evidence = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["tag"] = tag,
                    ["preferred"] = pref,
                    ["step"] = rec.Step,
                    ["action"] = rec.Action,
                    ["sources"] = rec.EvidenceSources.ToList(),
                    ["hypotheses"] = rec.Hypotheses.Select(h => new Dictionary<string, object?>
                    {
                        ["id"] = h.Id, ["status"] = h.Status, ["log_bf"] = h.LogBf, ["log_score"] = h.LogScore,
                    }).ToList(),
                    ["note"] = "pe_routing_record_only; wm_accept_stub",
                }, PeFeatureCandidate.JsonOpts);

                while (_pending.Count(c => c.Status == PeCandidateStatus.Pending) >= MaxPending)
                {
                    var oldest = _pending.FirstOrDefault(c => c.Status == PeCandidateStatus.Pending);
                    if (oldest == null) break;
                    oldest.Status = PeCandidateStatus.Rejected;
                    oldest.Detail = "dropped_queue_cap";
                    _dropped++;
                }

                var cand = new PeFeatureCandidate
                {
                    CandidateId = _nextId++,
                    CreatedAt = now,
                    Tag = tag,
                    EvidenceJson = evidence,
                    Status = PeCandidateStatus.Pending,
                    Step = rec.Step,
                    PreferredHypothesis = pref,
                    RunId = runId,
                };
                _pending.Add(cand);
                _enqueued++;
                created.Add(cand);
            }
        }
        return created;
    }


    /// <summary>
    /// S4: 重要度スコアから RecommendedStatus を書く。Status は変えない（WM 自動拡大禁止）。
    /// </summary>
    public int ApplyImportanceRecommendations(
        IEnumerable<GameKouryakuKun.Core.Importance.FeatureImportanceRecord> features,
        double acceptMin,
        double rejectMax)
    {
        ArgumentNullException.ThrowIfNull(features);
        var byTag = new Dictionary<string, GameKouryakuKun.Core.Importance.FeatureImportanceRecord>(StringComparer.Ordinal);
        foreach (var f in features.Where(x => x.Status == "scored" && x.FeatureId.StartsWith("pe:", StringComparison.Ordinal)))
        {
            var tag = f.FeatureId["pe:".Length..];
            if (!byTag.TryGetValue(tag, out var cur) || f.Score > cur.Score) byTag[tag] = f;
        }
        // ctx feature can boost representation_coarse / reduces_with_history
        var ctx = features.FirstOrDefault(x => x.FeatureId == "ctx:local_context" && x.Status == "scored");
        int n = 0;
        lock (_gate)
        {
            foreach (var c in _pending.Where(x => x.Status == PeCandidateStatus.Pending))
            {
                double? score = null;
                string? method = null;
                if (byTag.TryGetValue(c.Tag, out var fr))
                {
                    score = fr.Score;
                    method = fr.Method;
                }
                if (ctx != null && (c.Tag == PeRoutingTags.RepresentationCoarse || c.Tag == PeRoutingTags.ReducesWithHistory))
                {
                    score = Math.Max(score ?? 0, ctx.Score);
                    method = method == null ? ctx.Method : method + "+" + ctx.Method;
                }
                if (score == null) continue;
                c.Importance = Math.Round(score.Value, 4);
                c.ImportanceMethod = method;
                if (score.Value >= acceptMin) c.RecommendedStatus = PeCandidateStatus.Accepted;
                else if (score.Value <= rejectMax) c.RecommendedStatus = PeCandidateStatus.Rejected;
                else c.RecommendedStatus = null;
                c.Detail = (c.Detail == null ? "" : c.Detail + ";") + "s4_importance_recommend";
                n++;
            }
        }
        return n;
    }

    /// <summary>WM 採用は明示 API: status だけ変える（推奨とは別）。</summary>
    public bool TrySetStatus(long candidateId, string status, string? detail = null)
    {
        if (status is not (PeCandidateStatus.Pending or PeCandidateStatus.Accepted or PeCandidateStatus.Rejected))
            return false;
        lock (_gate)
        {
            var c = _pending.FirstOrDefault(x => x.CandidateId == candidateId);
            if (c == null) return false;
            c.Status = status;
            c.Detail = detail ?? c.Detail;
            return true;
        }
    }

    public IReadOnlyDictionary<string, long> TagCounts()
    {
        lock (_gate) return new Dictionary<string, long>(_tagCounts);
    }

    public IReadOnlyDictionary<string, long> PreferredCounts()
    {
        lock (_gate) return new Dictionary<string, long>(_preferredCounts);
    }

    public IReadOnlyList<PeFeatureCandidate> Snapshot(int limit = 100)
    {
        lock (_gate) return _pending.TakeLast(Math.Max(0, limit)).ToList();
    }

    public string SummaryLine()
    {
        lock (_gate)
        {
            string Tags = string.Join(" ", _tagCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
            string Pref = string.Join(" ", _preferredCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
            int pend = _pending.Count(c => c.Status == PeCandidateStatus.Pending);
            return $"pe_closed_loop tags=[{Tags}] preferred=[{Pref}] pending={pend} enqueued={_enqueued} dropped={_dropped}";
        }
    }

    public IReadOnlyList<string> DiagnosticLines()
    {
        lock (_gate)
        {
            var lines = new List<string>
            {
                SummaryLine(),
                $"pe_feature_candidates pending={_pending.Count(c => c.Status == PeCandidateStatus.Pending)} accepted={_pending.Count(c => c.Status == PeCandidateStatus.Accepted)} rejected={_pending.Count(c => c.Status == PeCandidateStatus.Rejected)}",
            };
            foreach (var c in _pending.Where(x => x.Status == PeCandidateStatus.Pending).TakeLast(5))
                lines.Add($"pe_candidate #{c.CandidateId} tag={c.Tag} step={c.Step} status={c.Status}");
            return lines;
        }
    }
}

public static class PeCandidateStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>文脈特徴候補。意味ラベル禁止。Accept しても本スライスでは WM 条件付けを広げない。</summary>
public sealed class PeFeatureCandidate
{
    public long CandidateId { get; init; }
    public string CreatedAt { get; init; } = "";
    public string Tag { get; init; } = "";
    public string EvidenceJson { get; init; } = "{}";
    public string Status { get; set; } = PeCandidateStatus.Pending;
    public long? Step { get; init; }
    public string? PreferredHypothesis { get; init; }
    public long? RunId { get; init; }
    public string? Detail { get; set; }
    /// <summary>S4: 重要度に基づく推奨。Status（実 WM 採用）とは独立。</summary>
    public string? RecommendedStatus { get; set; }
    public double? Importance { get; set; }
    public string? ImportanceMethod { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
