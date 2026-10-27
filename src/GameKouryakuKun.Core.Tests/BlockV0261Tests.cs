using GameKouryakuKun.Core.Growth;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.26.1: 知っている状態の数の減りも同じ版の組だけで FAIL にする（v0.26.0 で操作の集合を変えたため、版をまたぐと減り得る）</summary>
public class BlockV0261Tests
{
    private static GrowthSnapshot S(long run, long seq, string why, int states, string ver) =>
        new GrowthSnapshot(run, seq, (int)seq, 3, 0, 1, 2, 1, 0, 0, 0, 0, 5, 10, 1.0, 0.5, 0.5, 1, 0, 0, 0, 0, 0, new Dictionary<int, int>(), why) { AppVersion = ver } with { StatesTotal = states };
    private static GrowthCheck C(IEnumerable<GrowthSnapshot> xs) => GrowthEvaluator.Evaluate(xs.ToList()).Single(c => c.Name.Contains("引き継ぎ"));

    [Fact]
    public void Same_version_state_decrease_still_fails()
    {
        var c = C(new[] { S(1, 1, "start", 200, "0.26.2"), S(1, 2, "end", 221, "0.26.2"), S(2, 3, "start", 210, "0.26.2"), S(2, 4, "end", 215, "0.26.2") });
        Assert.Equal(GrowthLevel.Fail, c.Level);
    }

    [Fact]
    public void Cross_version_state_decrease_is_noted_not_failed()
    {
        var c = C(new[] { S(1, 1, "start", 200, "0.25.4"), S(1, 2, "end", 221, "0.25.4"), S(2, 3, "start", 210, "0.26.0"), S(2, 4, "end", 215, "0.26.0") });
        Assert.Equal(GrowthLevel.Pass, c.Level);
        Assert.Contains("221→210", c.Detail);
    }
}

/// <summary>v0.26.2: 探索の終わりに目的・地域・文字の状態を保存する（実機 #86→#87 で値打ちが引き継がれなかった）</summary>
public class BlockV0262Tests
{
    [Fact]
    public void Session_saves_goal_frontier_and_text_state_at_end()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), "GameKouryakuKun.App/Session.cs"));
        int end = src.IndexOf("Snap(\"end\", Step);", StringComparison.Ordinal);
        Assert.True(end > 0);
        var after = src[end..];
        int cut = after.IndexOf("private ", StringComparison.Ordinal);
        var body = cut > 0 ? after[..cut] : after;
        Assert.Contains("SaveGoalModel(\"self_goals\"", body);
        Assert.Contains("SaveGoalModel(\"frontier\"", body);
        Assert.Contains("SaveGoalModel(\"state_text\"", body);
    }

    [Fact]
    public void Goal_value_round_trips_after_baseline_only_steps()
    {
        var g = new GameKouryakuKun.Core.Goals.SelfGoals();
        for (int i = 0; i < 5; i++) g.StepGain(0.2, goalStep: false);
        var v = g.Value(GameKouryakuKun.Core.Goals.GoalKind.Reach);
        for (int i = 0; i < 50; i++) g.StepGain(0.9, goalStep: false);   // 目的の出来事のない基準だけの更新
        var v2 = g.Value(GameKouryakuKun.Core.Goals.GoalKind.Reach);
        var h = new GameKouryakuKun.Core.Goals.SelfGoals(); h.ImportState(g.ExportState());
        Assert.Equal(v2, h.Value(GameKouryakuKun.Core.Goals.GoalKind.Reach), 6);
        _ = v;
    }
}

/// <summary>v0.26.3: 修正の版（0.26.2）より前の版で始まった組の引き継ぎの問題は INFO。0.26.2 以降は従来どおり FAIL</summary>
public class BlockV0263Tests
{
    private static GrowthSnapshot S(long run, long seq, string why, double vr, string ver) =>
        new(run, seq, (int)seq, 3, 0, 1, 2, 1, 0, 0, 0, 0, 5, 10, 1.0, vr, 0.5, 1, 0, 0, 0, 0, 0, new Dictionary<int, int>(), why) { AppVersion = ver };
    private static List<GrowthCheck> E(params GrowthSnapshot[] xs) => GrowthEvaluator.Evaluate(xs.ToList()).Where(c => c.Name.Contains("引き継ぎ")).ToList();

