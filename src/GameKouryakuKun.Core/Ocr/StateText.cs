using System.Text;
using System.Text.Json;

namespace GameKouryakuKun.Core.Ocr;

/// <summary>
/// v0.25.2: OCR の結果を「意味を持たない観測の特徴」にする。言葉の意味・ゲームの意味は解釈しない（辞書・単語の照合なし）。
/// 文字があるか・行の数・文字の数・数字の断片の数・文字の並びのハッシュ・配置のハッシュだけ。
/// </summary>
public sealed record TextFeatures(int Lines, int Chars, int Numbers, ulong TextHash, ulong LayoutHash)
{
    public bool HasText => Chars > 0;

    public const int LayoutCols = 8;   // 根拠: 汎用（見た目の状態の区画と同じ 8×6）
    public const int LayoutRows = 6;   // 根拠: 汎用（同上）

    public static TextFeatures Of(OcrResult r, int width, int height)
    {
        var lines = r.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        // 文字の並び: 正規化して、数字は 1 つの記号にまとめる（数字の値が変わるだけなら同じ並び＝数値は別の量）
        var sb = new StringBuilder(); int nums = 0, chars = 0;
        foreach (var l in lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X))
        {
            foreach (var t in OcrTokens.Split(l.Text)) { if (t.Kind == OcrTokenKind.Number) { nums++; sb.Append('#'); } else sb.Append(t.Text); chars += t.Text.Length; }
            sb.Append('\n');
        }
        var cells = lines.Select(l => (Math.Clamp((l.Box.Y + l.Box.H / 2) * LayoutRows / Math.Max(1, height), 0, LayoutRows - 1) * LayoutCols
                                       + Math.Clamp((l.Box.X + l.Box.W / 2) * LayoutCols / Math.Max(1, width), 0, LayoutCols - 1))).Distinct().OrderBy(x => x);
        return new TextFeatures(lines.Count, chars, nums, Fnv(sb.ToString()), Fnv(string.Join(",", cells)));
    }

    public static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 1099511628211UL; }
        return h;
    }
}

/// <summary>状態ごとの文字の観測の集計（区切りの候補・場面を人が確かめる材料。報酬・選択には使わない）</summary>
public sealed class StateTextBook
{
    public int SamplesPerState { get; init; } = 5;    // 根拠: 汎用（状態ごとに数回読めば、文字が出るか・変わるかが分かる最小の回数）
    public int KeepTexts { get; init; } = 3;          // 根拠: 汎用（人が見る例の数）
    public int MaxTextChars { get; init; } = 120;     // 根拠: 汎用（診断の表示の長さ）

    public sealed class Entry
    {
        public int Samples, WithText;
        public Dictionary<ulong, int> Texts { get; } = new();
        public Dictionary<ulong, int> Layouts { get; } = new();
        public List<string> Examples { get; } = new();
    }
    private readonly Dictionary<string, Entry> _s = new(StringComparer.Ordinal);
    public long Calls { get; private set; }
    public long Failures { get; private set; }
    public double TotalMs { get; private set; }
    public IReadOnlyDictionary<string, Entry> States => _s;

    /// <summary>この状態をもう一度読むか（決まった回数まで。区切りの候補は追加で読む）</summary>
    public bool Wants(string state, bool candidate) => !_s.TryGetValue(state, out var e) || e.Samples < SamplesPerState * (candidate ? 2 : 1);

    public void Failed() { Calls++; Failures++; }

    public void Add(string state, OcrResult r, int width, int height)
    {
        Calls++; TotalMs += r.ElapsedMs;
        var f = TextFeatures.Of(r, width, height);
        var e = _s.TryGetValue(state, out var x) ? x : _s[state] = new Entry();
        e.Samples++;
        if (!f.HasText) return;
        e.WithText++;
        e.Texts[f.TextHash] = e.Texts.GetValueOrDefault(f.TextHash) + 1;
        e.Layouts[f.LayoutHash] = e.Layouts.GetValueOrDefault(f.LayoutHash) + 1;
        var t = string.Join(" / ", r.Lines.Select(l => l.Text.Trim()).Where(s => s.Length > 0));
        if (t.Length > MaxTextChars) t = t[..MaxTextChars] + "…";
        if (e.Examples.Count < KeepTexts && !e.Examples.Contains(t)) e.Examples.Add(t);
    }

    /// <summary>人が読む要約（文字の意味は解釈しない）</summary>
    public string Describe(string state)
    {
        if (!_s.TryGetValue(state, out var e) || e.Samples == 0) return "文字の観測なし";
        return $"文字あり {e.WithText}/{e.Samples}・文字の並び {e.Texts.Count} 種類・配置 {e.Layouts.Count} 種類" +
               (e.Examples.Count > 0 ? "・例「" + string.Join("」「", e.Examples) + "」" : "");
    }

    public string Summary() => $"読んだ回数 {Calls}（失敗 {Failures}・平均 {(Calls - Failures > 0 ? TotalMs / (Calls - Failures) : 0):0} ms）・状態 {_s.Count}（文字あり {_s.Values.Count(e => e.WithText > 0)}）";

    public string ExportState() => JsonSerializer.Serialize(new
    {
        v = 1, calls = Calls, failures = Failures, ms = TotalMs,
        s = _s.ToDictionary(kv => kv.Key, kv => new { n = kv.Value.Samples, t = kv.Value.WithText,
            h = kv.Value.Texts.ToDictionary(x => x.Key.ToString(), x => x.Value), l = kv.Value.Layouts.ToDictionary(x => x.Key.ToString(), x => x.Value), ex = kv.Value.Examples })
    });

    public void ImportState(string json)
    {
        using var d = JsonDocument.Parse(json); var r = d.RootElement;
        if (!r.TryGetProperty("v", out var v) || v.GetInt32() != 1) return;
        Calls = r.GetProperty("calls").GetInt64(); Failures = r.GetProperty("failures").GetInt64(); TotalMs = r.GetProperty("ms").GetDouble();
        foreach (var p in r.GetProperty("s").EnumerateObject())
        {
            var e = new Entry { Samples = p.Value.GetProperty("n").GetInt32(), WithText = p.Value.GetProperty("t").GetInt32() };
            foreach (var h in p.Value.GetProperty("h").EnumerateObject()) e.Texts[ulong.Parse(h.Name)] = h.Value.GetInt32();
            foreach (var h in p.Value.GetProperty("l").EnumerateObject()) e.Layouts[ulong.Parse(h.Name)] = h.Value.GetInt32();
            foreach (var x in p.Value.GetProperty("ex").EnumerateArray()) e.Examples.Add(x.GetString() ?? "");
            _s[p.Name] = e;
        }
    }
}
