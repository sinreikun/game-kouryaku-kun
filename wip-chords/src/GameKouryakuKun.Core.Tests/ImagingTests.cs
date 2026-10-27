using GameKouryakuKun.Core.Imaging;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class FrameDiffTests
{
    [Fact]
    public void Identical_frames_have_no_change()
    {
        var c = new Canvas(320, 200);
        var d = FrameDiff.Compare(c.ToFrame(0), c.ToFrame(1));
        Assert.True(d.Comparable); Assert.Equal(0, d.ChangedRatio); Assert.Equal(0, d.ComponentCount);
        Assert.False(ChangeCriteria.Default.IsChanged(d)); Assert.True(ChangeCriteria.Default.IsStill(d));
    }

    [Fact]
    public void Large_block_change_like_a_menu_is_detected()
    {
        var a = new Canvas(320, 200); var b = a.Clone().Rect(40, 120, 240, 60, 250, 250, 250);
        var d = FrameDiff.Compare(a.ToFrame(0), b.ToFrame(1), new DiffOptions { SampleStep = 1 });
        Assert.InRange(d.LargestComponentRatio, 0.2, 0.25);
        Assert.Equal(1, d.ComponentCount);
        Assert.True(ChangeCriteria.Default.IsChanged(d));
    }

    [Fact]
    public void Scattered_small_changes_like_npc_motion_are_not_a_change()
    {
        var a = new Canvas(640, 400); var b = a.Clone();
        // 8px 角の小さな変化を離して10個（合計は約0.25%だが、まとまった領域がない）
        for (int i = 0; i < 10; i++) b.Rect(20 + i * 60, 30 + (i % 3) * 100, 8, 8, 255, 0, 0);
        var d = FrameDiff.Compare(a.ToFrame(0), b.ToFrame(1), new DiffOptions { SampleStep = 1 });
        Assert.True(d.ComponentCount >= 10);
        Assert.True(d.LargestComponentRatio < 0.001);
        Assert.False(ChangeCriteria.Default.IsChanged(d));
    }

    [Fact]
    public void Mask_excludes_region()
    {
        var a = new Canvas(320, 200); var b = a.Clone().Rect(0, 0, 64, 64, 255, 255, 255);
        var grid = new BlockGrid(320, 200, 8);
        var mask = new BlockMask(grid);
        for (int by = 0; by < 8; by++) for (int bx = 0; bx < 8; bx++) mask[bx, by] = true;
        var d = FrameDiff.Compare(a.ToFrame(0), b.ToFrame(1), null, mask);
        Assert.Equal(0, d.ChangedRatio);
    }

    [Fact]
    public void Size_mismatch_is_incomparable_and_counts_as_change()
    {
        var d = FrameDiff.Compare(new Canvas(100, 100).ToFrame(0), new Canvas(120, 100).ToFrame(1));
        Assert.False(d.Comparable);
        Assert.True(ChangeCriteria.Default.IsChanged(d));
        Assert.False(ChangeCriteria.Default.IsStill(d));
    }

    [Fact]
    public void Sampled_diff_is_close_to_full_diff()
    {
        var a = new Canvas(320, 200); var b = a.Clone().Rect(50, 50, 120, 80, 200, 10, 10);
        var full = FrameDiff.Compare(a.ToFrame(0), b.ToFrame(1), new DiffOptions { SampleStep = 1 });
        var half = FrameDiff.Compare(a.ToFrame(0), b.ToFrame(1), new DiffOptions { SampleStep = 2 });
        Assert.InRange(Math.Abs(full.LargestComponentRatio - half.LargestComponentRatio), 0, 0.01);
    }
}

public class NoiseCalibratorTests
{
    [Fact]
    public void Idle_animation_is_masked_and_thresholds_stay_above_noise()
    {
        var baseC = new Canvas(320, 200);
        var frames = new List<Frame>();
        for (int i = 0; i < 8; i++)
        {
            var c = baseC.Clone();
            // 待機モーション: 中央の 24x24 が毎フレーム色を変える
            c.Rect(148, 88, 24, 24, (byte)(i * 30), 100, 100);
            frames.Add(c.ToFrame(i * 33));
        }
        var r = NoiseCalibrator.Calibrate(frames);
        Assert.NotNull(r.IdleMask);
        Assert.InRange(r.IdleMask!.CoveredRatio(), 0.005, 0.05);
        Assert.Equal(0, r.NoiseMaxRatio); // マスク後は残留ノイズなし
        // マスク後、メニュー相当の変化は検出できる
        var menu = baseC.Clone().Rect(20, 130, 280, 60, 250, 250, 250).ToFrame(500);
        Assert.True(r.Criteria.IsChanged(FrameDiff.Compare(frames[0], menu, null, r.IdleMask)));
    }

