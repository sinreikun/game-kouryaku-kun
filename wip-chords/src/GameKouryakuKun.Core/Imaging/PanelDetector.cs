namespace GameKouryakuKun.Core.Imaging;

/// <summary>見つかった矩形の輪郭（取得画像の画素座標）。汎用の視覚特徴で、意味は付けない（U4）</summary>
public readonly record struct PanelRect(int X, int Y, int Width, int Height);

/// <summary>
/// 矩形の輪郭（クラス名は旧名の panel のまま）の汎用検出（phase2-design.md §3.2 の 3。U4: 四角形・線は持たせてよい一般の視覚特徴）。
/// 「周りより明るい細い線」が水平に長く伸び、その両端から同じく細い線が下へ伸びている形（矩形の上辺と左右の辺）を探す。
/// 色・明るさ・太さの閾値は特定のゲームの画面の意味から決めない（design.md 最上位原則）。
/// - 明るさは RGB の最大値で測り、閾値はその画面の明るさの分布（2〜98 パーセンタイルの幅）に対する比で決める
/// - 線の太さ・照合の距離は画像の高さに対する比で決める
/// 既定の比は、合成画面のテスト（白・色付きの枠、彩度の高い明るい面、格子模様）で確かめた汎用の値。
/// </summary>
public sealed class PanelDetector
{
    /// <summary>線とみなす明るさ: その画面の明るさの幅のうち、下からこの比より上</summary>
    public double BrightFraction { get; init; } = 0.5;  // 根拠: 合成テスト ImagingTests（矩形の輪郭）＋実機性能評価（輪郭の検出の正否のみ・U5）
    /// <summary>線の両側のどちらかが、明るさの幅のこの比以上暗いこと（太い明るい面を線とみなさない）</summary>
    public double ContrastFraction { get; init; } = 0.25;  // 根拠: 同上
    /// <summary>線の太さの上限（画像の高さに対する比）</summary>
    public double MaxThicknessRatio { get; init; } = 0.01;  // 根拠: 同上（細い線＝画像の高さの1%以下）
    /// <summary>上辺の最小の長さ（画像の幅に対する比）</summary>
    public double MinTopRatio { get; init; } = 0.08;  // 根拠: 同上（小さすぎる矩形は数えない）
    /// <summary>左右の辺の最小の長さ（画像の高さに対する比）</summary>
    public double MinSideRatio { get; init; } = 0.05;  // 根拠: 同上
    /// <summary>線の太さを数えるとき、隣の画素を同じ線とみなす色の差（各色の差の和）の上限。明るさの幅（3色分）に対する比</summary>
    public double ColorStepFraction { get; init; } = 0.12;  // 根拠: 同上
    /// <summary>明るさの幅がこれより小さい画面（ほぼ一様な画面）では枠を探さない</summary>
    public int MinDynamicRange { get; init; } = 16;  // 根拠: 汎用（ほぼ一様な画面では探さない）

