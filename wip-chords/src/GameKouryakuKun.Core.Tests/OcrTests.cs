using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Ocr;
using GameKouryakuKun.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace GameKouryakuKun.Core.Tests;

public class OcrTests
{
    private readonly ITestOutputHelper _out;
    public OcrTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Ctc_decode_collapses_repeats_and_skips_blank_and_maps_space()
    {
        string[] dict = { "あ", "い", "う" }; // index 1..3, 4 = 空白
        int C = 5;
        int[] path = { 0, 1, 1, 0, 1, 2, 4, 3, 3, 0 };
        var p = new float[path.Length * C];
        for (int t = 0; t < path.Length; t++) { for (int c = 0; c < C; c++) p[t * C + c] = 0.01f; p[t * C + path[t]] = 0.9f; }
        var (text, conf) = PaddleOcrEngine.CtcDecode(p, 0, path.Length, C, dict);
        Assert.Equal("ああい う", text);
        Assert.Equal(0.9, conf, 3);
    }

    [Fact]
    public void Prob_map_gives_unclipped_boxes_in_reading_order()
    {
        int pw = 40, ph = 20;
        var p = new float[pw * ph];
        void Fill(int x, int y, int w, int h, float v) { for (int j = y; j < y + h; j++) for (int i = x; i < x + w; i++) p[j * pw + i] = v; }
        Fill(22, 2, 10, 4, 0.9f);  // 1行目・右
        Fill(2, 3, 12, 4, 0.9f);   // 1行目・左
        Fill(2, 12, 30, 5, 0.8f);  // 2行目
        Fill(36, 12, 2, 2, 0.9f);  // 小さすぎ → 除外
        Fill(20, 16, 6, 3, 0.35f); // 平均確率が低い → 除外
        var boxes = PaddleOcrEngine.BoxesFromProbMap(p, pw, ph, 2, 2, 80, 40, new PaddleOcrOptions());
        Assert.Equal(3, boxes.Count);
        Assert.True(boxes[0].X < boxes[1].X && boxes[1].Y < boxes[2].Y);
        Assert.True(boxes[0].X < 4 && boxes[0].W > 24); // unclip で広がる
        Assert.All(boxes, b => Assert.True(b.X >= 0 && b.Y >= 0 && b.X + b.W <= 80 && b.Y + b.H <= 40));
    }

    private sealed class FakeEngine : IOcrEngine
    {
        public string Id { get; init; } = "";
        public string DisplayName => Id;
        public bool Ok { get; init; }
        public bool Throws { get; init; }
        public (bool Ok, string Detail) Availability() => (Ok, Ok ? "ok" : "未導入");
        public Task<OcrResult> RecognizeAsync(Frame image, CancellationToken ct = default) =>
            Throws ? throw new InvalidOperationException("壊れた") : Task.FromResult(new OcrResult(Id, new[] { new OcrLine("文字", 0.9, new OcrBox(0, 0, 1, 1)) }, 1, ""));
        public void Dispose() { }
    }

    [Fact]
    public async Task Chain_falls_back_to_next_available_engine()
    {
        var chain = new OcrEngineChain(new IOcrEngine[] { new FakeEngine { Id = "paddle", Ok = false }, new FakeEngine { Id = "broken", Ok = true, Throws = true }, new FakeEngine { Id = "windows", Ok = true } });
        var (r, tried) = await chain.RecognizeAsync(new Canvas(4, 4).ToFrame(0));
        Assert.Equal("windows", r!.EngineId);
        Assert.Equal(3, tried.Count);
        Assert.Equal("broken", chain.FirstAvailable()!.Id);
    }

    [Fact]
    public void Paddle_reports_missing_components_without_loading_anything()
    {
        using var d = new TempDir();
        using var e = new PaddleOcrEngine(d.Path, d.Path);
        var (ok, detail) = e.Availability();
        Assert.False(ok);
        Assert.Contains("未導入", detail);
    }

