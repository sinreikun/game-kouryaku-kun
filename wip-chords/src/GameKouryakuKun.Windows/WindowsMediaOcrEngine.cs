using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Ocr;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace GameKouryakuKun.Windows;

/// <summary>
/// Windows 内蔵 OCR（Windows.Media.Ocr）。追加ダウンロード不要のフォールバック。
/// 画面キャプチャ画像（人間が見る画面と同じもの）だけを入力にする。
/// 日本語の言語パック（OCR 機能）が入っていないと日本語は読めないので、その場合は利用不可として理由を返す。
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    public string Id => "windows-media-ocr";
    public string DisplayName => "Windows 内蔵 OCR";
    private WinOcr.OcrEngine? _engine;
    private string? _langTag;
    private string? _error;
    private readonly object _gate = new();
    private readonly bool _allowNonJapanese;

    /// <param name="allowNonJapanese">日本語が無いときにユーザー言語の OCR で代用するか（英数字だけは読める）</param>
    public WindowsMediaOcrEngine(bool allowNonJapanese = true) => _allowNonJapanese = allowNonJapanese;

    private WinOcr.OcrEngine? Engine()
    {
        lock (_gate)
        {
            if (_engine != null || _error != null) return _engine;
            try
            {
                var ja = new global::Windows.Globalization.Language("ja");
                if (WinOcr.OcrEngine.IsLanguageSupported(ja)) { _engine = WinOcr.OcrEngine.TryCreateFromLanguage(ja); _langTag = "ja"; }
                if (_engine == null && _allowNonJapanese)
                {
                    _engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
                    _langTag = _engine?.RecognizerLanguage?.LanguageTag;
                }
                if (_engine == null) _error = "OCR に使える言語がありません（設定 → 時刻と言語 → 言語と地域 で「日本語」を追加し、オプションで OCR を入れてください）";
            }
            catch (Exception e) { _error = $"Windows OCR を初期化できません: {e.GetType().Name}: {e.Message}"; }
            return _engine;
        }
    }

    public (bool Ok, string Detail) Availability()
    {
        var e = Engine();
        if (e == null) return (false, _error ?? "利用不可");
        return (true, _langTag == "ja" ? "日本語で認識" : $"日本語の OCR 言語がないため {_langTag} で代用（日本語は読めません）");
    }

    public async Task<OcrResult> RecognizeAsync(Frame image, CancellationToken ct = default)
    {
        var engine = Engine() ?? throw new InvalidOperationException(_error);
        var sw = Stopwatch.StartNew();
        // 最大寸法を超える場合は縮小
        var img = image;
        int max = (int)WinOcr.OcrEngine.MaxImageDimension, factor = 1;
        while (Math.Max(img.Width, img.Height) / factor > max) factor++;
        if (factor > 1) img = image.Downscale(factor);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(img.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, img.Width, img.Height, BitmapAlphaMode.Premultiplied);
        ct.ThrowIfCancellationRequested();
        var r = await engine.RecognizeAsync(bmp).AsTask(ct).ConfigureAwait(false);
        bool cjk = _langTag?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true;
        var lines = new List<OcrLine>();
        foreach (var l in r.Lines)
        {
            if (l.Words.Count == 0) continue;
            double x0 = l.Words.Min(w => w.BoundingRect.X), y0 = l.Words.Min(w => w.BoundingRect.Y);
            double x1 = l.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width), y1 = l.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            // 日本語は単語ごとに空白が入るので詰める
            string text = cjk ? string.Concat(l.Words.Select(w => w.Text)) : l.Text;
            lines.Add(new OcrLine(text, null, new OcrBox((int)(x0 * factor), (int)(y0 * factor), (int)Math.Ceiling((x1 - x0) * factor), (int)Math.Ceiling((y1 - y0) * factor))));
        }
        return new OcrResult(Id, lines, sw.Elapsed.TotalMilliseconds, $"言語 {_langTag}{(factor > 1 ? $" ／ 1/{factor} に縮小" : "")}");
    }

    public void Dispose() { }
}
