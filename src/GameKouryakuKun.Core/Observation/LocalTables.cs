namespace GameKouryakuKun.Core.Observation;

/// <summary>
/// v0.31.5（P-6 の一部・仕様 §7）: 見た目の表。「見た目 a の候補に手 x をしたら、次の手で同じ追跡の見た目は b」を数える。
/// 見た目は色の符号のハッシュ（意味なし）。追跡が続いた候補だけを数える。予測は最頻（Jeffreys の確率つき）
/// </summary>
public sealed class AppearanceTable
{
    private readonly Dictionary<(string App, string Action), Dictionary<string, int>> _c = new();
    public long Observed { get; private set; }
    public long Hits { get; private set; }
    public long Predicted { get; private set; }

    public void Observe(EntityLayer? prev, EntityLayer cur, string action)
    {
        if (prev == null) return;
        var before = prev.Candidates.GroupBy(c => c.Track).ToDictionary(g => g.Key, g => g.First().Appearance);
        foreach (var c in cur.Candidates)
        {
            if (c.New || !before.TryGetValue(c.Track, out var a)) continue;
            var p = Predict(a, action);
            if (p != null) { Predicted++; if (p.Value.Next == c.Appearance) Hits++; }
            var k = (a, action); if (!_c.TryGetValue(k, out var d)) _c[k] = d = new();
            d[c.Appearance] = d.GetValueOrDefault(c.Appearance) + 1; Observed++;
        }
    }

    public (string Next, double P, int N)? Predict(string app, string action)
    {
        if (!_c.TryGetValue((app, action), out var d) || d.Count == 0) return null;
        int n = d.Values.Sum(); var b = d.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First();
        return (b.Key, (b.Value + 0.5) / (n + 0.5 * (d.Count + 1)), n);   // 根拠: Jeffreys（見たことのない見た目の分を 1 つ足す）
    }

    public int Keys => _c.Count;
}

/// <summary>
/// v0.31.5（P-6 の一部・仕様 §9・§10）: 数字の変化の量の表。「区画と行の中の順番で決まる数字の系列に、手 x をしたときの差」を数え、最頻の差を予測する。
/// 何の数かは持たない。差 0（変わらない）も数える
/// </summary>
public sealed class NumberDeltaTable
{
    private readonly Dictionary<(int Cell, int Index, string Action), Dictionary<double, int>> _c = new();
    public long Observed { get; private set; }
    public long Hits { get; private set; }
    public long Predicted { get; private set; }

    public void Observe(TextLayer? text, string action)
    {
        if (text == null) return;
        foreach (var n in text.Numbers)
        {
            if (n.Previous is not double pv) continue;
            double d = Math.Round(n.Value - pv, 6);
            var p = Predict(n.Cell, n.Index, action);
            if (p != null) { Predicted++; if (p.Value.Delta == d) Hits++; }
            var k = (n.Cell, n.Index, action); if (!_c.TryGetValue(k, out var m)) _c[k] = m = new();
            m[d] = m.GetValueOrDefault(d) + 1; Observed++;
        }
    }

    public (double Delta, double P, int N)? Predict(int cell, int index, string action)
    {
        if (!_c.TryGetValue((cell, index, action), out var m) || m.Count == 0) return null;
        int n = m.Values.Sum(); var b = m.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First();
        return (b.Key, (b.Value + 0.5) / (n + 0.5 * (m.Count + 1)), n);
    }
}
