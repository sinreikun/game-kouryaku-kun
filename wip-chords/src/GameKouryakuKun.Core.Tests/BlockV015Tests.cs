using GameKouryakuKun.Core.Control;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Viewer;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.15.0: 入力なしの変化と操作の関与の推定・待つ手の悪用防止・1手サマリー・成長の計器・Human Control。合成データだけ（ゲームの知識なし）</summary>
public class BlockV015Tests
{
    private static List<ExploreAction> Acts() => Explorer.FromLogicalKeys(new[] { "button1", "button2", "button3" }).Append(ExploreAction.Wait).ToList();

    [Fact]
    public void Attribution_separates_natural_change_from_action_effect()
    {
        var ex = new Explorer(Acts(), 1);
        var w = ExploreAction.Wait; var b1 = ex.Actions.First(a => a.Name == "button1"); var b2 = ex.Actions.First(a => a.Name == "button2");
        // S0 は入力なしでも 9 割変わる（自然な変化）。button1 も 9 割（自然と区別できない）。button2 は必ず別の画面 S1 へ（操作の効果）
        var rng = new Random(2);
        for (int i = 0; i < 60; i++)
        {
            ex.Observe("S0", w, "S0", rng.NextDouble() < 0.9);
            ex.Observe("S0", b1, "S0", rng.NextDouble() < 0.9);
            ex.Observe("S0", b2, "S1", true);
        }
        var a1 = NaturalChange.Attribute(ex, "S0", "button1"); var a2 = NaturalChange.Attribute(ex, "S0", "button2");
        Assert.True(a1.Known && a2.Known);
        Assert.True(a1.PActionCaused < 0.15, $"button1 {a1.PActionCaused}");
        Assert.True(a2.NextDivergence > 0.9, $"button2 の行き先の違い {a2.NextDivergence}");
        Assert.True(double.IsNaN(NaturalChange.Attribute(ex, "S9", "button1").PActionCaused));   // 待つ手の観測がない画面は未知
    }

    [Fact]
    public void Wait_is_not_favored_on_a_screen_that_changes_by_itself()
    {
        // 入力なしでいつも変わる画面: 待つ手の「変化が多い」は価値にならない（変化・移動の項は入力なしの基準を差し引く）
        var ex = new Explorer(Acts(), 5) { Momentum = 0, ChordProbeProbability = 0 };
        int waits = 0, n = 3000;
        for (int i = 0; i < n; i++)
        {
            var c = ex.Choose("S0");
            if (c.Action.IsWait) waits++;
            ex.Observe("S0", c.Action, "S0", true);   // どの手でも同じように変わる
        }
        Assert.True(waits < n * 0.35, $"待つ手 {waits}/{n}");
        var cand = ex.Choose("S0").Candidates.First(c => c.Action == "wait");
        Assert.True(cand.Rates < 0.01, $"待つ手の変化の項 {cand.Rates}");
    }

    [Fact]
    public void Wait_is_not_avoided_either_when_nothing_is_known()
    {
        var ex = new Explorer(Acts(), 7) { Momentum = 0, ChordProbeProbability = 0 };
        var cands = ex.Choose("S5").Candidates;
        var w = cands.First(c => c.Action == "wait"); var b = cands.First(c => c.Action == "button1");
        Assert.Equal(b.Score, w.Score, 6);   // 固定の価値の上乗せも割引もない
    }

    [Fact]
    public void Momentum_never_repeats_wait()
    {
        var ex = new Explorer(Acts(), 3) { Momentum = 1.0, ChordProbeProbability = 0 };
        ex.Observe("S0", ExploreAction.Wait, "S0", true);
        ex.Observe("S0", ExploreAction.Wait, "S0", true);
        Assert.NotEqual("momentum", ex.Choose("S0").Rule);
    }

