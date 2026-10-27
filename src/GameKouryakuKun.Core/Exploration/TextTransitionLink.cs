namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.28.0: 「前の手で文字が変わったこと」が、次の手の遷移（行き先の分布）を変えるかを経験から測る（意味は持たない）。
/// 画面ごとに、前の手で文字が変わった後 と 変わらなかった後 の（手 → 行き先の回数）を分けて数え、
/// 2 つの行き先の分布が違うかのベイズ因子（状態の分割と同じ式）で、文字の変化が予測の手がかりになるかを見る。
/// どの文字列の変化が先行したかも数える（文字列の意味は使わない。正規化した文字そのものを鍵にする）。
/// </summary>
public sealed class TextTransitionLink
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, int>>[]> _g = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Node, string Text), Dictionary<string, int>> _byText = new();
    public long Observed { get; private set; }

    /// <summary>prevChanged: 前の手で文字が変わったか（分からなければ呼ばない）。changedTexts: 前の手で変わった区画の文字</summary>
    public void Observe(string node, string action, bool prevChanged, string next, IEnumerable<string>? changedTexts = null)
    {
        if (!_g.TryGetValue(node, out var g)) _g[node] = g = new[] { new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal), new(StringComparer.Ordinal) };
        var side = g[prevChanged ? 1 : 0];
        if (!side.TryGetValue(action, out var m)) side[action] = m = new(StringComparer.Ordinal);
        m[next] = m.GetValueOrDefault(next) + 1; Observed++;
        if (prevChanged && changedTexts != null)
            foreach (var t in changedTexts.Distinct())
            {
                var k = (node, GameKouryakuKun.Core.Ocr.OcrTokens.Describe(t));
                if (!_byText.TryGetValue(k, out var bm)) _byText[k] = bm = new(StringComparer.Ordinal);
                bm[next] = bm.GetValueOrDefault(next) + 1;
            }
    }

    /// <summary>画面ごとのベイズ因子の対数（正＝文字が変わった後は遷移が違う）。比べられる手がなければ null</summary>
    public (double LogBf, int Common)? Evidence(string node, int minTries = 3)
    {
        if (!_g.TryGetValue(node, out var g)) return null;
        var (lbf, common, _) = VisualStateBook.NextEvidence(g[0], g[1], minTries);
        return common == 0 ? null : (lbf, common);
    }

    public IEnumerable<string> Nodes => _g.Keys;

    /// <summary>文字の変化が遷移の手がかりになる画面（強い証拠）・ならない画面・未定の数</summary>
    public (int Predictive, int NotPredictive, int Undecided) Counts()
    {
        int p = 0, n = 0, u = 0;
        foreach (var node in _g.Keys)
        {
            if (Evidence(node) is not { } e) continue;
            if (e.LogBf >= SplitReview.RevertMinLogBayesFactor) p++; else if (e.LogBf <= -SplitReview.RevertMinLogBayesFactor) n++; else u++;
        }
        return (p, n, u);
    }

    /// <summary>同じ文字の変化の後に同じ行き先が続いた回数が多いもの（記録用）</summary>
    public IEnumerable<(string Node, string Text, string Next, int Count, int Total)> TopTexts(int k) =>
        _byText.Select(kv => { var best = kv.Value.OrderByDescending(x => x.Value).First(); return (kv.Key.Node, kv.Key.Text, best.Key, best.Value, kv.Value.Values.Sum()); })
               .Where(x => x.Item5 >= 2).OrderByDescending(x => x.Item4).ThenBy(x => x.Item2, StringComparer.Ordinal).Take(k);
}
