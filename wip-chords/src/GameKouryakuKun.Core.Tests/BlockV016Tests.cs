using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Goals;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.16.0: 自己生成の目的（安全な部分）と周期的な変化の検出。合成データだけ（ゲームの知識なし）</summary>
public class BlockV016Tests
{
    private static List<ExploreAction> Acts() => Explorer.FromLogicalKeys(new[] { "button1", "button2", "button3" }).Append(ExploreAction.Wait).ToList();

    private static void Obs(Explorer ex, TransitionModel m, ref long seq, string from, string a, string to)
    {
        var act = ex.Actions.First(x => x.Name == a);
        ex.Observe(from, act, to, from != to); m.Observe(seq++, from, a, to, from != to);
    }

    /// <summary>A --button1--> B --button2--> C（C は未試行の操作が残る）。A では全部試してある</summary>
    private static (Explorer, TransitionModel) World()
    {
        var ex = new Explorer(Acts(), 1); var m = new TransitionModel(); long seq = 1;
        for (int i = 0; i < 6; i++)
        {
            Obs(ex, m, ref seq, "A", "button1", "B"); Obs(ex, m, ref seq, "B", "button2", "C"); Obs(ex, m, ref seq, "C", "button3", "A");
            Obs(ex, m, ref seq, "A", "button2", "A"); Obs(ex, m, ref seq, "A", "button3", "A"); Obs(ex, m, ref seq, "A", "wait", "A");
            Obs(ex, m, ref seq, "B", "button1", "B"); Obs(ex, m, ref seq, "B", "button3", "B"); Obs(ex, m, ref seq, "B", "wait", "B");
        }
        return (ex, m);
    }

    [Fact]
    public void Plan_follows_known_relations_and_never_uses_wait()
    {
        var (ex, m) = World();
        var g = new SelfGoals();
        var p = g.PlanPath(ex, m, "A", "C");
        Assert.NotNull(p);
        Assert.Equal(new[] { "button1", "button2" }, p!.Select(x => x.Action));
        Assert.DoesNotContain(p, x => x.Action == "wait");
    }