    [Fact]
    public void Temporal_book_describes_time_characteristics_without_meaning()
    {
        var t = new TemporalBook();
        // A: 入力なしでは変わらない / B: 一部が変わり続ける / C: 入力なしで別の画面へ / D: 待ち始めは変わるが待ち続けると止まる
        for (int i = 0; i < 10; i++) { t.Break(); t.ObserveStep("A", "wait", false, "A"); }
        for (int i = 0; i < 10; i++) { t.Break(); t.ObserveStep("B", "wait", true, "B"); }
        for (int i = 0; i < 10; i++) { t.Break(); t.ObserveStep("C", "wait", true, "X"); }
        for (int i = 0; i < 6; i++) { t.Break(); t.ObserveStep("D", "wait", true, "D"); t.ObserveStep("D", "wait", false, "D"); t.ObserveStep("D", "wait", false, "D"); }
        Assert.Contains("still", t.Profile("A").Traits);
        Assert.Contains("partial_continuous", t.Profile("B").Traits);
        Assert.Contains("natural_transition", t.Profile("C").Traits);
        Assert.Contains("stops_after_time", t.Profile("D").Traits);
        Assert.Contains("unobserved", t.Profile("Z").Traits);
        // キーを送った手は待ちの連続を切る
        t.ObserveStep("D", "button1", false, "D"); t.ObserveStep("D", "wait", true, "D");
        Assert.Equal(7, t.Profile("D").FirstWaitTries);
    }

    private static DecisionRecord Dec(string node, string chosen, string rule, string? top, double p, string actual, bool revised = false, string src = "agent", int tries = 3)
    {
        var pred = new PredictedOutcome(node, chosen, top == null ? Array.Empty<(string, double)>() : new[] { (top, p) }, 0.5, 1, top == null ? 0 : 5, 0, 0, Array.Empty<string>(), 0.2);
        var chk = new PredictionCheck(pred, actual, actual != node, 0.5, 1, 0, top == actual, 0.1);
        var cand = new CandidateScore(chosen, 1, 1, tries, 0, 0, 0, 0.2, 1, false, false, Array.Empty<string>());
        return new DecisionRecord(1, 1, node, chosen, null, rule, 1, 1, new[] { cand }, pred, chk, revised ? 7 : null) { ActionSource = src };
    }

    [Fact]
    public void Step_summary_uses_symbols_not_only_color_and_shows_source()
    {
        var hit = StepSummary.Of(827, Dec("m51", "button3", "scored", "m18", 0.74, "m18", tries: 0), "m18", true, false);
        Assert.Contains("#827 [AI]", hit.Line1); Assert.Contains("○予測一致 74%", hit.Line1); Assert.Contains("?未試行の確認", hit.Line2); Assert.Contains("確信↑", hit.Line2);
        var miss = StepSummary.Of(828, Dec("m18", "button7", "scored", "m18", 0.6, "m42", revised: true), "m42", true, true);
        Assert.Contains("×予測外れ", miss.Line1); Assert.Contains("★新State", miss.Line1); Assert.Contains("↻", miss.Marks); Assert.Contains("仮説修正", miss.Line2);
        var hum = StepSummary.Of(1843, Dec("m31", "button7", "human", null, 0, "m82", src: "human"), "m82", true, true);
        Assert.Contains("[HUMAN]", hum.Line1); Assert.Contains("人間の操作", hum.Line2); Assert.Contains("予測なし", hum.Line1);
    }

    [Fact]
    public void Viewer_pause_freezes_display_but_not_recording_and_resume_jumps_to_latest()
    {
        var h = new StepHistory();
        for (int i = 1; i <= 20; i++) h.Add(StepSummary.Of(i, Dec("a", "button1", "scored", "a", 0.5, "a"), "a", false, false));
        h.SetPaused(true);
        for (int i = 21; i <= 30; i++) h.Add(StepSummary.Of(i, Dec("a", "button1", "scored", "a", 0.5, "a"), "a", false, false));
        var (items, behind) = h.View(8);
        Assert.Equal(20, items[^1].Step); Assert.Equal(10, behind); Assert.Equal(30, h.Count);
        h.SetPaused(false);
        Assert.Equal(30, h.View(8).Items[^1].Step);
    }

