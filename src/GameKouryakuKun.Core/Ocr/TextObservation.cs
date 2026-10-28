using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Ocr;

/// <summary>
/// v0.27.2: 1 手ごとの文字の観測（意味は持たない）。読んだ文字列と位置だけ。
/// 区画（GridCols×GridRows）ごとの画素のハッシュで「前に読んだときから変わった区画」だけを読み直し、変わっていない区画の行は前の結果を使う。
/// 同じ見た目の区画の並び（ハッシュ）を読んだことがあれば、その結果を使う（読まない）。
/// </summary>
public sealed record TextSnapshot(long FrameId, int Width, int Height, IReadOnlyList<OcrLine> Lines, ulong[] CellHashes)
{
    public TextFeatures Features => TextFeatures.Of(new OcrResult("snapshot", Lines, 0, ""), Width, Height);
}

/// <summary>読み方の計画: 全部読む・一部（矩形）だけ読む・読まない（前の結果か同じ見た目の結果を使う）</summary>
public sealed record TextReadPlan(string Kind, OcrBox? Region, IReadOnlyList<OcrLine> Kept, ulong[] CellHashes, ulong RegionHash);

public sealed class TextReader
{
    public const int GridCols = 16;   // 根拠: 汎用（文字の行の高さより細かく、画素ハッシュの計算が軽い区画の数）
    public const int GridRows = 12;   // 根拠: 汎用（同上・画面の縦横比 4:3 前後）
    public int CacheSize { get; init; } = 256;   // 根拠: 汎用（状態の数（数百）と同じ桁。古いものから捨てる）
    private readonly Dictionary<ulong, IReadOnlyList<OcrLine>> _cache = new();
    private readonly Queue<ulong> _order = new();
    public long FullReads { get; private set; }
    public long RegionReads { get; private set; }
    public long Reused { get; private set; }
    public long CacheHits { get; private set; }
    /// <summary>S4: Attention によるエンジン defer（継承。欠測にしない）。</summary>
    public long DeferredReads { get; private set; }
    public double ReadAreaSum { get; private set; }
    /// <summary>v0.29.0: 直前の ReadAsync で読み直した矩形（全体・継承なら null）</summary>
    public OcrBox? LastRegion { get; private set; }

    /// <summary>区画ごとの画素のハッシュ（間引いて計算。明るさの小さな揺れは下位ビットを落として吸収）</summary>
    public static ulong[] Cells(Frame f)
    {
        var h = new ulong[GridCols * GridRows];
        for (int gy = 0; gy < GridRows; gy++)
        for (int gx = 0; gx < GridCols; gx++)
        {
            int x0 = gx * f.Width / GridCols, x1 = (gx + 1) * f.Width / GridCols, y0 = gy * f.Height / GridRows, y1 = (gy + 1) * f.Height / GridRows;
            int sx = Math.Max(1, (x1 - x0) / 16), sy = Math.Max(1, (y1 - y0) / 16);
            ulong v = 14695981039346656037UL;
            for (int y = y0; y < y1; y += sy)
            for (int x = x0; x < x1; x += sx)
            {
                int i = (y * f.Width + x) * 4;
                v ^= (ulong)((f.Bgra[i] >> 4) | ((f.Bgra[i + 1] >> 4) << 4) | ((f.Bgra[i + 2] >> 4) << 8)); v *= 1099511628211UL;
            }
            h[gy * GridCols + gx] = v;
        }
        return h;
    }

    /// <summary>v0.29.2: 取り込みのノイズで区画のハッシュが変わらないようにする（既存の画素のしきい値。Cells と同じ 16×16 点）</summary>
    public CellStabilizer Stabilizer { get; } = new(GridCols, GridRows, 16);   // 根拠: Cells と同じ間引きの点の数
    private long _lastFrame = long.MinValue; private ulong[]? _lastCells;
    /// <summary>この画面のノイズに強い区画のハッシュ（同じ画面なら同じ結果を返す）</summary>
    public ulong[] StableCells(Frame f)
    {
        if (_lastCells != null && _lastFrame == f.Id) return _lastCells;
        _lastCells = Stabilizer.Apply(f); _lastFrame = f.Id; return _lastCells;
    }

