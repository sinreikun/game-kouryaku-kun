using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameKouryakuKun.Phase0;

public sealed class TestResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    /// <summary>OK / NG / PARTIAL / SKIPPED / ABORTED / INFO</summary>
    public string Verdict { get; set; } = "INFO";
    public string Summary { get; set; } = "";
    public Dictionary<string, object?> Data { get; set; } = new();
    public List<string> Images { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class Report
{
    public string Tool { get; set; } = "ゲーム攻略くん Phase0 Probe v0.1.0";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public Dictionary<string, object?> Environment { get; set; } = new();
    public Dictionary<string, object?> Target { get; set; } = new();
    public Config Config { get; set; } = new();
    public List<TestResult> Results { get; set; } = new();

    [JsonIgnore] public string Dir { get; set; } = "";

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string VerdictJa(string v) => v switch
    {
        "OK" => "✅ OK", "NG" => "❌ NG", "PARTIAL" => "⚠️ 一部可", "SKIPPED" => "⏭ スキップ", "ABORTED" => "⛔ 中止", _ => "ℹ️ 情報",
    };

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "report.json"), JsonSerializer.Serialize(this, Opt), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Dir, "report.md"), ToMarkdown(), new UTF8Encoding(false));
    }

    private static string Cell(object? v)
    {
        string s = v switch
        {
            null => "-",
            string str => str,
            double d => d.ToString("0.#####"),
            _ => JsonSerializer.Serialize(v, Opt),
        };
        return s.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ゲーム攻略くん Phase 0 検証レポート");
        sb.AppendLine();
        sb.AppendLine($"- 作成: {CreatedAt:yyyy-MM-dd HH:mm:ss}（PCのローカル時刻）");
        sb.AppendLine($"- ツール: {Tool}");
        sb.AppendLine();
        sb.AppendLine("## 結果一覧");
        sb.AppendLine();
        sb.AppendLine("| ID | テスト | 判定 | 要約 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in Results) sb.AppendLine($"| {r.Id} | {Cell(r.Name)} | {VerdictJa(r.Verdict)} | {Cell(r.Summary)} |");
        sb.AppendLine();
        sb.AppendLine("## 環境");
        sb.AppendLine();
        foreach (var kv in Environment) sb.AppendLine($"- **{kv.Key}**: {Cell(kv.Value)}");
        sb.AppendLine();
        sb.AppendLine("## 対象ウィンドウ");
        sb.AppendLine();
        foreach (var kv in Target) sb.AppendLine($"- **{kv.Key}**: {Cell(kv.Value)}");
        sb.AppendLine();
        sb.AppendLine("## 設定");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(Config, Opt));
        sb.AppendLine("```");
        foreach (var r in Results)
        {
            sb.AppendLine();
            sb.AppendLine($"## {r.Id} {r.Name}");
            sb.AppendLine();
            sb.AppendLine($"- 判定: **{VerdictJa(r.Verdict)}**");
            sb.AppendLine($"- 要約: {r.Summary}");
            sb.AppendLine($"- 時刻: {r.StartedAt:HH:mm:ss} 〜 {r.FinishedAt:HH:mm:ss}");
            if (r.Data.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| 項目 | 値 |");
                sb.AppendLine("|---|---|");
                foreach (var kv in r.Data) sb.AppendLine($"| {Cell(kv.Key)} | {Cell(kv.Value)} |");
            }
            if (r.Notes.Count > 0)
            {
                sb.AppendLine();
                foreach (var n in r.Notes) sb.AppendLine($"- {n}");
            }
            if (r.Images.Count > 0)
            {
                sb.AppendLine();
                foreach (var i in r.Images) sb.AppendLine($"![{i}]({i.Replace(" ", "%20")})");
            }
        }
        return sb.ToString();
    }
}

public sealed class Config
{
    public string TargetQuery { get; set; } = "";
    public string OpenKey { get; set; } = "";
    public string CloseKey { get; set; } = "";
    public string PadOpenButton { get; set; } = "";
    public string PadCloseButton { get; set; } = "";
    public int HoldMs { get; set; } = 80;
    public int WaitAfterInputMs { get; set; } = 900;
    public int RateSeconds { get; set; } = 5;
    public int PixelThreshold { get; set; } = 24;
    public double MinChangedRatio { get; set; } = 0.003;
    public bool PostMessageAlsoWmChar { get; set; } = false;
}