    [Fact]
    public void Growth_series_and_events()
    {
        var g = new GrowthTracker(window: 10);
        for (int i = 1; i <= 40; i++)
        {
            bool hit = i > 20;   // 後半は当たる
            g.Add(StepSummary.Of(i, Dec("a", "button1", "scored", "a", 0.6, hit ? "a" : "b", revised: i == 15), hit ? "a" : "b", true, i == 5), false);
        }
        var acc = g.Series(GrowthMetric.PredictionAccuracy);
        Assert.True(acc[^1].Value > 0.9 && acc[0].Value < 0.1);
        Assert.Contains(g.Events(), e => e.Kind == GrowthEventKind.NewState && e.Step == 5);
        Assert.Contains(g.Events(), e => e.Kind == GrowthEventKind.Revision && e.Symbol == "↻");
    }

    [Fact]
    public void Human_control_accepts_only_neutral_names_and_flags_every_switch()
    {
        var hc = new HumanControl(new[] { "button1", "dpad_up", "wait" });
        Assert.False(hc.Enqueue("button1"));            // 自動のときは受け付けない
        hc.SetEnabled(true); Assert.True(hc.TakeSwitch()); Assert.False(hc.TakeSwitch());
        Assert.True(hc.Enqueue("button1")); Assert.False(hc.Enqueue("決定")); Assert.False(hc.Enqueue("button1+dpad_up"));
        Assert.True(hc.Enqueue("wait")); Assert.True(hc.Enqueue("dpad_up")); Assert.False(hc.Enqueue("button1"));   // 積みすぎ
        Assert.True(hc.TryDequeue(out var a) && a == "button1");
        hc.SetEnabled(false); Assert.True(hc.TakeSwitch());
        Assert.False(hc.TryDequeue(out _));               // 切り替えたら古い手は捨てる
        Assert.Equal(2, hc.Switches);
    }

    [Fact]
    public void Viewer_types_are_display_only_and_human_steps_never_become_sequences_or_rewards()
    {
        var root = TestPaths.SrcRoot();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "GameKouryakuKun.Core", "Exploration"), "*.cs"))
        {
            var t = File.ReadAllText(f);
            foreach (var x in new[] { "StepSummary", "StepHistory", "GrowthTracker", "GrowthMeters", "HumanControl" }) Assert.DoesNotContain(x, t);
        }
        var session = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("isHuman ? Enumerable.Empty<SequenceEvent>() : seqMiner.Observe(st)", session);
        Assert.Contains("Human.TakeSwitch()", session);
        Assert.Contains("explorer.ResetContext()", session);
        foreach (var bad in new[] { "imitat", "Imitat", "demonstration", "reward", "Reward" }) Assert.DoesNotContain(bad, session);
    }

    [Fact]
    public void Schema_v10_records_action_source_and_accepts_human_rule()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.15.0");
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        s.AppendStream(run, null, new StreamStep(1, "m1", "button1", true, "m2", 0.1), 1, Array.Empty<BoundaryReason>(), actionSource: "human");
        s.AppendStream(run, null, new StreamStep(2, "m2", "wait", false, "m2", 0), 1, Array.Empty<BoundaryReason>());
        s.AddDecision(Dec("m1", "button1", "human", null, 0, "m2", src: "human") with { Seq = 1 });
        Assert.Equal("human", s.Db.Scalar("SELECT action_source FROM experience_stream WHERE seq=1"));
        Assert.Equal("agent", s.Db.Scalar("SELECT action_source FROM experience_stream WHERE seq=2"));
        Assert.Equal("human", s.Db.Scalar("SELECT action_source FROM decision_records WHERE seq=1"));
        Assert.Equal(1L, s.Db.Scalar("SELECT steps FROM experience_by_source WHERE action_source='human'"));
        Assert.ThrowsAny<Exception>(() => s.Db.Exec("INSERT INTO experience_stream(seq, state, action, changed, next_state, action_source) VALUES(9,'a','b',0,'a','teacher')"));
    }
}
