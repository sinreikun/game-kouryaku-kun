using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Observation;

/// <summary>
/// v0.30.1: 画面全体の共通の平行移動（視点の移動のような、多くの領域が同じだけずれる動き）。意味（カメラ・自分）は付けない。
/// 細かい格子の区画あたり 4×4 点の明るさの縮小画像（128×96）で、ずれの候補を総当たりし、差の和がいちばん小さいずれを選ぶ。
/// ずれで差の半分以上が説明できるときだけ共通の動きとする（説明できない動きは個々の候補のまま）
/// </summary>
public sealed class CommonMotion
{
    public const int Sx = FineGrid.DefaultCols * 4, Sy = FineGrid.DefaultRows * 4;   // 根拠: 細かい区画あたり 4×4 点（区画の 1/4 の細かさでずれを測る）
    public int PixelThreshold { get; init; } = new DiffOptions().PixelThreshold;   // 根拠: 画素の差の既存のしきい値
    private byte[]? _prev;
    public long Shifted { get; private set; }

    public static byte[] Downsample(Frame f)
    {
        var o = new byte[Sx * Sy];
        for (int y = 0; y < Sy; y++)
        for (int x = 0; x < Sx; x++)
        {
            int px0 = x * f.Width / Sx, px1 = Math.Max(px0 + 1, (x + 1) * f.Width / Sx), py0 = y * f.Height / Sy, py1 = Math.Max(py0 + 1, (y + 1) * f.Height / Sy);
            long s = 0; int n = 0;
            for (int py = py0; py < py1; py += Math.Max(1, (py1 - py0) / 3))
            for (int px = px0; px < px1; px += Math.Max(1, (px1 - px0) / 3))
            { int i = (py * f.Width + px) * 4; s += (f.Bgra[i] + 2 * f.Bgra[i + 1] + f.Bgra[i + 2]) / 4; n++; }
            o[y * Sx + x] = (byte)(s / Math.Max(1, n));
        }
        return Flatten(o, Sx, Sy, ShadeWindow);
    }

    public const int ShadeWindow = 25;   // 根拠: 128×96 の約 1/5（マス数個分より広い、画面に固定の明るさのむら〔周辺の暗さなど〕の大きさ）

