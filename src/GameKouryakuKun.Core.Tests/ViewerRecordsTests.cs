using System.Text.RegularExpressions;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.13.0: 観測の確かさの表示・仮説の板・新発見の一覧（表示だけ。選択・学習には使わない）</summary>
public class ViewerRecordsTests
{
    private static string Src(string rel) => File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), rel));

    private static DecisionRecord Dec(bool amb, double vis = 0.95, double mode = 0.3, double age = 40) =>
        new(1, null, "s", "a", null, "scored", 0.5, 1, Array.Empty<CandidateScore>(), null, null, null) { ObsVisRatio = vis, ObsModeRatio = mode, FrameAgeMs = age, Ambiguous = amb };

    [Fact]
    public void Observation_confidence_is_shown_from_the_record_only()
    {
        var t = DecisionText.Observation(Dec(true));
        Assert.Contains("0.95", t); Assert.Contains("40 ms", t); Assert.Contains("作り直しに使いません", t);
        Assert.Contains("境目から離れている", DecisionText.Observation(Dec(false, 0.4)));
        Assert.Contains("新しい状態", DecisionText.Observation(Dec(false, 1.3)));
        Assert.Equal("観測: 記録なし", DecisionText.Observation(Dec(false, double.NaN, double.NaN, double.NaN)));
        var m = new TransitionModel();
        var chk = m.Observe(1, "s", "a", "t", true);
        Assert.Contains("観測:", DecisionText.Result(Dec(true) with { Check = chk }));
    }

    [Fact]
    public void Hypothesis_board_classifies_relations_from_model_quantities_and_reading_it_changes_nothing()
    {
        var m = new TransitionModel();
        long q = 0;
        for (int i = 0; i < 40; i++) m.Observe(++q, "s", "a", "x", true);         // 決まった結果 → 確か
        m.Observe(++q, "s", "b", "y", true);                                         // 1回だけ → 確かめ中
        for (int i = 0; i < 60; i++) m.Observe(++q, "u", "a", "x", true);
        ModelRevision? rev = null;
        for (int i = 0; i < 60 && rev == null; i++) rev = m.Observe(++q, "u", "a", "z", true).Revision;   // 結果が変わる → 作り直した直後
        Assert.NotNull(rev);
        var before = (m.Checks, m.Revisions, m.Predict("s", "a").PTop, m.Uncertainty("u", "a"));
        var hs = HypothesisBoard.Build(m.Summaries(), Array.Empty<(int, string, string, bool)>(), Array.Empty<ActionSequenceCandidate>(), q, 2000);
        Assert.Equal(before, (m.Checks, m.Revisions, m.Predict("s", "a").PTop, m.Uncertainty("u", "a")));
        HypothesisStatus St(string subj) => hs.Single(h => h.Subject == subj).Status;
        Assert.Equal(HypothesisStatus.Settled, St("s「a」"));
        Assert.Equal(HypothesisStatus.Testing, St("s「b」"));
        Assert.Equal(HypothesisStatus.Revised, St("u「a」"));
        var t = HypothesisBoard.Text(hs);
        Assert.Contains("[作り直した直後] u「a」", t);
        Assert.Contains("[確かめ中] s「b」", t);
    }

    [Fact]
    public void Sequence_and_split_hypotheses_follow_their_records()
    {
        var miner = new ActionSequenceMiner();
        string[] st = { "S", "X", "T" }, ac = { "a", "b", "c" };
        miner.Replay(Enumerable.Range(0, 300).Select(i => new StreamStep(i, st[i % 3], ac[i % 3], true, st[(i + 1) % 3], 0.1)));
        var c = miner.Active.First(x => x.Steps.SequenceEqual(new[] { "a", "b" }));
        var hs0 = HypothesisBoard.Build(Array.Empty<RelationSummary>(), new[] { (7, "m1", "m1#v7", true), (8, "m1", "m1#v8", false) }, miner.Candidates, 300, miner.DormantAfterSteps);
        Assert.Equal(HypothesisStatus.Untested, hs0.Single(h => h.Subject == c.Name).Status);
        Assert.Equal(HypothesisStatus.Settled, hs0.Single(h => h.Subject == "m1#v7").Status);
        Assert.Equal(HypothesisStatus.Withdrawn, hs0.Single(h => h.Subject == "m1#v8").Status);
        for (int i = 0; i < 6; i++) miner.ObserveExecution(c.Id, 300 + i, false);
        var h1 = HypothesisBoard.Build(Array.Empty<RelationSummary>(), Array.Empty<(int, string, string, bool)>(), miner.Candidates, 310, miner.DormantAfterSteps).Single(h => h.Subject == c.Name);
        Assert.Equal((HypothesisStatus.Settled, 6, 6), (h1.Status, h1.Checks, h1.Hits));
    }

    [Fact]
    public void Discovery_log_is_bounded_and_counts_every_kind()
    {
        var d = new DiscoveryLog();
        for (int i = 0; i < DiscoveryLog.Capacity + 50; i++) d.Add(new Discovery(i, i % 2 == 0 ? DiscoveryKind.Node : DiscoveryKind.Pattern, $"n{i}", ""));
        Assert.Equal(DiscoveryLog.Capacity, d.Recent(int.MaxValue).Count);
        Assert.Equal(DiscoveryLog.Capacity + 50, d.Counts().Values.Sum());
        Assert.StartsWith("#249", d.Text(3).Split('\n')[1]);
    }

    [Fact]
    public void Hypotheses_and_discoveries_are_display_only()
    {
        // 作る側は読むだけ（学習・選択の状態を変える呼び出しがない）
        var h = Src("GameKouryakuKun.Core/Exploration/Hypotheses.cs");
        Assert.DoesNotMatch(@"\.(Observe|Seed|Replay|SetPriors|SetPrior|Load|Activate|Deactivate|Reassign|Revert|ObserveExecution|ObserveNext|ObserveTransition|RefreshSplits)\(", h);
        foreach (var f in new[] { "GameKouryakuKun.Core/Exploration/Explorer.cs", "GameKouryakuKun.Core/Exploration/TransitionModel.cs", "GameKouryakuKun.Core/Exploration/ActionSequences.cs",
                     "GameKouryakuKun.Core/Exploration/VisualStates.cs", "GameKouryakuKun.Core/Exploration/SequenceLearning.cs", "GameKouryakuKun.Storage/ExperienceStore.cs" })
        {
            var s = Src(f);
            foreach (var w in new[] { "HypothesisBoard", "Hypothesis(", "DiscoveryLog", "Discovery(" }) Assert.DoesNotContain(w, s);
        }
        // Session では、仮説・新発見を読むのは表示用のプロパティと終了時のログだけ（選択の処理は読まない）
        var session = Src("GameKouryakuKun.App/Session.cs").Split('\n');
        var reads = session.Where(l => Regex.IsMatch(l, @"(?<!explorer\.)\bDiscoveries\.(Recent|Counts|Text)\(|\bHypotheses\.(GroupBy|Where|Select|Count|First|Any)\b")).ToList();
        Assert.All(reads, l => Assert.Matches(@"_by_kind|_by_status", l));
        Assert.Equal(2, reads.Count);
    }

    [Fact]
    public void Inspector_lists_discoveries_from_their_source_tables()
    {
        using var d = new TempDir();
        using (var s = ExperienceStore.Open(d["p"], "prof", "1", "0.13.0"))
            s.AddModelRevision(new ModelRevision(42, "s", "a", 10, 3, 5, "test"));
        using var ins = new DbInspector(Path.Combine(d["p"], "memory.sqlite"));
        var t = ins.Run("discoveries", Array.Empty<string>());
        Assert.Contains("revision", t); Assert.Contains("42", t);
    }
}
