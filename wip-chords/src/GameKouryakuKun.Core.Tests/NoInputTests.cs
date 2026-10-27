using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Turn;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.15.0: 入力なしの変化（選び直し・待つ手・画面ごとの時間的な変化の性質・時間切れの弱い証拠）。ゲームの知識は使わない合成データ</summary>
public class NoInputTests
{
    private static readonly Canvas Base = new(320, 200);
    // 合成の「画面」: 大きな箱の色で別の画面になる。分類は箱の色だけを見る
    private static Frame Screen(int id, double t) => Base.Clone().Rect(20, 20, 280, 160, (byte)(40 + id * 60), 80, 120).ToFrame(t);
    private static string ClassifyFrame(Frame f) => $"S{(f.Bgra[(100 * 320 + 160) * 4 + 0] - 40) / 60}";
    private static NoInputCheck<string> Gate(Frame? prev, Frame now, string cur, int sofar) =>
        NoInputGate.Check(prev, now, ChangeCriteria.Default, null, null, cur, ClassifyFrame, s => s, sofar, 2);

    [Fact]
    public void Unchanged_gap_is_not_a_no_input_change()
    {
        var g = Gate(Screen(0, 0), Screen(0, 150), "S0", 0);
        Assert.False(g.Changed); Assert.False(g.Rechoose);
        Assert.False(Gate(null, Screen(0, 0), "S0", 0).Changed);   // 最初の手
    }

    [Fact]
    public void Autonomous_transition_before_input_triggers_reselection_on_the_new_screen()
    {
        var g = Gate(Screen(0, 0), Screen(1, 150), "S0", 0);
        Assert.True(g.Changed); Assert.True(g.Transition); Assert.True(g.Rechoose);
        Assert.Equal("S1", g.Node);
    }

    [Fact]
    public void Change_within_the_same_screen_is_recorded_but_not_reselected()
    {
        // 同じ画面のまま一部が変わった（部分的な変化）: 観測として残すが、選び直さない
        var a = Screen(0, 0); var b = Base.Clone().Rect(20, 20, 280, 160, 40, 80, 120).Rect(30, 30, 80, 60, 250, 250, 250).ToFrame(150);
        var g = Gate(a, b, "S0", 0);
        Assert.True(g.Changed); Assert.False(g.Transition); Assert.False(g.Rechoose);
    }

    [Fact]
    public void Reselection_is_bounded_when_the_screen_keeps_changing_by_itself()
    {
        // 画面が入力なしで変わり続ける: 選び直しは 2 回まで。その後は今の画面のまま手を送る（探索を止めない）
        string cur = "S0"; int rechoose = 0, sent = 0, rechosen = 0; Frame prev = Screen(0, 0);
        var attributed = new List<string>();
        for (int i = 1; i <= 12; i++)
        {
            var baseline = Screen(i % 3, i * 150.0);
            var g = Gate(prev, baseline, cur, rechoose);
            if (g.Rechoose) { rechoose++; rechosen++; cur = g.Node!; prev = baseline; continue; }
            rechoose = 0;
            attributed.Add(cur); sent++;   // 手はこの画面のものとして記録される
            prev = baseline; cur = ClassifyFrame(baseline);
        }
        Assert.True(sent >= 4, $"手を送れた回数 {sent}");
        Assert.True(rechosen > 0);
        // 選び直した手は、選び直した後の画面のものとして記録される（前の画面の結果にならない）
        Assert.All(attributed, n => Assert.StartsWith("S", n));
    }

