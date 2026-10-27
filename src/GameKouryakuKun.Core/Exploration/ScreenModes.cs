using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>画面1枚の汎用の特徴（色の分布）。意味は付けない（phase2-design §7.1）</summary>
public sealed class ScreenSignature
{
    public const int Bins = 64;   // 根拠: 汎用（RGB 各4段階の色の分布）
    public float[] Hist { get; }

    public ScreenSignature(float[] hist) { Hist = hist; }

    public static ScreenSignature Of(Frame f, int stride = 6)
    {
        var h = new float[Bins]; var px = f.Bgra; int n = 0;
        for (int y = 0; y < f.Height; y += stride)
            for (int x = 0; x < f.Width; x += stride)
            {
                int p = (y * f.Width + x) * 4;
                int b = px[p] >> 6, g = px[p + 1] >> 6, r = px[p + 2] >> 6;
                h[(r << 4) | (g << 2) | b]++; n++;
            }
        for (int i = 0; i < Bins; i++) h[i] /= Math.Max(1, n);
        return new ScreenSignature(h);
    }

    /// <summary>0〜1 の距離（色の分布の差の半分）</summary>
    public double Distance(float[] proto)
    {
        double s = 0;
        for (int i = 0; i < Bins; i++) s += Math.Abs(Hist[i] - proto[i]);
        return s / 2;
    }
}

/// <summary>画面の種類の1つ（教師なしの群）</summary>
public sealed class ScreenMode
{
    public int Id { get; }
    public float[] Prototype { get; }
    public int Seen { get; set; }
    public ScreenMode(int id, float[] proto, int seen) { Id = id; Prototype = proto; Seen = seen; }
}

/// <summary>
/// 画面の種類を教師なしで決める（先着順の群分け）。基本の種類（色の分布）と、見えている矩形の輪郭の配置の組で
/// 「ノード」を作る。ノードの名前は番号と矩形の配置だけで、意味は付けない。
/// 新しい種類を作る距離は自己調整する: 既存の種類に割り当てた距離の平均＋4標準偏差を、[MinNewModeDistance, MaxNewModeDistance] に収める。
/// </summary>
public sealed class ScreenModeBook
{
    private readonly List<ScreenMode> _modes = new();
    private int _n; private double _mean, _m2;
    /// <summary>標本が少ないうちの値</summary>
    public double InitialNewModeDistance { get; init; } = 0.35; // 根拠: 汎用（色の分布の総変動距離 35%＝画面の約1/3の色が入れ替わる）。自己調整で上書き
    public double MinNewModeDistance { get; init; } = 0.25;     // 根拠: 汎用（自己調整の下限: 小さな表示の変化で種類を分けすぎない）
    public double MaxNewModeDistance { get; init; } = 0.5;      // 根拠: 汎用（自己調整の上限: 色の半分が入れ替わる画面は別の種類）
    public int MinSamples { get; init; } = 30;                  // 根拠: 汎用（分布を推定する最小の標本数）
    public double SpreadFactor { get; init; } = 4;              // 根拠: 汎用（同じ種類の中のばらつきの4標準偏差を超えたら別の種類）
    public IReadOnlyList<ScreenMode> Modes => _modes;

    /// <summary>今の「新しい種類を作る距離」（自己調整）</summary>
    public double NewModeDistance => _n < MinSamples ? InitialNewModeDistance
        : Math.Clamp(_mean + SpreadFactor * Math.Sqrt(_m2 / Math.Max(1, _n - 1)), MinNewModeDistance, MaxNewModeDistance);

    public void Load(IEnumerable<(int Id, float[] Proto, int Seen)> modes)
    {
        foreach (var (id, p, s) in modes) if (p.Length == ScreenSignature.Bins && _modes.All(m => m.Id != id)) _modes.Add(new ScreenMode(id, (float[])p.Clone(), s));
    }

    /// <summary>最も近い種類を返す。近いものがなければ新しい種類を作る</summary>
    public (int Mode, bool IsNew, double Distance) Assign(ScreenSignature s)
    {
        ScreenMode? best = null; double bd = double.MaxValue;
        foreach (var m in _modes) { double d = s.Distance(m.Prototype); if (d < bd) { bd = d; best = m; } }
        if (best == null || bd > NewModeDistance)
        {
            var m = new ScreenMode(_modes.Count == 0 ? 1 : _modes.Max(x => x.Id) + 1, (float[])s.Hist.Clone(), 1);
            _modes.Add(m);
            return (m.Id, true, best == null ? 1 : bd);
        }
        _n++; double dd = bd - _mean; _mean += dd / _n; _m2 += dd * (bd - _mean);
        best.Seen++;
        float k = 1f / Math.Min(best.Seen, 50);   // ゆっくり追従する平均
        for (int i = 0; i < ScreenSignature.Bins; i++) best.Prototype[i] += k * (s.Hist[i] - best.Prototype[i]);
        return (best.Id, false, bd);
    }

    /// <summary>矩形の輪郭の配置を粗く量子化した文字列（6 分割）。なければ空</summary>
    public static string PanelLayout(IReadOnlyList<PanelRect> panels, int w, int h)
    {
        if (panels.Count == 0) return "";
        static int Q(double v) => (int)Math.Round(v * 6);
        return string.Join(";", panels.Select(p => $"{Q(p.X / (double)w)},{Q(p.Y / (double)h)},{Q(p.Width / (double)w)}").Distinct().OrderBy(s => s, StringComparer.Ordinal));
    }

    public static string NodeName(int mode, string panelLayout) => panelLayout.Length == 0 ? $"m{mode}" : $"m{mode}|{panelLayout}";
    /// <summary>ノード名に矩形の配置が含まれるか（汎用の視覚特徴の有無。意味は付けない）</summary>
    public static bool HasPanel(string node) => node.Contains('|');
}
