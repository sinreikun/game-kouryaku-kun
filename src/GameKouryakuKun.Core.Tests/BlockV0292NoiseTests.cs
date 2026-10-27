using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Core.Ocr;
using Xunit;
using TextReader = GameKouryakuKun.Core.Ocr.TextReader;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.29.2: 取り込みのノイズ（実機 #110: 落ち着いた画面でも区画のほぼ全部が「変わった」）。正解は試験の側だけ</summary>
public class BlockV0292NoiseTests
{
    private const int W = 320, H = 240;
    private static Frame Img(long id, int seed, int noise, Action<byte[]>? draw = null)
    {
        var r = new Random(seed); var b = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            byte v = (byte)(60 + (i % W) / 4);   // なめらかな背景（4 ビットの境目をまたぐ値を含む）
            for (int c = 0; c < 3; c++) b[i * 4 + c] = (byte)Math.Clamp(v + r.Next(-noise, noise + 1), 0, 255);
            b[i * 4 + 3] = 255;
        }
        draw?.Invoke(b);
        return new Frame(id, W, H, b, 0);
    }
    private static void Box(byte[] b, int x0, int y0, int w, int h, byte v)
    { for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) { int i = (y * W + x) * 4; b[i] = b[i + 1] = b[i + 2] = v; } }

    [Fact]
    public void Capture_noise_changes_no_cell_but_raw_hash_would_change_most()
    {
        var s = new CellStabilizer(TextReader.GridCols, TextReader.GridRows, 16);
        var a = s.Apply(Img(1, 1, 3)); int raw = 0, stable = 0;
        var ra = TextReader.Cells(Img(1, 1, 3));
        for (int k = 2; k < 12; k++)
        {
            var f = Img(k, k, 3);
            var rb = TextReader.Cells(f); raw += rb.Where((h, i) => h != ra[i]).Count();
            var b = s.Apply(f); stable += b.Where((h, i) => h != a[i]).Count();
        }
        Assert.True(raw > 10 * 192 / 2);   // 完全一致のハッシュでは半分以上が変わる（実機 #110 と同じ現象）
        Assert.Equal(0, stable);
    }

    [Fact]
    public void Real_small_change_is_still_detected_under_noise_in_the_right_cell()
    {
        var s = new CellStabilizer(TextReader.GridCols, TextReader.GridRows, 16);
        var a = s.Apply(Img(1, 1, 3));
        var b = s.Apply(Img(2, 2, 3, px => Box(px, 105, 45, 8, 12, 250)));   // 文字 1 つほどの明るい点（区画 (5,2) の中）
        var ch = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToList();
        Assert.Equal(new[] { 2 * TextReader.GridCols + 5 }, ch);
    }

    [Fact]
    public void Slow_drift_accumulates_against_the_reference_and_is_eventually_a_change()
    {
        var s = new CellStabilizer(4, 3, 8);
        Frame Flat(long id, byte v) { var b = new byte[W * H * 4]; for (int i = 0; i < W * H; i++) { b[i * 4] = b[i * 4 + 1] = b[i * 4 + 2] = v; b[i * 4 + 3] = 255; } return new Frame(id, W, H, b, 0); }
        var a = s.Apply(Flat(1, 100)); bool changed = false; int at = -1;
        for (int k = 1; k <= 10 && !changed; k++) { var b = s.Apply(Flat(k + 1, (byte)(100 + 5 * k))); if (!b.SequenceEqual(a)) { changed = true; at = k; } }
        Assert.True(changed); Assert.Equal(5, at);   // 25 > 24 で初めて
    }

    [Fact]
    public void Text_reader_reuses_lines_on_noise_only_frames()
    {
        var r = new TextReader();
        var f1 = Img(1, 1, 3); var p1 = r.Plan(f1, null);
        var snap = new TextSnapshot(1, W, H, new[] { new OcrLine("35", 0.9, new OcrBox(10, 10, 30, 12)) }, p1.CellHashes);
        var p2 = r.Plan(Img(2, 2, 3), snap);
        Assert.Equal("reuse", p2.Kind);
    }

    [Fact]
    public void Entities_do_not_become_one_full_screen_blob_under_noise()
    {
        var t = new EntityTracker();
        t.Update(FineGrid.Of(Img(1, 1, 3), t.Stabilizer));
        for (int k = 0; k < 6; k++)
        {
            var l = t.Update(FineGrid.Of(Img(k + 2, k + 2, 3, px => Box(px, 50 + 10 * k, 100, 20, 20, 250)), t.Stabilizer));
            Assert.All(l.Candidates, c => Assert.True(c.Cells <= 12));
            Assert.NotEmpty(l.Candidates);
        }
    }
}