    [Fact]
    public void Pre_fix_pair_is_info_not_fail()
    {
        var cs = E(S(1, 1, "start", 0.5, "0.26.1"), S(1, 2, "end", 1.48, "0.26.1"), S(2, 3, "start", 3.41, "0.26.1"), S(2, 4, "end", 3.0, "0.26.1"));
        Assert.Equal(GrowthLevel.Pass, cs.Single(c => !c.Name.Contains("修正前")).Level);
        var info = cs.Single(c => c.Name.Contains("修正前"));
        Assert.Equal(GrowthLevel.Info, info.Level);
        Assert.Contains("#2 価値が引き継がれていない", info.Detail);
    }

    [Fact]
    public void Fix_version_or_later_pair_still_fails()
    {
        var cs = E(S(1, 1, "start", 0.5, "0.26.2"), S(1, 2, "end", 1.48, "0.26.2"), S(2, 3, "start", 3.41, "0.26.2"), S(2, 4, "end", 3.0, "0.26.2"));
        Assert.Equal(GrowthLevel.Fail, cs.Single().Level);
        cs = E(S(1, 1, "start", 0.5, "0.26.3"), S(1, 2, "end", 1.48, "0.26.3"), S(2, 3, "start", 3.41, "0.26.3"), S(2, 4, "end", 3.0, "0.26.3"));
        Assert.Equal(GrowthLevel.Fail, cs.Single().Level);
        Assert.True(GrowthEvaluator.BeforeFix("0.26.1")); Assert.False(GrowthEvaluator.BeforeFix("0.26.2")); Assert.False(GrowthEvaluator.BeforeFix(""));
    }
}

public class BlockV027Tests
{
    [Fact]
    public void Baseline_shrinks_to_prior_with_few_samples_and_learns_rate()
    {
        var nb = new Core.Exploration.NaturalBaseline();
        Assert.Null(nb.Rates("s"));
        nb.ObserveActionTurn("s", 1000);
        nb.ObserveNatural("s", 500, true, false, fromWait: false);
        var r1 = nb.Rates("s")!.Value;   // 1 区間: 事前値に縮んで 1 未満
        Assert.InRange(r1.Change, 0.5, 0.99);
        for (int i = 0; i < 200; i++) nb.ObserveNatural("s", 500, i % 10 == 0, false, fromWait: false);
        var r2 = nb.Rates("s")!.Value;   // 500 ms あたり約 0.1。1000 ms の手には外挿しない（観測の範囲の 0.1）
        Assert.InRange(r2.Change, 0.07, 0.14);
        nb.ObserveActionTurn("s", 1); for (int i = 0; i < 2000; i++) nb.ObserveActionTurn("s", 1);   // 短い手には縮めて当てる
        Assert.True(nb.Rates("s")!.Value.Change < r2.Change / 2);
        Assert.True(r2.Leave < 0.05);
    }

    [Fact]
    public void Unchanged_gaps_lower_the_baseline_and_changed_gaps_raise_it()
    {
        var a = new Core.Exploration.NaturalBaseline(); var b = new Core.Exploration.NaturalBaseline();
        for (int i = 0; i < 30; i++) { a.ObserveNatural("s", 400, false, false, false); b.ObserveNatural("s", 400, i % 3 == 0, false, false); a.ObserveActionTurn("s", 800); b.ObserveActionTurn("s", 800); }
        Assert.True(a.Rates("s")!.Value.Change < b.Rates("s")!.Value.Change);
    }

