namespace GameKouryakuKun.Phase0;

/// <summary>BGRA 8bit の画像バッファ。統計・差分（連結領域つき）・PNG保存。</summary>
public sealed class FrameImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public string Source { get; init; } = "";
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public FrameImage(int w, int h, byte[] bgra) { Width = w; Height = h; Bgra = bgra; }

    public sealed record Stats(double MeanLuma, double StdLuma, double NearBlackRatio, bool LooksBlack, bool LooksUniform);

    public Stats ComputeStats()
    {
        long n = (long)Width * Height; if (n == 0) return new Stats(0, 0, 1, true, true);
        double sum = 0, sum2 = 0; long nearBlack = 0, cnt = 0;
        int step = Math.Max(1, (int)Math.Sqrt(n / 250_000.0));
        for (int y = 0; y < Height; y += step)
        for (int x = 0; x < Width; x += step)
        {
            int i = (y * Width + x) * 4;
            double l = 0.114 * Bgra[i] + 0.587 * Bgra[i + 1] + 0.299 * Bgra[i + 2];
            sum += l; sum2 += l * l; cnt++;
            if (l < 10) nearBlack++;
        }
        double mean = sum / cnt, std = Math.Sqrt(Math.Max(0, sum2 / cnt - mean * mean));
        return new Stats(Math.Round(mean, 2), Math.Round(std, 2), Math.Round((double)nearBlack / cnt, 4), mean < 8 && std < 4, std < 1.5);
    }

    /// <summary>差分結果。LargestComponentRatio は「ブロック単位で連結した最大の変化領域」の画面比。</summary>
    public sealed record Diff(bool Comparable, double ChangedRatio, double MeanAbsDiff, double LargestComponentRatio,
                              string LargestComponentBox, int ComponentCount, string Note)
    {
        public override string ToString() => Comparable
            ? $"変化率={ChangedRatio:0.#####} 最大連結領域={LargestComponentRatio:0.#####} {LargestComponentBox} 領域数={ComponentCount}"
            : $"比較不可({Note})";
    }

    public const int Block = 8;          // 連結判定のブロック一辺(px)
    public const double BlockFill = 0.25; // ブロック内で変化画素がこの割合以上なら「変化ブロック」

    /// <summary>ピクセル差分＋ブロック単位の連結領域解析。maskPath 指定時は変化画素=赤、最大領域=緑枠のPNGを保存。</summary>
    public static Diff Compare(FrameImage? a, FrameImage? b, int threshold = 24, string? maskPath = null)
    {
        if (a == null || b == null) return new Diff(false, 0, 0, 0, "", 0, "画像なし");
        if (a.Width != b.Width || a.Height != b.Height)
            return new Diff(false, 1, 0, 0, "", 0, $"サイズ不一致 {a.Width}x{a.Height} vs {b.Width}x{b.Height}");
        int w = a.Width, h = a.Height; long n = (long)w * h;
        int bw = (w + Block - 1) / Block, bh = (h + Block - 1) / Block;
        var blockCount = new int[bw * bh];
        var changedPx = maskPath != null ? new bool[n] : null;
        long changed = 0; double absSum = 0;
        for (int y = 0; y < h; y++)
        {
            int rowB = (y / Block) * bw;
            for (int x = 0; x < w; x++)
            {
                long p = (long)y * w + x, i = p * 4;
                int db = Math.Abs(a.Bgra[i] - b.Bgra[i]), dg = Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]), dr = Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]);
                absSum += (db + dg + dr) / 3.0;
                if (Math.Max(db, Math.Max(dg, dr)) > threshold)
                {
                    changed++; blockCount[rowB + x / Block]++;
                    if (changedPx != null) changedPx[p] = true;
                }
            }
        }
        // 変化ブロック → 4近傍で連結
        var on = new bool[bw * bh];
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            int cw = Math.Min(Block, w - bx * Block), ch = Math.Min(Block, h - by * Block);
            on[by * bw + bx] = blockCount[by * bw + bx] >= BlockFill * cw * ch;
        }
        var label = new int[bw * bh]; int comps = 0; long bestPx = 0; (int x0, int y0, int x1, int y1) best = (0, 0, 0, 0);
        var stack = new Stack<int>();
        for (int s = 0; s < on.Length; s++)
        {
            if (!on[s] || label[s] != 0) continue;
            comps++; long px = 0; int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            stack.Push(s); label[s] = comps;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); int cx = c % bw, cy = c / bw;
                px += Math.Min(Block, w - cx * Block) * Math.Min(Block, h - cy * Block);
                minX = Math.Min(minX, cx); minY = Math.Min(minY, cy); maxX = Math.Max(maxX, cx); maxY = Math.Max(maxY, cy);
                if (cx > 0 && on[c - 1] && label[c - 1] == 0) { label[c - 1] = comps; stack.Push(c - 1); }
                if (cx < bw - 1 && on[c + 1] && label[c + 1] == 0) { label[c + 1] = comps; stack.Push(c + 1); }
                if (cy > 0 && on[c - bw] && label[c - bw] == 0) { label[c - bw] = comps; stack.Push(c - bw); }
                if (cy < bh - 1 && on[c + bw] && label[c + bw] == 0) { label[c + bw] = comps; stack.Push(c + bw); }
            }
            if (px > bestPx) { bestPx = px; best = (minX * Block, minY * Block, Math.Min(w, (maxX + 1) * Block), Math.Min(h, (maxY + 1) * Block)); }
        }
        string box = bestPx > 0 ? $"[{best.x0},{best.y0}-{best.x1},{best.y1}]" : "";
        if (maskPath != null)
        {
            var mask = new byte[n * 4];
            for (long p = 0; p < n; p++)
            {
                long i = p * 4;
                byte g = (byte)((a.Bgra[i] + a.Bgra[i + 1] + a.Bgra[i + 2]) / 9);
                bool ch = changedPx![p];
                mask[i] = ch ? (byte)0 : g; mask[i + 1] = ch ? (byte)0 : g; mask[i + 2] = ch ? (byte)255 : g; mask[i + 3] = 255;
            }
            if (bestPx > 0)
            {
                void Px(int x, int y) { if (x < 0 || y < 0 || x >= w || y >= h) return; long i = ((long)y * w + x) * 4; mask[i] = 0; mask[i + 1] = 255; mask[i + 2] = 0; }
                for (int t = 0; t < 3; t++)
                {
                    for (int x = best.x0; x < best.x1; x++) { Px(x, best.y0 + t); Px(x, best.y1 - 1 - t); }
                    for (int y = best.y0; y < best.y1; y++) { Px(best.x0 + t, y); Px(best.x1 - 1 - t, y); }
                }
            }
            Png.SaveBgra(maskPath, w, h, mask);
        }
        return new Diff(true, Math.Round((double)changed / n, 5), Math.Round(absSum / n, 3), Math.Round((double)bestPx / n, 5), box, comps, "");
    }

    public void SavePng(string path) => Png.SaveBgra(path, Width, Height, Bgra);
}
