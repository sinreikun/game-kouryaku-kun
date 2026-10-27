using System.Text;

namespace GameKouryakuKun.Core.Ocr;

public enum OcrTokenKind { Text, Number }

/// <summary>OCR の1行を分けた断片。Number は 0-9 の連続（全角は半角にそろえる）。意味（何の数値か）は付けない</summary>
public sealed record OcrToken(OcrTokenKind Kind, string Text, long? Value);

/// <summary>
/// OCR 文字列を「数字の並び」と「それ以外の文字列」に分ける（設計方針: OCR は文字列と数値トークンの抽出まで。
/// 文字列が何を意味するかは AI が経験から学ぶので、ここでは決め打ちしない）。
/// 例: "AB15/15" → [Text "AB", Number 15, Text "/", Number 15]、"あいう94%" → [Text "あいう", Number 94, Text "%"]
/// </summary>
public static class OcrTokens
{
    public static IReadOnlyList<OcrToken> Split(string text)
    {
        var s = (text ?? "").Normalize(NormalizationForm.FormKC);
        var list = new List<OcrToken>();
        var sb = new StringBuilder();
        bool? digits = null;
        void Flush()
        {
            if (sb.Length == 0) return;
            var t = sb.ToString(); sb.Clear();
            if (digits == true) list.Add(new OcrToken(OcrTokenKind.Number, t, t.Length <= 18 ? long.Parse(t) : null));
            else { t = t.Trim(); if (t.Length > 0) list.Add(new OcrToken(OcrTokenKind.Text, t, null)); }
        }
        foreach (var ch in s)
        {
            bool d = ch is >= '0' and <= '9';
            if (digits != null && d != digits) Flush();
            digits = d; sb.Append(ch);
        }
        Flush();
        return list;
    }

    /// <summary>行の中の数値だけ（出現順）</summary>
    public static IReadOnlyList<long> Numbers(string text) => Split(text).Where(t => t.Value != null).Select(t => t.Value!.Value).ToList();

    /// <summary>表示用: "AB|15|/|15"</summary>
    public static string Describe(string text) => string.Join("|", Split(text).Select(t => t.Kind == OcrTokenKind.Number ? $"#{t.Text}" : t.Text));
}