    public TextReadPlan Plan(Frame f, TextSnapshot? prev)
    {
        var cells = StableCells(f);
        ulong all = Hash(cells, Enumerable.Range(0, cells.Length));
        bool same = prev != null && prev.Width == f.Width && prev.Height == f.Height;
        if (same && prev!.CellHashes.SequenceEqual(cells)) return new TextReadPlan("reuse", null, prev.Lines, cells, all);
        if (_cache.ContainsKey(all)) return new TextReadPlan("cache", null, _cache[all], cells, all);
        if (!same) return new TextReadPlan("full", null, Array.Empty<OcrLine>(), cells, all);
        var changed = Enumerable.Range(0, cells.Length).Where(i => cells[i] != prev!.CellHashes[i]).ToList();
        int cx0 = changed.Min(i => i % GridCols), cx1 = changed.Max(i => i % GridCols), cy0 = changed.Min(i => i / GridCols), cy1 = changed.Max(i => i / GridCols);
        // 変わった区画に掛かる行は読み直す: 読む矩形をその行まで広げる（行が途中で切れないように）
        var box = new OcrBox(cx0 * f.Width / GridCols, cy0 * f.Height / GridRows, (cx1 + 1) * f.Width / GridCols - cx0 * f.Width / GridCols, (cy1 + 1) * f.Height / GridRows - cy0 * f.Height / GridRows);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var l in prev!.Lines)
                if (Intersects(l.Box, box) && !Contains(box, l.Box)) { box = Union(box, l.Box); grew = true; }
        }
        if ((double)box.W * box.H >= 0.999 * f.Width * f.Height) return new TextReadPlan("full", null, Array.Empty<OcrLine>(), cells, all);
        var kept = prev.Lines.Where(l => !Intersects(l.Box, box)).ToList();
        int bx0 = box.X * GridCols / f.Width, bx1 = Math.Min(GridCols - 1, (box.X + box.W - 1) * GridCols / f.Width), by0 = box.Y * GridRows / f.Height, by1 = Math.Min(GridRows - 1, (box.Y + box.H - 1) * GridRows / f.Height);
        ulong rh = Hash(cells, Enumerable.Range(0, cells.Length).Where(i => i % GridCols >= bx0 && i % GridCols <= bx1 && i / GridCols >= by0 && i / GridCols <= by1)) ^ (ulong)(box.X * 7919 + box.Y * 104729 + box.W * 31 + box.H);
        if (_cache.TryGetValue(rh, out var cl)) return new TextReadPlan("cache", box, kept.Concat(cl).ToList(), cells, rh);
        return new TextReadPlan("region", box, kept, cells, rh);
    }

    /// <summary>計画どおりに読む（engine は矩形の画像を受け取る）。読んだ結果は画面の座標に戻す</summary>
    public async Task<(TextSnapshot Snap, string Kind, double Ms, double Area)> ReadAsync(Frame f, TextSnapshot? prev, Func<Frame, Task<OcrResult?>> engine, bool forceDeferEngine = false)
    {
        var p = Plan(f, prev); LastRegion = p.Region;
        if (p.Kind is "reuse") { Reused++; return (new TextSnapshot(f.Id, f.Width, f.Height, p.Kept, p.CellHashes), p.Kind, 0, 0); }
        if (p.Kind is "cache") { CacheHits++; return (new TextSnapshot(f.Id, f.Width, f.Height, p.Kept, p.CellHashes), p.Kind, 0, 0); }
        // S4 live ROI: soft defer of full/region engine call — inherit previous lines, refresh cell hashes.
        // Kind "deferred" → TextLayer Inherited（not_observed にしない・永久削除しない）。
        if (forceDeferEngine && prev != null && prev.Width == f.Width && prev.Height == f.Height)
        {
            DeferredReads++;
            LastRegion = null;
            var cells = StableCells(f);
            return (new TextSnapshot(f.Id, f.Width, f.Height, prev.Lines, cells), "deferred", 0, 0);
        }
        Frame img = f; int ox = 0, oy = 0; double area = 1;
        if (p.Region is { } b) { img = Crop(f, b); ox = b.X; oy = b.Y; area = (double)b.W * b.H / ((double)f.Width * f.Height); RegionReads++; } else FullReads++;
        ReadAreaSum += area;
        var r = await engine(img).ConfigureAwait(false);
        if (r == null) throw new InvalidOperationException("OCR の結果なし");
        var read = r.Lines.Select(l => l with { Box = new OcrBox(l.Box.X + ox, l.Box.Y + oy, l.Box.W, l.Box.H) }).ToList();
        if (p.Region != null && prev != null) read = KeepUnchanged(read, prev, p.CellHashes, f);
        var lines = p.Kept.Concat(read).ToList();
        Put(p.RegionHash, read);
        if (p.Region != null) Put(Hash(p.CellHashes, Enumerable.Range(0, p.CellHashes.Length)), lines);   // 画面全体の見た目でも引けるように
        return (new TextSnapshot(f.Id, f.Width, f.Height, lines, p.CellHashes), p.Kind, r.ElapsedMs, area);
    }

    /// <summary>v0.29.3: 一部の読み直しで、画素の区画が 1 つも変わっていない行は前の読みを使う（同じ画素を切り出し方を変えて読み直しただけの揺れ）。
    /// 実機 #111: 1 つの画面で 230 手、画素の変わらない行の文字が切り出しの読み直しで毎手違い、文字の変化の不確かさが下がらなかった</summary>
    public long KeptUnchangedLines { get; private set; }
    private List<OcrLine> KeepUnchanged(List<OcrLine> read, TextSnapshot prev, ulong[] cells, Frame f)
    {
        if (prev.CellHashes.Length != cells.Length || prev.Width != f.Width || prev.Height != f.Height) return read;
        bool Changed(OcrBox b)
        {
            int x0 = Math.Clamp(b.X * GridCols / f.Width, 0, GridCols - 1), x1 = Math.Clamp((b.X + b.W - 1) * GridCols / f.Width, 0, GridCols - 1);
            int y0 = Math.Clamp(b.Y * GridRows / f.Height, 0, GridRows - 1), y1 = Math.Clamp((b.Y + b.H - 1) * GridRows / f.Height, 0, GridRows - 1);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) if (cells[y * GridCols + x] != prev.CellHashes[y * GridCols + x]) return true;
            return false;
        }
        static bool In(OcrBox b, double x, double y) => x >= b.X && x < b.X + b.W && y >= b.Y && y < b.Y + b.H;
        var outl = new List<OcrLine>(read.Count);
        foreach (var l in read)
        {
            OcrLine? old = null;
            if (!Changed(l.Box))
                old = prev.Lines.FirstOrDefault(o => !Changed(o.Box) && In(o.Box, l.Box.X + l.Box.W / 2.0, l.Box.Y + l.Box.H / 2.0) && In(l.Box, o.Box.X + o.Box.W / 2.0, o.Box.Y + o.Box.H / 2.0));   // 互いの中心が互いの矩形の中（固定の重なりの割合なし）
            if (old != null && !outl.Contains(old)) { outl.Add(old); if (old.Text != l.Text) KeptUnchangedLines++; }
            else outl.Add(l);
        }
        return outl;
    }

    private void Put(ulong k, IReadOnlyList<OcrLine> v)
    {
        if (_cache.ContainsKey(k)) return;
        _cache[k] = v; _order.Enqueue(k);
        while (_order.Count > CacheSize) _cache.Remove(_order.Dequeue());
    }

    public static Frame Crop(Frame f, OcrBox b)
    {
        int x0 = Math.Clamp(b.X, 0, f.Width - 1), y0 = Math.Clamp(b.Y, 0, f.Height - 1);
        int w = Math.Clamp(b.W, 1, f.Width - x0), h = Math.Clamp(b.H, 1, f.Height - y0);
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(f.Bgra, ((y0 + y) * f.Width + x0) * 4, buf, y * w * 4, w * 4);
        return new Frame(f.Id, w, h, buf, f.TimestampMs, f.Source + "+crop");
    }

    private static ulong Hash(ulong[] c, IEnumerable<int> idx) { ulong v = 1469598103934665603UL; foreach (var i in idx) { v ^= c[i] + (ulong)i; v *= 1099511628211UL; } return v; }
    private static bool Intersects(OcrBox a, OcrBox b) => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
    private static bool Contains(OcrBox a, OcrBox b) => b.X >= a.X && b.Y >= a.Y && b.X + b.W <= a.X + a.W && b.Y + b.H <= a.Y + a.H;
    private static OcrBox Union(OcrBox a, OcrBox b) { int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y); return new OcrBox(x, y, Math.Max(a.X + a.W, b.X + b.W) - x, Math.Max(a.Y + a.H, b.Y + b.H) - y); }
}

