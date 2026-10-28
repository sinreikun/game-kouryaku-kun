using GameKouryakuKun.Core.Intrinsic;

namespace GameKouryakuKun.Core.Outer;

/// <summary>
/// S7: Intrinsic ポートフォリオ重みの適用・rollback 履歴（レジストリポインタと対で使う）。
/// 本番コード改変はしない。kind=weights のみ適用。
/// </summary>
public sealed class PortfolioWeightState
{
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, double> _current = IntrinsicPortfolioWeights.Default();
    private readonly Stack<(string BundleId, IReadOnlyDictionary<string, double> Weights)> _history = new();
    public string? AppliedBundleId { get; private set; }

    public IReadOnlyDictionary<string, double> Current
    {
        get { lock (_gate) return _current; }
    }

    public int HistoryDepth
    {
        get { lock (_gate) return _history.Count; }
    }

    public bool CanRollback
    {
        get { lock (_gate) return _history.Count > 0; }
    }

    public (bool Ok, string Detail) ApplyFromBundle(ModelBundleSpec bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!string.Equals(bundle.Kind, ModelBundleKinds.Weights, StringComparison.Ordinal))
            return (false, "kind_not_weights:" + bundle.Kind);
        if (string.Equals(bundle.Kind, ModelBundleKinds.CodeEdit, StringComparison.Ordinal))
            return (false, "not_supported:code_edit");
        var next = IntrinsicPortfolioWeights.FromParameters(bundle.Parameters);
        lock (_gate)
        {
            _history.Push((AppliedBundleId ?? "champion/bootstrap@0", Clone(_current)));
            _current = next;
            AppliedBundleId = bundle.Id;
        }
        return (true, "weights_applied:" + bundle.Id);
    }

    public (bool Ok, string Detail) Rollback()
    {
        lock (_gate)
        {
            if (_history.Count == 0) return (false, "weight_rollback_unavailable");
            var prev = _history.Pop();
            _current = prev.Weights;
            AppliedBundleId = prev.BundleId;
            return (true, "weights_restored:" + prev.BundleId);
        }
    }

    public string SummaryLine()
    {
        lock (_gate)
        {
            string w = string.Join(",", IntrinsicComponents.All.Select(id =>
                $"{id}:{_current.GetValueOrDefault(id):0.00}"));
            return $"portfolio_weights bundle={AppliedBundleId ?? "(default)"} history={_history.Count} [{w}]";
        }
    }

    private static IReadOnlyDictionary<string, double> Clone(IReadOnlyDictionary<string, double> src) =>
        new Dictionary<string, double>(src, StringComparer.Ordinal);
}
