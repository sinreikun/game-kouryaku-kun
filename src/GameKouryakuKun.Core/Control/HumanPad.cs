namespace GameKouryakuKun.Core.Control;

/// <summary>
/// Human Control の窓のボタン配置（v0.26.0）。並べ方は物理キーの形（8方向・テンキーの並び）だけで決め、キーの意味は持たない。
/// 送るのは Allowed（Profile の単独キー・chords・待つ）にある操作名だけ。同時押しは「同時押しのキー」を押したままにして方向を押す。
/// </summary>
public static class HumanPad
{
    public sealed record Cell(int Row, int Col, string Action, string Label, int ColSpan = 1);

    /// <summary>8方向（3×3、中央は空き）。方向は論理キー名、斜めは名前順に + でつないだ名前（Explorer.ChordOf と同じ規則）</summary>
    public static readonly IReadOnlyList<(int Row, int Col, string[] Keys)> Directions = new (int, int, string[])[]
    {
        (0, 0, new[] { "dpad_up", "dpad_left" }), (0, 1, new[] { "dpad_up" }), (0, 2, new[] { "dpad_up", "dpad_right" }),
        (1, 0, new[] { "dpad_left" }),                                         (1, 2, new[] { "dpad_right" }),
        (2, 0, new[] { "dpad_down", "dpad_left" }), (2, 1, new[] { "dpad_down" }), (2, 2, new[] { "dpad_down", "dpad_right" }),
    };

    public static string Name(IEnumerable<string> keys)
    {
        var k = keys.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return string.Join("+", k);
    }

    /// <summary>テンキーの並び（物理キー名 → 行・列）。Num8/Num9 は Profile にないので空き</summary>
    private static readonly IReadOnlyDictionary<string, (int R, int C, int Span)> NumLayout = new Dictionary<string, (int, int, int)>(StringComparer.OrdinalIgnoreCase)
    {
        ["Num7"] = (0, 0, 1), ["Num8"] = (0, 1, 1), ["Num9"] = (0, 2, 1),
        ["Num4"] = (1, 0, 1), ["Num5"] = (1, 1, 1), ["Num6"] = (1, 2, 1),
        ["Num1"] = (2, 0, 1), ["Num2"] = (2, 1, 1), ["Num3"] = (2, 2, 1),
        ["Num0"] = (3, 0, 2),
    };

    /// <summary>方向のボタン。modifier（同時押しのキーの論理名。null＝なし）と合わせた操作が Allowed にないものは Action を空にする（押せない）</summary>
    public static List<Cell> DirectionCells(IReadOnlyDictionary<string, string> physical, ISet<string> allowed, string? modifier)
    {
        var list = new List<Cell>();
        foreach (var (r, c, keys) in Directions)
        {
            var name = Name(modifier == null ? keys : keys.Append(modifier));
            var label = string.Join("+", (modifier == null ? keys : new[] { modifier }.Concat(keys)).Select(k => physical.TryGetValue(k, out var p) ? p : k));
            list.Add(new Cell(r, c, allowed.Contains(name) ? name : "", label));
        }
        return list;
    }

    /// <summary>テンキーの形に並ぶ単独キー（物理キー名が Num0〜Num9 の論理キー）。それ以外のボタン（Tab など）は Others</summary>
    public static (List<Cell> Numpad, List<Cell> Others) ButtonCells(IReadOnlyDictionary<string, string> physical, ISet<string> allowed)
    {
        var num = new List<Cell>(); var others = new List<Cell>();
        foreach (var (logical, phys) in physical.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (logical.StartsWith("dpad_", StringComparison.Ordinal) || !allowed.Contains(logical)) continue;
            if (NumLayout.TryGetValue(phys, out var at)) num.Add(new Cell(at.R, at.C, logical, phys, at.Span));
            else others.Add(new Cell(others.Count, 0, logical, phys));
        }
        return (num, others);
    }

    /// <summary>同時押しのキー（Allowed の同時押しに、方向と一緒に現れる dpad 以外の論理キー）。名前順</summary>
    public static List<string> Modifiers(ISet<string> allowed) =>
        allowed.Where(n => n.Contains('+'))
            .Select(n => n.Split('+')).Where(p => p.Any(k => k.StartsWith("dpad_", StringComparison.Ordinal)))
            .SelectMany(p => p.Where(k => !k.StartsWith("dpad_", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
}
