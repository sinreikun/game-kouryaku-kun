using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Perception;

/// <summary>振る舞いだけで決める領域の汎用の種類（phase2-design §3.1）。ゲーム上の意味は付けない</summary>
public enum RegionKind { Unknown, Translating, FixedText, FixedGraphic, Overlay, FixedInTranslating, Blank }

/// <summary>見つけた領域（取得画像の画素座標の矩形と、相対座標）</summary>
public sealed record Region(RegionKind Kind, int X, int Y, int Width, int Height, int Blocks, double Confidence, double FillRatio)
{
    public (double X, double Y, double W, double H) Relative(int imgW, int imgH) => (X / (double)imgW, Y / (double)imgH, Width / (double)imgW, Height / (double)imgH);
    public double Area => (double)Width * Height;
}

/// <summary>配置の推定結果（領域の汎用の種類だけ）。操作対象・注目点のような、特定の物を前提にした量は持たない（2026-10-18 09:36）</summary>
public sealed record LayoutEstimate(int ImageWidth, int ImageHeight, int BlockSize, RegionKind[] Blocks, int BlocksX, int BlocksY,
    IReadOnlyList<Region> Regions, int MoveTurns, int BigTurns)
{
    public RegionKind KindAt(int x, int y)
    {
        int bx = Math.Clamp(x / BlockSize, 0, BlocksX - 1), by = Math.Clamp(y / BlockSize, 0, BlocksY - 1);
        return Blocks[by * BlocksX + bx];
    }
    public Region? Largest(RegionKind k) => Regions.Where(r => r.Kind == k).OrderByDescending(r => r.Blocks).FirstOrDefault();
}

/// <summary>
/// 画面の各ブロックが入力・時間に対してどう振る舞うかを数え、領域を汎用の種類に分ける（§3.2）。
/// 使うのは前後の画面と「画面全体が平行移動したか（MotionEstimator の推定）」「推定した移動量」だけ（キーの種類・意味は使わない）。
/// </summary>
public sealed class LayoutLearner
{
    public int Scale { get; init; } = 4;  // 根拠: 汎用（縮小率）
    public int BlockPx { get; init; } = 16;  // 根拠: 汎用（ブロックの大きさ）
    public float ChangeThreshold { get; init; } = 10f;  // 根拠: 汎用（輝度差の下限）
    public double BigChangeRatio { get; init; } = 0.25;  // 根拠: 汎用（画面の1/4以上の変化＝大きな切替）

    private int _w, _h, _gw, _gh, _bx, _by, _bs;
    private int[] _bigTurnsStatic = Array.Empty<int>(), _moveInformative = Array.Empty<int>(), _moveTranslated = Array.Empty<int>(),
        _moveNotTranslated = Array.Empty<int>(), _moveStatic = Array.Empty<int>(), _edgeSum = Array.Empty<int>(), _edgeObs = Array.Empty<int>(), _varSum = Array.Empty<int>();
    private int _big, _moves;

    public int BigTurns => _big;
    public int MoveTurns => _moves;

    private void Init(Frame f)
    {
        _w = f.Width; _h = f.Height; _gw = f.Width / Scale; _gh = f.Height / Scale;
        _bs = Math.Max(1, BlockPx / Scale); _bx = (_gw + _bs - 1) / _bs; _by = (_gh + _bs - 1) / _bs;
        int nb = _bx * _by;
        _bigTurnsStatic = new int[nb]; _moveInformative = new int[nb]; _moveTranslated = new int[nb]; _moveNotTranslated = new int[nb]; _moveStatic = new int[nb];
        _edgeSum = new int[nb]; _edgeObs = new int[nb]; _varSum = new int[nb];

        _big = 0; _moves = 0;
    }

