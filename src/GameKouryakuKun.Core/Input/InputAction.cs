namespace GameKouryakuKun.Core.Input;

public enum PressMode
{
    /// <summary>短押し（既定）。押下時間が長くてもオートリピートは送らない＝1手</summary>
    Tap,
    /// <summary>押しっぱなし。RepeatDelay 後に最後のキーの WM_KEYDOWN(repeat) を連送する（連続移動など、意図的な場合だけ）</summary>
    Hold,
}

/// <summary>
/// 1回の操作。論理キー名（Profile の input.keys のキー）を1つ以上指定する。2つ以上なら同時押し。
/// HoldMs 省略時は Profile の default_hold_ms（人がキーを軽く1回押す長さ。身体の設定）。
/// </summary>
public sealed record InputAction(IReadOnlyList<string> LogicalKeys, int? HoldMs = null, PressMode Mode = PressMode.Tap, string? Label = null)
{
    public static InputAction Tap(params string[] keys) => new(keys);
    public static InputAction TapFor(int holdMs, params string[] keys) => new(keys, holdMs);
    public static InputAction Hold(int holdMs, params string[] keys) => new(keys, holdMs, PressMode.Hold);
    public string Describe() => Label ?? (string.Join("+", LogicalKeys) + (HoldMs is int h ? $"({h}ms{(Mode == PressMode.Hold ? ",hold" : "")})" : ""));
}

public enum KeyEventType { Down, Repeat, Up }

public sealed record PlannedKeyEvent(double AtMs, KeySpec Key, KeyEventType Type);

public sealed record InputTiming
{
    /// <summary>同時押しのキー間隔（押す順・離す順）</summary>
    public int ChordGapMs { get; init; } = 15;  // 根拠: 汎用（同時押しのキー間隔＝人の同時押し程度）
    public int RepeatDelayMs { get; init; } = 250;  // 根拠: 汎用（OS のキーリピート開始の標準的な値）
    public int RepeatIntervalMs { get; init; } = 33;  // 根拠: 汎用（OS のキーリピート間隔の標準的な値）
    public static readonly InputTiming Default = new();
}

/// <summary>操作を「いつ・どのキーを・押す/リピート/離す」の列に展開する（純粋関数。テスト可能）。</summary>
public static class InputPlanner
{
    public static IReadOnlyList<PlannedKeyEvent> Plan(IReadOnlyList<KeySpec> keys, int holdMs, PressMode mode, InputTiming? timing = null)
    {
        var t = timing ?? InputTiming.Default;
        if (keys.Count == 0) throw new ArgumentException("キーがありません");
        if (keys.Select(k => k.Vk).Distinct().Count() != keys.Count) throw new ArgumentException("同じキーが重複しています");
        if (holdMs < 1) throw new ArgumentOutOfRangeException(nameof(holdMs));
        var list = new List<PlannedKeyEvent>();
        double at = 0;
        for (int i = 0; i < keys.Count; i++) { list.Add(new(at, keys[i], KeyEventType.Down)); if (i < keys.Count - 1) at += t.ChordGapMs; }
        double lastDown = at, release = lastDown + holdMs;
        if (mode == PressMode.Hold)
            for (double r = lastDown + t.RepeatDelayMs; r < release; r += t.RepeatIntervalMs)
                list.Add(new(r, keys[^1], KeyEventType.Repeat));
        at = release;
        for (int i = keys.Count - 1; i >= 0; i--) { list.Add(new(at, keys[i], KeyEventType.Up)); if (i > 0) at += t.ChordGapMs; }
        return list;
    }

    /// <summary>WM_KEYDOWN/WM_KEYUP の lParam（repeat count=1, scan, extended, previous state, transition）。</summary>
    public static long MakeKeyLParam(KeySpec k, KeyEventType type)
    {
        long l = 1;
        l |= (long)(k.Scan & 0xFF) << 16;
        if (k.Extended) l |= 1L << 24;
        if (type != KeyEventType.Down) l |= 1L << 30; // 直前に押されていた（Repeat/Up）
        if (type == KeyEventType.Up) l |= 1L << 31;   // 離した
        return l;
    }
}

/// <summary>
/// 禁止キーの検査。Profile の forbidden_keys / forbidden_combos に加え、ゲームに関係なく常に禁止する組合せを持つ
/// （Windowsキー、Alt+F4、Alt+Tab 等。フォーカスを奪う・終了させる・OS機能を呼ぶ恐れがあるため）。
/// </summary>
public sealed class ForbiddenKeyGuard
{
    public static readonly IReadOnlyList<string> AlwaysForbiddenKeys = new[] { "LWin", "RWin", "Apps", "PrintScreen" };
    public static readonly IReadOnlyList<string> AlwaysForbiddenCombos = new[] { "Alt+F4", "Alt+Tab", "Alt+Escape", "Ctrl+Escape", "Ctrl+Shift+Escape", "Alt+Space", "Alt+Enter", "Shift+Tab" };

    private readonly List<KeySpec> _keys;
    private readonly List<KeyCombo> _combos;

    public ForbiddenKeyGuard(IEnumerable<KeySpec>? forbiddenKeys = null, IEnumerable<KeyCombo>? forbiddenCombos = null)
    {
        _keys = AlwaysForbiddenKeys.Select(KeySpec.Parse).Concat(forbiddenKeys ?? Array.Empty<KeySpec>()).ToList();
        _combos = AlwaysForbiddenCombos.Select(KeyCombo.Parse).Concat(forbiddenCombos ?? Array.Empty<KeyCombo>()).ToList();
    }

    public IReadOnlyList<KeySpec> Keys => _keys;

    /// <summary>修飾キーは左右を区別せず族で比較、それ以外は VK と拡張フラグで比較（Enter と NumEnter は別）</summary>
    public static bool Same(KeySpec f, KeySpec k) =>
        f.IsModifier || k.IsModifier ? string.Equals(f.Family, k.Family, StringComparison.OrdinalIgnoreCase) : f.Vk == k.Vk && f.Extended == k.Extended;
    public IReadOnlyList<KeyCombo> Combos => _combos;

    /// <summary>禁止なら理由、許可なら null</summary>
    public string? Check(IReadOnlyCollection<KeySpec> keys)
    {
        foreach (var k in keys)
        {
            if (_keys.Any(f => Same(f, k))) return $"禁止キー {k.Name} を含みます";
        }
        foreach (var c in _combos) if (c.IsContainedIn(keys)) return $"禁止の組合せ {c} を含みます";
        return null;
    }
}