    [Fact]
    public void Too_wide_animation_is_not_masked_but_raises_threshold()
    {
        var frames = Enumerable.Range(0, 5).Select(i => new Canvas(160, 100).Rect(0, 0, 160, 40, (byte)(i * 40), 0, 0).ToFrame(i * 33)).ToList();
        var r = NoiseCalibrator.Calibrate(frames);
        Assert.Null(r.IdleMask);
        Assert.True(r.Criteria.ChangeComponent > 0.3);
    }

    [Fact]
    public void Fewer_than_two_frames_gives_defaults()
    {
        var r = NoiseCalibrator.Calibrate(new[] { new Canvas(10, 10).ToFrame(0) });
        Assert.Equal(ChangeCriteria.Default, r.Criteria);
    }
}

public class FixedOverlayTrackerTests
{
    [Fact]
    public void Screen_fixed_overlay_is_found_during_scrolls()
    {
        var t = new FixedOverlayTracker { MinEvents = 3 };
        Frame Make(int off) => Canvas.Checker(320, 192, 16, off, 0).Rect(100, 60, 40, 32, 200, 150, 50).Rect(0, 0, 320, 16, 20, 20, 20).ToFrame(off);
        var prev = Make(0);
        for (int k = 1; k <= 5; k++)
        {
            var cur = Make(k * 16);
            var d = FrameDiff.Compare(prev, cur, new DiffOptions { SampleStep = 1 });
            t.Observe(d);
            prev = cur;
        }
        var regions = t.Regions();
        Assert.True(t.BigEvents >= 3);
        Assert.Contains(regions, r => r.X0 >= 96 && r.X1 <= 144 && r.Y0 >= 56 && r.Y1 <= 96 && r.X0 <= 120 && r.X1 >= 120); // 半透明の四角に相当（縁のブロックは除く）
        Assert.Contains(regions, r => r.Y0 == 0 && r.X1 - r.X0 >= 300);                       // 上部のステータス帯に相当
    }
}

public class PanelDetectorTests
{
    private static Canvas Field(int w = 480, int h = 270)
    {
        // 地図らしい模様（彩度のある格子＋明るい点）。枠は無い
        var c = Canvas.Checker(w, h, 24, 0, 0);
        for (int i = 0; i < 40; i++) c.Rect((i * 37) % w, (i * 53) % h, 2, 2, 250, 250, 250);
        return c;
    }

    private static Canvas WithPanel(Canvas c, int x, int y, int pw, int ph, int t = 2)
    {
        c.Rect(x, y, pw, ph, 30, 30, 30);                 // 暗い地
        c.Rect(x, y, pw, t, 240, 240, 240);               // 上辺
        c.Rect(x, y + ph - t, pw, t, 240, 240, 240);      // 下辺
        c.Rect(x, y, t, ph, 240, 240, 240);               // 左辺
        c.Rect(x + pw - t, y, t, ph, 240, 240, 240);      // 右辺
        return c;
    }

    [Fact]
    public void Field_without_border_has_no_panel()
    {
        Assert.Empty(new PanelDetector().Detect(Field().ToFrame(0)));
        Assert.Empty(new PanelDetector().Detect(new Canvas(320, 180, 20).ToFrame(0)));
    }

    [Fact]
    public void Bordered_rectangle_is_detected_with_its_position()
    {
        var f = WithPanel(Field(), 60, 40, 200, 90).ToFrame(0);
        var r = new PanelDetector().Detect(f);
        Assert.NotEmpty(r);
        Assert.Contains(r, p => Math.Abs(p.X - 60) <= 2 && Math.Abs(p.Y - 40) <= 2 && Math.Abs(p.Width - 200) <= 4);
    }

    [Fact]
    public void Wide_bright_area_or_short_line_is_not_a_panel()
    {
        var c = Field();
        c.Rect(0, 100, 480, 60, 245, 245, 245);           // 太い明るい帯（線ではない）
        c.Rect(100, 20, 20, 2, 240, 240, 240);            // 短い線
        Assert.Empty(new PanelDetector().Detect(c.ToFrame(0)));
    }