    /// <summary>1手分を足す。shifted は画面全体の平行移動が推定されたか、motion はその移動量（なければ null）</summary>
    public void Observe(Frame before, Frame after, bool shifted, MotionCandidate? motion)
    {
        if (!before.SameSize(after)) return;
        if (_w != before.Width || _h != before.Height) Init(before);   // サイズが変わったら数え直す（§3.5）
        var a = GrayImage.FromFrame(before, Scale); var b = GrayImage.FromFrame(after, Scale);
        int gw = _gw, gh = _gh, bs = _bs;
        var d0 = new bool[gw * gh]; int changed = 0;
        for (int i = 0; i < gw * gh; i++) if (Math.Abs(a.Px[i] - b.Px[i]) > ChangeThreshold) { d0[i] = true; changed++; }
        double ratio = changed / (double)(gw * gh);
        // 見た目の統計（文字らしさ: 強い輪郭の割合、ばらつき）
        for (int by = 0; by < _by; by++)
            for (int bx = 0; bx < _bx; bx++)
            {
                int e = 0, n = 0; double s = 0, s2 = 0;
                for (int y = by * bs; y < Math.Min(gh, by * bs + bs); y++)
                    for (int x = bx * bs; x < Math.Min(gw, bx * bs + bs); x++)
                    {
                        float v = b.Px[y * gw + x]; s += v; s2 += v * v; n++;
                        if (x + 1 < gw && Math.Abs(v - b.Px[y * gw + x + 1]) > 40) e++;
                        if (y + 1 < gh && Math.Abs(v - b.Px[(y + 1) * gw + x]) > 40) e++;
                    }
                int k = by * _bx + bx;
                _edgeSum[k] += (int)Math.Round(100.0 * e / Math.Max(1, n)); _edgeObs[k]++;
                double var = n > 0 ? s2 / n - (s / n) * (s / n) : 0; _varSum[k] += (int)Math.Min(10000, var);
            }
        bool big = ratio >= BigChangeRatio;
        if (big) _big++;
        int mdx = 0, mdy = 0; bool haveMotion = shifted && motion is { } mc && mc.Length > Scale;
        if (haveMotion) { mdx = (int)Math.Round(motion!.Value.Dx / Scale); mdy = (int)Math.Round(motion.Value.Dy / Scale); _moves++; }
        for (int by = 0; by < _by; by++)
            for (int bx = 0; bx < _bx; bx++)
            {
                int k = by * _bx + bx, ch = 0, n = 0, tr = 0, ntr = 0, inside = 0;
                for (int y = by * bs; y < Math.Min(gh, by * bs + bs); y++)
                    for (int x = bx * bs; x < Math.Min(gw, bx * bs + bs); x++)
                    {
                        int i = y * gw + x; n++;
                        if (d0[i]) ch++;
                        if (!haveMotion) continue;
                        int tx = x + mdx, ty = y + mdy;
                        if (tx < 0 || ty < 0 || tx >= gw || ty >= gh) continue;
                        inside++;
                        // before(x) と after(x+d) を、1画素のずれを許して比べる
                        float va = a.Px[i]; float best = float.MaxValue;
                        for (int ey = -1; ey <= 1; ey++)
                            for (int ex = -1; ex <= 1; ex++)
                            {
                                int qx = tx + ex, qy = ty + ey;
                                if (qx < 0 || qy < 0 || qx >= gw || qy >= gh) continue;
                                best = Math.Min(best, Math.Abs(va - b.Px[qy * gw + qx]));
                            }
                        bool translated = best <= ChangeThreshold * 1.5f;
                        if (d0[i] && translated) tr++;
                        if (d0[i] && !translated) ntr++;
                    }
                bool blockChanged = ch > n * 0.25;
                if (big && !blockChanged) _bigTurnsStatic[k]++;
                if (!haveMotion || inside < n / 2) continue;
                if (blockChanged)
                {
                    _moveInformative[k]++;
                    if (tr >= ntr) _moveTranslated[k]++; else _moveNotTranslated[k]++;
                }
                else _moveStatic[k]++;
            }
    }

