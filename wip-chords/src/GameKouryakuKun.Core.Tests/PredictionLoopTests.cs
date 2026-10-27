using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Perception;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// 予測 → 比較 → 修正 → 次の選択（v0.11.0）。合成の世界だけで、内部モデルが外れから修正され、その結果が次の選択を変えることを確かめる
/// </summary>
public class PredictionLoopTests
{
    [Fact]
    public void Persistent_change_in_a_relation_is_revised_quickly()
    {
        var m = new TransitionModel(); long seq = 0;
        for (int i = 0; i < 40; i++) m.Observe(++seq, "s", "button1", "A", true);
        Assert.Equal("A", m.Predict("s", "button1").TopNext);
        int switched = -1; ModelRevision? rev = null;
        for (int i = 0; i < 40 && switched < 0; i++)
        {
            var c = m.Observe(++seq, "s", "button1", "B", true);
            rev ??= c.Revision;
            if (m.Predict("s", "button1").TopNext == "B") switched = i + 1;
        }
        Assert.NotNull(rev);
        Assert.InRange(switched, 1, 12);   // 修正がなければ A の 40 回を超えるまで（41 回）B を予測しない
        Assert.True(rev!.WeightAfter < rev.WeightBefore);
        Assert.Equal(1, m.Revisions);
    }

    [Fact]
    public void Stationary_random_relations_are_rarely_revised()
    {
        long total = 0;
        for (int seed = 1; seed <= 10; seed++)
        {
            var rng = new Random(seed); var m = new TransitionModel(); long seq = 0;
            string[] outs = { "A", "B", "C" };
            for (int i = 0; i < 6000; i++)
            {
                int node = rng.Next(5), act = rng.Next(4);
                // 関係ごとに一定の分布（決まった結果・半々・偏った3通り）
                string next = ((node + act) % 3) switch { 0 => "A", 1 => rng.NextDouble() < 0.5 ? "A" : "B", _ => outs[rng.NextDouble() < 0.8 ? 0 : rng.Next(1, 3)] };
                m.Observe(++seq, "s" + node, "button" + act, next, next != "A");
            }
            total += m.Revisions;
        }
        Assert.True(total <= 30, $"修正 {total} 回（6 万回の観測。分布は変わらない）");
    }

    [Fact]
    public void Uncertainty_falls_with_evidence_and_rises_after_revision()
    {
        var m = new TransitionModel(); long seq = 0;
        double u0 = m.Uncertainty("s", "button1");
        Assert.Equal(1.0, u0, 3);
        for (int i = 0; i < 30; i++) m.Observe(++seq, "s", "button1", "A", true);
        double u30 = m.Uncertainty("s", "button1");
        Assert.True(u30 < 0.2, $"{u30}");
        for (int i = 0; i < 12 && m.Revisions == 0; i++) m.Observe(++seq, "s", "button1", "B", true);
        Assert.True(m.Revisions >= 1);
        Assert.True(m.Uncertainty("s", "button1") > u30 * 2, "修正のあとは関係が不確かになる");
    }

    [Fact]
    public void Revision_changes_the_next_action_selection()
    {
        // 2 つの操作をどちらも十分に試した後、片方の関係だけが変わる。修正の後はその操作を選ぶ割合が上がる
        double Share(bool revise)
        {
            var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2" });
            var m = new TransitionModel(); long seq = 0;
            var ex = new Explorer(acts, 11) { UncertaintyValue = (n, a) => m.Uncertainty(n, a.Name), Momentum = 0, ChordProbeProbability = 0 };
            for (int i = 0; i < 30; i++) foreach (var a in acts) { ex.Observe("s", a, "s", false); m.Observe(++seq, "s", a.Name, "s", false); }
            if (revise) for (int i = 0; i < 12 && m.Revisions == 0; i++) { ex.Observe("s", acts[0], "s", true); m.Observe(++seq, "s", acts[0].Name, "t", true); }
            else for (int i = 0; i < 12; i++) { ex.Observe("s", acts[0], "s", false); m.Observe(++seq, "s", acts[0].Name, "s", false); }
            int k = 0; for (int i = 0; i < 2000; i++) if (ex.Choose("s").Action == acts[0]) k++;
            return k / 2000.0;
        }
        double with = Share(true), without = Share(false);
        Assert.True(with > without + 0.03, $"修正あり {with:P1}／なし {without:P1}");
    }

