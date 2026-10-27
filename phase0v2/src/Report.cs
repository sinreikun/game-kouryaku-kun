using System.Text;

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
    public List<string> Warnings { get; set; } = new();

    public Dictionary<string, object?> ToDict() => new()
    {
        ["id"] = Id, ["name"] = Name, ["startedAt"] = StartedAt, ["finishedAt"] = FinishedAt, ["verdict"] = Verdict, ["summary"] = Summary,
        ["data"] = Data, ["warnings"] = Warnings, ["notes"] = Notes, ["images"] = Images,
    };
}

public sealed class Config
{
    public string TargetQuery { get; set; } = "";
    public string OpenKey { get; set; } = TornekoPreset.Menu;
    public string CloseKey { get; set; } = TornekoPreset.Cancel;
    public string PadOpenButton { get; set; } = "";
    public string PadCloseButton { get; set; } = "";
    public int HoldMs { get; set; } = 80;
    public int WaitAfterInputMs { get; set; } = 1200;
    public int RateSeconds { get; set; } = 5;
    public int PixelThreshold { get; set; } = 24;
    public double MinChangedRatio { get; set; } = 0.003;
    /// <summary>無入力の揺れを測る組数（最大値を採用）</summary>
    public int NoiseSamples { get; set; } = 5;
    /// <summary>受理と判定するのに必要な「最大連結変化領域」の画面比（メニュー窓は約5%、NPC1体は1%未満）</summary>
    public double MinComponentRatio { get; set; } = 0.015;
    public int T6Minutes { get; set; } = 5;
    public int T6IntervalSec { get; set; } = 15;
    public StopMode StopMode { get; set; } = StopMode.AllButOwn;

    public Dictionary<string, object?> ToDict() => new()
    {
        ["TargetQuery"] = TargetQuery, ["OpenKey"] = OpenKey, ["OpenKeyResolved"] = Safe(OpenKey), ["CloseKey"] = CloseKey, ["CloseKeyResolved"] = Safe(CloseKey),
        ["PadOpenButton"] = PadOpenButton, ["PadCloseButton"] = PadCloseButton, ["HoldMs"] = HoldMs, ["WaitAfterInputMs"] = WaitAfterInputMs,
        ["RateSeconds"] = RateSeconds, ["PixelThreshold"] = PixelThreshold, ["MinChangedRatio"] = MinChangedRatio, ["NoiseSamples"] = NoiseSamples,
        ["MinComponentRatio"] = MinComponentRatio, ["T6Minutes"] = T6Minutes, ["T6IntervalSec"] = T6IntervalSec, ["StopMode"] = StopMode.ToString(),
    };
    private static string Safe(string k) { try { return KeySpec.Parse(k).ToString(); } catch (Exception e) { return "解釈不可: " + e.Message; } }
}

public sealed class Report
{
    public string Tool { get; set; } = "ゲーム攻略くん Phase0 Probe v0.2.0";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public Dictionary<string, object?> Environment { get; set; } = new();
    public Dictionary<string, object?> Target { get; set; } = new();
    public Config Config { get; set; } = new();
    public List<TestResult> Results { get; set; } = new();
    public string Dir { get; set; } = "";

    public static string VerdictJa(string v) => v switch
    {
        "OK" => "✅ OK", "NG" => "❌ NG", "PARTIAL" => "⚠️ 一部可", "SKIPPED" => "⏭ スキップ", "ABORTED" => "⛔ 中止", _ => "ℹ️ 情報",
    };

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var root = new Dictionary<string, object?>
        {
            ["tool"] = Tool, ["createdAt"] = CreatedAt, ["environment"] = Environment, ["target"] = Target,
            ["config"] = Config.ToDict(), ["results"] = Results.Select(r => (object?)r.ToDict()).ToList(),
        };
        File.WriteAllText(Path.Combine(Dir, "report.json"), Json.Write(root), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Dir, "report.md"), ToMarkdown(), new UTF8Encoding(false));
    }

    private static string Cell(object? v)
    {
        string s = v switch { null => "-", string str => str, double d => d.ToString("0.#####"), bool b => b ? "true" : "false", _ => v.ToString() ?? "" };
        return s.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ゲーム攻略くん Phase 0 検証レポート (v2)").AppendLine();
        sb.AppendLine($"- 作成: {CreatedAt:yyyy-MM-dd HH:mm:ss}（PCのローカル時刻）");
        sb.AppendLine($"- ツール: {Tool}").AppendLine();
        sb.AppendLine("## 結果一覧").AppendLine();
        sb.AppendLine("| ID | テスト | 判定 | 要約 |").AppendLine("|---|---|---|---|");
        foreach (var r in Results) sb.AppendLine($"| {r.Id} | {Cell(r.Name)} | {VerdictJa(r.Verdict)} | {Cell(r.Summary)} |");
        sb.AppendLine().AppendLine("## 環境").AppendLine();
        foreach (var kv in Environment) sb.AppendLine($"- **{kv.Key}**: {Cell(kv.Value)}");
        sb.AppendLine().AppendLine("## 対象ウィンドウ").AppendLine();
        foreach (var kv in Target) sb.AppendLine($"- **{kv.Key}**: {Cell(kv.Value)}");
        sb.AppendLine().AppendLine("## 設定").AppendLine();
        foreach (var kv in Config.ToDict()) sb.AppendLine($"- {kv.Key}: {Cell(kv.Value)}");
        foreach (var r in Results)
        {
            sb.AppendLine().AppendLine($"## {r.Id} {r.Name}").AppendLine();
            sb.AppendLine($"- 判定: **{VerdictJa(r.Verdict)}**");
            sb.AppendLine($"- 要約: {r.Summary}");
            sb.AppendLine($"- 時刻: {r.StartedAt:HH:mm:ss} 〜 {r.FinishedAt:HH:mm:ss}");
            foreach (var w in r.Warnings) sb.AppendLine($"- ⚠️ **警告**: {w}");
            if (r.Data.Count > 0)
            {
                sb.AppendLine().AppendLine("| 項目 | 値 |").AppendLine("|---|---|");
                foreach (var kv in r.Data) sb.AppendLine($"| {Cell(kv.Key)} | {Cell(kv.Value)} |");
            }
            if (r.Notes.Count > 0) { sb.AppendLine(); foreach (var n in r.Notes) sb.AppendLine($"- {n}"); }
            if (r.Images.Count > 0) { sb.AppendLine(); foreach (var i in r.Images) sb.AppendLine($"![{i}]({i.Replace(" ", "%20")})"); }
        }
        return sb.ToString();
    }
}
