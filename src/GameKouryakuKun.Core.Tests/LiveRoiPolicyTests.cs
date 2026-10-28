using GameKouryakuKun.Core.Importance;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Ocr;
using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class LiveRoiPolicyTests : IDisposable
{
    public LiveRoiPolicyTests()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.ImportanceEnvName, null);
        Environment.SetEnvironmentVariable(LiveRoiPolicy.LiveRoiEnvName, null);
        RuntimeFlags.AttachImportance = false;
        RuntimeFlags.ApplyImportanceEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.ImportanceEnvName, null);
        Environment.SetEnvironmentVariable(LiveRoiPolicy.LiveRoiEnvName, null);
        RuntimeFlags.AttachImportance = false;
        RuntimeFlags.ApplyImportanceEffective();
    }

    [Fact]
    public void Live_follows_importance_unless_live_roi_zero()
    {
        Assert.False(LiveRoiPolicy.IsLiveEnabled(false));
        Assert.True(LiveRoiPolicy.IsLiveEnabled(true));
        Environment.SetEnvironmentVariable(LiveRoiPolicy.LiveRoiEnvName, "0");
        Assert.False(LiveRoiPolicy.IsLiveEnabled(true));
        Environment.SetEnvironmentVariable(LiveRoiPolicy.LiveRoiEnvName, "1");
        Assert.True(LiveRoiPolicy.IsLiveEnabled(true));
    }

    [Fact]
    public void Defer_respects_floor_and_high_weight()
    {
        Assert.False(LiveRoiPolicy.ShouldDeferEngineRead(0.9, 0, out var r1));
        Assert.Equal("high_ocr_weight", r1);
        Assert.True(LiveRoiPolicy.ShouldDeferEngineRead(AttentionWeights.RareRecheckFloor, 0, out var r2));
        Assert.Equal("defer_low_ocr_weight", r2);
        Assert.False(LiveRoiPolicy.ShouldDeferEngineRead(AttentionWeights.RareRecheckFloor, LiveRoiPolicy.MaxDeferStepsAtFloor, out var r3));
        Assert.Equal("rare_recheck_floor", r3);
    }

    [Fact]
    public void TextReader_deferred_inherits_lines_not_not_observed()
    {
        var bgra = new byte[8 * 8 * 4];
        var f0 = new Frame(1, 8, 8, bgra, 0, "t");
        var f1 = new Frame(2, 8, 8, bgra.ToArray(), 1, "t");
        // change one pixel so Plan would want full/region
        f1.Bgra[0] = 255;
        var prev = new TextSnapshot(1, 8, 8, new[] { new OcrLine("x", 0.9f, new OcrBox(0, 0, 2, 2)) }, GameKouryakuKun.Core.Ocr.TextReader.Cells(f0));
        var reader = new GameKouryakuKun.Core.Ocr.TextReader();
        int engineCalls = 0;
        var task = reader.ReadAsync(f1, prev, _ => { engineCalls++; return Task.FromResult<OcrResult?>(new OcrResult("e", Array.Empty<OcrLine>(), 1, "full")); }, forceDeferEngine: true);
        var (snap, kind, _, _) = task.GetAwaiter().GetResult();
        Assert.Equal("deferred", kind);
        Assert.Equal(0, engineCalls);
        Assert.Equal(1, reader.DeferredReads);
        Assert.Single(snap.Lines);
        Assert.Equal("x", snap.Lines[0].Text);
        // Bundle/TextLayer path: deferred → Inherited, not NotObserved
        var text = new ObservationBuilder().Text(prev, snap, kind, null, null, 0);
        Assert.Contains(LayerStatus.Inherited, text.Status);
        Assert.DoesNotContain(LayerStatus.NotObserved, text.Status);
    }

    [Fact]
    public void Health_distinguishes_live_roi()
    {
        using var d = new TempDir();
        var logs = d["logs"];
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "app-1.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"feature_importance","feature_id":"pe:representation_coarse","score":0.75,"method":"pe_tag_proxy","status":"scored"}""",
            """{"type":"attention_weights","summary":"importance n=1","policy":"live_ocr_defer","live":true,"ocr":0.3}""",
            """{"type":"live_roi","mode":"deferred","ocr_weight":0.3,"reason":"defer_low_ocr_weight","policy":"live_ocr_defer"}""",
            """{"type":"live_roi","mode":"forced_read","ocr_weight":0.3,"reason":"rare_recheck_floor","kind":"region"}""",
        });
        using var mem = ExperienceStore.Open(d["mem"], "p", "1", "0.34.1");
        using var app = AppStore.Open(d["app"], "0.34.1");
        var r = HealthReport.Build("0.34.1", mem.Db, app.Db, null, new[] { path }, 2, null);
        var live = r.Checks.Single(c => c.Area == "重要度" && c.Name.Contains("ライブ ROI"));
        Assert.Equal(HealthReport.Level.Ok, live.Level);
        Assert.Contains("deferred", live.Detail);
        Assert.Contains("forced_read", live.Detail);
        Assert.DoesNotContain("キャラ", r.Text);
    }

    [Fact]
    public void Contract_and_audit_v0341_mark_s4_closed()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s4-importance-attention-contract.md"));
        Assert.Contains("ライブ ROI", text);
        Assert.Contains("deferred", text);
        Assert.Contains("GKK_LIVE_ROI", text);
        Assert.Contains("S4 クローズ", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.34.1.md")));
    }
}