/// <summary>
/// v0.27.2: 2 つの文字の観測の差（意味は付けない）。行は位置の区画（TextFeatures の 8×6）で対応させる。
/// Kind: none（変化なし）・numbers（数字の断片だけが変わった＝並びは同じ）・strings（同じ位置の文字列が変わった）・layout（行が増えた・消えた）
/// </summary>
/// <remarks>v0.28.0: 文字の差はすべて文字の変化として数える（v0.27.3 の「画素が変わっていない所の差を除く」はやめた: 間引いた画素のハッシュでは小さな文字の変化を見落としうる）。
/// 画素のハッシュが変わっていない位置の差の数は PixelUnconfirmed として別に残すだけ（揺れかどうかは入力なしの基準と手との共変動で統計的に分ける）</remarks>
public sealed record TextDiff(string Kind, int Added, int Removed, int Changed, int NumbersOnly, IReadOnlyList<int> Cells, int PixelUnconfirmed = 0)
{
    public bool Any => Kind != "none";
    public static readonly string[] Kinds = { "none", "numbers", "strings", "layout" };

    /// <summary>行の中心が入る文字の配置の区画（8×6）</summary>
    public static int LayoutCell(OcrLine l, int width, int height) =>
        Math.Clamp((l.Box.Y + l.Box.H / 2) * TextFeatures.LayoutRows / Math.Max(1, height), 0, TextFeatures.LayoutRows - 1) * TextFeatures.LayoutCols
        + Math.Clamp((l.Box.X + l.Box.W / 2) * TextFeatures.LayoutCols / Math.Max(1, width), 0, TextFeatures.LayoutCols - 1);