    [Fact]
    public void Reach_goal_is_pursued_achieved_and_reviewed_with_learning_progress()
    {
        var (ex, m) = World();
        var g = new SelfGoals { MaxGoalShare = 1 };
        var rng = new Random(3);
        SelfGoal? goal = null;
        for (int i = 0; i < 50 && goal == null; i++) { goal = g.Propose(ex, m, "A", 1, rng); }
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Reach, goal!.Kind); Assert.Equal("C", goal.Target);
        Assert.False(string.IsNullOrEmpty(goal.Origin));
        string cur = "A"; long seq = 1000;
        while (g.Active != null)
        {
            var a = g.NextAction(ex, m, cur, 1); if (a == null) break;
            var to = cur == "A" ? "B" : "C";
            Obs(ex, m, ref seq, cur, a, to); g.Observed(cur, a, to, true, 1); cur = to;
        }
        Assert.Equal(1, g.Achieved);
        // 達成の後に C の未試行の操作を試す（探索の手）→ 学習の進みとして測る
        Obs(ex, m, ref seq, "C", "button1", "C"); Obs(ex, m, ref seq, "C", "button2", "C");
        var rev = g.Review(ex, m, 100);
        Assert.Single(rev);
        Assert.True(rev[0].Progress > 0);
    }

    [Fact]
    public void Goal_without_learning_progress_rests_and_lowers_value()
    {
        var (ex, m) = World();
        var g = new SelfGoals { MaxGoalShare = 1 };
        var rng = new Random(5);
        double v0 = g.Value(GoalKind.Reach);
        SelfGoal? goal = null;
        for (int i = 0; i < 50 && goal == null; i++) goal = g.Propose(ex, m, "A", 1, rng);
        Assert.NotNull(goal);
        // v0.24.0: 探索の手は学べていて、目的に使った手は何も学べなかった
        for (int i = 0; i < 10; i++) g.StepGain(0.1, goalStep: false);
        g.StepGain(0, goalStep: true); g.StepGain(0, goalStep: true);
        g.Abandon("試験", 1);
        g.Review(ex, m, 10);
        Assert.True(g.Value(GoalKind.Reach) < v0);
        // 休みの間は同じ相手の目的を作らない
        for (int i = 0; i < 50; i++) { var x = g.Propose(ex, m, "A", 12, rng); if (x != null) { Assert.NotEqual("C", x.Target); g.Abandon("t", 12); } }
    }

    [Fact]
    public void Goal_share_is_capped_so_exploration_stays_primary()
    {
        var (ex, m) = World();
        var g = new SelfGoals();
        for (int i = 0; i < 100; i++) g.Tick(true);
        Assert.Null(g.Propose(ex, m, "A", 1, new Random(1)));
    }

    [Fact]
    public void Natural_relations_are_not_used_as_goal_paths()
    {
        // A では待っても button1 と同じように B へ移る（入力なしの遷移）→ 道筋に使わない（ノイズ・自然な変化を追わない）
        var ex = new Explorer(Acts(), 1); var m = new TransitionModel(); long seq = 1;
        for (int i = 0; i < 8; i++) { Obs(ex, m, ref seq, "A", "button1", "B"); Obs(ex, m, ref seq, "A", "wait", "B"); Obs(ex, m, ref seq, "B", "button3", "A"); }
        Assert.Null(new SelfGoals().PlanPath(ex, m, "A", "B"));
    }

    [Fact]
    public void Periodic_natural_change_is_detected_and_irregular_is_not()
    {
        var t = new TemporalBook();
        for (int i = 0; i < 8; i++) t.ObserveNaturalChange("P", 1000 + i * 500 + (i % 2 == 0 ? 10 : -10));
        Assert.Contains("periodic", t.Profile("P").Traits);
        Assert.InRange(t.Profile("P").PeriodMs, 450, 550);
        var rng = new Random(2); double now = 0;
        for (int i = 0; i < 12; i++) { now += 100 + rng.NextDouble() * 1500; t.ObserveNaturalChange("Q", now); }
        Assert.DoesNotContain("periodic", t.Profile("Q").Traits);
        // キーを送ると連続が切れる（入力の効果を周期と取り違えない）
        var u = new TemporalBook();
        for (int i = 0; i < 8; i++) { u.ObserveNaturalChange("R", i * 500); u.BreakRun(); }
        Assert.Null(u.Period("R"));
    }

    [Fact]
    public void Goal_value_never_reads_growth_meters_or_counts_as_reward()
    {
        var src = File.ReadAllText(Path.Combine(ConstraintAuditTests_SrcRoot(), "GameKouryakuKun.Core", "Goals", "SelfGoals.cs"));
        var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.DoesNotContain("Growth", code);
        Assert.DoesNotContain("Discoveries", code);
        Assert.DoesNotContain("Reward", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wait_gets_measured_even_when_keys_have_long_change_history_but_is_not_favored()
    {
        // 実機 v0.15.1: 経験の長いキーの手ばかり選ばれ、待つ手が 262 手中 1 手だった。基準が分からないうちは、キーの変化を満額の効果としない
        var ex = new Explorer(Acts(), 11) { Momentum = 0, ChordProbeProbability = 0 };
        var rng = new Random(4);
        foreach (var a in ex.Actions.Where(a => !a.IsWait)) for (int i = 0; i < 60; i++) ex.Observe("S", a, "S", rng.NextDouble() < 0.7);
        int waits = 0;
        for (int i = 0; i < 200; i++)
        {
            var c = ex.Choose("S");
            if (c.Action.IsWait) waits++;
            ex.Observe("S", c.Action, "S", rng.NextDouble() < 0.7);   // 入力なしでも同じように変わる画面
        }
        Assert.InRange(waits, 3, 70);   // 測られる（数回以上）が、35% を超えて選ばれない
    }

    [Fact]
    public void Diagnostic_export_puts_the_zip_file_on_the_clipboard_and_never_fails_the_export()
    {
        var src = File.ReadAllText(Path.Combine(ConstraintAuditTests_SrcRoot(), "GameKouryakuKun.App", "MainWindow.xaml.cs"));
        Assert.Contains("SetFileDropList", src);
        Assert.Contains("CopyZipToClipboardAsync(r.ZipPath)", src);
        var m = src[src.IndexOf("private static async Task<(bool Ok, string? Error)> CopyZipToClipboardAsync")..];
        m = m[..m.IndexOf("\n    }\n")];
        Assert.Contains("catch (Exception", m);   // 例外は外へ出さない
    }

    [Fact]
    public void Goals_achieved_twice_on_same_path_become_waiting_sequence_candidates_without_extra_value()
    {
        var (ex, m) = World();
        var g = new SelfGoals { MaxGoalShare = 1, BaseCooldown = 0 };
        var rng = new Random(9); long seq = 5000; int achieved = 0;
        for (int round = 0; round < 40 && achieved < 2; round++)
        {
            var goal = g.Propose(ex, m, "A", round * 10, rng);
            if (goal == null || goal.Kind != GoalKind.Reach || goal.Target != "C") { if (goal != null) g.Abandon("t", round * 10); g.Review(ex, m, round * 10 + 100); continue; }
            string cur = "A";
            while (g.Active != null) { var a = g.NextAction(ex, m, cur, round * 10); if (a == null) break; var to = cur == "A" ? "B" : "C"; Obs(ex, m, ref seq, cur, a, to); g.Observed(cur, a, to, true, round * 10); cur = to; }
            Obs(ex, m, ref seq, "C", "button3", "A");
            achieved = g.Achieved;
            g.Review(ex, m, round * 10 + 100);
        }
        Assert.True(achieved >= 2);
        var sk = Assert.Single(g.SkillCandidates);
        Assert.Equal(new[] { "button1", "button2" }, sk.Steps); Assert.Equal("A", sk.Start); Assert.Equal("C", sk.End);
        var miner = new ActionSequenceMiner();
        var ev = miner.AddFromGoal(sk.Steps, sk.Start, sk.End, sk.Achieved, sk.Attempts, seq);
        Assert.NotNull(ev);
        Assert.Equal("waiting", ev!.Kind);                       // 枠に入るかは同じ規則で決める（すぐには選択の候補にしない）
        Assert.Equal("goal", ev.Candidate.Origin);
        Assert.Equal(SequenceStatus.Dormant, ev.Candidate.Status);
        Assert.Null(miner.AddFromGoal(sk.Steps, sk.Start, sk.End, 2, 2, seq));   // 同じ並びは足さない
        Assert.Null(miner.AddFromGoal(new[] { "button1" }, "A", "B", 3, 3, seq)); // 1 手は並びではない
    }

    [Fact]
    public void Plan_uses_an_active_skill_as_one_edge_and_shortens_the_path()
    {
        // A → B → C → D（各 1 手）。A から C へは並び #1（button1 → button2）が確かにある
        var ex = new Explorer(Acts(), 1); var m = new TransitionModel(); long seq = 1;
        for (int i = 0; i < 6; i++) { Obs(ex, m, ref seq, "A", "button1", "B"); Obs(ex, m, ref seq, "B", "button2", "C"); Obs(ex, m, ref seq, "C", "button3", "D"); Obs(ex, m, ref seq, "D", "button1", "A"); }
        var plain = new SelfGoals().PlanPath(ex, m, "A", "D")!;
        Assert.Equal(3, plain.Count);
        var sk = ex.AddSequence(new ActionSequenceCandidate { Id = 1, Steps = new[] { "button1", "button2" }, Start = "A", End = "C" })!;
        for (int i = 0; i < 4; i++) { ex.ObserveComposite("A", sk, "C", true, false); m.Observe(seq++, "A", sk.Name, "C", true); }
        var p = new SelfGoals().PlanPath(ex, m, "A", "D")!;
        Assert.Equal(new[] { sk.Name, "button3" }, p.Select(x => x.Action));     // 4 手以上の道筋もスキル＋1 手で表せる
        ex.Deactivate(sk.Name);
        Assert.Equal(3, new SelfGoals().PlanPath(ex, m, "A", "D")!.Count);       // 休止中のスキルは使わない
    }

    [Fact]
    public void Reach_targets_include_fully_tried_but_still_uncertain_screens()
    {
        // B はすべて試してあるが、button1 の結果が毎回ばらつく（不確かさが大きい）。A からは button1 で B へ確かに行ける
        var ex = new Explorer(Acts(), 1); var m = new TransitionModel(); long seq = 1; var rng = new Random(1);
        for (int i = 0; i < 8; i++)
        {
            Obs(ex, m, ref seq, "A", "button1", "B");
            Obs(ex, m, ref seq, "B", "button1", "X" + rng.Next(6)); Obs(ex, m, ref seq, "B", "button2", "X" + rng.Next(6)); Obs(ex, m, ref seq, "B", "button3", "X" + rng.Next(6));
            Obs(ex, m, ref seq, "A", "button2", "A"); Obs(ex, m, ref seq, "A", "button3", "A"); Obs(ex, m, ref seq, "A", "wait", "A");
        }
        foreach (var n in new[] { "X0", "X1", "X2", "X3", "X4", "X5" }) foreach (var a in new[] { "button1", "button2", "button3" }) Obs(ex, m, ref seq, n, a, "A");
        var g = new SelfGoals { MaxGoalShare = 1, MinRetestUncertainty = 0.99 };   // 今の画面 A の確かめ直しは作らせない
        SelfGoal? goal = null;
        for (int i = 0; i < 50 && goal == null; i++) goal = g.Propose(ex, m, "A", 1, new Random(i));
        Assert.Null(goal);   // 不確かさ 0.99 以上の画面はない
        g = new SelfGoals { MaxGoalShare = 1 };
        for (int i = 0; i < 80 && (goal == null || goal.Kind != GoalKind.Reach); i++) { goal = g.Propose(ex, m, "A", 1, new Random(i)); if (goal != null && goal.Kind != GoalKind.Reach) { g.Abandon("t", 1); goal = null; } }
        Assert.NotNull(goal);
        Assert.Equal("B", goal!.Target);
        Assert.Contains("不確かさ", goal.Origin);
    }

    [Fact]
    public void Human_control_can_start_without_auto_exploration_through_the_same_session_path()
    {
        var root = ConstraintAuditTests_SrcRoot();
        var ses = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "Session.cs"));
        var win = File.ReadAllText(Path.Combine(root, "GameKouryakuKun.App", "MainWindow.xaml.cs"));
        // 同じ PlaySession（入力・観測・記録の経路）で、最初から Human Control を ON にするだけ
        Assert.Contains("if (_o.StartWithHumanControl) Human.SetEnabled(true);", ses);
        Assert.Contains("string source = isHuman ? \"human\" : \"agent\";", ses);
        Assert.Contains("StartWithHumanControl = true", win);
        Assert.Contains("if (!SessionActive) { StartSession(humanOnly: true); return; }", win);
        // 人間の手は並びの学習（まね）に入れない
        Assert.Contains("isHuman ? Enumerable.Empty<SequenceEvent>() : seqMiner.Observe(st)", ses);
        // 切り替えは最初の手の前に観測し直しとして記録される
        var hc = new GameKouryakuKun.Core.Control.HumanControl(new[] { "button1", "wait" });
        hc.SetEnabled(true);
        Assert.True(hc.TakeSwitch()); Assert.True(hc.Enabled);
        Assert.True(hc.Enqueue("button1")); Assert.False(hc.Enqueue("not-a-key"));
    }

    private static string ConstraintAuditTests_SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
