using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Observation;

namespace GameKouryakuKun.Core.Causality;

/// <summary>
/// S3 Predictive WM 短時間層（docs/s3-causal-wm-contract.md）。
/// 既存 LocalEventModel / TransitionModel / EntityExperience の予測をラップするだけ（数式・学習は変えない）。
/// </summary>
public static class PredictiveWmShortHorizon
{
    public const string SurfaceId = "predictive_wm_short_horizon@1";
    /// <summary>根拠: ln2。LocalEventModel の持続外れ（平均驚き &gt; ln2）と同じ尺度。</summary>
    public const double SurpriseMissThreshold = 0.693147;

    /// <summary>
    /// 次手の局所要因（移動 / 出現消失 / 残差相当の出来事 / 文字数字）を既存モデルから読む。
    /// </summary>
    public static ShortHorizonForecast Forecast(ShortHorizonInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.LocalEvents == null || string.IsNullOrEmpty(input.Node))
        {
            return new ShortHorizonForecast
            {
                Status = LayerStatus.NotObserved,
                Note = "no local_event_model or node",
                Surface = SurfaceId,
            };
        }

        var local = input.LocalEvents;
        var node = input.Node!;
        var action = input.Action ?? "";
        var pooled = local.Predict(node, action);
        if (pooled == null)
        {
            return new ShortHorizonForecast
            {
                Status = LayerStatus.NotObserved,
                Note = "local_event_model has no (node,action) counts yet",
                Surface = SurfaceId,
            };
        }

        (double[] P, (int Dx, int Dy)? Move)? ctxPred = null;
        bool ctxApplied = false;
        int help = 0;
        if (!string.IsNullOrEmpty(input.ContextKey))
        {
            ctxPred = local.PredictContext(node, action, input.ContextKey!);
            for (int i = 0; i < LocalEventModel.Events.Length; i++)
                if (local.ContextHelps(node, action, i)) help++;
            // PredictContext increments ContextUsed only when split rates are applied.
            ctxApplied = help > 0 && ctxPred != null;
        }

        var useP = (ctxPred ?? pooled).Value.P;
        var factors = new List<ShortHorizonFactor>(LocalEventModel.Events.Length);
        for (int i = 0; i < LocalEventModel.Events.Length; i++)
        {
            bool? occurred = input.OccurredEvents == null ? null : input.OccurredEvents.Contains(i);
            double? surp = input.Surprise != null && i < input.Surprise.Count ? input.Surprise[i] : null;
            factors.Add(new ShortHorizonFactor(LocalEventModel.Events[i], Math.Round(useP[i], 4), occurred, surp));
        }

        double? meanSurp = null;
        if (input.Surprise is { Count: > 0 } s)
            meanSurp = Math.Round(s.Average(), 4);

        bool localMiss = meanSurp is double ms && ms > SurpriseMissThreshold;
        bool? worldHit = null;
        if (input.WorldModel != null && !string.IsNullOrEmpty(input.ActualNextNode))
        {
            var wp = input.WorldModel.Predict(node, action);
            worldHit = wp.TopNext == input.ActualNextNode;
        }

        bool miss = localMiss || worldHit == false;
        double l1 = 0;
        if (ctxPred != null)
        {
            var cp = ctxPred.Value.P;
            for (int i = 0; i < cp.Length; i++) l1 += Math.Abs(cp[i] - pooled.Value.P[i]);
        }

        // EntityExperience: 読み取り専用ヒット率ヒント（学習に使わない）
        string? eeHint = null;
        if (input.EntityExperience != null && input.EntityExperience.Observed > 0)
        {
            long pred = 0, hit = 0;
            for (int t = 0; t < 4; t++) { pred += input.EntityExperience.Predicted[t, 0] + input.EntityExperience.Predicted[t, 1]; hit += input.EntityExperience.Hits[t, 0] + input.EntityExperience.Hits[t, 1]; }
            if (pred > 0) eeHint = $"ee_hit={(double)hit / pred:0.00}(n={pred})";
        }

        return new ShortHorizonForecast
        {
            Status = LayerStatus.Observed,
            Surface = SurfaceId,
            Factors = factors,
            PredictedMove = (ctxPred ?? pooled).Value.Move is { } m ? new[] { m.Dx, m.Dy } : null,
            ContextKey = input.ContextKey,
            ContextHelpEvents = help,
            ContextDivergenceL1 = Math.Round(l1, 4),
            MeanSurprise = meanSurp,
            PredictionMiss = miss,
            WorldHit = worldHit,
            Note = string.Join(";", new[]
            {
                help > 0 ? $"context_helps={help}" : "context_helps=0",
                miss ? "prediction_miss" : "prediction_ok_or_unchecked",
                eeHint,
            }.Where(x => x != null)!),
        };
    }
}

/// <summary>短時間予測の入力。意味ラベル禁止。無い源は null。</summary>
public sealed record ShortHorizonInput(
    string? Node,
    string? Action,
    LocalEventModel? LocalEvents = null,
    string? ContextKey = null,
    IReadOnlyList<int>? OccurredEvents = null,
    IReadOnlyList<double>? Surprise = null,
    TransitionModel? WorldModel = null,
    string? ActualNextNode = null,
    EntityExperience? EntityExperience = null);

/// <summary>局所要因 1 件の予測（形式名のみ）。</summary>
public sealed record ShortHorizonFactor(string Name, double P, bool? Occurred, double? Surprise);

/// <summary>短時間 Predictive WM の読み取り結果（WRAP。数式変更なし）。</summary>
public sealed class ShortHorizonForecast
{
    public string Status { get; init; } = LayerStatus.NotObserved;
    public string Surface { get; init; } = PredictiveWmShortHorizon.SurfaceId;
    public IReadOnlyList<ShortHorizonFactor> Factors { get; init; } = Array.Empty<ShortHorizonFactor>();
    public int[]? PredictedMove { get; init; }
    public string? ContextKey { get; init; }
    public int ContextHelpEvents { get; init; }
    public double ContextDivergenceL1 { get; init; }
    public double? MeanSurprise { get; init; }
    public bool PredictionMiss { get; init; }
    public bool? WorldHit { get; init; }
    public string? Note { get; init; }
}