    public static TextDiff Of(TextSnapshot a, TextSnapshot b)
    {
        static Dictionary<int, List<string>> ByCell(TextSnapshot s) => s.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .GroupBy(l => Math.Clamp((l.Box.Y + l.Box.H / 2) * TextFeatures.LayoutRows / Math.Max(1, s.Height), 0, TextFeatures.LayoutRows - 1) * TextFeatures.LayoutCols
                        + Math.Clamp((l.Box.X + l.Box.W / 2) * TextFeatures.LayoutCols / Math.Max(1, s.Width), 0, TextFeatures.LayoutCols - 1))
            .ToDictionary(g => g.Key, g => g.Select(l => l.Text.Trim()).OrderBy(x => x, StringComparer.Ordinal).ToList());
        var ca = ByCell(a); var cb = ByCell(b);
        int add = 0, rem = 0, chg = 0, num = 0, sup = 0; var cells = new List<int>();
        bool px = PixelCellsUsable(a, b);
        foreach (var k in ca.Keys.Union(cb.Keys).OrderBy(x => x))
        {
            if (px && !PixelsChanged(a, b, k) && !Same(ca, cb, k)) sup++;   // 数えるだけ（文字の変化からは除かない）
            bool ha = ca.TryGetValue(k, out var la), hb = cb.TryGetValue(k, out var lb);
            if (ha && !hb) { rem++; cells.Add(k); continue; }
            if (!ha && hb) { add++; cells.Add(k); continue; }
            string sa = string.Join("\n", la!), sb = string.Join("\n", lb!);
            if (sa == sb) continue;
            cells.Add(k);
            if (OcrTokens.Describe(sa).Split('|').Select(t => t.StartsWith('#') ? "#" : t).SequenceEqual(OcrTokens.Describe(sb).Split('|').Select(t => t.StartsWith('#') ? "#" : t))) num++; else chg++;
        }
        string kind = add + rem > 0 ? "layout" : chg > 0 ? "strings" : num > 0 ? "numbers" : "none";
        return new TextDiff(kind, add, rem, chg, num, cells, sup);
    }

    private static bool Same(Dictionary<int, List<string>> a, Dictionary<int, List<string>> b, int k) =>
        a.TryGetValue(k, out var x) && b.TryGetValue(k, out var y) && x.SequenceEqual(y);

    private static bool PixelCellsUsable(TextSnapshot a, TextSnapshot b) =>
        a.Width == b.Width && a.Height == b.Height && a.CellHashes.Length == TextReader.GridCols * TextReader.GridRows && b.CellHashes.Length == a.CellHashes.Length
        && (a.CellHashes.Any(h => h != 0) || b.CellHashes.Any(h => h != 0));

    /// <summary>文字の配置の区画（8×6）に重なる画素の区画（16×12）のどれかが変わったか</summary>
    private static bool PixelsChanged(TextSnapshot a, TextSnapshot b, int layoutCell)
    {
        int lx = layoutCell % TextFeatures.LayoutCols, ly = layoutCell / TextFeatures.LayoutCols;
        int x0 = lx * TextReader.GridCols / TextFeatures.LayoutCols, x1 = Math.Max(x0, (lx + 1) * TextReader.GridCols / TextFeatures.LayoutCols - 1);
        int y0 = ly * TextReader.GridRows / TextFeatures.LayoutRows, y1 = Math.Max(y0, (ly + 1) * TextReader.GridRows / TextFeatures.LayoutRows - 1);
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { int i = y * TextReader.GridCols + x; if (a.CellHashes[i] != b.CellHashes[i]) return true; }
        return false;
    }
}