    [Fact]
    public void Wait_uncertainty_declines_with_observations_without_new_constants()
    {
        var nb = new Core.Exploration.NaturalBaseline();
        double u0 = nb.Uncertainty("s"); Assert.Equal(1, u0, 6);
        double prev = u0;
        for (int i = 0; i < 40; i++) { nb.ObserveNatural("s", 300, i % 4 == 0, false, true); double u = nb.Uncertainty("s"); Assert.True(u <= prev + 1e-12); prev = u; }
        Assert.True(prev < 0.2);
    }

    [Fact]
    public void Attribution_uses_baseline_so_natural_change_is_not_credited_to_actions()
    {
        var ex = new Core.Exploration.Explorer(Core.Exploration.Explorer.FromLogicalKeys(new[] { "button1", "button3" }).Append(Core.Exploration.ExploreAction.Wait).ToList(), 1);
        var b1 = ex.Actions.First(x => x.Name == "button1"); var b3 = ex.Actions.First(x => x.Name == "button3");
        for (int i = 0; i < 20; i++) { ex.Observe("s", b1, "s", i % 10 < 8); ex.Observe("s", b3, "s", i % 10 < 8); }
        var w = ex.Actions.First(x => x.IsWait); ex.Observe("s", w, "s", false);   // 待つ手は 1 回だけ・変化なし
        double before = Core.Exploration.NaturalChange.Attribute(ex, "s", "button1").PActionCaused;
        var nb = new Core.Exploration.NaturalBaseline(); ex.Baseline = nb;
        for (int i = 0; i < 60; i++) { nb.ObserveNatural("s", 1000, i % 4 != 0, false, false); nb.ObserveActionTurn("s", 1000); }
        double after = Core.Exploration.NaturalChange.Attribute(ex, "s", "button1").PActionCaused;
        Assert.True(before > 0.6, $"before {before}");
        Assert.True(after < 0.3, $"after {after}");
        Assert.InRange(ex.NaturalRates("s").Change, 0.6, 0.85);
    }

    [Fact]
    public void Baseline_roundtrips_and_observation_is_not_masked()
    {
        var nb = new Core.Exploration.NaturalBaseline();
        nb.ObserveNatural("s", 300, true, true, false); nb.ObserveNatural("s", 900, false, false, true); nb.ObserveActionTurn("s", 700);
        var c = new Core.Exploration.NaturalBaseline(); c.ImportState(nb.ExportState());
        Assert.Equal(nb.Counts("s"), c.Counts("s")); Assert.Equal(nb.Rates("s"), c.Rates("s"));
        Assert.Equal(1, c.Counts("s").Gap); Assert.Equal(1, c.Counts("s").Wait);
    }
}

public class BlockV0271Tests
{
    [Fact]
    public void Attribution_with_baseline_but_no_wait_stats_does_not_throw()
    {
        var ex = new Core.Exploration.Explorer(Core.Exploration.Explorer.FromLogicalKeys(new[] { "button1" }).Append(Core.Exploration.ExploreAction.Wait).ToList(), 1);
        ex.Observe("s", ex.Actions.First(x => x.Name == "button1"), "t", true);
        var nb = new Core.Exploration.NaturalBaseline(); ex.Baseline = nb;
        nb.ObserveNatural("s", 500, false, false, true); nb.ObserveActionTurn("s", 500);
        var a = Core.Exploration.NaturalChange.Attribute(ex, "s", "button1");   // 0.27.0 の実機で NullReference
        Assert.False(double.IsNaN(a.PActionCaused)); Assert.True(double.IsNaN(a.NextDivergence));
    }

    [Fact]
    public void After_action_windows_are_counted_but_not_used_for_the_rate()
    {
        var nb = new Core.Exploration.NaturalBaseline();
        for (int i = 0; i < 50; i++) nb.ObserveAfterAction("s", true);
        Assert.Null(nb.Rates("s")); Assert.Equal(1, nb.Uncertainty("s"), 6);   // 待つ手の価値は残る
        Assert.Equal((50, 50), nb.AfterAction("s"));
        var c = new Core.Exploration.NaturalBaseline(); c.ImportState(nb.ExportState()); Assert.Equal((50, 50), c.AfterAction("s"));
    }

