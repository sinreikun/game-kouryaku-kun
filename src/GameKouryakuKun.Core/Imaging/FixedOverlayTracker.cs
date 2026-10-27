namespace GameKouryakuKun.Core.Imaging;

/// <summary>
/// 画面固定UIの候補を見つける（汎用: 画面の大きな切替が起きても位置・見た目が変わらない画素の集まり）。
/// 画面の大部分が変化した差分（スクロール等）のたびに、変化しなかったブロックを数え、
/// 大変化の大半で変化しなかったブロックを「画面固定UI候補」とする。意味（それが何か）は付けない。
/// 後段（認識・自キャラ推定）で、スクロール量の推定やタイル照合からこの領域を除外するために使う。
/// </summary>
public sealed class FixedOverlayTracker
{
    public double BigChangeRatio { get; init; } = 0.25;  // 根拠: 汎用（画面の1/4以上が変わる＝大きな切替）。合成テスト ImagingTests
    public double StableFraction { get; init; } = 0.8;  // 根拠: 汎用（大きな切替の8割で変わらない画素＝固定表示）
    public int MinEvents { get; init; } = 4;  // 根拠: 汎用（判断に要る最小の切替回数）
    private int[]? _unchanged;
    private BlockGrid _grid;
    public int BigEvents { get; private set; }

    public void Observe(DiffResult d)
    {
        if (!d.Comparable || d.ChangedBlocks == null || d.ChangedRatio < BigChangeRatio) return;
        if (_unchanged == null || _grid != d.Grid) { _grid = d.Grid; _unchanged = new int[d.Grid.Count]; BigEvents = 0; }
        BigEvents++;
        for (int k = 0; k < _unchanged.Length; k++) if (!d.ChangedBlocks[k]) _unchanged[k]++;
    }

    public BlockMask? CandidateMask()
    {
        if (_unchanged == null || BigEvents < MinEvents) return null;
        int need = (int)Math.Ceiling(BigEvents * StableFraction);
        return new BlockMask(_grid, _unchanged.Select(c => c >= need).ToArray());
    }

    public sealed record Region(int X0, int Y0, int X1, int Y1, double AreaRatio)
    {
        public override string ToString() => $"[{X0},{Y0}-{X1},{Y1}] 面積{AreaRatio:P2}";
    }

    /// <summary>候補ブロックを連結した領域（面積の大きい順）。minAreaRatio 未満は捨てる。</summary>
    public IReadOnlyList<Region> Regions(double minAreaRatio = 0.0005)
    {
        var m = CandidateMask();
        if (m == null) return Array.Empty<Region>();
        var g = m.Grid; var seen = new bool[g.Count]; var list = new List<Region>(); var st = new Stack<int>();
        double total = (double)g.Width * g.Height;
        for (int s = 0; s < g.Count; s++)
        {
            if (!m.Bits[s] || seen[s]) continue;
            seen[s] = true; st.Push(s); long px = 0; int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
            while (st.Count > 0)
            {
                int c = st.Pop(), cx = c % g.Cols, cy = c / g.Cols;
                px += g.CellWidth(cx) * g.CellHeight(cy);
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); x1 = Math.Max(x1, cx); y1 = Math.Max(y1, cy);
                foreach (var n in new[] { c - 1, c + 1, c - g.Cols, c + g.Cols })
                {
                    if (n < 0 || n >= g.Count || seen[n] || !m.Bits[n]) continue;
                    if ((n == c - 1 && cx == 0) || (n == c + 1 && cx == g.Cols - 1)) continue;
                    seen[n] = true; st.Push(n);
                }
            }
            if (px / total >= minAreaRatio)
                list.Add(new Region(x0 * g.Block, y0 * g.Block, Math.Min(g.Width, (x1 + 1) * g.Block), Math.Min(g.Height, (y1 + 1) * g.Block), Math.Round(px / total, 5)));
        }
        return list.OrderByDescending(r => r.AreaRatio).ToList();
    }
}