public class BlockV0293ReaderVarianceTests
{
    private const int W = 320, H = 240;
    private static Frame Img(long id, Action<byte[]>? draw = null)
    {
        var b = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { b[i * 4] = b[i * 4 + 1] = b[i * 4 + 2] = 50; b[i * 4 + 3] = 255; }
        draw?.Invoke(b); return new Frame(id, W, H, b, 0);
    }
    private static void Box(byte[] b, int x0, int y0, int w, int h, byte v)
    { for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) { int i = (y * W + x) * 4; b[i] = b[i + 1] = b[i + 2] = v; } }

    [Fact]
    public async Task Region_reread_of_unchanged_pixels_keeps_previous_text_but_changed_line_is_read()
    {
        // 正解: 行 A（左上、画素は変わらない）・行 B（右下、画素が変わる）。読み手は切り出しで行 A を毎回違う文字に読む（揺れ）
        var r = new GameKouryakuKun.Core.Ocr.TextReader();
        var f1 = Img(1, px => { Box(px, 10, 2, 200, 8, 250); Box(px, 200, 200, 40, 12, 250); });
        int call = 0;
        Task<OcrResult?> Engine(Frame f)
        {
            call++;
            var l = new List<OcrLine>();
            // 全体の座標で返す（切り出しなら原点がずれるので戻す）
            if (f.Width == W) { l.Add(new OcrLine("HP 35", 0.9, new OcrBox(10, 2, 200, 8))); l.Add(new OcrLine("abc", 0.9, new OcrBox(200, 200, 40, 12))); }
            else { l.Add(new OcrLine("HP 3S" + call, 0.9, new OcrBox(10 - Off.X, 2 - Off.Y, 200, 8))); l.Add(new OcrLine("xyz", 0.9, new OcrBox(200 - Off.X, 200 - Off.Y, 40, 12))); }
            return Task.FromResult<OcrResult?>(new OcrResult("t", l, 1, "region"));
        }
        var (s1, k1, _, _) = await r.ReadAsync(f1, null, Engine);
        Assert.Equal("full", k1);
        // 行 B と、行 A の右の小さな点の画素を変える（読む矩形が行 A に掛かり、行 A ごと読み直される）
        var f2 = Img(2, px => { Box(px, 10, 2, 200, 8, 250); Box(px, 200, 200, 40, 12, 120); Box(px, 285, 2, 6, 6, 200); });
        var plan = r.Plan(f2, s1); Off = (plan.Region?.X ?? 0, plan.Region?.Y ?? 0);
        var (s2, k2, _, _) = await r.ReadAsync(f2, s1, Engine);
        Assert.True(k2 == "region", k2 + " " + plan.Region + " " + string.Join("/", s2.Lines.Select(l => l.Text)));
        Assert.Contains(s2.Lines, l => l.Text == "HP 35");      // 画素が同じ行は前の読み
        Assert.Contains(s2.Lines, l => l.Text == "xyz");        // 画素が変わった行は新しい読み
        Assert.Equal(1, r.KeptUnchangedLines);
    }
    private static (int X, int Y) Off;
}