    [Fact]
    public void Version1_baseline_state_is_discarded()
    {
        var c = new Core.Exploration.NaturalBaseline();
        c.ImportState("{\"v\":1,\"n\":{\"s\":[10,9,0,1000,1,100,10,0]}}");
        Assert.Equal(0, c.Observations("s"));
    }

    [Fact]
    public void Gap_net_change_uses_the_same_criterion_as_turn_net_change()
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "GameKouryakuKun.Core", "Turn", "NoInputGate.cs"));
        Assert.Contains("criteria.IsChanged(d)", src);
        var tdet = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "GameKouryakuKun.Core", "Turn", "TurnDetector.cs"));
        Assert.Contains("Criteria.IsChanged(final)", tdet);
        var ses = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("if (prevWasWait) natural.ObserveNatural(", ses);
    }
}

public class BlockV0272Tests
{
    private static Core.Imaging.Frame F(int w, int h, Action<byte[]>? paint = null, long id = 1)
    { var b = new byte[w * h * 4]; paint?.Invoke(b); return new Core.Imaging.Frame(id, w, h, b, 0); }
    private static void Rect(byte[] b, int w, int x0, int y0, int x1, int y1, byte v) { for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * w + x) * 4; b[i] = b[i + 1] = b[i + 2] = v; } }

    [Fact]
    public async Task Reader_reuses_unchanged_frames_and_reads_only_changed_region()
    {
        var rd = new Core.Ocr.TextReader(); int calls = 0; Core.Imaging.Frame? lastImg = null;
        Task<Core.Ocr.OcrResult?> Eng(Core.Imaging.Frame f) { calls++; lastImg = f; return Task.FromResult<Core.Ocr.OcrResult?>(new Core.Ocr.OcrResult("t", new[] { new Core.Ocr.OcrLine("HP 10", 1, new Core.Ocr.OcrBox(2, 2, 20, 8)) }, 5, "")); }
        var f1 = F(320, 240, b => Rect(b, 320, 0, 0, 40, 20, 200));
        var (s1, k1, _, _) = await rd.ReadAsync(f1, null, Eng);
        Assert.Equal("full", k1); Assert.Equal(1, calls);
        var (s2, k2, _, _) = await rd.ReadAsync(F(320, 240, b => Rect(b, 320, 0, 0, 40, 20, 200), 2), s1, Eng);
        Assert.Equal("reuse", k2); Assert.Equal(1, calls); Assert.Single(s2.Lines);
        var f3 = F(320, 240, b => { Rect(b, 320, 0, 0, 40, 20, 200); Rect(b, 320, 200, 200, 240, 220, 90); }, 3);
        var (s3, k3, _, area) = await rd.ReadAsync(f3, s2, Eng);
        Assert.Equal("region", k3); Assert.Equal(2, calls); Assert.True(area < 0.2);
        Assert.True(lastImg!.Width < 320 && lastImg.Height < 240);
        Assert.Equal(2, s3.Lines.Count);   // 変わっていない区画の行は残り、読んだ行は画面の座標に戻る
        Assert.Contains(s3.Lines, l => l.Box.X >= 200 - 20);
        var (_, k4, _, _) = await rd.ReadAsync(F(320, 240, b => Rect(b, 320, 0, 0, 40, 20, 200), 4), s3, Eng);   // 前に見た見た目へ戻る
        Assert.Equal("cache", k4);
    }

    private static Core.Ocr.TextSnapshot S(params (string t, int x, int y)[] l) =>
        new(1, 800, 600, l.Select(z => new Core.Ocr.OcrLine(z.t, 1, new Core.Ocr.OcrBox(z.x, z.y, 40, 10))).ToList(), new ulong[Core.Ocr.TextReader.GridCols * Core.Ocr.TextReader.GridRows]);

    [Fact]
    public void Text_diff_kinds_are_neutral()
    {
        Assert.Equal("none", Core.Ocr.TextDiff.Of(S(("abc", 10, 10)), S(("abc", 10, 10))).Kind);
        Assert.Equal("numbers", Core.Ocr.TextDiff.Of(S(("x 12/30", 10, 10)), S(("x 9/30", 10, 10))).Kind);
        Assert.Equal("strings", Core.Ocr.TextDiff.Of(S(("abc", 10, 10)), S(("abd", 10, 10))).Kind);
        var d = Core.Ocr.TextDiff.Of(S(("abc", 10, 10)), S(("abc", 10, 10), ("new", 500, 400)));
        Assert.Equal("layout", d.Kind); Assert.Equal(1, d.Added);
    }

    [Fact]
    public void Text_change_book_separates_action_caused_from_natural_text_change()
    {
        var b = new Core.Exploration.TextChangeBook();
        var ch = new Core.Ocr.TextDiff("numbers", 0, 0, 0, 1, new[] { 1 }); var no = new Core.Ocr.TextDiff("none", 0, 0, 0, 0, Array.Empty<int>());
        for (int i = 0; i < 20; i++) { b.ObserveAction("s", "a", ch); b.ObserveAction("s", "z", i % 2 == 0 ? ch : no); b.Natural.ObserveActionTurn("s", 500); }
        Assert.True(double.IsNaN(b.ActionCaused("s", "a")));   // 入力なしの文字の観測がない
        for (int i = 0; i < 20; i++) b.Natural.ObserveNatural("s", 500, i % 2 == 0, false, true);
        Assert.True(b.ActionCaused("s", "a") > 0.4); Assert.True(b.ActionCaused("s", "z") < 0.1);
        Assert.Equal("numbers", b.Predict("s", "a")!.Value.Kind);
        Assert.True(b.Uncertainty("s", "a") < b.Uncertainty("s", "q"));
        Assert.True(b.Hits > 0 && b.Hits <= b.Checks);
    }

    private static void Obs(Core.Exploration.Explorer ex, string node, string act, string to, int n) { var a = ex.Actions.First(x => x.Name == act); for (int i = 0; i < n; i++) ex.Observe(node, a, to, true); }

    [Fact]
    public void Foreground_merges_only_with_matching_outcomes_and_same_text_layout()
    {
        var ex = new Core.Exploration.Explorer(Core.Exploration.Explorer.FromLogicalKeys(new[] { "button1", "button2", "button3" }).ToList(), 1);
        foreach (var bg in new[] { "m44", "m46" })
        { Obs(ex, $"{bg}|1,1,4", "button1", $"{bg}|2,2,2", 8); Obs(ex, $"{bg}|1,1,4", "button2", $"{bg}|1,1,4", 8); Obs(ex, $"{bg}|1,1,4", "button3", $"{bg}|3,3,3", 8); }
        // 同じ前景だが行き先の前景が違う背景
        Obs(ex, "m2|1,1,4", "button1", "m2|9,9,9", 8); Obs(ex, "m2|1,1,4", "button2", "m2|8,8,8", 8); Obs(ex, "m2|1,1,4", "button3", "m2|7,7,7", 8);
        var fg = new Core.Exploration.ForegroundStates();
        fg.Review(ex.Nodes, 3);
        Assert.All(fg.Pairs.Values, p => Assert.Equal("text_pending", p.Status));   // 文字が未観測のうちはまとめない
        foreach (var n in new[] { "m44|1,1,4", "m46|1,1,4", "m2|1,1,4" }) fg.ObserveText(n, 42, true);
        fg.Review(ex.Nodes, 3);
        Assert.Equal("merged", fg.Pairs[("m44|1,1,4", "m46|1,1,4")].Status);
        Assert.Equal("separate", fg.Pairs[("m2|1,1,4", "m44|1,1,4")].Status);
        Assert.Equal(new[] { "m46|1,1,4" }, fg.Peers("m44|1,1,4").ToArray());
        Assert.Empty(fg.Peers("m2|1,1,4"));
        fg.ObserveText("m46|1,1,4", 7, true); fg.Review(ex.Nodes, 3);
        Assert.Equal("text_differs", fg.Pairs[("m44|1,1,4", "m46|1,1,4")].Status);
        Assert.True(ex.Nodes.ContainsKey("m46|1,1,4"));   // 元のノードは消さない
    }

    [Fact]
    public void Peer_tries_lower_novelty_but_untried_stays_direct()
    {
        var ex = new Core.Exploration.Explorer(Core.Exploration.Explorer.FromLogicalKeys(new[] { "button1", "button2" }).ToList(), 1) { Momentum = 0 };
        Obs(ex, "s", "button1", "t", 1);
        double n0 = ex.Choose("s").Candidates.First(c => c.Action == "button1").Novelty;
        ex.PeerTries = (n, a) => 9;
        var c = ex.Choose("s").Candidates;
        Assert.True(c.First(x => x.Action == "button1").Novelty < n0);
        Assert.Equal(2.0, c.First(x => x.Action == "button2").Novelty);
    }

    [Fact]
    public void Text_step_stats_states_and_pace()
    {
        var s = new Core.Ocr.TextStepStats();
        Assert.Equal("pending", s.StateAt(1));
        s.Arrived(1, "full", 600, 1); Assert.Equal("fresh", s.StateAt(2)); Assert.Equal("stale", s.StateAt(4));
        s.Arrived(4, "region", 200, 0.1); s.Arrived(5, "reuse", 0, 0);
        Assert.Equal(2, s.Latency().N); Assert.Equal(600, s.Latency().Max);
        s.TurnTiming(true, 500, 200); s.TurnTiming(false, 400, 180);
        Assert.Equal(500, s.MeanBoundary().Busy); Assert.Equal(180, s.MeanGap().Idle);
    }
}

