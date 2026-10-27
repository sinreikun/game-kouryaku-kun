using GameKouryakuKun.Core.Exploration;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// 経験から見つけた操作の並びを複合の操作の候補にする（v0.9.0。2026-10-18 10:49 のユーザー条件）:
/// ただ多い並びは昇格させない／始まりの似かよりと遷移の再現性を記録する／中立の名前で、事前の価値は上乗せしない
/// </summary>
public class ActionSequenceTests
{
    private static long _q;
    private static StreamStep S(string s, string a, string n) => new(++_q, s, a, s != n, n, 0.1);

    [Fact]
    public void Only_reproducible_transitions_from_a_consistent_start_become_candidates()
    {
        var rnd = new Random(3); var m = new ActionSequenceMiner(); var created = new List<ActionSequenceCandidate>();
        void Feed(IEnumerable<StreamStep> xs) { foreach (var x in xs) created.AddRange(m.Observe(x).Where(e => e.Kind is "promoted" or "waiting").Select(e => e.Candidate)); }
        for (int k = 0; k < 30; k++)
        {
            // (1) 再現する遷移: s0 で button1 → button2 と押すと必ず s9 へ
            Feed(new[] { S("s0", "button1", "s5"), S("s5", "button2", "s9") });
            _q += 5;   // 通し番号が飛ぶ所（連続していない）では並びを数えない
            // (2) ただ多いだけ: 同じ所から button3 → button3 と押すが、行き先は毎回ばらばら
            Feed(new[] { S("t0", "button3", "t1"), S("t1", "button3", "r" + rnd.Next(1000)) });
            _q += 5;
            // (3) あちこちで続けて押されただけ: 始まりが毎回違う
            var st = "u" + rnd.Next(1000);
            Feed(new[] { S(st, "dpad_up", "w1"), S("w1", "dpad_down", "w2") });
            _q += 5;
            // (4) 何も起きない並び（行き先が始まりと同じ）
            Feed(new[] { S("z0", "button4", "z0"), S("z0", "button4", "z0") });
            _q += 5;
        }
        var c = Assert.Single(created);
        Assert.Equal("ActionSequence #1", c.Name);   // 中立の名前（意味は付けない）
        Assert.Equal(new[] { "button1", "button2" }, c.Steps);
        Assert.Equal("s0", c.Start); Assert.Equal("s9", c.End);
        Assert.True(c.Reproducibility >= 0.6 && c.StartSimilarity >= 0.25 && c.AtStart >= 3);
        // 記録される量（回数・始まりの似かより・再現性）は続けて更新される
        Assert.Equal(30, m.Candidates.Single().Total);
    }

