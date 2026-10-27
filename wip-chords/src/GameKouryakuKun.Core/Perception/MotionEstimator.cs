using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Perception;

/// <summary>動く面の平行移動の候補（元の画素単位）。内容が画面上で (Dx, Dy) だけ動いた（after(x+d) ≈ before(x)）</summary>
public readonly record struct MotionCandidate(double Dx, double Dy, double Cost)
{
    public double Length => Math.Sqrt(Dx * Dx + Dy * Dy);
}

/// <summary>1手の前後の画面の移動推定の結果</summary>
public sealed record MotionResult(IReadOnlyList<MotionCandidate> Candidates, double ChangedRatio, double Confidence)
{
    public MotionCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>
/// 前後の画面から、画面全体の平行移動の候補を求める（phase2-design §4.2）。
/// 変化した画素だけで照合する（固定表示や、周期の模様で1マス分重なる部分は自然に除かれ、壁の縁などの周期の弱い特徴が決め手になる）。
/// 周期の整数倍だけ違う候補も、費用の近いものは残す。ゲーム固有の値は使わない。
/// </summary>
public sealed class MotionEstimator
{
    public int CoarseScale { get; init; } = 8;  // 根拠: 汎用（粗い探索の縮小率）
    public int FineScale { get; init; } = 2;  // 根拠: 汎用（細かい探索の縮小率）
    /// <summary>探す移動量の上限（取得画像の長辺に対する比）</summary>
    public double MaxShiftRatio { get; init; } = 0.14;  // 根拠: 合成テスト（未作成・A1）で確認予定。汎用（1手の平行移動の上限＝長辺の1/7）
    /// <summary>「変化した」とみなす輝度差</summary>
    public float ChangeThreshold { get; init; } = 8f;  // 根拠: 汎用（輝度差の下限）
    /// <summary>変化した画素がこの割合より少なければ、移動なしとする</summary>
    public double MinChangedRatio { get; init; } = 0.02;  // 根拠: 汎用（2%未満の変化は移動なし）
    public int MaxCandidates { get; init; } = 4;  // 根拠: 汎用（候補数の上限）
    /// <summary>最良の費用の何倍までを候補に残すか</summary>
    public double KeepCostFactor { get; init; } = 1.35;  // 根拠: 汎用（周期の整数倍の候補を残す幅）