public class BlockV0273Tests
{
    private static Core.Ocr.TextSnapshot S(ulong[] cells, params (string t, int x, int y)[] l) =>
        new(1, 800, 600, l.Select(z => new Core.Ocr.OcrLine(z.t, 1, new Core.Ocr.OcrBox(z.x, z.y, 40, 10))).ToList(), cells);
    private static ulong[] Cells(params int[] changed) { var c = Enumerable.Range(1, 192).Select(i => (ulong)i).ToArray(); foreach (var i in changed) c[i] += 1000; return c; }

    [Fact]
    public void V028_text_change_without_pixel_change_is_still_a_text_change_and_flagged()
    {
        var a = S(Cells(), ("abc", 10, 10)); var b = S(Cells(), ("abc", 10, 10), ("ghost", 500, 400));
        var d = Core.Ocr.TextDiff.Of(a, b);
        Assert.Equal("layout", d.Kind); Assert.Equal(1, d.PixelUnconfirmed);   // v0.28.0: 除かない（数えるだけ）
        int pcx = (500 + 20) * 16 / 800, pcy = (400 + 5) * 12 / 600;
        var d2 = Core.Ocr.TextDiff.Of(a, S(Cells(pcy * 16 + pcx), ("abc", 10, 10), ("ghost", 500, 400)));
        Assert.Equal("layout", d2.Kind); Assert.Equal(0, d2.PixelUnconfirmed);
    }

    [Fact]
    public void V028_small_digit_change_is_detected_even_if_pixel_hash_is_same()
    {
        var d = Core.Ocr.TextDiff.Of(S(Cells(), ("x 12", 10, 10)), S(Cells(), ("x 13", 10, 10)));
        Assert.Equal("numbers", d.Kind);
    }
}

public class BlockV0274Tests
{
    [Fact]
    public void Goal_share_is_not_judged_on_tiny_runs()
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "GameKouryakuKun.Storage", "HealthReport.cs"));
        Assert.Contains("gc == 0 || ag < 50 ? Level.NotTested : share > 0.35 ? Level.Warn : Level.Ok", src);   // しきい値 0.35 は同じ
    }
}