    [Fact]
    public void Candidate_has_no_prior_bonus_and_is_tried_only_at_its_start()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2", "button3", "dpad_up" });
        var ex = new Explorer(acts, 7);
        var seq = ex.AddSequence(new ActionSequenceCandidate { Id = 1, Steps = new[] { "button1", "button2" }, Start = "a" });
        Assert.NotNull(seq);
        Assert.Equal(ActionGroup.Sequence, seq!.Group);
        var count = new Dictionary<string, int>();
        for (int i = 0; i < 6000; i++) { var ch = ex.Choose("a").Action.Name; count[ch] = count.TryGetValue(ch, out var v) ? v + 1 : 1; }
        double singles = acts.Average(a => count.TryGetValue(a.Name, out var v) ? v : 0);
        Assert.InRange(count[seq.Name], singles * 0.8, singles * 1.2);   // 未試行の単独の操作と同じ扱い
        for (int i = 0; i < 2000; i++) Assert.NotEqual(seq.Name, ex.Choose("b").Action.Name);   // ほかのノードでは試さない
    }

    [Fact]
    public void Candidate_value_is_learned_from_later_experience_like_a_single_action()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2" });
        var ex = new Explorer(acts, 1);
        var seq = ex.AddSequence(new ActionSequenceCandidate { Id = 1, Steps = new[] { "button1", "button2" }, Start = "a" })!;
        ex.ObserveComposite("a", seq, "a", false, false);
        ex.ObserveComposite("a", seq, "a", false, false);
        var st = ex.Nodes["a"].Actions[seq.Name];
        Assert.Equal(2, st.Tries); Assert.Equal(0, st.Changes);
        // 遷移の記録として保存・読み込みできる（並びを先に候補へ加えてから）
        var ex2 = new Explorer(acts, 1);
        ex2.AddSequence(new ActionSequenceCandidate { Id = 1, Steps = new[] { "button1", "button2" }, Start = "a" });
        ex2.Load(ex.Transitions());
        Assert.Equal(2, ex2.Nodes["a"].Actions[seq.Name].Tries);
        // 並びの中の操作が候補にない並びは加えない
        Assert.Null(ex2.AddSequence(new ActionSequenceCandidate { Id = 2, Steps = new[] { "button1", "button9" }, Start = "a" }));
    }

    [Fact]
    public void Active_cap_swaps_by_general_statistics_and_never_forgets()
    {
        // 枠は同時に選択の候補に入れる数の上限（記憶の上限ではない）
        var m = new ActionSequenceMiner { MaxActive = 2, DormantAfterSteps = 300 };
        var rnd = new Random(4); var ev = new List<SequenceEvent>();
        void Rep(string start, string a1, string a2, string end) { _q += 3; ev.AddRange(m.Observe(S(start, a1, start + "_m"))); ev.AddRange(m.Observe(S(start + "_m", a2, end))); }
        for (int k = 0; k < 6; k++) { Rep("A", "button1", "button2", "A9"); Rep("B", "button3", "button4", "B9"); }
        Assert.Equal(2, m.Active.Count());
        for (int k = 0; k < 6; k++) Rep("C", "dpad_up", "dpad_down", "C9");
        var c = m.Candidates.Single(x => x.Start == "C");
        Assert.Equal(SequenceStatus.Dormant, c.Status);                     // 枠が埋まっている間は待つ
        Assert.Contains(ev, e => e.Candidate == c && e.Kind == "waiting");
        // A の行き先がばらばらになった（最近の再現性が下がった）→ 休止にして、C を入れる
        for (int k = 0; k < 12; k++) { Rep("A", "button1", "button2", "R" + rnd.Next(1000)); Rep("B", "button3", "button4", "B9"); Rep("C", "dpad_up", "dpad_down", "C9"); }
        var a = m.Candidates.Single(x => x.Start == "A");
        Assert.Equal(SequenceStatus.Dormant, a.Status);
        Assert.Equal(SequenceStatus.Active, c.Status);
        Assert.Contains(ev, e => e.Candidate == a && e.Kind == "dormant" && e.Reason.Contains("再現性"));
        // B が長く再現しない（始まりに来ない）→ 休止。A が再び再現するようになったら戻る
        for (int k = 0; k < 160; k++) { Rep("A", "button1", "button2", "A9"); Rep("C", "dpad_up", "dpad_down", "C9"); }
        var b = m.Candidates.Single(x => x.Start == "B");
        Assert.Equal(SequenceStatus.Dormant, b.Status);
        Assert.Contains(ev, e => e.Candidate == b && e.Kind == "dormant" && e.Reason.Contains("再現していない"));
        Assert.Equal(SequenceStatus.Active, a.Status);
        Assert.Contains(ev, e => e.Candidate == a && e.Kind == "reactivated");
        Assert.Equal(3, m.Candidates.Count);   // 記録は消さない
        Assert.Equal(new[] { 1, 2, 3 }, m.Candidates.Select(x => x.Id).OrderBy(x => x));
    }

    [Fact]
    public void Dormant_sequence_is_not_chosen_but_keeps_its_statistics()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2" });
        var ex = new Explorer(acts, 2);
        var seq = ex.AddSequence(new ActionSequenceCandidate { Id = 1, Steps = new[] { "button1", "button2" }, Start = "a" })!;
        ex.ObserveComposite("a", seq, "b", true, false);
        ex.Deactivate(seq.Name);
        for (int i = 0; i < 1000; i++) Assert.NotEqual(seq.Name, ex.Choose("a").Action.Name);
        Assert.Contains(ex.Transitions(), t => t.Action == seq.Name);
        ex.Activate(seq.Name);
        Assert.Contains(Enumerable.Range(0, 2000).Select(_ => ex.Choose("a").Action.Name), n => n == seq.Name);
    }
}

