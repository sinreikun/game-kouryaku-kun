namespace GameKouryakuKun.Core.Imaging;

public sealed record DiffOptions
{
    /// <summary>画素のチャネル差がこれを超えたら変化画素</summary>
    public int PixelThreshold { get; init; } = 24;  // 根拠: 汎用（8bit の色で約1割の差＝圧縮・拡大のノイズより上）。合成テスト ImagingTests ＋実機性能評価（変化検出の正否のみ・U5）
    /// <summary>連結判定のブロック一辺(px)</summary>
    public int Block { get; init; } = 8;  // 根拠: 汎用（差分を数える小区画の大きさ）
    /// <summary>ブロック内の変化画素がこの割合以上なら「変化ブロック」</summary>
    public double BlockFill { get; init; } = 0.25;  // 根拠: 汎用（小区画の1/4以上が変われば区画の変化）。合成テスト ImagingTests
    /// <summary>間引き（1=全画素, 2=縦横1/2）。速度と精度のトレードオフ</summary>
    public int SampleStep { get; init; } = 2;  // 根拠: 汎用（速度のための間引き）
    public static readonly DiffOptions Default = new();
}

/// <summary>差分の結果。LargestComponentRatio は「ブロック単位で4近傍連結した最大の変化領域」の画面比（マスク外の面積基準）。</summary>
public sealed record DiffResult(bool Comparable, double ChangedRatio, double LargestComponentRatio, int ComponentCount,
                                (int X0, int Y0, int X1, int Y1) LargestBox, string Note)
{
    /// <summary>変化ブロックのビット列（固定UI検出などで使う。Comparable=false なら null）</summary>
    public bool[]? ChangedBlocks { get; init; }
    public BlockGrid Grid { get; init; }
    public static DiffResult Incomparable(string note) => new(false, 1, 1, 0, default, note);
    public override string ToString() => Comparable
        ? $"変化率={ChangedRatio:0.#####} 最大連結={LargestComponentRatio:0.#####} [{LargestBox.X0},{LargestBox.Y0}-{LargestBox.X1},{LargestBox.Y1}] 領域数={ComponentCount}"
        : $"比較不可({Note})";
}

/// <summary>
/// 画素差分＋ブロック単位の連結領域解析（Phase 0 で有効性を確認した方式）。
/// 「散らばった小さな変化（小さな動き・アニメ・ノイズ）」と「まとまった大きな変化（重ね表示・全体の平行移動）」を区別できる。
/// </summary>
public static class FrameDiff
{
    public static DiffResult Compare(Frame? a, Frame? b, DiffOptions? options = null, BlockMask? ignore = null)
    {
        var o = options ?? DiffOptions.Default;
        if (a == null || b == null) return DiffResult.Incomparable("画像なし");
        if (!a.SameSize(b)) return DiffResult.Incomparable($"サイズ不一致 {a.Width}x{a.Height} vs {b.Width}x{b.Height}");
        var grid = BlockGrid.For(a, o.Block);
        if (ignore != null && ignore.Grid != grid) ignore = null; // サイズが変わったマスクは使わない
        int w = a.Width, h = a.Height, s = Math.Max(1, o.SampleStep), cols = grid.Cols;
        var changedCount = new int[grid.Count];
        var sampledCount = new int[grid.Count];
        long changed = 0, sampled = 0;
        byte[] pa = a.Bgra, pb = b.Bgra;
        for (int y = 0; y < h; y += s)
        {
            int rowB = (y / o.Block) * cols;
            for (int x = 0; x < w; x += s)
            {
                int bi = rowB + x / o.Block;
                if (ignore != null && ignore.Bits[bi]) continue;
                int i = (y * w + x) * 4;
                int db = Math.Abs(pa[i] - pb[i]), dg = Math.Abs(pa[i + 1] - pb[i + 1]), dr = Math.Abs(pa[i + 2] - pb[i + 2]);
                sampledCount[bi]++; sampled++;
                if (db > o.PixelThreshold || dg > o.PixelThreshold || dr > o.PixelThreshold) { changedCount[bi]++; changed++; }
            }
        }
        var on = new bool[grid.Count];
        long unmaskedPx = 0;
        for (int by = 0; by < grid.Rows; by++)
        for (int bx = 0; bx < cols; bx++)
        {
            int k = by * cols + bx;
            if (ignore != null && ignore.Bits[k]) continue;
            unmaskedPx += grid.CellWidth(bx) * grid.CellHeight(by);
            on[k] = sampledCount[k] > 0 && changedCount[k] >= o.BlockFill * sampledCount[k];
        }
        if (unmaskedPx == 0) return DiffResult.Incomparable("全域がマスクされています");

        var label = new int[grid.Count];
        var stack = new Stack<int>();
        int comps = 0; long bestPx = 0; (int, int, int, int) best = default;
        for (int start = 0; start < on.Length; start++)
        {
            if (!on[start] || label[start] != 0) continue;
            comps++; long px = 0; int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            label[start] = comps; stack.Push(start);
            while (stack.Count > 0)
            {
                int c = stack.Pop(), cx = c % cols, cy = c / cols;
                px += grid.CellWidth(cx) * grid.CellHeight(cy);
                if (cx < minX) minX = cx; if (cy < minY) minY = cy; if (cx > maxX) maxX = cx; if (cy > maxY) maxY = cy;
                if (cx > 0 && on[c - 1] && label[c - 1] == 0) { label[c - 1] = comps; stack.Push(c - 1); }
                if (cx < cols - 1 && on[c + 1] && label[c + 1] == 0) { label[c + 1] = comps; stack.Push(c + 1); }
                if (cy > 0 && on[c - cols] && label[c - cols] == 0) { label[c - cols] = comps; stack.Push(c - cols); }
                if (cy < grid.Rows - 1 && on[c + cols] && label[c + cols] == 0) { label[c + cols] = comps; stack.Push(c + cols); }
            }
            if (px > bestPx)
            {
                bestPx = px;
                best = (minX * o.Block, minY * o.Block, Math.Min(w, (maxX + 1) * o.Block), Math.Min(h, (maxY + 1) * o.Block));
            }
        }
        return new DiffResult(true,
            Math.Round(sampled == 0 ? 0 : (double)changed / sampled, 5),
            Math.Round((double)bestPx / unmaskedPx, 5), comps, best, "")
        { ChangedBlocks = on, Grid = grid };
    }
}
