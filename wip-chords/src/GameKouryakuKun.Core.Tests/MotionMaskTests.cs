using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Turn;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.13.1: 起動時の較正の後に現れた常時アニメ（別の画面で動き続ける所）を、時間切れの手から画面ごとに学ぶ</summary>
public class MotionMaskTests
{
    private static readonly Canvas Base = new(320, 200);
    // 右上で毎フレーム色が変わる 64x48 の領域（較正のときには無かった）
    private static Canvas Anim(Canvas c, int i) => c.Rect(240, 8, 64, 48, (byte)(i % 2 == 0 ? 40 : 220), (byte)(i * 70 % 256), 90);
    private static TurnDetector Make() => new(new TurnDetectorOptions { NoChangeTimeoutMs = 600, StableMs = 200, MaxMs = 3000, NoFrameStillMs = 200 });

    /// <summary>入力で下の大きな箱が出て、その後は右上だけが動き続ける1手</summary>
    private static TurnBoundary Turn(TurnDetector d, bool respond = true)
    {
        int i = 0;
        d.Begin(Anim(Base.Clone(), i++).ToFrame(0), 0, "button1");
        TurnBoundary? b = null;
        for (double t = 30; b == null && t < 6000; t += 30, i++)
        {
            var c = Base.Clone();
            if (respond && t >= 90) c.Rect(20, 130, 280, 60, 250, 250, 250);
            b = d.OnFrame(Anim(c, i).ToFrame(t));
        }
        return b!;
    }

    [Fact]
    public void Animation_after_calibration_times_out_until_learned_then_gives_normal_boundaries()
    {
        var d = Make(); var book = new MotionMaskBook();
        var b1 = Turn(d);
        Assert.Equal(TurnKind.Timeout, b1.Kind);
        Assert.NotNull(b1.LateMotion);
        var u1 = book.Observe("m1", b1.LateGrid, b1.LateMotion!);
        Assert.False(u1.Changed);                              // 1回だけでは隠さない
        Assert.Null(book.MaskFor("m1", null));
        var b2 = Turn(d);
        var u2 = book.Observe("m1", b2.LateGrid, b2.LateMotion!);
        Assert.True(u2.Changed); Assert.False(u2.TooWide);
        Assert.InRange(u2.Coverage, 0.01, book.MaxMaskRatio);
        var mask = book.MaskFor("m1", null)!;
        // 応答のあった下の箱（入力直後に変わって止まった所）はマスクに入らない
        var grid = mask.Grid;
        Assert.False(mask[(20 + 140) / grid.Block, (130 + 30) / grid.Block]);
        Assert.True(mask[(240 + 32) / grid.Block, (8 + 24) / grid.Block]);
        d.Mask = mask;
        var b3 = Turn(d);
        Assert.Equal(TurnKind.Normal, b3.Kind);
        Assert.True(b3.NetChange);
        Assert.True(b3.BoundaryMs < 1500);
        // 応答のない手は「変化なし」と判定できる（時間切れではない）
        var b4 = Turn(d, respond: false);
        Assert.Equal(TurnKind.NoChange, b4.Kind);
        // 別の画面には使わない
        Assert.Null(book.MaskFor("m2", null));
    }

    [Fact]
    public void Too_wide_motion_is_not_masked_and_base_mask_is_kept()
    {
        var book = new MotionMaskBook();
        var grid = new BlockGrid(320, 200, 8);
        var all = Enumerable.Repeat(true, grid.Count).ToArray();
        book.Observe("m1", grid, all);
        var u = book.Observe("m1", grid, all);
        Assert.True(u.TooWide); Assert.True(u.Changed);
        var baseMask = new BlockMask(grid); baseMask[0, 0] = true;
        Assert.Same(baseMask, book.MaskFor("m1", baseMask));
        Assert.Equal(0, book.Learned);
    }

    [Fact]
    public void Learned_mask_is_united_with_calibration_mask()
    {
        var book = new MotionMaskBook();
        var grid = new BlockGrid(320, 200, 8);
        var bits = new bool[grid.Count]; bits[grid.Count - 1] = true;
        book.Observe("m1", grid, bits); book.Observe("m1", grid, bits);
        var baseMask = new BlockMask(grid); baseMask[0, 0] = true;
        var m = book.MaskFor("m1", baseMask)!;
        Assert.True(m[0, 0]); Assert.True(m.Bits[grid.Count - 1]);
        Assert.False(baseMask.Bits[grid.Count - 1]);          // 較正のマスクは書き換えない
    }

