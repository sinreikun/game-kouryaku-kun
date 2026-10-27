using GameKouryakuKun.Core.Ocr;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.27.2: 手ごとの文字の変化の種類（TextDiff.Kind）を、画面×手の関係として数える（意味は付けない）。
/// 予測: 種類ごとの割合（Jeffreys の +0.5）。不確かさ: TransitionModel と同じ Dirichlet 情報量。
/// 入力なしの文字の変化: 待つ手（と待つ手の直後の区間）の文字の変化を NaturalBaseline と同じ規則で数え、操作が起こした文字の変化を分ける。
/// </summary>
public sealed class TextChangeBook
{
    private readonly Dictionary<(string, string), int[]> _c = new();
    public NaturalBaseline Natural { get; } = new();
    public long Observed { get; private set; }
    public long Changed { get; private set; }
    public long Checks { get; private set; }
    public long Hits { get; private set; }
    /// <summary>v0.28.0: 文字の配置の区画（8×6）ごとの、手で変わる所と手によらず変わる所の区別</summary>
    public RegionCausality Regions { get; } = new(GameKouryakuKun.Core.Ocr.TextFeatures.LayoutCols * GameKouryakuKun.Core.Ocr.TextFeatures.LayoutRows);
    /// <summary>文字が変わった手のうち、手が起こしたと見込める分の合計（変わった手ごとに 0〜1）。残りは手によらない分</summary>
    public double CausedSum { get; private set; }
    public long NaturalTextObservations { get; private set; }

    private static int K(string kind) => Math.Max(0, Array.IndexOf(TextDiff.Kinds, kind));

    public int[] Counts(string node, string action) => _c.TryGetValue((node, action), out var c) ? c : new int[TextDiff.Kinds.Length];
    public int Tries(string node, string action) => Counts(node, action).Sum();

    /// <summary>最も出やすい種類と、その割合（観測がなければ null）</summary>
    public (string Kind, double P)? Predict(string node, string action)
    {
        var c = Counts(node, action); int n = c.Sum(); if (n == 0) return null;
        int best = Array.IndexOf(c, c.Max());
        return (TextDiff.Kinds[best], (c[best] + 0.5) / (n + 0.5 * c.Length));
    }

    /// <summary>文字が変わる割合（Jeffreys）</summary>
    public double ChangeRate(string node, string action) { var c = Counts(node, action); int n = c.Sum(); return (n - c[0] + 0.5) / (n + 1.0); }

    /// <summary>操作が起こした文字の変化の割合（入力なしの文字の変化を引いた残り。入力なしの観測がなければ NaN）</summary>
    public double ActionCaused(string node, string action)
    {
        if (Tries(node, action) == 0 || Natural.Rates(node) is not { } r) return double.NaN;
        double pa = ChangeRate(node, action); return pa <= 0 ? 0 : Math.Max(0, pa - r.Change) / pa;
    }

    /// <summary>
    /// v0.28.0: この手で「手が起こす文字の変化」が起きる見込み。区画ごとに（この手での変わる割合 − 手によらない基準）の最大。
    /// 基準は待つ手 → 手によらない区画の全部の手の割合 → 事前値 0.5 の順。文字の変化そのもの（ChangeRate）は別に持つ
    /// </summary>
    public double ActionCausedRate(string node, string action)
    {
        if (Tries(node, action) == 0) return 0;
        double best = 0;
        for (int i = 0; i < Regions.Cells; i++)
        {
            var (n, c) = Regions.ActionCounts(node, action, i); if (c == 0) continue;
            double pa = (c + 0.5) / (n + 1.0), pn = Regions.NaturalRate(node, i)?.P ?? 0.5;
            // v0.28.2: 好奇心の項なので、確かに予測できるようになった変化は値打ちを失う（この区画が変わるかの 2 値の不確かさ＝もう 1 回試して分かる量を掛ける。既存の機構）。
            // 実機 #105: 店の画面で 41 回試した手の文字の項が 0.25 のまま下がらず、試すほど上がっていた
            best = Math.Max(best, (pa - pn) * TransitionModel.BinaryUncertainty(c, n - c));
        }
        return best;
    }

    /// <summary>手が起こす文字の変化の最も強い区画と、その確からしさ（記録用）</summary>
    public (int Cell, double Rate, double Confidence, string Basis) TopCause(string node, string action)
    {
        (int, double, double, string) r = (-1, 0, 0, "none");
        for (int i = 0; i < Regions.Cells; i++)
        {
            var (n, c) = Regions.ActionCounts(node, action, i); if (c == 0) continue;
            var nr = Regions.NaturalRate(node, i);
            double v = (c + 0.5) / (n + 1.0) - (nr?.P ?? 0.5);
            if (r.Item1 < 0 || v > r.Item2) r = (i, v, Regions.Confidence(node, action, i), nr?.Source ?? "prior");
        }
        return r;
    }

    /// <summary>入力なし（待つ手）の文字の差。観測として数え、区画の基準に入れる</summary>
    public void ObserveNaturalDiff(string node, TextDiff d) { Regions.ObserveNatural(node, d.Cells); NaturalTextObservations++; }

    public double Uncertainty(string node, string action)
    {
        var c = Counts(node, action);
        var a = c.Select(x => x + 1.0).ToArray();
        return Math.Clamp(TransitionModel.DirichletInformation(a) / TransitionModel.DirichletInformation(Enumerable.Repeat(1.0, c.Length).ToArray()), 0, 1);
    }

    /// <summary>キーを送った手の結果。先に予測と比べてから数える</summary>
    public void ObserveAction(string node, string action, TextDiff d)
    {
        if (Predict(node, action) is { } p) { Checks++; if (p.Kind == d.Kind) Hits++; }
        if (!_c.TryGetValue((node, action), out var c)) _c[(node, action)] = c = new int[TextDiff.Kinds.Length];
        double caused = d.Any ? Regions.DiffCaused(node, action, d.Cells) : 0;   // 先に（この観測を入れる前の）区画の基準で分ける
        c[K(d.Kind)]++; Observed++; if (d.Any) { Changed++; CausedSum += caused; }
        LastCaused = caused;
        Regions.ObserveAction(node, action, d.Cells);
    }
    public double LastCaused { get; private set; }

    public string ExportState() => System.Text.Json.JsonSerializer.Serialize(new
    {
        v = 1,
        c = _c.Select(kv => new object[] { kv.Key.Item1, kv.Key.Item2, kv.Value }).ToList(),
        regions = Regions.ExportState(),
    });

    public void ImportState(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json); var r = doc.RootElement;
        foreach (var x in r.GetProperty("c").EnumerateArray())
        {
            var a = x[2].EnumerateArray().Select(v => v.GetInt32()).ToArray();
            if (a.Length == TextDiff.Kinds.Length) _c[(x[0].GetString()!, x[1].GetString()!)] = a;
        }
        Regions.ImportState(r.GetProperty("regions").GetString()!);
    }

    public string Summary() => $"文字の差を見た手 {Observed}（変わった {Changed}）・予測の当たり {Hits}/{Checks}・入力なしの文字の観測 {Natural.TotalObservations}・手が起こした分 {CausedSum:0.#}/{Changed}";
}
