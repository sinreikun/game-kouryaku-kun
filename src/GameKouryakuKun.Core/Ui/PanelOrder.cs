namespace GameKouryakuKun.Core.Ui;

/// <summary>
/// v0.30.2: 右側の欄の並び順（表示だけ。ui-settings.json の値 panel_order に、欄の名前を改行でつないで保存）。
/// 保存した順にない欄（新しい版で増えた欄）は既定の順の位置で後ろに足し、もう無い欄は捨てる
/// </summary>
public static class PanelOrder
{
    public const string Key = "panel_order";

    public static List<string> Apply(IReadOnlyList<string> defaults, string? saved)
    {
        var known = new HashSet<string>(defaults, StringComparer.Ordinal);
        var o = new List<string>();
        if (!string.IsNullOrEmpty(saved))
            foreach (var t in saved.Split('\n')) if (known.Contains(t) && !o.Contains(t)) o.Add(t);
        foreach (var d in defaults) if (!o.Contains(d)) o.Add(d);
        return o;
    }

    /// <summary>欄を delta（−1 で上・+1 で下）だけ動かした順。端では動かない</summary>
    public static List<string> Move(IReadOnlyList<string> order, string title, int delta)
    {
        var o = order.ToList(); int i = o.IndexOf(title), j = i + delta;
        if (i < 0 || j < 0 || j >= o.Count) return o;
        (o[i], o[j]) = (o[j], o[i]);
        return o;
    }

    public static string Serialize(IEnumerable<string> order) => string.Join("\n", order);
}
