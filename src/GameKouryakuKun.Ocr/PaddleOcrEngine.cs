using System.Diagnostics;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Ocr;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GameKouryakuKun.Ocr;

public sealed record PaddleOcrOptions
{
    /// <summary>検出入力の長辺上限（32の倍数に丸める）。小さい文字が多い画面では大きめが有利</summary>
    public int DetMaxSide { get; init; } = 1280;
    public string DetFile { get; init; } = "det.onnx";
    public string RecFile { get; init; } = "rec_japan.onnx";
    public string DictFile { get; init; } = "japan_dict.txt";
    /// <summary>
    /// 出力してよい文字の一覧（1行1文字。省略可）。多言語モデルで簡体字などへ揺れるのを防ぐため、
    /// 一覧にない文字のクラスは復号で選ばない（言語の文字集合の制限であって、ゲームの語彙ではない）。
    /// </summary>
    public string? AllowedCharsFile { get; init; }
    /// <summary>行の中の大きな空き（文字の高さ×この比以上）で領域を分ける。0 で無効。アイコン・見出しと数値がつながるのを防ぐ</summary>
    public float SplitGapRatio { get; init; } = 0f;
    /// <summary>認識用の切り出しに足す余白（行の高さに対する比）。左右・上下</summary>
    public float RecPadX { get; init; } = 0f;
    public float RecPadY { get; init; } = 0f;
    /// <summary>認識の前に輝度だけにする（色付き文字の読み違い対策の実験用）</summary>
    public bool RecGray { get; init; }
    /// <summary>この信頼度未満の行は結果から外す（地形の模様などの誤検出対策。外した数は Detail に出す）。0 で無効</summary>
    public double MinConfidence { get; init; } = 0;
    public string EngineId { get; init; } = "paddle-ppocrv4-ja";
    public string EngineName { get; init; } = "PaddleOCR 日本語 (PP-OCRv4 / ONNX Runtime)";
    public float DetThreshold { get; init; } = 0.3f;
    public float BoxThreshold { get; init; } = 0.5f;
    public float UnclipRatio { get; init; } = 1.6f;
    public int MinBoxSide { get; init; } = 3;
    public int RecHeight { get; init; } = 48;
    public int RecMaxWidth { get; init; } = 1600;
    public int RecBatch { get; init; } = 6;
    public int IntraOpThreads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>PP-OCRv4（det ＋ 日本語 rec）。コンポーネント ocr-paddle-ja</summary>
    public static PaddleOcrOptions V4 => new();
    /// <summary>
    /// PP-OCRv5 mobile（det ＋ 多言語 rec、日本語の文字集合に制限、信頼度0.5未満を除外）。コンポーネント ocr-paddle-v5。
    /// 実機画像31枚での文字読み取りのシステム性能評価（U5: 一時的な正解。学習・判定には使っていない）で、v4 の 111/203 → 187/203（英字と数字の連なりを安定して分離）。
    /// </summary>
    public static PaddleOcrOptions V5 => new()
    {
        DetFile = "det.onnx", RecFile = "rec.onnx", DictFile = "dict.txt", AllowedCharsFile = "allowed_chars.txt",
        MinConfidence = 0.5, EngineId = "paddle-ppocrv5", EngineName = "PaddleOCR (PP-OCRv5 mobile / ONNX Runtime)",
    };
}