    [Fact]
    public void Colored_border_is_detected()
    {
        // 白以外の色の細い枠（暗い地）。色は合成のもので、特定のゲームの色ではない
        var c = Field();
        c.Rect(60, 150, 300, 80, 14, 26, 44);
        c.Rect(60, 150, 300, 3, 80, 126, 225); c.Rect(60, 227, 300, 3, 80, 126, 225);
        c.Rect(60, 150, 3, 80, 80, 126, 225); c.Rect(357, 150, 3, 80, 80, 126, 225);
        Assert.Contains(new PanelDetector().Detect(c.ToFrame(0)), p => Math.Abs(p.X - 60) <= 4 && Math.Abs(p.Y - 150) <= 3 && Math.Abs(p.Width - 300) <= 8);
    }

    [Fact]
    public void Bright_colored_field_without_border_is_not_a_panel()
    {
        // 彩度の高い明るい面（草地など）と、その中の色付きの短い線・太い帯
        var c = new Canvas(480, 270, 40);
        c.Rect(0, 0, 480, 120, 30, 200, 40);
        c.Rect(0, 130, 480, 40, 40, 140, 230);
        c.Rect(100, 200, 30, 2, 40, 140, 230);
        for (int i = 0; i < 30; i++) c.Rect((i * 41) % 480, 180 + (i * 7) % 80, 6, 6, 20, 210, 30);
        Assert.Empty(new PanelDetector().Detect(c.ToFrame(0)));
    }

    [Fact]
    public void Detection_does_not_depend_on_resolution_or_overall_brightness()
    {
        // 閾値は画像の大きさ・明るさの分布に対する比なので、縮小・暗くした画面でも同じ枠が見つかる
        foreach (var (w, h, k) in new[] { (480, 270, 1.0), (960, 540, 1.0), (480, 270, 0.5) })
        {
            var c = new Canvas(w, h, 40);
            for (int i = 0; i < 60; i++) c.Rect((i * 53) % w, (i * 29) % h, w / 40, h / 40, (byte)(90 * k), (byte)(160 * k), (byte)(60 * k));
            int x = w / 8, y = h / 2, pw = w / 2, ph = h / 4, t = Math.Max(2, h / 135);
            c.Rect(x, y, pw, ph, (byte)(20 * k), (byte)(20 * k), (byte)(20 * k));
            byte v = (byte)(230 * k);
            c.Rect(x, y, pw, t, v, v, v); c.Rect(x, y + ph - t, pw, t, v, v, v); c.Rect(x, y, t, ph, v, v, v); c.Rect(x + pw - t, y, t, ph, v, v, v);
            Assert.Contains(new PanelDetector().Detect(c.ToFrame(0)), p => Math.Abs(p.X - x) <= t + 2 && Math.Abs(p.Y - y) <= t + 2);
        }
    }

    [Fact]
    public void Uniform_or_noise_screens_have_no_panel()
    {
        Assert.Empty(new PanelDetector().Detect(new Canvas(320, 180, 128).ToFrame(0)));
        var n = new Canvas(320, 180, 0); var rng = new Random(1);
        for (int y = 0; y < 180; y += 2) for (int x = 0; x < 320; x += 2) { byte v = (byte)rng.Next(256); n.Rect(x, y, 2, 2, v, (byte)rng.Next(256), (byte)rng.Next(256)); }
        Assert.Empty(new PanelDetector().Detect(n.ToFrame(0)));
    }

    [Fact]
    public void Horizontal_line_without_sides_is_not_a_panel()
    {
        var c = Field();
        c.Rect(40, 200, 400, 2, 240, 240, 240);
        Assert.Empty(new PanelDetector().Detect(c.ToFrame(0)));
    }
}

public class SceneStatsTests
{
    [Fact]
    public void Mean_luminance_is_a_plain_feature()
    {
        Assert.Equal(5 / 255.0, SceneStats.MeanLuminance(new Canvas(320, 180, 5).ToFrame(0)), 3);
        var half = new Canvas(320, 180, 0); half.Rect(0, 0, 160, 180, 255, 255, 255);
        Assert.InRange(SceneStats.MeanLuminance(half.ToFrame(0)), 0.45, 0.55);
    }
}