    public IReadOnlyList<PanelRect> Detect(Frame f)
    {
        int w = f.Width, h = f.Height;
        var px = f.Bgra;
        var lum = new short[w * h];
        var bright = new bool[w * h];
        var hist = new int[256]; int nh = 0;
        for (int i = 0, p = 0; i < w * h; i++, p += 4)
        {
            int mx = Math.Max(px[p + 2], Math.Max(px[p + 1], px[p]));
            lum[i] = (short)mx;
            if ((i & 7) == 0) { hist[mx]++; nh++; }
        }
        int P(double q) { int t = (int)(q * nh), c = 0; for (int v = 0; v < 256; v++) { c += hist[v]; if (c > t) return v; } return 255; }
        int lo = P(0.02), hi = P(0.98), range = hi - lo;
        if (range < MinDynamicRange) return Array.Empty<PanelRect>();
        int brightMin = lo + (int)Math.Round(BrightFraction * range);
        int EdgeContrast = Math.Max(1, (int)Math.Round(ContrastFraction * range));
        int MaxLineThickness = Math.Max(2, (int)Math.Round(MaxThicknessRatio * h));
        int SideOffset = MaxLineThickness / 2 + 2;
        int CornerSlack = MaxLineThickness;
        int MaxColorStep = Math.Max(6, (int)Math.Round(ColorStepFraction * 3 * range));
        for (int i = 0; i < w * h; i++) bright[i] = lum[i] >= brightMin;
        int o = SideOffset;
        // 画像の外は「暗い」とみなさない（画面の端に接した明るい面を線と誤らない）
        short At(int x, int y, short own) => (x < 0 || y < 0 || x >= w || y >= h) ? own : lum[y * w + x];
        // 明るい画素の縦・横の連続の長さ（線の太さの判定用）
        var upRun = new int[w * h]; var dnRun = new int[w * h]; var lfRun = new int[w * h]; var rtRun = new int[w * h];
        // 隣の画素が明るく、かつ色が近いときだけ同じ線（面）として続ける（色の違う明るい面に接した線を太いとみなさない）
        bool Same(int i, int j)
        {
            int p = i * 4, q = j * 4;
            return Math.Abs(px[p] - px[q]) + Math.Abs(px[p + 1] - px[q + 1]) + Math.Abs(px[p + 2] - px[q + 2]) <= MaxColorStep;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; if (!bright[i]) continue;
                upRun[i] = (y > 0 && bright[i - w] && Same(i, i - w) ? upRun[i - w] : 0) + 1;
                lfRun[i] = (x > 0 && bright[i - 1] && Same(i, i - 1) ? lfRun[i - 1] : 0) + 1;
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x; if (!bright[i]) continue;
                dnRun[i] = (y < h - 1 && bright[i + w] && Same(i, i + w) ? dnRun[i + w] : 0) + 1;
                rtRun[i] = (x < w - 1 && bright[i + 1] && Same(i, i + 1) ? rtRun[i + 1] : 0) + 1;
            }
        var hthin = new bool[w * h];
        var vthin = new bool[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (!bright[i]) continue;
            short own = lum[i];
            int l = own - EdgeContrast;
            hthin[i] = upRun[i] + dnRun[i] - 1 <= MaxLineThickness && (At(x, y - o, own) < l || At(x, y + o, own) < l);
            vthin[i] = lfRun[i] + rtRun[i] - 1 <= MaxLineThickness && (At(x - o, y, own) < l || At(x + o, y, own) < l);
        }
        // 下向きの縦線の長さ（連続）
        var vlen = new int[(h + 1) * w];
        for (int y = h - 1; y >= 0; y--)
        for (int x = 0; x < w; x++)
            vlen[y * w + x] = vthin[y * w + x] ? vlen[(y + 1) * w + x] + 1 : 0;
        int minTop = (int)(w * MinTopRatio), minSide = (int)(h * MinSideRatio), s = CornerSlack;
        int MaxV(int x0, int x1, int y0)
        {
            int best = 0;
            for (int yy = y0; yy < Math.Min(h, y0 + s); yy++)
            for (int xx = Math.Max(0, x0); xx <= Math.Min(w - 1, x1); xx++)
                best = Math.Max(best, vlen[yy * w + xx]);
            return best;
        }
        var found = new List<PanelRect>();
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                if (!hthin[y * w + x]) { x++; continue; }
                int a = x, last = x;
                // 線の途中の短い切れ目（線に接した小さな明るい点などで太く見えた所）は、太さの上限まで許す
                while (x < w && (hthin[y * w + x] || x - last <= s))
                {
                    if (hthin[y * w + x]) last = x;
                    x++;
                }
                x = last + 1;
                int b = x;  // [a, b)
                if (b - a < minTop) continue;
                int l = MaxV(a - s, a + s, y), r = MaxV(b - 1 - s, b - 1 + s, y);
                if (l < minSide || r < minSide) continue;
                var rect = new PanelRect(a, y, b - a, Math.Min(l, r));
                if (!found.Any(q => Math.Abs(q.X - rect.X) < 6 && Math.Abs(q.Y - rect.Y) < 6 && Math.Abs(q.Width - rect.Width) < 12)) found.Add(rect);
            }
        }
        return found;
    }

    public bool AnyPanel(Frame f) => Detect(f).Count > 0;
}