/// <summary>
/// PaddleOCR（PP-OCRv4/v5 の det ＋ rec）の ONNX 推論（設計書 §2.2 OCR）。
///  検出: 正規化（ImageNet 平均・分散）→ 確率マップ → 2値化 → 連結領域 → 矩形 → 拡張（unclip）
///  認識: 高さ48に縮小した行画像 → CTC 貪欲復号（blank=0、辞書、末尾=空白）
/// 入力のチャネル順は PaddleOCR の学習時と同じ BGR。読んだ文字列は補正しない（辞書・攻略知識による修正なし）。
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine
{
    public string Id => _o.EngineId;
    public string DisplayName => _o.EngineName;
    private readonly string _modelDir, _ortDir;
    private readonly PaddleOcrOptions _o;
    private InferenceSession? _det, _rec;
    private string[]? _dict;
    private bool[]? _allowed;
    private readonly object _gate = new();
    public string? LastError { get; private set; }

    public PaddleOcrEngine(string modelDir, string ortDir, PaddleOcrOptions? options = null)
    {
        _modelDir = modelDir; _ortDir = ortDir; _o = options ?? new PaddleOcrOptions();
    }

    public (bool Ok, string Detail) Availability()
    {
        foreach (var f in new[] { _o.DetFile, _o.RecFile, _o.DictFile }.Concat(_o.AllowedCharsFile is string a ? new[] { a } : Array.Empty<string>()))
            if (!File.Exists(Path.Combine(_modelDir, f))) return (false, $"モデル未導入（{f} がありません。セットアップで「PaddleOCR 日本語モデル」を導入）");
        if (!File.Exists(Path.Combine(_ortDir, OrtNative.LibraryFileName))) return (false, "ONNX Runtime 未導入（セットアップで導入）");
        if (LastError != null) return (false, "初期化に失敗: " + LastError);
        return (true, $"モデル: {_modelDir}");
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_det != null) return;
            try
            {
                if (!OrtNative.Configure(_ortDir)) throw new FileNotFoundException("ONNX Runtime が見つかりません", Path.Combine(_ortDir, OrtNative.LibraryFileName));
                var so = new SessionOptions { IntraOpNumThreads = _o.IntraOpThreads, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                _det = new InferenceSession(Path.Combine(_modelDir, _o.DetFile), so);
                _rec = new InferenceSession(Path.Combine(_modelDir, _o.RecFile), so);
                _dict = File.ReadAllLines(Path.Combine(_modelDir, _o.DictFile));
                _allowed = _o.AllowedCharsFile is string af ? BuildMask(_dict, File.ReadAllLines(Path.Combine(_modelDir, af))) : null;
                LastError = null;
            }
            catch (Exception e) { LastError = $"{e.GetType().Name}: {e.Message}"; _det?.Dispose(); _rec?.Dispose(); _det = _rec = null; throw; }
        }
    }

    public Task<OcrResult> RecognizeAsync(Frame image, CancellationToken ct = default) => Task.Run(() => Recognize(image, ct), ct);

    public OcrResult Recognize(Frame image, CancellationToken ct = default)
    {
        EnsureLoaded();
        var sw = Stopwatch.StartNew();
        var boxes = Detect(image);
        double detMs = sw.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();
        var all = RecognizeBoxes(image, boxes, ct);
        var lines = _o.MinConfidence > 0 ? all.Where(l => (l.Confidence ?? 1) >= _o.MinConfidence).ToList() : all;
        int dropped = all.Count - lines.Count;
        return new OcrResult(Id, lines, sw.Elapsed.TotalMilliseconds,
            $"検出 {boxes.Count} 領域 {detMs:0}ms ／ 認識 {sw.Elapsed.TotalMilliseconds - detMs:0}ms" + (dropped > 0 ? $" ／ 信頼度{_o.MinConfidence:0.0#}未満 {dropped} 行を除外" : ""));
    }

    // ---------------- 検出 ----------------
    internal List<OcrBox> Detect(Frame img)
    {
        float scale = Math.Min(1f, (float)_o.DetMaxSide / Math.Max(img.Width, img.Height));
        int dw = Math.Max(32, (int)Math.Round(img.Width * scale / 32) * 32), dh = Math.Max(32, (int)Math.Round(img.Height * scale / 32) * 32);
        var input = new DenseTensor<float>(new[] { 1, 3, dh, dw });
        float[] mean = { 0.485f, 0.456f, 0.406f }, std = { 0.229f, 0.224f, 0.225f };
        FillBgr(img, 0, 0, img.Width, img.Height, dw, dh, input.Buffer.Span, 0, dw, (v, c) => (v / 255f - mean[c]) / std[c]);
        string inName = _det!.InputMetadata.Keys.First();
        using var outs = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });
        var prob = outs.First().AsTensor<float>();
        var dims = prob.Dimensions; int ph = dims[^2], pw = dims[^1];
        var p = prob.ToArray();
        return BoxesFromProbMap(p, pw, ph, (float)img.Width / pw, (float)img.Height / ph, img.Width, img.Height, _o);
    }

    /// <summary>確率マップ → 文字領域の矩形（元画像座標）。8近傍の連結領域ごとに平均確率で選別し、矩形を unclip で拡張する。</summary>
    internal static List<OcrBox> BoxesFromProbMap(float[] p, int pw, int ph, float sx, float sy, int imgW, int imgH, PaddleOcrOptions o)
    {
        var seen = new bool[p.Length];
        var boxes = new List<OcrBox>();
        var stack = new Stack<int>();
        for (int start = 0; start < p.Length; start++)
        {
            if (seen[start] || p[start] <= o.DetThreshold) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, n = 0; double sum = 0;
            var pix = new List<int>();
            seen[start] = true; stack.Push(start);
            while (stack.Count > 0)
            {
                int c = stack.Pop(), cx = c % pw, cy = c / pw;
                n++; sum += p[c]; pix.Add(c);
                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx; if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= pw || ny >= ph) continue;
                    int k = ny * pw + nx;
                    if (!seen[k] && p[k] > o.DetThreshold) { seen[k] = true; stack.Push(k); }
                }
            }
            if (Math.Min(maxX - minX + 1, maxY - minY + 1) < o.MinBoxSide || sum / n < o.BoxThreshold) continue;
            foreach (var (x0, x1) in SplitColumns(pix, pw, minX, maxX, maxY - minY + 1, o.SplitGapRatio))
            {
                int sMinY = int.MaxValue, sMaxY = -1, sMinX = int.MaxValue, sMaxX = -1;
                foreach (var c in pix) { int cx = c % pw; if (cx < x0 || cx > x1) continue; int cy = c / pw; if (cy < sMinY) sMinY = cy; if (cy > sMaxY) sMaxY = cy; if (cx < sMinX) sMinX = cx; if (cx > sMaxX) sMaxX = cx; }
                if (sMaxY < 0) continue;
                int bw = sMaxX - sMinX + 1, bh = sMaxY - sMinY + 1;
                if (Math.Min(bw, bh) < o.MinBoxSide) continue;
                double d = bw * bh * o.UnclipRatio / (2.0 * (bw + bh)); // 矩形の unclip（Vatti の多角形オフセットを矩形で近似）
                double fx0 = (sMinX - d) * sx, fy0 = (sMinY - d) * sy, fx1 = (sMaxX + 1 + d) * sx, fy1 = (sMaxY + 1 + d) * sy;
                int ix0 = Math.Clamp((int)Math.Floor(fx0), 0, imgW - 1), iy0 = Math.Clamp((int)Math.Floor(fy0), 0, imgH - 1);
                int ix1 = Math.Clamp((int)Math.Ceiling(fx1), ix0 + 1, imgW), iy1 = Math.Clamp((int)Math.Ceiling(fy1), iy0 + 1, imgH);
                boxes.Add(new OcrBox(ix0, iy0, ix1 - ix0, iy1 - iy0));
            }
        }
        // 上から下、同じ行（中心の高さが近い）なら左から右
        return SortReadingOrder(boxes).ToList();
    }

    /// <summary>
    /// 連結領域を、ほぼ空の列が「高さ×ratio」以上続く所で分ける（DB の確率マップでは字間の広い見出しと数値がつながることがある）。
    /// 「ほぼ空」= その列にある画素が高さの 15% 未満。
    /// </summary>
    internal static List<(int X0, int X1)> SplitColumns(List<int> pix, int pw, int minX, int maxX, int height, float ratio)
    {
        var result = new List<(int, int)>();
        if (ratio <= 0) { result.Add((minX, maxX)); return result; }
        var col = new int[maxX - minX + 1];
        foreach (var c in pix) col[c % pw - minX]++;
        int thin = Math.Max(1, (int)(height * 0.15)), minGap = Math.Max(2, (int)Math.Ceiling(height * ratio));
        int segStart = 0, x = 0;
        while (x < col.Length)
        {
            if (col[x] >= thin) { x++; continue; }
            int g0 = x; while (x < col.Length && col[x] < thin) x++;
            if (x - g0 >= minGap && g0 > segStart && x < col.Length) { result.Add((minX + segStart, minX + g0 - 1)); segStart = x; }
        }
        result.Add((minX + segStart, maxX));
        return result;
    }

    internal static IEnumerable<OcrBox> SortReadingOrder(List<OcrBox> boxes)
    {
        var sorted = boxes.OrderBy(b => b.Y + b.H / 2.0).ToList();
        var rows = new List<List<OcrBox>>();
        foreach (var b in sorted)
        {
            var row = rows.LastOrDefault();
            if (row != null && Math.Abs(row.Average(r => r.Y + r.H / 2.0) - (b.Y + b.H / 2.0)) < Math.Min(row.Average(r => r.H), b.H) * 0.5) row.Add(b);
            else rows.Add(new List<OcrBox> { b });
        }
        return rows.SelectMany(r => r.OrderBy(b => b.X));
    }

    // ---------------- 認識 ----------------
    private List<OcrLine> RecognizeBoxes(Frame img, List<OcrBox> boxes, CancellationToken ct)
    {
        var result = new OcrLine?[boxes.Count];
        var order = Enumerable.Range(0, boxes.Count).OrderBy(i => (double)boxes[i].W / boxes[i].H).ToList();
        string inName = _rec!.InputMetadata.Keys.First();
        for (int s = 0; s < order.Count; s += _o.RecBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = order.Skip(s).Take(_o.RecBatch).Select(i => i).ToList();
            int H = _o.RecHeight;
            var crops = batch.Select(i => Pad(boxes[i], img.Width, img.Height)).ToList();
            var widths = crops.Select(b => Math.Clamp((int)Math.Ceiling(H * (double)b.W / b.H), 8, _o.RecMaxWidth)).ToList();
            int W = Math.Max(widths.Max(), H * 320 / 48);
            var input = new DenseTensor<float>(new[] { batch.Count, 3, H, W }); // パディングは 0（正規化後の値）
            var span = input.Buffer.Span;
            for (int k = 0; k < batch.Count; k++)
            {
                var b = crops[k];
                FillBgr(img, b.X, b.Y, b.W, b.H, widths[k], H, span, k * 3 * H * W, W, (v, _) => (v / 255f - 0.5f) / 0.5f, _o.RecGray);
            }
            using var outs = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });
            var t = outs.First().AsTensor<float>();
            int T = t.Dimensions[1], C = t.Dimensions[2];
            var arr = t.ToArray();
            for (int k = 0; k < batch.Count; k++)
            {
                var (text, conf) = CtcDecode(arr, k * T * C, T, C, _dict!, _allowed);
                result[batch[k]] = new OcrLine(text, conf, boxes[batch[k]]);
            }
        }
        return result.Where(l => l != null && l.Text.Trim().Length > 0).Select(l => l!).ToList();
    }

    private OcrBox Pad(OcrBox b, int iw, int ih)
    {
        if (_o.RecPadX <= 0 && _o.RecPadY <= 0) return b;
        int px = (int)Math.Round(b.H * _o.RecPadX), py = (int)Math.Round(b.H * _o.RecPadY);
        int x0 = Math.Max(0, b.X - px), y0 = Math.Max(0, b.Y - py), x1 = Math.Min(iw, b.X + b.W + px), y1 = Math.Min(ih, b.Y + b.H + py);
        return new OcrBox(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>CTC 貪欲復号。index 0 = blank、1..N = 辞書、N+1 = 空白（PaddleOCR の use_space_char）</summary>
    /// <summary>クラスごとの可否（0=blank と末尾の空白は常に可。ASCII と全角英数記号も常に可）</summary>
    internal static bool[] BuildMask(string[] dict, IEnumerable<string> allowedLines)
    {
        var set = new HashSet<string>(allowedLines.Where(l => l.Length > 0), StringComparer.Ordinal);
        var m = new bool[dict.Length + 2];
        m[0] = true; m[^1] = true;
        for (int i = 0; i < dict.Length; i++)
        {
            var ch = dict[i];
            bool basic = ch.Length == 1 && (ch[0] < 0x80 || (ch[0] >= 0xFF01 && ch[0] <= 0xFF5E) || (ch[0] >= 0x3000 && ch[0] <= 0x30FF));
            m[i + 1] = basic || set.Contains(ch);
        }
        return m;
    }

    internal static (string Text, double Confidence) CtcDecode(float[] probs, int offset, int T, int C, string[] dict, bool[]? allowed = null)
    {
        var sb = new System.Text.StringBuilder();
        int prev = -1; double confSum = 0; int n = 0;
        for (int t = 0; t < T; t++)
        {
            int best = 0; float bp = float.MinValue; int o = offset + t * C;
            for (int c = 0; c < C; c++) if (probs[o + c] > bp && (allowed == null || c >= allowed.Length || allowed[c])) { bp = probs[o + c]; best = c; }
            if (best != 0 && best != prev)
            {
                string ch = best - 1 < dict.Length ? dict[best - 1] : " ";
                sb.Append(ch); confSum += bp; n++;
            }
            prev = best;
        }
        return (sb.ToString(), n == 0 ? 0 : confSum / n);
    }

    /// <summary>BGRA の矩形を (dw×dh) へバイリニア縮小し、CHW（B,G,R の順）で dst に書く。行の幅は rowStride（≥dw）。</summary>
    internal static void FillBgr(Frame img, int x, int y, int w, int h, int dw, int dh, Span<float> dst, int offset, int rowStride, Func<float, int, float> norm, bool gray = false)
    {
        var src = img.Bgra; int iw = img.Width;
        int plane = dh * rowStride;
        double fx = (double)w / dw, fy = (double)h / dh;
        Span<double> v = stackalloc double[3];
        for (int j = 0; j < dh; j++)
        {
            double syf = y + (j + 0.5) * fy - 0.5; int y0 = (int)Math.Floor(syf); double ay = syf - y0;
            int ya = Math.Clamp(y0, y, y + h - 1), yb = Math.Clamp(y0 + 1, y, y + h - 1);
            for (int i = 0; i < dw; i++)
            {
                double sxf = x + (i + 0.5) * fx - 0.5; int x0 = (int)Math.Floor(sxf); double ax = sxf - x0;
                int xa = Math.Clamp(x0, x, x + w - 1), xb = Math.Clamp(x0 + 1, x, x + w - 1);
                int p00 = (ya * iw + xa) * 4, p01 = (ya * iw + xb) * 4, p10 = (yb * iw + xa) * 4, p11 = (yb * iw + xb) * 4;
                for (int c = 0; c < 3; c++)
                    v[c] = (src[p00 + c] * (1 - ax) + src[p01 + c] * ax) * (1 - ay) + (src[p10 + c] * (1 - ax) + src[p11 + c] * ax) * ay;
                if (gray) { double l = 0.114 * v[0] + 0.587 * v[1] + 0.299 * v[2]; v[0] = v[1] = v[2] = l; }
                for (int c = 0; c < 3; c++) dst[offset + c * plane + j * rowStride + i] = norm((float)v[c], c);
            }
        }
    }

    public void Dispose() { lock (_gate) { _det?.Dispose(); _rec?.Dispose(); _det = _rec = null; } }
}
