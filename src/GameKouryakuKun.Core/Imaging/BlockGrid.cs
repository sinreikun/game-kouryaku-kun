namespace GameKouryakuKun.Core.Imaging;

/// <summary>画面を一辺 Block px の格子に分けたときの寸法。差分・マスク・固定UI検出が共有する。</summary>
public readonly record struct BlockGrid(int Width, int Height, int Block)
{
    public int Cols => (Width + Block - 1) / Block;
    public int Rows => (Height + Block - 1) / Block;
    public int Count => Cols * Rows;
    public int CellWidth(int bx) => Math.Min(Block, Width - bx * Block);
    public int CellHeight(int by) => Math.Min(Block, Height - by * Block);
    public static BlockGrid For(Frame f, int block) => new(f.Width, f.Height, block);
}

/// <summary>ブロック単位の真偽マスク（true = 差分判定から除外）。</summary>
public sealed class BlockMask
{
    public BlockGrid Grid { get; }
    public bool[] Bits { get; }
    public BlockMask(BlockGrid grid) { Grid = grid; Bits = new bool[grid.Count]; }
    public BlockMask(BlockGrid grid, bool[] bits) { if (bits.Length != grid.Count) throw new ArgumentException("ブロック数が一致しません"); Grid = grid; Bits = bits; }
    public bool this[int bx, int by] { get => Bits[by * Grid.Cols + bx]; set => Bits[by * Grid.Cols + bx] = value; }
    public int CountSet() => Bits.Count(b => b);
    /// <summary>マスクされた画素の画面比</summary>
    public double CoveredRatio()
    {
        long px = 0;
        for (int by = 0; by < Grid.Rows; by++)
        for (int bx = 0; bx < Grid.Cols; bx++)
            if (this[bx, by]) px += Grid.CellWidth(bx) * Grid.CellHeight(by);
        return (double)px / ((long)Grid.Width * Grid.Height);
    }
    /// <summary>マスクを1ブロック膨張させる（アニメ領域の縁の取りこぼし対策）</summary>
    public BlockMask Dilate()
    {
        var o = new BlockMask(Grid);
        for (int by = 0; by < Grid.Rows; by++)
        for (int bx = 0; bx < Grid.Cols; bx++)
        {
            if (!this[bx, by]) continue;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = bx + dx, y = by + dy;
                if (x >= 0 && y >= 0 && x < Grid.Cols && y < Grid.Rows) o[x, y] = true;
            }
        }
        return o;
    }
    public override string ToString() => $"マスク {CountSet()}/{Grid.Count}ブロック（画面の{CoveredRatio():P1}）";
}