    public LayoutEstimate? Build()
    {
        if (_w == 0) return null;
        int nb = _bx * _by;
        var kinds = new RegionKind[nb];
        var conf = new double[nb];
        for (int k = 0; k < nb; k++)
        {
            double edge = _edgeObs[k] > 0 ? _edgeSum[k] / (double)_edgeObs[k] : 0;
            double var = _edgeObs[k] > 0 ? _varSum[k] / (double)_edgeObs[k] : 0;
            double staticBig = _big > 0 ? _bigTurnsStatic[k] / (double)_big : 0;
            int moveObs = _moveInformative[k] + _moveStatic[k];
            double trRate = _moveInformative[k] > 0 ? _moveTranslated[k] / (double)_moveInformative[k] : 0;
            double ntrRate = moveObs > 0 ? _moveNotTranslated[k] / (double)moveObs : 0;
            double informative = moveObs > 0 ? _moveInformative[k] / (double)moveObs : 0;
            if (_big >= 3 && staticBig >= 0.85)
            {
                if (var < 4 && edge < 1) { kinds[k] = RegionKind.Blank; conf[k] = staticBig; }
                else { kinds[k] = edge >= 12 ? RegionKind.FixedText : RegionKind.FixedGraphic; conf[k] = staticBig; }
            }
            else if (_moveInformative[k] >= 3 && trRate >= 0.6) { kinds[k] = RegionKind.Translating; conf[k] = trRate; }
            else if (_moveInformative[k] >= 3 && ntrRate >= 0.5 && informative >= 0.5) { kinds[k] = RegionKind.Overlay; conf[k] = ntrRate; }
            else if (moveObs >= 3 && informative < 0.3 && var < 4) { kinds[k] = RegionKind.Blank; conf[k] = 1 - informative; }
            else kinds[k] = RegionKind.Unknown;
        }
        // 面に囲まれた固定の小さな塊は「面の中の固定の前景」（意味は付けない）
        var comps = Components(kinds);
        foreach (var c in comps.Where(c => c.Kind is RegionKind.FixedText or RegionKind.FixedGraphic))
        {
            int translating = 0, other = 0;
            foreach (var k in c.Cells)
            {
                int x = k % _bx, y = k / _bx;
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= _bx || ny >= _by) { other++; continue; }
                    var nk = kinds[ny * _bx + nx];
                    if (nk == c.Kind) continue;
                    if (nk == RegionKind.Translating) translating++; else other++;
                }
            }
            if (translating > 0 && translating >= 2 * other && c.Cells.Count <= nb / 50)
                foreach (var k in c.Cells) kinds[k] = RegionKind.FixedInTranslating;
        }
        comps = Components(kinds);
        var regions = new List<Region>();
        int bpx = _bs * Scale;
        foreach (var c in comps)
        {
            if (c.Kind is RegionKind.Unknown or RegionKind.Blank) continue;
            if (c.Cells.Count < (c.Kind == RegionKind.FixedInTranslating ? 1 : 3)) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0; double cs = 0;
            foreach (var k in c.Cells) { int x = k % _bx, y = k / _bx; minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); cs += conf[k]; }
            int rw = maxX - minX + 1, rh = maxY - minY + 1;
            regions.Add(new Region(c.Kind, minX * bpx, minY * bpx, Math.Min(_w, (maxX + 1) * bpx) - minX * bpx, Math.Min(_h, (maxY + 1) * bpx) - minY * bpx,
                c.Cells.Count, cs / c.Cells.Count, c.Cells.Count / (double)(rw * rh)));
        }
        return new LayoutEstimate(_w, _h, bpx, kinds, _bx, _by, regions, _moves, _big);
    }

    private List<(RegionKind Kind, List<int> Cells)> Components(RegionKind[] kinds)
    {
        var seen = new bool[kinds.Length]; var list = new List<(RegionKind, List<int>)>();
        for (int s = 0; s < kinds.Length; s++)
        {
            if (seen[s]) continue;
            var kind = kinds[s]; var cells = new List<int>(); var q = new Queue<int>(); q.Enqueue(s); seen[s] = true;
            while (q.Count > 0)
            {
                int k = q.Dequeue(); cells.Add(k); int x = k % _bx, y = k / _bx;
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= _bx || ny >= _by) continue;
                    int nk = ny * _bx + nx;
                    if (!seen[nk] && kinds[nk] == kind) { seen[nk] = true; q.Enqueue(nk); }
                }
            }
            list.Add((kind, cells));
        }
        return list;
    }
}
