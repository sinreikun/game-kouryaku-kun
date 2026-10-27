using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Ocr;

public sealed record OcrBox(int X, int Y, int W, int H)
{
    public override string ToString() => $"({X},{Y}) {W}x{H}";
}

/// <summary>読み取った1行。Confidence はエンジンが返す場合のみ（0〜1）</summary>
public sealed record OcrLine(string Text, double? Confidence, OcrBox Box);

public sealed record OcrResult(string EngineId, IReadOnlyList<OcrLine> Lines, double ElapsedMs, string Detail)
{
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>
/// OCR エンジン（設計書 付録A IOcrEngine）。画面の文字を読むだけで、読んだ文字列の意味付けはしない
/// （ラベルは読んだ文字列のまま扱う。攻略知識・辞書による補正は入れない）。
/// </summary>
public interface IOcrEngine : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>使えるか（モデル未導入・言語パック未導入などの理由つき）</summary>
    (bool Ok, string Detail) Availability();
    Task<OcrResult> RecognizeAsync(Frame image, CancellationToken ct = default);
}

/// <summary>エンジンを優先順に試す（既定: PaddleOCR → Windows 内蔵 OCR）</summary>
public sealed class OcrEngineChain
{
    public IReadOnlyList<IOcrEngine> Engines { get; }
    public OcrEngineChain(IEnumerable<IOcrEngine> engines) => Engines = engines.ToList();

    public IOcrEngine? FirstAvailable() => Engines.FirstOrDefault(e => e.Availability().Ok);

    public async Task<(OcrResult? Result, IReadOnlyList<string> Tried)> RecognizeAsync(Frame image, CancellationToken ct = default)
    {
        var tried = new List<string>();
        foreach (var e in Engines)
        {
            var (ok, detail) = e.Availability();
            if (!ok) { tried.Add($"{e.DisplayName}: 使用不可（{detail}）"); continue; }
            try { var r = await e.RecognizeAsync(image, ct).ConfigureAwait(false); tried.Add($"{e.DisplayName}: OK"); return (r, tried); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { tried.Add($"{e.DisplayName}: 失敗（{ex.GetType().Name}: {ex.Message}）"); }
        }
        return (null, tried);
    }
}