    /// <summary>
    /// 実モデル＋Linux版 ONNX Runtime での実行（環境があるときだけ）。
    /// 実機で保存されたゲーム画面のスクリーンショット（人間が見る画面と同じもの）を読む。
    /// </summary>
    [Fact]
    public void Real_paddle_model_reads_rendered_and_game_screens_when_available()
    {
        string model = Environment.GetEnvironmentVariable("GKK_OCR_MODEL_DIR") ?? "/workspace/ocrtest/model";
        string ort = Environment.GetEnvironmentVariable("GKK_ORT_DIR") ?? "/workspace/ocrtest/ort";
        using var e = new PaddleOcrEngine(model, ort);
        if (!e.Availability().Ok) { _out.WriteLine("スキップ: " + e.Availability().Detail); return; }
        string shots = Environment.GetEnvironmentVariable("GKK_OCR_SAMPLES") ?? "/workspace/game-kouryaku-kun/phase1-results";
        int n = 0;
        if (Directory.Exists(shots))
            foreach (var f in Directory.GetFiles(shots, "*.png", SearchOption.AllDirectories).Where(f => f.Contains("1before") || f.Contains("calibration")).OrderBy(f => f).Take(4))
            {
                var img = FramePngExtensions.LoadPng(f);
                var r = e.Recognize(img);
                _out.WriteLine($"== {Path.GetFileName(f)} {img.Width}x{img.Height}: {r.Detail}");
                foreach (var l in r.Lines) _out.WriteLine($"  [{l.Confidence:0.00}] {l.Box} {l.Text}");
                Assert.NotEmpty(r.Lines);
                n++;
            }
        _out.WriteLine($"読んだ画像: {n}");
    }
}

public class OcrTokenAndMaskTests
{
    [Theory]
    [InlineData("AB15/15", "AB|#15|/|#15")]
    [InlineData("あいう94%", "あいう|#94|%")]
    [InlineData("Lv1", "Lv|#1")]
    [InlineData("１階", "#1|階")]
    [InlineData("0G", "#0|G")]
    [InlineData("ＯＧ", "OG")]          // 全角は半角にそろえるだけ。O を 0 に直すような推測はしない
    [InlineData("", "")]
    public void Splits_into_number_and_text_tokens(string input, string expected)
        => Assert.Equal(expected, OcrTokens.Describe(input));

    [Fact]
    public void Numbers_returns_values_in_order()
        => Assert.Equal(new long[] { 15, 15 }, OcrTokens.Numbers("AB 15 / 15"));

    [Fact]
    public void Allowed_char_mask_prefers_next_best_allowed_class()
    {
        string[] dict = { "满", "満", "A" };  // 1..3, 4=空白
        var mask = PaddleOcrEngine.BuildMask(dict, new[] { "満" });
        Assert.Equal(new[] { true, false, true, true, true }, mask); // blank, 满×, 満○, A(ASCII)○, 空白○
        int C = 5; var p = new float[2 * C];
        p[0 * C + 1] = 0.7f; p[0 * C + 2] = 0.2f; p[0 * C + 0] = 0.1f;  // 1文字目: 满 > 満
        p[1 * C + 0] = 0.9f;                                            // blank
        Assert.Equal("满", PaddleOcrEngine.CtcDecode(p, 0, 2, C, dict).Text);
        Assert.Equal("満", PaddleOcrEngine.CtcDecode(p, 0, 2, C, dict, mask).Text);
    }

    [Fact]
    public void V5_preset_uses_separate_files_and_confidence_filter()
    {
        var o = PaddleOcrOptions.V5;
        Assert.Equal(("det.onnx", "rec.onnx", "dict.txt", "allowed_chars.txt"), (o.DetFile, o.RecFile, o.DictFile, o.AllowedCharsFile));
        Assert.True(o.MinConfidence > 0);
        Assert.NotEqual(PaddleOcrOptions.V4.EngineId, o.EngineId);
    }
}
