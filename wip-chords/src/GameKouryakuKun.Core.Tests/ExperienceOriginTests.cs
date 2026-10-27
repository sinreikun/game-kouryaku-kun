using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Perception;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// 経験の出どころ（v0.10.0。2026-10-18 13:07 のユーザー決定）: 似た画面の経験は弱い事前の推定（コピーしない）、分割は読み直し（継承しない）、
/// 区画の反応は情報の量として使う（変化の大きさを良いとしない）、DB で直接・読み直し・推定を区別する
/// </summary>
public class ExperienceOriginTests
{
    [Fact]
    public void Borrowed_prior_is_weak_and_direct_experience_dominates_quickly()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2" });
        var ex = new Explorer(acts, 3);
        for (int i = 0; i < 20; i++) ex.Observe("x", acts[0], "x" + i, true);   // 似た画面 x では button1 でいつも別の画面へ
        ex.SetPrior("y", "x", "borrowed", 1.0);
        var (c0, l0, _, n0, used) = ex.Blended("y", "button1");
        Assert.Equal(1.0, n0); Assert.Equal(1.0, l0 / n0); Assert.Single(used);          // 仮の観測 1 回分だけ
        Assert.Equal(0, ex.Nodes.TryGetValue("y", out var ny) && ny.Actions.TryGetValue("button1", out var sy) ? sy.Tries : 0);   // 直接の回数には入れない（コピーしない）
        for (int i = 0; i < 9; i++) ex.Observe("y", acts[0], "y", false);
        var (c1, l1, _, n1, _) = ex.Blended("y", "button1");
        Assert.True(l1 / n1 <= 0.1 + 1e-9, $"直接の経験 9 回の後の割合 {l1 / n1}");       // 直接の経験がすぐに支配する
        Assert.Equal(9, ex.Nodes["y"].Actions["button1"].Tries);
    }

    [Fact]
    public void No_borrowing_when_the_similarity_itself_lacks_evidence()
    {
        var b = new VisualStateBook();
        var s1 = Enumerable.Repeat(0.1f, VisualLayout.Length).ToArray(); var s2 = Enumerable.Repeat(0.9f, VisualLayout.Length).ToArray();
        var (va, _) = b.Assign(s1, "m1"); var (vb, _) = b.Assign(s2, "m2");
        foreach (var v in new[] { va, vb }) for (int i = 0; i < 5; i++) { b.ObserveTransition(v, "button1", true, 99); b.ObserveTransition(v, "button2", false, v); }
        Assert.Equal(b.TransitionGroups()[va], b.TransitionGroups()[vb]);   // 群にはなる（記録）
        Assert.Empty(b.BorrowPairs());                                     // 比べた入力が 2 つだけ: 借りるには証拠が足りない
        foreach (var v in new[] { va, vb }) for (int i = 0; i < 5; i++) b.ObserveTransition(v, "dpad_up", true, v);
        var p = Assert.Single(b.BorrowPairs());
        Assert.Equal(("m1", "m2"), (p.NodeA, p.NodeB));
        Assert.True(p.Strength > 0 && p.Strength <= b.BorrowStrength);
    }

    [Fact]
    public void Information_value_rewards_unknown_relations_not_big_changes()
    {
        var map = new ReactionMap();
        Assert.Equal(1.0, map.InformationGain("m1", "button1"), 6);   // 何も分かっていない
        var a = new byte[160 * 90 * 4]; var all = (byte[])a.Clone(); Array.Fill(all, (byte)255);
        var f0 = new Frame(1, 160, 90, a, 0); var f1 = new Frame(2, 160, 90, all, 0);
        for (int i = 0; i < 20; i++)
        {
            map.Observe("m1", "big", f0, f1);      // 毎回すべての区画が変わる
            map.Observe("m1", "none", f0, f0);     // 毎回まったく反応しない
            map.Observe("m1", "mixed", f0, i % 2 == 0 ? f1 : f0);   // ときどき反応する
        }
        double big = map.InformationGain("m1", "big"), none = map.InformationGain("m1", "none"), mixed = map.InformationGain("m1", "mixed");
        Assert.Equal(none, big, 9);                 // 大きく変わることに価値はない（反応しないことが分かった観測も同じだけ情報）
        Assert.True(big < 0.3, $"20 回の後 {big}");
        Assert.True(mixed > big, $"まだ不確かな関係 {mixed} > 分かった関係 {big}");
        // 選択に入れても、変化の大きい操作が優先されることはない
        var ex = new Explorer(Explorer.FromLogicalKeys(new[] { "button1", "button2" }), 5) { InformationValue = (n, x) => x.Name == "button1" ? big : none };
        var cnt = Enumerable.Range(0, 4000).Select(_ => ex.Choose("q").Action.Name).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        Assert.InRange(cnt["button1"] / (double)cnt["button2"], 0.85, 1.15);
    }

    [Fact]
    public void Reassign_moves_observations_without_changing_totals()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "button1" });
        var ex = new Explorer(acts, 1);
        for (int i = 0; i < 6; i++) ex.Observe("C", acts[0], i < 4 ? "D" : "C", i < 4);
        int total = ex.Transitions().Sum(t => t.N);
        ex.Reassign("C", "button1", "D", "C#v2", "D", 3, 3);
        ex.Reassign("C", "button1", "D", "C#v2", "D", 5, 5);   // 手元にある回数（残り 1）までしか移さない
        Assert.Equal(total, ex.Transitions().Sum(t => t.N));
        Assert.Equal(4, ex.Nodes["C#v2"].Actions["button1"].Tries);
        Assert.Equal(2, ex.Nodes["C"].Actions["button1"].Tries);
    }

    [Fact]
    public void Split_reclassifies_past_records_and_keeps_direct_records_intact()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.10.0");
        Assert.Equal(HealthReport.ExpectedMemorySchema, s.Db.SchemaVersion);
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        // C（見た目 1）→ D、C（見た目 2）→ D、D → C（行き先の見た目 2）、見た目の記録のない古い手
        s.AppendStream(run, null, new StreamStep(1, "C", "button1", true, "D", 0.1), 1, Array.Empty<BoundaryReason>());           // v6 より前（見た目なし）
        s.AppendStream(run, null, new StreamStep(2, "D", "button2", true, "C", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 5);
        s.AppendStream(run, null, new StreamStep(3, "C", "button1", true, "D", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 2);
        s.AppendStream(run, null, new StreamStep(4, "D", "button2", true, "C", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 5);
        s.AppendStream(run, null, new StreamStep(5, "C", "button1", true, "D", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 1);
        var rc = s.ReclassifyForSplit("C", 2, "C#v2");
        Assert.Contains(rc, r => r.OrigFrom == "C" && r.NewFrom == "C#v2" && r.N == 1);                       // 手 3 の始まり
        Assert.Contains(rc, r => r.OrigFrom == "D" && r.OrigTo == "C" && r.NewTo == "C#v2" && r.N == 1);      // 手 2 の行き先（次の手 3 の見た目が 2）
        // 直接の記録はそのまま、読み直しは別の表。出どころの一覧で区別できる
        Assert.Equal(3L, s.Db.Scalar("SELECT COUNT(*) FROM experience_stream WHERE state='C'"));
        Assert.Equal(2L, s.Db.Scalar("SELECT COUNT(*) FROM stream_reinterpretations"));
        var st = s.LoadStream(10);
        Assert.Equal("C#v2", st.Single(x => x.Seq == 3).State);
        Assert.Equal("C#v2", st.Single(x => x.Seq == 2).NextState);
        Assert.Equal("C", st.Single(x => x.Seq == 1).State);   // 見た目の記録のない手は読み直せない（分割前のノードのまま）
        Assert.True(st.Single(x => x.Seq == 3).Reinterpreted); Assert.False(st.Single(x => x.Seq == 1).Reinterpreted);   // 読み直した手かどうかを区別して読める
        s.AddTransitions(new[] { ("C", "button1", "D", 3) });
        s.SavePriors(new[] { ("C#v2", "C", "parent", 2.0, "split vis 2") });
        var prov = s.Db.Query("SELECT origin, COUNT(*) FROM experience_provenance GROUP BY origin ORDER BY origin", r => (r.GetString(0), r.GetInt64(1)));
        Assert.Equal(new[] { ("direct", 1L), ("parent", 1L), ("reclassified", 2L) }, prov);
        Assert.Equal(2, s.LoadReclassified().Count);
        Assert.ThrowsAny<Exception>(() => s.Db.Exec("INSERT INTO experience_priors(node, source_node, origin, strength) VALUES('a','b','copied',1)"));   // 出どころの種類は決まった値だけ
    }
}