    [Fact]
    public void Prediction_reports_provenance_of_its_evidence()
    {
        var m = new TransitionModel(); long seq = 0;
        for (int i = 0; i < 4; i++) m.Observe(++seq, "x", "button1", "z", true);
        m.Observe(++seq, "y", "button1", "y", false, reinterpreted: true);
        m.SetPriors(new[] { ("y", new ExperiencePrior("x", "borrowed", 1.0)) });
        var p = m.Predict("y", "button1");
        Assert.Equal(1, p.DirectN); Assert.Equal(1, p.ReinterpretedN); Assert.Equal(1.0, p.PriorN);
        Assert.Equal(new[] { "borrowed:x" }, p.PriorSources);
        var q = m.Predict("x", "button1");
        Assert.Equal(0, q.PriorN); Assert.Empty(q.PriorSources);   // 推定の推定はしない・出どころのない推定は付かない
    }

    [Fact]
    public void Explorer_choice_carries_structured_score_components()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2", "dpad_up" });
        var ex = new Explorer(acts, 2) { Momentum = 0, ChordProbeProbability = 0, UncertaintyValue = (_, _) => 0.5, InformationValue = (_, _) => 0.25 };
        var c = ex.Choose("s");
        Assert.Equal("scored", c.Rule);
        Assert.Equal(3, c.Candidates.Count);
        Assert.Equal(1.0, c.Candidates.Sum(x => x.Probability), 6);
        var mine = c.Candidates.Single(x => x.Action == c.Action.Name);
        Assert.Equal(c.Probability, mine.Probability, 9);
        foreach (var x in c.Candidates)
        {
            double expect = x.Reversibility * (0.5 + x.Novelty + x.Rates + 0.5 * x.Information + 0.5 * x.Uncertainty);
            Assert.Equal(expect, x.Score, 9);   // 記録した内訳から点数を再計算できる
        }
    }

    [Fact]
    public void Decision_records_prediction_check_and_revision_are_stored_and_traceable()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.11.0");
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        var m = new TransitionModel(); var acts = Explorer.FromLogicalKeys(new[] { "button1", "button2" });
        var ex = new Explorer(acts, 1) { Momentum = 0, ChordProbeProbability = 0, UncertaintyValue = (n, a) => m.Uncertainty(n, a.Name) };
        long seq = 0; long revisions = 0;
        for (int i = 0; i < 60; i++)
        {
            var c = ex.Choose("s"); var p = m.Predict("s", c.Action.Name);
            string next = c.Action.Name == "button1" ? (i < 30 ? "s" : "t") : "s";
            var st = new StreamStep(++seq, "s", c.Action.Name, next != "s", next, 0);
            s.AppendStream(run, null, st, 0, Array.Empty<BoundaryReason>());
            ex.Observe("s", c.Action, next, next != "s");
            var chk = m.Observe(st.Seq, "s", c.Action.Name, next, next != "s");
            long? rid = chk.Revision is { } r ? s.AddModelRevision(r) : null; if (rid != null) revisions++;
            var dr = new DecisionRecord(st.Seq, null, "s", c.Action.Name, null, c.Rule, c.Probability, c.Score, c.Candidates, p, chk, rid);
            s.AddDecision(dr);
            if (i == 59) { Assert.Contains("点数", DecisionText.Candidates(dr)); Assert.Contains("予測の証拠", DecisionText.Choice(dr)); Assert.Contains("外れ", DecisionText.Result(dr)); }
        }
        Assert.True(revisions >= 1);
        var rows = s.RecentDecisions(100);
        Assert.Equal(60, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.CandidatesJson));
        Assert.Equal(revisions, s.RecentRevisions(100).Count);
        Assert.Equal(revisions, rows.Count(r => r.RevisionId != null));
        // 記録した判断は連続履歴の手を指し、修正も記録した判断から辿れる
        Assert.Equal(0L, s.Db.Scalar("SELECT COUNT(*) FROM decision_records d WHERE NOT EXISTS (SELECT 1 FROM experience_stream x WHERE x.seq = d.seq)"));
        Assert.Equal(60L, s.Db.Scalar("SELECT COUNT(*) FROM decision_records WHERE json_extract(candidates_json, '$.total') = 2"));
        Assert.Equal(60L, s.Db.Scalar("SELECT COUNT(*) FROM decision_records WHERE json_array_length(candidates_json, '$.kept[0]') = json_array_length(candidates_json, '$.fields')"));
        Assert.Equal(60L, s.Db.Scalar("SELECT COUNT(*) FROM decision_records d WHERE EXISTS (SELECT 1 FROM json_each(d.candidates_json, '$.kept') k WHERE json_extract(k.value, '$[0]') = d.chosen)"));   // 選んだ候補は必ず残る
        Assert.ThrowsAny<Exception>(() => s.Db.Exec("INSERT INTO decision_records(node, chosen, rule) VALUES('a','b','guess')"));   // 規則は決まった値だけ
        // 修正しても直接の記録は消えない
        Assert.Equal(60L, s.Db.Scalar("SELECT COUNT(*) FROM experience_stream"));
    }

    [Fact]
    public void Replacing_priors_keeps_their_history()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.11.0");
        s.SavePriors(new[] { ("a", "b", "borrowed", 0.5, "e1"), ("c", "d", "parent", 2.0, "e2") });
        s.SavePriors(new[] { ("a", "b", "borrowed", 0.8, "e3") });
        s.SavePriors(new[] { ("a", "b", "borrowed", 0.8, "e3") });   // 変化なしは記録しない
        var ev = s.Db.Query("SELECT node, kind FROM experience_prior_events ORDER BY event_id", r => (r.GetString(0), r.GetString(1)));
        Assert.Equal(new[] { ("a", "added"), ("c", "added"), ("a", "changed"), ("c", "removed") }, ev);
        Assert.Equal(1L, s.Db.Scalar("SELECT COUNT(*) FROM experience_priors"));
    }

    [Fact]
    public void Model_rebuilt_by_replay_reaches_the_same_state()
    {
        var steps = new List<StreamStep>(); var rng = new Random(3);
        for (int i = 1; i <= 500; i++) { string a = "button" + rng.Next(3); string nx = i < 250 ? "x" : (a == "button0" ? "y" : "x"); steps.Add(new StreamStep(i, "s", a, nx != "s", nx, 0)); }
        var live = new TransitionModel(); foreach (var st in steps) live.Observe(st.Seq, st.State, st.Action, st.NextState, st.Changed);
        var re = new TransitionModel(); re.Replay(steps);
        Assert.Equal(live.Revisions, re.Revisions);
        foreach (var a in new[] { "button0", "button1", "button2" })
        {
            var p = live.Predict("s", a); var q = re.Predict("s", a);
            Assert.Equal(p.TopNext, q.TopNext); Assert.Equal(p.PTop, q.PTop, 9); Assert.Equal(p.Uncertainty, q.Uncertainty, 9);
        }
    }

    [Fact]
    public void After_a_split_the_rebuilt_model_predicts_for_the_new_node_from_reinterpreted_steps()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.11.0");
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        long q = 0;
        for (int i = 0; i < 10; i++)
        {
            int vis = i % 2 == 0 ? 1 : 2;   // 見た目 2 のときだけ button1 で E へ
            s.AppendStream(run, null, new StreamStep(++q, "C", "button1", true, vis == 2 ? "E" : "D", 0.1), 1, Array.Empty<BoundaryReason>(), 0, vis);
            s.AppendStream(run, null, new StreamStep(++q, vis == 2 ? "E" : "D", "button2", true, "C", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 9);
        }
        var before = new TransitionModel(); before.Replay(s.LoadStream(1000));
        Assert.True(before.Predict("C", "button1").Uncertainty > 0.05);   // 分ける前は D と E が混ざる
        s.ReclassifyForSplit("C", 2, "C#v2");
        var after = new TransitionModel(); after.Replay(s.LoadStream(1000));
        var p = after.Predict("C#v2", "button1");
        Assert.Equal("E", p.TopNext);
        Assert.Equal(5, p.DirectN); Assert.Equal(5, p.ReinterpretedN);   // 証拠は直接の記録の読み直しだと区別できる
        Assert.Equal("D", after.Predict("C", "button1").TopNext);
        Assert.Equal(20L, s.Db.Scalar("SELECT COUNT(*) FROM experience_stream"));   // 直接の記録はそのまま
    }

    [Fact]
    public void Split_needs_evidence_not_just_a_large_difference_in_few_samples()
    {
        VisualStateBook Book(int scale)
        {
            var b = new VisualStateBook();
            var (va, _) = b.Assign(Enumerable.Repeat(0.2f, VisualLayout.Length).ToArray(), "m1");
            var (vb, _) = b.Assign(Enumerable.Repeat(0.8f, VisualLayout.Length).ToArray(), "m1");
            foreach (var act in new[] { "button1", "button2" })
            {
                // 総変動距離 2/3（はっきり違って見える）が、各 3 回ずつしかない（ベイズ因子の対数は約 2.06）
                for (int k = 0; k < scale; k++)
                {
                    b.ObserveTransition(va, act, false, va); b.ObserveTransition(va, act, false, va); b.ObserveTransition(va, act, true, va);
                    b.ObserveTransition(vb, act, true, 99); b.ObserveTransition(vb, act, true, 99); b.ObserveTransition(vb, act, true, vb);
                }
            }
            return b;
        }
        Assert.Empty(Book(1).RefreshSplits());               // 少ない観測の偶然かもしれない差では分けない
        var big = Book(4); var made = big.RefreshSplits();    // 同じ割合で観測が 4 倍あれば分ける
        Assert.Single(made);
        Assert.True(big.SplitEvidence(1, 2) >= big.SplitMinLogBayesFactor);
    }
}