    [Fact]
    public void Wait_is_a_neutral_action_that_does_not_count_toward_input_reachability_or_chords()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2" }).Append(ExploreAction.Wait).ToList();
        var ex = new Explorer(acts, 3) { ChordProbeProbability = 1, Momentum = 0 };
        for (int i = 0; i < 100; i++) ex.Observe("S0", ExploreAction.Wait, "S0", false);
        Assert.Equal(0, ex.NoChangeStreak);   // 待つ手の「変化なし」は入力が届かない証拠ではない
        for (int i = 0; i < 200; i++)
        {
            var c = ex.Choose("S1");
            if (c.Rule == "chord_probe") Assert.DoesNotContain("wait", c.Action.Name);
        }
        // 新しい画面では待つ手も候補になる（未試行の新しさで選ばれうる）
        var fresh = new Explorer(acts, 4) { ChordProbeProbability = 0, Momentum = 0 };
        var cands = fresh.Choose("S9").Candidates;
        Assert.Contains(cands, x => x.Action == "wait");
        Assert.Throws<ArgumentException>(() => new Explorer(new List<ExploreAction> { ExploreAction.Wait }, 1));
    }

    [Fact]
    public void No_input_relation_is_learned_in_the_model_like_any_other_relation()
    {
        var m = new TransitionModel(); long q = 0;
        for (int i = 0; i < 6; i++) m.Observe(q++, "S0", ExploreAction.Wait.Name, "S1", true);
        var p = m.Predict("S0", "wait");
        Assert.Equal("S1", p.TopNext); Assert.True(p.PChange > 0.5);
        Assert.Contains(m.Summaries(), r => r.Action == "wait" && r.Node == "S0");
    }

    [Fact]
    public void Motion_characteristics_round_trip_through_the_learning_db_and_show_as_hypotheses()
    {
        var grid = new BlockGrid(320, 200, 8);
        var bits = new bool[grid.Count]; bits[5] = bits[6] = true;
        var a = new MotionMaskBook();
        a.Observe("m1", grid, bits); a.Observe("m1", grid, bits); a.ObserveActivity("m1", 0.5); a.ObserveActivity("m1", 0.3);
        using var d = new TempDir();
        using (var s = ExperienceStore.Open(d["p"], "prof", "1", "0.15.0"))
        {
            s.SaveScreenMotion(a.Export());
            s.AddNoInputEvent(null, 10, "S0", "S1", true, 0.4, 150, true);
        }
        using var s2 = ExperienceStore.Open(d["p"], "prof", "1", "0.15.0");
        var b = new MotionMaskBook();
        Assert.Equal(1, b.Import(s2.LoadScreenMotion()));
        Assert.Equal(a.MaskFor("m1", null)!.Bits, b.MaskFor("m1", null)!.Bits);   // 起動し直しても学び直さない
        var sum = b.Summaries().Single();
        Assert.True(sum.Learned); Assert.Equal(2, sum.ActivityTurns); Assert.Equal(0.4, sum.MeanActivity, 6);
        var ev = s2.LoadNoInputEvents().Single();
        Assert.True(ev.Transition); Assert.Equal("S1", ev.To);
        var hs = HypothesisBoard.Build(Array.Empty<RelationSummary>(), Array.Empty<(int, string, string, bool)>(), Array.Empty<ActionSequenceCandidate>(), 0, 100, b.Summaries());
        var h = hs.Single();
        Assert.Equal(HypothesisKind.Motion, h.Kind); Assert.Equal(HypothesisStatus.Settled, h.Status);
        Assert.Contains("入力なしで動き続ける所", HypothesisBoard.Text(hs));
    }

    [Fact]
    public void Session_treats_timeouts_as_weak_evidence_on_screens_that_change_by_themselves_and_never_sends_keys_for_wait()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), "GameKouryakuKun.App/Session.cs"));
        Assert.Contains("wp.PChange > AutonomousChangeRate", src);
        Assert.Contains("waiting ? new InputReceipt(", src);              // 待つ手ではキーを送らない
        Assert.Contains("NoInputGate.Check(", src);
        Assert.Contains("!waiting && b.Kind == TurnKind.Normal", src);    // 待つ手は応答時間の自己調整に入れない
        Assert.Contains("AddNoInputEvent(", src);
        Assert.Contains("SaveScreenMotion(", src);
    }
}