    [Fact]
    public void Health_warns_when_late_turns_are_mostly_timeouts()
    {
        using var d = new TempDir();
        using var mem = ExperienceStore.Open(d["p"], "prof", "1", "0.13.1");
        using var app = AppStore.Open(d["a"], "0.13.1");
        Directory.CreateDirectory(d["logs"]);
        string Log(string name, Func<int, string> kind)
        {
            var t0 = new DateTimeOffset(2026, 10, 18, 14, 0, 0, TimeSpan.FromHours(9));
            var lines = Enumerable.Range(0, 40).Select(i => $"{{\"t\":\"{t0.AddMilliseconds(i * 700):yyyy-MM-ddTHH:mm:ss.fffzzz}\",\"type\":\"turn\",\"kind\":\"{kind(i)}\",\"boundary_ms\":500,\"node\":\"m2|x\"}}").ToList();
            var path = Path.Combine(d["logs"], name); File.WriteAllLines(path, lines); return path;
        }
        HealthReport.Check Item(string log) => HealthReport.Build("0.13.1", mem.Db, app.Db, null, new[] { log }, 2, null).Checks.Single(c => c.Name.StartsWith("時間切れの手の割合"));
        // 前半は時間切れ、学習後の後半は落ち着いた → 問題なし
        var fixedLog = Item(Log("app-1.jsonl", i => i < 20 ? "Timeout" : "Normal"));
        Assert.Equal(HealthReport.Level.Ok, fixedLog.Level);
        Assert.Contains("m2 20", fixedLog.Detail);
        // 後半も時間切れが多い → 注意
        Assert.Equal(HealthReport.Level.Warn, Item(Log("app-2.jsonl", i => i < 20 ? "Normal" : "Timeout")).Level);
    }

    [Fact]
    public void Health_reports_no_input_changes_and_activity_inside_excluded_regions()
    {
        using var d = new TempDir();
        using var mem = ExperienceStore.Open(d["p"], "prof", "1", "0.14.1");
        using var app = AppStore.Open(d["a"], "0.14.1");
        Directory.CreateDirectory(d["logs"]);
        var log = Path.Combine(d["logs"], "app-1.jsonl");
        File.WriteAllLines(log, new[] { "{\"t\":\"2026-10-18T15:00:00.000+09:00\",\"type\":\"session_end\",\"reason\":\"time_completed\",\"steps\":40,\"diagnostic\":false," +
            "\"no_input_changes\":7,\"no_input_transitions\":3,\"rechosen\":2,\"masked_turns\":30,\"masked_activity_mean\":0.41,\"masked_only_turns\":4}" });
        var r = HealthReport.Build("0.14.1", mem.Db, app.Db, null, new[] { log }, 2, null);
        var ni = r.Checks.Single(c => c.Name.StartsWith("入力なしの変化"));
        Assert.Contains("7 回", ni.Detail); Assert.Contains("別の画面になっていた 3", ni.Detail); Assert.Contains("選び直した 2", ni.Detail);
        var mk = r.Checks.Single(c => c.Name.StartsWith("判定から外した所の中の変化"));
        Assert.Contains("0.41", mk.Detail); Assert.Contains("外した所だけが変わった手 4", mk.Detail);
    }

    [Fact]
    public void Session_keeps_no_input_changes_out_of_the_next_action_result()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), "GameKouryakuKun.App/Session.cs"));
        // 入力なしの遷移を見つけたら、手を送る前に選び直す（上限つき）。入力なしの変化は観測として記録する
        int noInput = src.IndexOf("no_input_change", StringComparison.Ordinal), send = src.IndexOf("active.Send(action)", StringComparison.Ordinal);
        Assert.True(noInput > 0 && noInput < send, "入力なしの変化の確認が入力の前にない");
        Assert.Contains("rechoose, MaxRechoose", src);
        Assert.Contains("masked_activity", src);
    }
}
