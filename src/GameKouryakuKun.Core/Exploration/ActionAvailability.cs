namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.32.5（t106u）: 人が決める「自律探索で使ってよい入力」。人の側の制約で、ゲームの知識ではない（意味は AI に渡さない）。
/// 入力はキーの名前（論理キー・同時押し・待つ手）で扱う。Human Control の入力と起動時の入力の診断には関係しない。
/// 並び（Sequence）は、その中の手のどれかが止められていれば使わない
/// </summary>
public sealed class ActionAvailability
{
    public const string SettingsKey = "explore_disabled_actions";
    private volatile HashSet<string> _disabled = new(StringComparer.Ordinal);
    public event Action? Changed;

    public IReadOnlySet<string> Disabled => _disabled;
    public bool Any => _disabled.Count > 0;

    public bool IsAllowed(ExploreAction a) =>
        !_disabled.Contains(a.Name) && (a.Steps is not { Count: > 0 } s || s.All(IsAllowed));

    public void Set(string action, bool enabled)
    {
        var n = new HashSet<string>(_disabled, StringComparer.Ordinal);
        if (enabled ? !n.Remove(action) : !n.Add(action)) return;
        _disabled = n; Changed?.Invoke();
    }

    public void EnableAll() { if (_disabled.Count == 0) return; _disabled = new(StringComparer.Ordinal); Changed?.Invoke(); }

    public string Serialize() => string.Join("\n", _disabled.OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>保存した値から復元する。今の入力の一覧にない名前は捨てる（known が null なら全部残す）</summary>
    public void Load(string? saved, IEnumerable<string>? known = null)
    {
        var k = known == null ? null : new HashSet<string>(known, StringComparer.Ordinal);
        var n = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(saved)) foreach (var t in saved.Split('\n')) if (t.Length > 0 && (k == null || k.Contains(t))) n.Add(t);
        _disabled = n; Changed?.Invoke();
    }
}
