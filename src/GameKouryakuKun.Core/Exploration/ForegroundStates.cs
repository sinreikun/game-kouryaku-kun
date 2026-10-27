namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.27.2（提案 1）: 背景（画面全体の色の種類 m○）によらない、前景の配置（ノード名の「|」の後ろ）＋文字の配置（OCR の配置のハッシュ）による別の状態の鍵。
/// 同じ鍵で背景だけ違うノードの組は、手ごとの「次の前景の鍵」の分布が同じという証拠が強いとき（SplitReview と同じベイズ因子・同じ強さ）だけまとめる。
/// 別々の証拠が強ければ（VisualStateBook の分割と同じ強さ）分けたまま。どちらでもなければ保留。元のノードと観測は消さない（まとめは別の見方）。
/// </summary>
public sealed class ForegroundStates
{
    public const string Pending = "?";
    private readonly Dictionary<string, string> _textLayout = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<(string, string), (string Status, double LogBf, int Common)> Pairs => _pairs;
    private readonly Dictionary<(string, string), (string Status, double LogBf, int Common)> _pairs = new();

    public static string Background(string node) => node.Split('#')[0].Split('|')[0];
    public static string Layout(string node) { var b = node.Split('#')[0]; int i = b.IndexOf('|'); return i < 0 ? "-" : b[(i + 1)..]; }

    /// <summary>文字の配置を覚える（OCR が届いた時から鍵に入る。届くまでは「未観測」）</summary>
    public void ObserveText(string node, ulong layoutHash, bool hasText) => _textLayout[node] = hasText ? layoutHash.ToString("x16") : "none";
    public string TextKey(string node) => _textLayout.TryGetValue(node, out var t) ? t : Pending;

    /// <summary>前景の鍵: 前景の配置＋文字の配置（文字が未観測なら "?"）</summary>
    public string Key(string node) => Layout(node) + "@" + TextKey(node);

    /// <summary>
    /// 鍵が同じ（文字の配置が未観測のものは前景の配置だけで比べ、まとめはしない）で背景が違う組を比べ直す。
    /// next: ノード → 手 → 次のノードの回数（Explorer の集計）。次のノードは前景の配置にそろえて比べる（自分へ戻るのは "="）
    /// </summary>
    public void Review(IReadOnlyDictionary<string, NodeStats> nodes, int minTries)
    {
        _parent.Clear(); _pairs.Clear();
        var groups = nodes.Keys.Where(n => Layout(n) != "-").GroupBy(n => Layout(n)).Where(g => g.Select(Background).Distinct().Count() > 1);
        foreach (var g in groups)
        {
            var l = g.OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (int i = 0; i < l.Count; i++)
            for (int j = i + 1; j < l.Count; j++)
            {
                if (Background(l[i]) == Background(l[j])) continue;
                var ta = TextKey(l[i]); var tb = TextKey(l[j]);
                var ca = FgCounts(l[i], nodes[l[i]]); var cb = FgCounts(l[j], nodes[l[j]]);
                var (lbf, k, _) = VisualStateBook.NextEvidence(ca, cb, minTries * SplitReview.RevertTriesFactor);
                string st;
                if (ta == Pending || tb == Pending) st = "text_pending";
                else if (ta != tb) st = "text_differs";
                else if (k >= SplitReview.RevertMinCommonActions && lbf <= -SplitReview.RevertMinLogBayesFactor) { st = "merged"; Union(l[i], l[j]); }
                else if (k >= SplitReview.RevertMinCommonActions && lbf >= SplitReview.RevertMinLogBayesFactor) st = "separate";
                else st = "pending";
                _pairs[(l[i], l[j])] = (st, lbf, k);
            }
        }
    }

    public static Dictionary<string, Dictionary<string, int>> FgCounts(string node, NodeStats ns)
    {
        var r = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (a, st) in ns.Actions)
        {
            if (st.Tries == 0) continue;
            var c = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (to, n) in st.To) { var k = to == node ? "=" : Layout(to); c[k] = c.GetValueOrDefault(k) + n; }
            r[a] = c;
        }
        return r;
    }

    private string Find(string x) { while (_parent.TryGetValue(x, out var p) && p != x) x = p; return x; }
    private void Union(string a, string b) { var ra = Find(a); var rb = Find(b); if (ra != rb) _parent[rb] = ra; if (!_parent.ContainsKey(ra)) _parent[ra] = ra; }

    /// <summary>まとめた仲間（自分を除く）</summary>
    public IEnumerable<string> Peers(string node)
    {
        if (!_parent.ContainsKey(node)) yield break;
        var r = Find(node);
        foreach (var k in _parent.Keys) if (k != node && Find(k) == r) yield return k;
    }

    public int Merged => _pairs.Values.Count(p => p.Status == "merged");
    public int Separate => _pairs.Values.Count(p => p.Status == "separate");
    public string Summary() => $"背景違いの同じ前景の組 {_pairs.Count}（まとめた {Merged}・別 {Separate}・文字が違う {_pairs.Values.Count(p => p.Status == "text_differs")}・文字が未観測 {_pairs.Values.Count(p => p.Status == "text_pending")}・保留 {_pairs.Values.Count(p => p.Status == "pending")}）";
}