    public MotionResult Estimate(Frame before, Frame after, Func<int, int, bool>? exclude = null)
    {
        if (!before.SameSize(after)) return new MotionResult(Array.Empty<MotionCandidate>(), 1, 0);
        int cs = CoarseScale;
        var a0 = GrayImage.FromFrame(before, cs); var b0 = GrayImage.FromFrame(after, cs);
        int w = a0.Width, h = a0.Height;
        var mask = new bool[w * h]; int changed = 0;
        for (int i = 0; i < w * h; i++)
        {
            int x = i % w, y = i / w;
            if (exclude != null && exclude(x * cs + cs / 2, y * cs + cs / 2)) continue;
            if (Math.Abs(a0.Px[i] - b0.Px[i]) > ChangeThreshold) { mask[i] = true; changed++; }
        }
        double ratio = changed / (double)(w * h);
        if (ratio < MinChangedRatio) return new MotionResult(Array.Empty<MotionCandidate>(), ratio, 0);
        var a = a0.HighPass(3); var b = b0.HighPass(3);
        int R = Math.Max(2, (int)Math.Round(MaxShiftRatio * Math.Max(before.Width, before.Height) / cs));
        int S = 2 * R + 1;
        var cost = new double[S * S];
        for (int dy = -R; dy <= R; dy++)
            for (int dx = -R; dx <= R; dx++)
                cost[(dy + R) * S + dx + R] = Cost(a, b, mask, dx, dy, minOverlap: 0.25);
        // 局所最小を候補にする
        var mins = new List<(int dx, int dy, double c)>();
        for (int dy = -R; dy <= R; dy++)
            for (int dx = -R; dx <= R; dx++)
            {
                double c = cost[(dy + R) * S + dx + R];
                if (double.IsInfinity(c)) continue;
                bool isMin = true;
                for (int ey = -1; ey <= 1 && isMin; ey++)
                    for (int ex = -1; ex <= 1; ex++)
                    {
                        if (ex == 0 && ey == 0) continue;
                        int qx = dx + ex, qy = dy + ey;
                        if (qx < -R || qx > R || qy < -R || qy > R) continue;
                        if (cost[(qy + R) * S + qx + R] < c) { isMin = false; break; }
                    }
                if (isMin) mins.Add((dx, dy, c));
            }
        if (mins.Count == 0) return new MotionResult(Array.Empty<MotionCandidate>(), ratio, 0);
        mins.Sort((p, q) => p.c.CompareTo(q.c));
        double best = mins[0].c;
        var coarse = mins.Where(m => m.c <= best * KeepCostFactor + 1e-6).Take(MaxCandidates).ToList();
        // 細かい段で詰める
        int fs = FineScale; int k = cs / fs;
        var a1 = GrayImage.FromFrame(before, fs).HighPass(3 * k / 2 + 1); var b1 = GrayImage.FromFrame(after, fs).HighPass(3 * k / 2 + 1);
        var a1raw = GrayImage.FromFrame(before, fs); var b1raw = GrayImage.FromFrame(after, fs);
        var m1 = new bool[a1.Width * a1.Height];
        for (int i = 0; i < m1.Length; i++)
        {
            int x = i % a1.Width, y = i / a1.Width;
            if (exclude != null && exclude(x * fs + fs / 2, y * fs + fs / 2)) continue;
            m1[i] = Math.Abs(a1raw.Px[i] - b1raw.Px[i]) > ChangeThreshold;
        }
        var outList = new List<MotionCandidate>();
        foreach (var (cdx, cdy, _) in coarse)
        {
            int bx = cdx * k, by = cdy * k; double bc = double.PositiveInfinity;
            var local = new Dictionary<(int, int), double>();
            for (int ey = -k; ey <= k; ey++)
                for (int ex = -k; ex <= k; ex++)
                {
                    double c = Cost(a1, b1, m1, cdx * k + ex, cdy * k + ey, minOverlap: 0.25, stride: 2);
                    local[(cdx * k + ex, cdy * k + ey)] = c;
                    if (c < bc) { bc = c; bx = cdx * k + ex; by = cdy * k + ey; }
                }
            if (double.IsInfinity(bc)) continue;
            // 放物線で小数の位置まで
            double sx = Sub(local, bx, by, 1, 0), sy = Sub(local, bx, by, 0, 1);
            outList.Add(new MotionCandidate((bx + sx) * fs, (by + sy) * fs, bc));
        }
        outList = outList.OrderBy(c => c.Cost).ToList();
        // 同じ位置に落ちた候補をまとめる
        var uniq = new List<MotionCandidate>();
        foreach (var c in outList) if (!uniq.Any(u => Math.Abs(u.Dx - c.Dx) < fs * 2 && Math.Abs(u.Dy - c.Dy) < fs * 2)) uniq.Add(c);
        double conf = uniq.Count >= 2 ? Math.Clamp((uniq[1].Cost - uniq[0].Cost) / Math.Max(1e-6, uniq[0].Cost), 0, 1) : (uniq.Count == 1 ? 1 : 0);
        return new MotionResult(uniq, ratio, conf);
    }

    private static double Sub(Dictionary<(int, int), double> m, int x, int y, int ux, int uy)
    {
        if (!m.TryGetValue((x - ux, y - uy), out var l) || !m.TryGetValue((x + ux, y + uy), out var r) || !m.TryGetValue((x, y), out var c)) return 0;
        if (double.IsInfinity(l) || double.IsInfinity(r)) return 0;
        double den = l - 2 * c + r;
        return den <= 1e-9 ? 0 : Math.Clamp(0.5 * (l - r) / den, -0.5, 0.5);
    }

    /// <summary>before の変化画素 x について |before(x) - after(x+d)| の平均</summary>
    private static double Cost(GrayImage a, GrayImage b, bool[] mask, int dx, int dy, double minOverlap, int stride = 1)
    {
        int w = a.Width, h = a.Height;
        int x0 = Math.Max(0, -dx), x1 = Math.Min(w, w - dx), y0 = Math.Max(0, -dy), y1 = Math.Min(h, h - dy);
        if (x1 - x0 < w * minOverlap || y1 - y0 < h * minOverlap) return double.PositiveInfinity;
        double s = 0; int n = 0;
        for (int y = y0; y < y1; y += stride)
        {
            int ra = y * w, rb = (y + dy) * w + dx;
            for (int x = x0; x < x1; x += stride)
            {
                if (!mask[ra + x]) continue;
                s += Math.Abs(a.Px[ra + x] - b.Px[rb + x]); n++;
            }
        }
        return n < 16 ? double.PositiveInfinity : s / n;
    }
}