    /// <summary>
    /// v0.30.7: 画面に固定の明るさのむら（周辺を暗くする効果など）を除く。各点を、まわり ShadeWindow 四方の平均で割って 128 倍する。
    /// むらはずれても動かないので、除かないと、ずらしたときに差が残る（#130 で上下の移動の差の比が 0.9 前後にとどまった）
    /// </summary>
    public static byte[] Flatten(byte[] a, int w, int h, int win)
    {
        var ii = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++) { long row = 0; for (int x = 0; x < w; x++) { row += a[y * w + x]; ii[(y + 1) * (w + 1) + x + 1] = ii[y * (w + 1) + x + 1] + row; } }
        var o = new byte[w * h]; int r = win / 2;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1), y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            long sum = ii[y1 * (w + 1) + x1] - ii[y0 * (w + 1) + x1] - ii[y1 * (w + 1) + x0] + ii[y0 * (w + 1) + x0];
            double m = (double)sum / ((x1 - x0) * (y1 - y0));
            o[y * w + x] = (byte)Math.Clamp(128.0 * a[y * w + x] / (m + 1), 0, 255);
        }
        return o;
    }

    private static double Sad(byte[] a, byte[] cur, int dx, int dy, int w = Sx, int h = Sy, int step = 1)
    {
        var b = cur; long s = 0; int n = 0;
        for (int y = Math.Max(0, dy); y < Math.Min(h, h + dy); y += step)
        for (int x = Math.Max(0, dx); x < Math.Min(w, w + dx); x += step) { s += Math.Abs(b[y * w + x] - a[(y - dy) * w + (x - dx)]); n++; }
        return n == 0 ? double.MaxValue : (double)s / n;
    }

    public const int CoarseTop = 6;   // 根拠: 粗い候補の上位（上下左右と斜めの近い候補が入る数）
    public const int CoarseX = 12, CoarseY = 9;   // 根拠: 粗い縮小 32×24 の 3/8（重なりが画面の 4 割近く残る範囲）

    /// <summary>128×96 を 4×4 点ずつ平均して 32×24 にする</summary>
    private static byte[] Shrink(byte[] a)
    {
        int w = Sx / 4, h = Sy / 4; var o = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { int s = 0; for (int yy = 0; yy < 4; yy++) for (int xx = 0; xx < 4; xx++) s += a[(y * 4 + yy) * Sx + x * 4 + xx]; o[y * w + x] = (byte)(s / 16); }
        return o;
    }

    /// <summary>v0.30.4: 直前に調べたずれ（dx, dy〔細かい区画〕, 全体の差の比, 良くなった区画の割合）。記録して実機で判定を確かめる</summary>
    public double[]? LastProbe { get; private set; }
    /// <summary>v0.30.6: 直前の判定のずらしても消えない差（底）</summary>
    public double LastFloor { get; private set; }
    public double LastMedian0 { get; private set; }
    /// <summary>v0.32.3: 直前の判定の理由（shifted / floor = ずらしてもそろう区画が少ない / improved = 良くなった区画が過半でない / small = 変わった区画が画面の半分未満）。診断のため</summary>
    public string LastReason { get; private set; } = "";
    /// <summary>v0.32.4: ふだんの変化を比べられる区画で測る（false で v0.32.3 までの測り方。比べるためだけ）</summary>
    /// <summary>v0.32.8: 診断用。直前に当てはめたずれの場（縮小の点。dx の 1, X, Y, XY → dy の 1, X, Y, XY）。採らなかったときも残す</summary>
    public double[]? LastWarpFit { get; private set; }
    public bool ComparableMedian { get; init; } = true;
    /// <summary>v0.32.3: 画面の過半が変わった手の、細かい明るさ（512×384）の中央 128×96 の切り出し（前・今）。ずらしてもそろわない差を後で確かめる（判定には使わない）</summary>
    public (byte[] Prev, byte[] Cur)? LastHiCrop { get; private set; }
    public static byte[] Crop(byte[] hi) { var o = new byte[Sx * Sy]; int x0 = (HX - Sx) / 2, y0 = (HY - Sy) / 2; for (int y = 0; y < Sy; y++) Array.Copy(hi, (y0 + y) * HX + x0, o, y * Sx, Sx); return o; }
    /// <summary>v0.30.6: 画面の過半が変わった手の、前と今の明るさの縮小（128×96）。診断のために記録する（判定には使わない）</summary>
    public (byte[] Prev, byte[] Cur)? LastSample { get; private set; }

    /// <summary>前の画面からの共通のずれ（細かい区画の単位）と、ずれを差し引いても変わった細かい区画。共通のずれがなければ null</summary>
    /// <summary>画面の過半の区画がずれで良くなった手（共通のずれの候補）</summary>
    public long Checked { get; private set; }
    /// <summary>ずれで差の半分以上は説明できたが、画面の一部だけだった手、または過半の区画は良くなったが全体の差は説明できなかった手</summary>
    public long Unexplained { get; private set; }
    /// <summary>v0.32.3: 説明できなかった手の内訳（底が高い／良くなった区画が過半でない）</summary>
    public long UnexplainedFloor { get; private set; }
    public long UnexplainedImproved { get; private set; }
    public int LastImproved { get; private set; }
    public int LastComparable { get; private set; }

    public const int HX = FineGrid.DefaultCols * 16, HY = FineGrid.DefaultRows * 16;   // 根拠: 細かい区画あたり 16×16 点（v0.30.5: 128×96 では草のような細かい模様が 1 点の中でずれ、差が残った）
    private byte[]? _prevHi;
    public const double NoiseFloor = 4;   // 根拠: 取り込みのノイズ（2×2 画素の平均で ±2 程度）の 2 倍

    /// <summary>細かい区画の 1/16 の細かさの明るさ（各点は 2×2 画素の平均）</summary>
    public static byte[] DownsampleHi(Frame f)
    {
        var o = new byte[HX * HY];
        for (int y = 0; y < HY; y++)
        {
            int py = Math.Min(f.Height - 2, y * f.Height / HY);
            for (int x = 0; x < HX; x++)
            {
                int px = Math.Min(f.Width - 2, x * f.Width / HX), s = 0;
                for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) { int k = ((py + dy) * f.Width + px + dx) * 4; s += (f.Bgra[k] + 2 * f.Bgra[k + 1] + f.Bgra[k + 2]) / 4; }
                o[y * HX + x] = (byte)(s / 4);
            }
        }
        return Flatten(o, HX, HY, ShadeWindow * 4);
    }

    /// <param name="changed">細かい格子で変わった区画（ノイズに強いハッシュ）。v0.30.5: ずれで説明できるかは、変わった区画の中で数える（動かない枠・UI は数えない）</param>
    public (double Dx, double Dy, IReadOnlyList<int> Residual)? Estimate(Frame f, IReadOnlyCollection<int> changed)
    {
        var cur = Downsample(f); var prev = _prev; _prev = cur; LastProbe = null; LastSample = null; LastHiCrop = null; LastReason = ""; LastField = null; LastWarp = null; LastWarpNote = ""; LastGlobal = null;
        var hi = DownsampleHi(f); var prevHi = _prevHi; _prevHi = hi;
        if (prev == null || prevHi == null) return null;
        double s0 = Sad(prev, cur, 0, 0); if (s0 <= 0) return null;
        // 粗い縮小（32×24）で画面の 3/8 までのずれを探し、128×96 で ±4 点、512×384 で ±6 点と詰める
        byte[] pc = Shrink(prev), cc = Shrink(cur);
        // v0.30.7: 粗い縮小では細かい模様が平均で消え、いちばん良いずれが外れることがある。粗い候補の上位 CoarseTop 個と (0,0) のまわりを 128×96 で詰める
        var coarse = new List<(double S, int X, int Y)>();
        for (int dy = -CoarseY; dy <= CoarseY; dy++)
        for (int dx = -CoarseX; dx <= CoarseX; dx++) coarse.Add((Sad(pc, cc, dx, dy, Sx / 4, Sy / 4), dx, dy));
        int mx = 0, my = 0; double mbest = s0;
        var starts = coarse.OrderBy(c => c.S).ThenBy(c => Math.Abs(c.X) + Math.Abs(c.Y)).Take(CoarseTop).Select(c => (c.X * 4, c.Y * 4)).Prepend((0, 0)).Distinct().ToList();
        var tried = new HashSet<(int, int)>();
        foreach (var (ox, oy) in starts)
        for (int dy = oy - 4; dy <= oy + 4; dy++)
        for (int dx = ox - 4; dx <= ox + 4; dx++)
        {
            if ((dx == 0 && dy == 0) || !tried.Add((dx, dy))) continue;
            double sv = Sad(prev, cur, dx, dy);
            if (sv < mbest - 1e-9) { mbest = sv; mx = dx; my = dy; }
        }
        if (mx == 0 && my == 0)
        {
            // v0.32.6: どうずらしても差が減らなかった手も、画面の過半が変わっていれば縮小画像を記録する（#152 の上下の手 35 手がすべてこれで、記録がなかった。判定は変えない）
            LastProbe = new[] { 0, 0, 1.0, 0 }; LastReason = "no_offset";
            if (changed.Count * 2 >= FineGrid.DefaultCols * FineGrid.DefaultRows) { LastSample = (prev, cur); LastHiCrop = (Crop(prevHi), Crop(hi)); }
            return Warp ? TryWarp(prev, cur, prevHi, hi, changed, countChecked: true) : null;
        }
        double h0 = Sad(prevHi, cur: hi, 0, 0, HX, HY, step: 2); int bx = mx * 4, by = my * 4; double best = double.MaxValue;
        for (int dy = my * 4 - 6; dy <= my * 4 + 6; dy++)
        for (int dx = mx * 4 - 6; dx <= mx * 4 + 6; dx++)
        {
            double sv = Sad(prevHi, hi, dx, dy, HX, HY, step: 2);
            if (sv < best - 1e-9) { best = sv; bx = dx; by = dy; }
        }
        // 根拠: 変わった区画の過半で、ずれが区画の差を半分より小さくすること（1 つの物の動きは変わった区画の一部しか良くしない。
        //       明るさの差の小さい模様のずれも数える。動かない枠・表示は変わった区画に入らないので数えない）
        // v0.30.7: 画面の部分ごとにずれが少し違う（斜めから見た画面では、上下の移動で奥と手前のずれが違う）。4×3 の部分ごとに、全体のずれの近く ±BlockSearch 点で詰める
        var c0 = CellSadHi(prevHi, hi, 0, 0);
        var bs = new (int X, int Y)[BlocksX * BlocksY];
        for (int k = 0; k < bs.Length; k++)
        {
            int gx0 = (k % BlocksX) * (HX / BlocksX), gy0 = (k / BlocksX) * (HY / BlocksY);
            double bb = double.MaxValue; bs[k] = (bx, by);
            for (int dy = by - BlockSearch; dy <= by + BlockSearch; dy += 2)
            for (int dx = bx - BlockSearch; dx <= bx + BlockSearch; dx += 2)
            {
                double v = BlockSad(prevHi, hi, dx, dy, gx0, gy0, HX / BlocksX, HY / BlocksY);
                if (v < bb - 1e-9) { bb = v; bs[k] = (dx, dy); }
            }
        }
        var cb = CellSadHi(prevHi, hi, 0, 0, i => bs[(i % FineGrid.DefaultCols) / (FineGrid.DefaultCols / BlocksX) + (i / FineGrid.DefaultCols) / (FineGrid.DefaultRows / BlocksY) * BlocksX]);
        // 変わった区画: ノイズに強いハッシュで変わった区画と、明るさの差の平均が NoiseFloor 以上の区画（明るさの差の小さい模様のずれはハッシュでは変化にならない）
        var set = new HashSet<int>(changed); for (int i = 0; i < c0.Length; i++) if (c0[i] >= NoiseFloor) set.Add(i);
        changed = set;
        // v0.30.6: 揺れる模様・取り込みのノイズで、ずらしても消えない差（底）がある。底はずらした後の差の下位 1/4（いちばんよく合った区画の残り）。
        //          良くなった＝底を引いた差が半分未満になる。底より小さい差しかない区画は比べない
        var cbs = changed.Where(i => cb[i] >= 0).Select(i => cb[i]).OrderBy(v => v).ToList();
        double floor = cbs.Count == 0 ? 0 : cbs[cbs.Count / 4];   // 根拠: 下位 1/4（四分位）
        int improved = 0, comparable = 0, all = 0;
        foreach (var i in changed) { all++; if (cb[i] < 0 || c0[i] <= floor) continue; comparable++; if ((cb[i] - floor) * 2 < c0[i] - floor) improved++; }
        LastFloor = Math.Round(floor, 2);
        // v0.32.4: ふだんの変化（中央値）は、ずらした後の底より大きく変わった区画（比べられる区画。良くなった区画を数えるのと同じ区画）で測る。
        //          v0.32.3 までは底以下しか変わっていない区画も入れていて、暗く模様の薄い画面〔#150 のダンジョン〕では中央値が下がり、
        //          そろった移動も「ずらしてもそろわない」と判定された（154/171 手）。暗転・切り替わりではほぼすべての区画が大きく変わるので中央値は変わらない
        var c0s = changed.Where(i => c0[i] >= 0 && (!ComparableMedian || c0[i] > floor)).Select(i => c0[i]).OrderBy(v => v).ToList();
        LastMedian0 = c0s.Count == 0 ? 0 : Math.Round(c0s[c0s.Count / 2], 2);
        LastSample = all * 2 >= FineGrid.DefaultCols * FineGrid.DefaultRows ? (prev, cur) : null;
        LastProbe = new[] { bx / 16.0, by / 16.0, Math.Round(best / Math.Max(1e-9, h0), 3), comparable == 0 ? 0 : Math.Round((double)improved / comparable, 3) };
        LastHiCrop = LastSample != null ? (Crop(prevHi), Crop(hi)) : null;
        LastImproved = improved; LastComparable = comparable;
        if (all * 2 < FineGrid.DefaultCols * FineGrid.DefaultRows) { LastReason = "small"; return null; }   // 根拠: 画面の過半の区画が変わった手だけ（1 つの物の動きは画面の一部）
        Checked++;
        // v0.30.7: 根拠: ずらすと変わった区画の 1/4 が、ふだんの変化の半分未満までそろうこと（画面の切り替わり・暗転では、どうずらしてもそろう区画がない。#130 の暗転は底/中央値 0.68 以上、移動は 0.3 以下）
        if (floor * 2 > LastMedian0) { LastReason = "floor"; if (Warp && TryWarp(prev, cur, prevHi, hi, changed, false) is { } w1) return w1; Unexplained++; UnexplainedFloor++; return null; }
        if (improved * 2 < comparable) { LastReason = "improved"; if (Warp && TryWarp(prev, cur, prevHi, hi, changed, false) is { } w2) return w2; Unexplained++; UnexplainedImproved++; return null; }
        LastReason = "shifted";
        var res = changed.Where(i => cb[i] >= 0 && c0[i] > floor && (cb[i] - floor) * 2 >= c0[i] - floor && cb[i] - floor > PixelThreshold).OrderBy(i => i).ToList();   // ずれで説明できず、ずらしても差が残る区画
        // v0.32.9: これまでの平行移動の判定で受け入れた（仮説の比べはしない。確からしさの内訳はその判定の数）
        LastGlobal = new GlobalTransformConfidence { Decision = "translation", Accepted = true, Method = "translation_check", Field = new[] { bx / 4.0, 0, 0, 0, by / 4.0, 0, 0, 0 },
            Continuity = _prevCenter is { } pc1 ? Math.Round(Math.Sqrt(Math.Pow(bx / 4.0 - pc1.U, 2) + Math.Pow(by / 4.0 - pc1.V, 2)), 2) : null };
        Shifted++; _prevCenter = (bx / 4.0, by / 4.0);   // 細かい明るさの点（1/16 区画）→ 縮小の点（1/4 区画）
        return (bx / 16.0, by / 16.0, res);
    }

    // ===== v0.32.7: 場所によって違うずれ（行ごとに違うずれ・拡大縮小を含む、なめらかなずれの場） =====
    /// <summary>v0.32.7: 1 つの平行移動で説明できないとき、なめらかなずれの場（双一次）を試す（false で v0.32.6 まで）</summary>
    public bool Warp { get; init; } = true;
    /// <summary>直前に受け入れたずれの場（細かい区画の座標 → ずれ〔細かい区画〕）。平行移動のときは null</summary>
    public Func<double, double, (double Dx, double Dy)>? LastField { get; private set; }
    /// <summary>直前に受け入れたずれの場の係数（dx: p0..p3, dy: q0..q3。正規化した座標 X, Y∈[−1,1] の 1, X, Y, XY の係数。単位は細かい区画）</summary>
    public double[]? LastWarp { get; private set; }
    /// <summary>v0.32.8: 直前にずれの場を試したときの経過（診断）</summary>
    public string LastWarpNote { get; private set; } = "";
    public long WarpShifted { get; private set; }
    /// <summary>ずれの場で説明できた手の、画面の中央のずれの向き別の数（x+ / x− / y+ / y− の組。画面の中身が動いた向き。y+ は画面の下へ）</summary>
    public Dictionary<string, long> WarpByDir { get; } = new(StringComparer.Ordinal);
    public const int WBX = 8, WBY = 6;   // 根拠: 縮小 128×96 を 16×16 点（細かい区画 4×4）ずつに分ける。双一次の 4 係数×2 に対して 48 か所（十分に多い）
    public const int WarpSearchY = Sy / 4 / 2;   // 根拠: 粗い縮小 24 行の半分（行ごとにずれが違うので、上下は画面の半分まで探す）

    private (double, double, IReadOnlyList<int>)? TryWarp(byte[] prev, byte[] cur, byte[] prevHi, byte[] hi, IReadOnlyCollection<int> changedIn, bool countChecked)
    {
        byte[] pc = Shrink(prev), cc = Shrink(cur); int sw = Sx / 4, sh = Sy / 4, bw = Sx / WBX, bh = Sy / WBY;
        var pts = new List<(double X, double Y, double U, double V, double W)>();
        for (int by = 0; by < WBY; by++)
        for (int bx = 0; bx < WBX; bx++)
        {
            int x0 = bx * bw, y0 = by * bh;
            double s0 = RegionSad(prev, cur, 0, 0, x0, y0, bw, bh, Sx, Sy); if (s0 < NoiseFloor) continue;   // 変わっていない所はずれの手がかりにならない
            var coarse = new List<(double S, int X, int Y)>();
            for (int dy = -WarpSearchY; dy <= WarpSearchY; dy++)
            for (int dx = -CoarseX; dx <= CoarseX; dx++)
            { double v = RegionSad(pc, cc, dx, dy, x0 / 4, y0 / 4, bw / 4, bh / 4, sw, sh); if (v < double.MaxValue) coarse.Add((v, dx, dy)); }
            double best = s0; int mx = 0, my = 0;
            var evals = new Dictionary<(int, int), double> { [(0, 0)] = s0 };
            foreach (var (_, ox, oy) in coarse.OrderBy(c => c.S).Take(CoarseTop))
            for (int dy = oy * 4 - 4; dy <= oy * 4 + 4; dy++)
            for (int dx = ox * 4 - 4; dx <= ox * 4 + 4; dx++)
            {
                if (evals.ContainsKey((dx, dy))) continue;
                double v = RegionSad(prev, cur, dx, dy, x0, y0, bw, bh, Sx, Sy); evals[(dx, dy)] = v;
                if (v < best - 1e-9) { best = v; mx = dx; my = dy; }
            }
            // v0.32.8: 重み = ずらして減った差の割合 × その減り方のうち、ほかのずれ（縮小の 2 点より離れた所）では得られない分。
            //          くり返し模様〔#153 の床〕ではいくつものずれで同じくらいそろうので、その所は手がかりにしない
            double second = evals.Where(kv => Math.Max(Math.Abs(kv.Key.Item1 - mx), Math.Abs(kv.Key.Item2 - my)) > 2).Select(kv => kv.Value).DefaultIfEmpty(s0).Min();
            double w = (s0 - best) / s0 * (s0 - best > 1e-9 ? Math.Clamp((second - best) / (s0 - best), 0, 1) : 0);
            if (w <= 0) continue;   // 模様のない所・説明できない所・どのずれか決まらない所は使わない
            pts.Add((2.0 * (x0 + bw / 2.0) / Sx - 1, 2.0 * (y0 + bh / 2.0) / Sy - 1, mx, my, w));
        }
        LastWarpNote = $"pts={pts.Count}"; LastWarpFit = null;
        if (pts.Count < 8) return Reject();   // 根拠: 係数 4 の 2 倍（過半を残しても係数より多い）
        // v0.32.8: 種から育てる当てはめ。いちばん重みの多く集まる所ごとのずれ（縮小の 2 点以内）を種にし、そこに集まった所で当てはめ、
        //          当てはめから 2 点以内の所を集め直して当て直す（3 回）。くり返し模様・個々に動く物・画面に固定の表示の所に引っぱられない
        bool Near(double[] fu0, double[] fv0, (double X, double Y, double U, double V, double W) p) => Math.Abs(Eval(fu0, p.X, p.Y) - p.U) <= 2 && Math.Abs(Eval(fv0, p.X, p.Y) - p.V) <= 2;
        var seed = pts.OrderByDescending(q => pts.Where(p => Math.Abs(p.U - q.U) <= 2 && Math.Abs(p.V - q.V) <= 2).Sum(p => p.W)).First();
        double[] pu = { seed.U, 0, 0, 0 }, pv = { seed.V, 0, 0, 0 };
        // 種から隣へ広げる: 上下左右の隣の所のずれが、すでに入れた所のずれから縮小の 2 点以内なら入れる（ずれの場はなめらか。拡大縮小があっても隣どうしはほぼ同じ）
        double gx = 2.0 / WBX * 1.01, gy = 2.0 / WBY * 1.01;   // 隣の区画の中心の間隔（丸めの余裕 1%）
        var grown = new List<(double X, double Y, double U, double V, double W)> { seed }; var todo = new Queue<(double X, double Y, double U, double V, double W)>(grown);
        while (todo.Count > 0)
        {
            var q = todo.Dequeue();
            foreach (var p in pts)
                if (!grown.Contains(p) && Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y) > 0 && (Math.Abs(p.X - q.X) <= gx && Math.Abs(p.Y - q.Y) < 1e-6 || Math.Abs(p.Y - q.Y) <= gy && Math.Abs(p.X - q.X) < 1e-6)
                    && Math.Abs(p.U - q.U) <= 2 && Math.Abs(p.V - q.V) <= 2) { grown.Add(p); todo.Enqueue(p); }
        }
        var use = grown;
        for (int it = 0; it < 3; it++)   // 広げた所で当てはめ、当てはめから 2 点以内の所を集め直して当て直す
        {
            int k = use.Count >= 8 ? 4 : use.Count >= 6 ? 3 : 1;   // 根拠: 係数の数の 2 倍の所があるときだけその項を使う（1, X, Y で 6、XY も入れて 8）
            if (k > 1) { pu = Fit(use, p => p.U, k); pv = Fit(use, p => p.V, k); }
            else { double sw0 = use.Sum(p => p.W); if (sw0 > 0) { pu = new[] { use.Sum(p => p.W * p.U) / sw0, 0, 0, 0 }; pv = new[] { use.Sum(p => p.W * p.V) / sw0, 0, 0, 0 }; } }
            var cu = pu; var cv = pv; use = pts.Where(p => Near(cu, cv, p)).ToList(); if (use.Count == 0) break;
        }
        // 細かい区画ごとのずれ（縮小の点 → 細かい明るさの点は 4 倍）で、平行移動と同じ基準で確かめる
        (int, int) Cell(int i)
        {
            double X = 2.0 * ((i % FineGrid.DefaultCols) + 0.5) / FineGrid.DefaultCols - 1, Y = 2.0 * ((i / FineGrid.DefaultCols) + 0.5) / FineGrid.DefaultRows - 1;
            return ((int)Math.Round(Eval(pu, X, Y) * 4), (int)Math.Round(Eval(pv, X, Y) * 4));
        }
        double cxu = Eval(pu, 0, 0), cyv = Eval(pv, 0, 0);
        LastWarpFit = pu.Concat(pv).ToArray();
        double maxMove = pts.Max(p => Math.Max(Math.Abs(Eval(pu, p.X, p.Y)), Math.Abs(Eval(pv, p.X, p.Y))));
        if (maxMove < 1) return Reject();   // 縮小の 1 点未満しか動かない＝ずれではない
        // v0.32.8: 根拠: 画面の四隅のずれと中央のずれの違いが、中央のずれの大きいほうの成分を超えないこと（どの隅も中央と逆向きには動かない＝1 つの移動）。
        var corners = new[] { (-1.0, -1.0), (1.0, -1.0), (-1.0, 1.0), (1.0, 1.0) };
        double spread = corners.Max(c => Math.Max(Math.Abs(Eval(pu, c.Item1, c.Item2) - Eval(pu, 0, 0)), Math.Abs(Eval(pv, c.Item1, c.Item2) - Eval(pv, 0, 0))));
        double center = Math.Max(Math.Abs(Eval(pu, 0, 0)), Math.Abs(Eval(pv, 0, 0)));
        LastWarpNote += $" fit=({string.Join(",", pu.Select(v => Math.Round(v, 1)))}|{string.Join(",", pv.Select(v => Math.Round(v, 1)))})";
        // v0.32.9 (t108u): 画面全体の変換かどうかを、仮説どうしの比べ（BIC で近似したベイズ因子）で決める
        var conf = CompareHypotheses(prev, cur, pts, bw, bh, pu, pv, (seed.U, seed.V));
        LastGlobal = conf; GlobalCompared++; GlobalByDecision[conf.Decision] = GlobalByDecision.GetValueOrDefault(conf.Decision) + 1;
        LastWarpNote += " " + conf.Note;
        // 根拠: 共通の変換（field か translation）が最も確からしく、従う点が画面を 4×3 に分けた場所の過半に広がっていること
        //       （画面全体の変換は画面のあちこちで観測される。1 か所に固まった一致は、個々に動く物・くり返し模様の取り違え〔#153 の手 6・34〕）
        if (conf.Decision is not ("field" or "translation")) return Reject();
        if (conf.Coverage * 2 < 1) { LastGlobal = conf with { Decision = "none" }; LastWarpNote += " cov<half"; return Reject(); }
        pu = conf.Field.Take(4).ToArray(); pv = conf.Field.Skip(4).ToArray();   // 最尤に合わせ直した場を使う
        spread = corners.Max(c => Math.Max(Math.Abs(Eval(pu, c.Item1, c.Item2) - Eval(pu, 0, 0)), Math.Abs(Eval(pv, c.Item1, c.Item2) - Eval(pv, 0, 0))));
        center = Math.Max(Math.Abs(Eval(pu, 0, 0)), Math.Abs(Eval(pv, 0, 0)));
        if (spread > center) { LastWarpNote += $" spread={spread:0.0}>center={center:0.0}"; LastGlobal = conf with { Decision = "none", Accepted = false }; return Reject(); }
        cxu = Eval(pu, 0, 0); cyv = Eval(pv, 0, 0); LastWarpFit = pu.Concat(pv).ToArray();
        LastGlobal = conf with { Accepted = true, Continuity = _prevCenter is { } pc0 ? Math.Round(Math.Sqrt(Math.Pow(cxu - pc0.U, 2) + Math.Pow(cyv - pc0.V, 2)), 2) : null };
        _prevCenter = (cxu, cyv);
        var c0 = CellSadHi(prevHi, hi, 0, 0);
        var cb = CellSadHi(prevHi, hi, 0, 0, Cell);
        var set = new HashSet<int>(changedIn); for (int i = 0; i < c0.Length; i++) if (c0[i] >= NoiseFloor) set.Add(i);
        var cbs = set.Where(i => cb[i] >= 0).Select(i => cb[i]).OrderBy(v => v).ToList();
        double floor = cbs.Count == 0 ? 0 : cbs[cbs.Count / 4];
        int improved = 0, comparable = 0;
        foreach (var i in set) { if (cb[i] < 0 || c0[i] <= floor) continue; comparable++; if ((cb[i] - floor) * 2 < c0[i] - floor) improved++; }
        var c0s = set.Where(i => c0[i] >= 0 && c0[i] > floor).Select(i => c0[i]).OrderBy(v => v).ToList();
        double med0 = c0s.Count == 0 ? 0 : c0s[c0s.Count / 2];
        if (countChecked) Checked++;
        LastWarpNote += $" floor={floor:0.0} med0={med0:0.0} imp={improved}/{comparable}";
        LastFloor = Math.Round(floor, 2); LastMedian0 = Math.Round(med0, 2); LastImproved = improved; LastComparable = comparable;
        var res = set.Where(i => cb[i] >= 0 && c0[i] > floor && (cb[i] - floor) * 2 >= c0[i] - floor && cb[i] - floor > PixelThreshold).OrderBy(i => i).ToList();
        LastWarp = pu.Concat(pv).Select(v => Math.Round(v / 4, 4)).ToArray();   // 縮小の点 → 細かい区画
        var fu = pu.ToArray(); var fv = pv.ToArray();
        LastField = (fx, fy) => (Eval(fu, 2 * (fx + 0.5) / FineGrid.DefaultCols - 1, 2 * (fy + 0.5) / FineGrid.DefaultRows - 1) / 4, Eval(fv, 2 * (fx + 0.5) / FineGrid.DefaultCols - 1, 2 * (fy + 0.5) / FineGrid.DefaultRows - 1) / 4);
        LastReason = conf.Decision == "field" ? "warp" : "model_translation"; LastProbe = new[] { Math.Round(cxu / 4, 4), Math.Round(cyv / 4, 4), 0, comparable == 0 ? 0 : Math.Round((double)improved / comparable, 3) };
        string dir = (Math.Abs(cyv) >= 2 ? (cyv > 0 ? "y+" : "y-") : "") + (Math.Abs(cxu) >= 2 ? (cxu > 0 ? "x+" : "x-") : "");   // 根拠: 縮小の 2 点（細かい区画の半分）
        if (dir.Length == 0) dir = "0";
        WarpByDir[dir] = WarpByDir.GetValueOrDefault(dir) + 1;
        WarpShifted++; Shifted++;
        return (cxu / 4, cyv / 4, res);

        static (double, double, IReadOnlyList<int>)? Reject() => null;
    }

    /// <summary>v0.32.9 (t108u): 直前の「画面全体の変換か」の比べ（null は比べていない）</summary>
    public GlobalTransformConfidence? LastGlobal { get; private set; }
    private (double U, double V)? _prevCenter;
    /// <summary>v0.32.9: 仮説を比べた手の数と、最も確からしかった仮説別の数（none / zero / translation / field。field は受け入れた数ではない）</summary>
    public long GlobalCompared { get; private set; }
    public Dictionary<string, long> GlobalByDecision { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// v0.32.9 (t108u): 区画ごとに見つけたずれ（局所の観測点）を、仮説ごとの混合モデルで説明し、BIC で比べる。
    /// 各点は「変換に従う（予測のまわりに正規分布）」か「従わない（探した範囲に一様＝個々に動く物・くり返し模様の取り違え・切り替わり）」のどちらか。
    /// 仮説: none（共通の変換なし。全点が一様）、zero（動かない）、translation（1 つのずれ）、field（双一次のずれの場）。係数・σ・従う割合を EM で最尤に合わせる。
    /// 根拠: BIC の罰 = 係数の数 / 2 × ln(点の数)。決め方は事後の確率が最も高い仮説（同じ事前。調整値なし）。σ² の下限は整数への丸めの分散 1/12
    /// </summary>
    private static GlobalTransformConfidence CompareHypotheses(byte[] prev, byte[] cur, List<(double X, double Y, double U, double V, double W)> pts, int bw, int bh, double[] pu0, double[] pv0, (double U, double V) seed)
    {
        int N = pts.Count; double lnN = Math.Log(N);
        double A = (2.0 * CoarseX * 4 + 1) * (2.0 * WarpSearchY * 4 + 1);   // 探した範囲の広さ（縮小の点）
        (double LL, double[] U, double[] V, double[] R) Em(double[] u, double[] v, int k)
        {
            u = u.ToArray(); v = v.ToArray(); double pi = 0.5, s2 = 1; var r = new double[N]; double ll = 0;   // 初めは半々・1 点
            for (int it = 0; it < 30; it++)   // 根拠: EM の繰り返しの上限（48 点・係数 8 で収まる回数。打ち切りは尤度を下げない）
            {
                ll = 0; double sr = 0, se = 0;
                for (int b = 0; b < N; b++)
                {
                    var p = pts[b]; double du = p.U - Eval(u, p.X, p.Y), dv = p.V - Eval(v, p.X, p.Y);
                    double g = pi * Math.Exp(-(du * du + dv * dv) / (2 * s2)) / (2 * Math.PI * s2), o = (1 - pi) / A;
                    r[b] = g / (g + o); ll += Math.Log(g + o); sr += r[b]; se += r[b] * (du * du + dv * dv);
                }
                pi = Math.Clamp(sr / N, 1.0 / N, 1 - 1.0 / N); s2 = Math.Max(1.0 / 12, se / Math.Max(1e-9, 2 * sr));
                if (k > 0)
                {
                    var rw = pts.Select((p, b) => (p.X, p.Y, p.U, p.V, W: r[b])).ToList();
                    if (k == 1) { double w = rw.Sum(q => q.W); if (w > 0) { u = new[] { rw.Sum(q => q.W * q.U) / w, 0, 0, 0 }; v = new[] { rw.Sum(q => q.W * q.V) / w, 0, 0, 0 }; } }
                    else { u = Fit(rw, q => q.U); v = Fit(rw, q => q.V); }
                }
            }
            return (ll, u, v, r);
        }
        double sNone = N * Math.Log(1 / A);
        var zero = Em(new double[4], new double[4], 0); double sZero = zero.LL - 2 / 2.0 * lnN;   // σ・割合
        var tr = Em(new[] { seed.U, 0, 0, 0 }, new[] { seed.V, 0, 0, 0 }, 1); double sTrans = tr.LL - 4 / 2.0 * lnN;   // ずれ 2・σ・割合
        var fi = Em(pu0, pv0, 4); double sField = fi.LL - 10 / 2.0 * lnN;   // 係数 8・σ・割合
        var sc = new[] { sNone, sZero, sTrans, sField }; double mx = sc.Max(); var e = sc.Select(x => Math.Exp(x - mx)).ToArray(); double sum = e.Sum();
        string[] names = { "none", "zero", "translation", "field" };
        int best = Array.IndexOf(sc, mx);
        var (u, v, r) = best == 2 ? (tr.U, tr.V, tr.R) : best == 3 ? (fi.U, fi.V, fi.R) : (zero.U, zero.V, zero.R);
        var inl = Enumerable.Range(0, N).Where(b => r[b] > 0.5).ToList();   // 根拠: 従う確率が従わない確率より高い点
        int cover = inl.Select(b => ((int)Math.Min(3, (pts[b].X + 1) * 2), (int)Math.Min(2, (pts[b].Y + 1) * 1.5))).Distinct().Count();
        var resid = inl.Select(b => Math.Sqrt(Math.Pow(pts[b].U - Eval(u, pts[b].X, pts[b].Y), 2) + Math.Pow(pts[b].V - Eval(v, pts[b].X, pts[b].Y), 2))).OrderBy(x => x).ToList();
        double before = 0, after = 0;
        foreach (var b in inl)
        {
            int x0 = (int)Math.Round((pts[b].X + 1) * Sx / 2 - bw / 2.0), y0 = (int)Math.Round((pts[b].Y + 1) * Sy / 2 - bh / 2.0);
            double a0 = RegionSad(prev, cur, 0, 0, x0, y0, bw, bh, Sx, Sy), a1 = RegionSad(prev, cur, (int)Math.Round(Eval(u, pts[b].X, pts[b].Y)), (int)Math.Round(Eval(v, pts[b].X, pts[b].Y)), x0, y0, bw, bh, Sx, Sy);
            if (a0 < double.MaxValue && a1 < double.MaxValue) { before += a0; after += a1; }
        }
        return new GlobalTransformConfidence
        {
            Decision = names[best], PNone = Math.Round(e[0] / sum, 4), PZero = Math.Round(e[1] / sum, 4), PTranslation = Math.Round(e[2] / sum, 4), PField = Math.Round(e[3] / sum, 4),
            LogOdds = Math.Round(mx - sc.Where((_, i) => i != best).Max(), 2), Field = u.Concat(v).Select(x => Math.Round(x, 3)).ToArray(),
            Inliers = inl.Count, Points = N, Coverage = Math.Round(cover / 12.0, 3),
            MedianResidual = resid.Count == 0 ? 0 : Math.Round(resid[resid.Count / 2], 2), ErrorReduction = before <= 0 ? 0 : Math.Round(1 - after / before, 3),
        };
    }
    private static double Eval(double[] p, double x, double y) => p[0] + p[1] * x + p[2] * y + p[3] * x * y;

    /// <summary>重みつき最小二乗（1, X, Y, XY）。解けないときは平行移動（重みつき平均）</summary>
    private static double[] Fit(List<(double X, double Y, double U, double V, double W)> pts, Func<(double X, double Y, double U, double V, double W), double> val, int k = 4)
    {
        var a = new double[4, 5]; for (int i = k; i < 4; i++) a[i, i] = 1;   // 使わない項は 0 に固定
        foreach (var p in pts)
        {
            double[] f = { 1, p.X, p.Y, p.X * p.Y }; double z = val(p);
            for (int i = 0; i < k; i++) { for (int j = 0; j < k; j++) a[i, j] += p.W * f[i] * f[j]; a[i, 4] += p.W * f[i] * z; }
        }
        for (int c = 0; c < 4; c++)
        {
            int piv = c; for (int r = c + 1; r < 4; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-9)
            {
                double sw = pts.Sum(p => p.W); return new[] { sw > 0 ? pts.Sum(p => p.W * val(p)) / sw : 0, 0, 0, 0 };
            }
            for (int j = 0; j < 5; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
            for (int r = 0; r < 4; r++) if (r != c) { double q = a[r, c] / a[c, c]; for (int j = 0; j < 5; j++) a[r, j] -= q * a[c, j]; }
        }
        return Enumerable.Range(0, 4).Select(i => a[i, 4] / a[i, i]).ToArray();
    }

    /// <summary>今の画面の (x0,y0) から w×h の範囲と、前の画面の (x−dx, y−dy) との差の平均（重なりが範囲の半分未満なら MaxValue）</summary>
    private static double RegionSad(byte[] prev, byte[] cur, int dx, int dy, int x0, int y0, int w, int h, int W, int H)
    {
        long s = 0; int n = 0;
        for (int y = y0; y < y0 + h; y++) { int py = y - dy; if (py < 0 || py >= H) continue; for (int x = x0; x < x0 + w; x++) { int px = x - dx; if (px < 0 || px >= W) continue; s += Math.Abs(cur[y * W + x] - prev[py * W + px]); n++; } }
        return n * 2 < w * h ? double.MaxValue : (double)s / n;   // 根拠: 範囲の半分以上が重なること
    }

    public const int BlocksX = 4, BlocksY = 3, BlockSearch = 12;   // 根拠: 細かい区画 8×8 ずつの部分。部分ごとのずれの違いは細かい区画の 3/4（12 点）まで

    private static double BlockSad(byte[] prev, byte[] cur, int dx, int dy, int x0, int y0, int w, int h)
    {
        long s = 0; int n = 0;
        for (int y = y0; y < y0 + h; y += 2)
        {
            int py = y - dy; if (py < 0 || py >= HY) continue;
            for (int x = x0; x < x0 + w; x += 2) { int px = x - dx; if (px < 0 || px >= HX) continue; s += Math.Abs(cur[y * HX + x] - prev[py * HX + px]); n++; }
        }
        return n * 4 < w * h / 4 ? double.MaxValue : (double)s / n;   // 根拠: 部分の 1/4 以上が重なること
    }

    private static double[] CellSadHi(byte[] prev, byte[] cur, int bx, int by, Func<int, (int X, int Y)>? perCell = null)
    {
        var r = new double[FineGrid.DefaultCols * FineGrid.DefaultRows];
        for (int cy = 0; cy < FineGrid.DefaultRows; cy++)
        for (int cx = 0; cx < FineGrid.DefaultCols; cx++)
        {
            long s = 0; int n = 0; bool edge = false;
            if (perCell != null) (bx, by) = perCell(cy * FineGrid.DefaultCols + cx);
            for (int y = cy * 16; y < cy * 16 + 16 && !edge; y += 2)
            for (int x = cx * 16; x < cx * 16 + 16; x += 2)
            {
                int px = x - bx, py = y - by;
                if (px < 0 || py < 0 || px >= HX || py >= HY) { edge = true; break; }
                s += Math.Abs(cur[y * HX + x] - prev[py * HX + px]); n++;
            }
            r[cy * FineGrid.DefaultCols + cx] = edge || n == 0 ? -1 : (double)s / n;
        }
        return r;
    }

    /// <summary>細かい区画ごとの平均の差（前の画面の外になる区画は −1）</summary>
    private static double[] CellSad(byte[] prev, byte[] cur, int bx, int by)
    {
        var r = new double[FineGrid.DefaultCols * FineGrid.DefaultRows];
        for (int cy = 0; cy < FineGrid.DefaultRows; cy++)
        for (int cx = 0; cx < FineGrid.DefaultCols; cx++)
        {
            long s = 0; int n = 0; bool edge = false;
            for (int y = cy * 4; y < cy * 4 + 4; y++)
            for (int x = cx * 4; x < cx * 4 + 4; x++)
            {
                int px = x - bx, py = y - by;
                if (px < 0 || py < 0 || px >= Sx || py >= Sy) { edge = true; continue; }
                s += Math.Abs(cur[y * Sx + x] - prev[py * Sx + px]); n++;
            }
            r[cy * FineGrid.DefaultCols + cx] = edge || n == 0 ? -1 : (double)s / n;
        }
        return r;
    }

    private List<int> Changed(byte[] prev, byte[] cur, int bx, int by)
    {
        var res = new List<int>();
        for (int cy = 0; cy < FineGrid.DefaultRows; cy++)
        for (int cx = 0; cx < FineGrid.DefaultCols; cx++)
        {
            long s = 0; int n = 0; bool edge = false;
            for (int y = cy * 4; y < cy * 4 + 4; y++)
            for (int x = cx * 4; x < cx * 4 + 4; x++)
            {
                int px = x - bx, py = y - by;
                if (px < 0 || py < 0 || px >= Sx || py >= Sy) { edge = true; continue; }
                s += Math.Abs(cur[y * Sx + x] - prev[py * Sx + px]); n++;
            }
            if (!edge && n > 0 && (double)s / n > PixelThreshold) res.Add(cy * FineGrid.DefaultCols + cx);   // 画面の端から入ってきた区画は比べられないので変化にしない
        }
        return res;
    }
}
