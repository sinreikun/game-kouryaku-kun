using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// 分割の一生（v0.12.0）: 読み直しの済み印（以前の分割の読み直し）・索引による読み直し（全履歴の走査と同じ結果）・取り消し・分け直し・次の画面による分割の証拠
/// </summary>
public class SplitLifecycleTests
{
    private static List<(long Seq, string State, string Action, string Next, int? Vis)> RandomStream(int n, int seed)
    {
        var rng = new Random(seed); var l = new List<(long, string, string, string, int?)>(); string cur = "C";
        for (int i = 1; i <= n; i++)
        {
            string nx = new[] { "C", "D", "E" }[rng.Next(3)];
            l.Add((i, cur, "button" + rng.Next(3), nx, i < n / 5 ? null : rng.Next(1, 4)));
            cur = rng.NextDouble() < 0.05 ? "E" : nx;   // ときどき状態がつながらない（記録の切れ目）
        }
        return l;
    }

    [Fact]
    public void Indexed_reinterpretation_matches_a_full_scan_reference()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.12.0");
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        var raw = RandomStream(3000, 4);
        foreach (var r in raw) s.AppendStream(run, null, new StreamStep(r.Seq, r.State, r.Action, true, r.Next, 0.1), 1, Array.Empty<BoundaryReason>(), 0, r.Vis);
        // 参照: 全履歴を順に見る素朴な読み直し（v0.10.0 の方法）
        var expect = new Dictionary<long, (string, string)>();
        for (int i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            int? nextVis = i + 1 < raw.Count && raw[i + 1].Seq == r.Seq + 1 && raw[i + 1].State == r.Next ? raw[i + 1].Vis : null;
            string nf = r.State == "C" && r.Vis == 2 ? "C#v2" : r.State, nt = r.Next == "C" && nextVis == 2 ? "C#v2" : r.Next;
            if (nf != r.State || nt != r.Next) expect[r.Seq] = (nf, nt);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rc = s.ReclassifyForSplit("C", 2, "C#v2");
        sw.Stop();
        var got = s.LoadStream(10000).Where(x => x.Reinterpreted).ToDictionary(x => x.Seq, x => (x.State, x.NextState));
        Assert.Equal(expect.Count, got.Count);
        foreach (var (q, v) in expect) Assert.Equal(v, got[q]);
        Assert.Equal(expect.Count, rc.Sum(x => x.N));
        Assert.Contains("idx_stream_state", s.Db.Query("EXPLAIN QUERY PLAN SELECT seq FROM experience_stream WHERE state = 'C'", r => r.GetString(3)).First());
    }

    [Fact]
    public void Split_made_before_reclassification_existed_is_reclassified_once()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.12.0");
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        s.AppendStream(run, null, new StreamStep(1, "m1", "button1", true, "m2", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 7);
        s.AppendStream(run, null, new StreamStep(2, "m2", "button2", true, "m1", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 3);
        s.AppendStream(run, null, new StreamStep(3, "m1", "button1", true, "m2", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 7);
        // 読み直しの機能より前の版が作った分割（済み印なし）
        s.Db.Exec("INSERT INTO state_splits(vis_id, coarse_node, split_node, distance, leader_vis_id, decided_at) VALUES(7,'m1','m1#v7',0.5,6,'x')");
        var pending = s.PendingSplitReclassifications();
        Assert.Equal(new[] { ("m1", 7, "m1#v7") }, pending);
        var rc = s.ReclassifyForSplit("m1", 7, "m1#v7");
        Assert.Equal(3, rc.Sum(x => x.N));   // 手 1・3 の始まり、手 2 の行き先
        Assert.Empty(s.PendingSplitReclassifications());   // 二度は読み直さない
    }

    [Fact]
    public void Migration_marks_splits_already_reclassified_by_v0_10()
    {
        using var d = new TempDir();
        string dir = d["p"];
        using (var s = ExperienceStore.Open(dir, "prof", "1", "0.11.0"))
        {
            long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
            s.AppendStream(run, null, new StreamStep(1, "m1", "button1", true, "m2", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 7);
            s.ReclassifyForSplit("m1", 7, "m1#v7");
            // v8 の状態に戻して移行を確かめる（済み印の列を消した形を作れないので、印を消して移行の UPDATE と同じ式を当てる）
            s.Db.Exec("UPDATE state_splits SET reclassified_at = NULL");
            s.Db.Exec("UPDATE state_splits SET reclassified_at = (SELECT MIN(d.created_at) FROM derived_transitions d WHERE d.split_vis = state_splits.vis_id) WHERE EXISTS (SELECT 1 FROM derived_transitions d WHERE d.split_vis = state_splits.vis_id)");
            Assert.Empty(s.PendingSplitReclassifications());
        }
        Assert.Contains("UPDATE state_splits SET reclassified_at = (SELECT MIN(d.created_at) FROM derived_transitions d WHERE d.split_vis = state_splits.vis_id)",
            File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), "GameKouryakuKun.Storage", "Schemas.cs")));
    }

    [Fact]
    public void Revert_reinterprets_split_records_back_and_resplit_restores_them_without_touching_direct_records()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.12.0");
        long run = s.BeginRun("1", "b", null, policyVersion: "explore-0.3");
        s.AppendStream(run, null, new StreamStep(1, "m1", "button1", true, "m2", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 7);     // 分割の前
        s.ReclassifyForSplit("m1", 7, "m1#v7", 0.6, 6);
        s.AppendStream(run, null, new StreamStep(2, "m2", "button2", true, "m1#v7", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 3);  // 分割の後（直接その名前で記録）
        s.AppendStream(run, null, new StreamStep(3, "m1#v7", "button1", true, "m2", 0.1), 1, Array.Empty<BoundaryReason>(), 0, 7);
        var rv = s.ReclassifyForRevert("m1#v7", 7, "m1");
        s.AddSplitEvent(7, "m1", "m1#v7", "reverted", "next", double.NaN, -3, rv.Sum(x => x.N), "test");
        Assert.Equal(3, rv.Sum(x => x.N));
        var st = s.LoadStream(10);
        Assert.All(st, x => Assert.DoesNotContain("#v7", x.State + x.NextState));
        Assert.Equal("reverted", s.Db.Scalar("SELECT status FROM state_splits WHERE vis_id=7"));
        Assert.Equal(2L, s.Db.Scalar("SELECT COUNT(*) FROM experience_stream WHERE state='m1#v7' OR next_state='m1#v7'"));   // 直接の記録はそのまま
        // 分け直し: 粗いノードとして読み直した手を、もう一度分けたノードとして読む
        s.AddSplitEvent(7, "m1", "m1#v7", "resplit", "next", 0.7, 3, 0, "test");
        Assert.Single(s.PendingSplitReclassifications());
        var rs = s.ReclassifyForSplit("m1", 7, "m1#v7");
        Assert.Equal(3, rs.Sum(x => x.N));
        st = s.LoadStream(10);
        Assert.Equal("m1#v7", st.Single(x => x.Seq == 3).State); Assert.Equal("m1#v7", st.Single(x => x.Seq == 2).NextState);
        Assert.Equal(new[] { "reverted", "resplit" }, s.Db.Query("SELECT kind FROM state_split_events ORDER BY event_id", r => r.GetString(0)));
        Assert.Equal(new[] { ("revert", 1L), ("split", 1L) }.OrderBy(x => x.Item1), s.Db.Query("SELECT cause, COUNT(DISTINCT cause) FROM derived_transitions GROUP BY cause ORDER BY cause", r => (r.GetString(0), r.GetInt64(1))));
    }

    [Fact]
    public void Split_by_next_screen_evidence_and_revert_only_on_strong_evidence_of_sameness()
    {
        var b = new VisualStateBook();
        var (va, _) = b.Assign(Enumerable.Repeat(0.2f, VisualLayout.Length).ToArray(), "m1");
        var (vb, _) = b.Assign(Enumerable.Repeat(0.8f, VisualLayout.Length).ToArray(), "m1");
        // 結果の3通り（いつも別の画面へ）は同じだが、行き先の画面が違う
        foreach (var act in new[] { "button1", "button2" })
            for (int i = 0; i < 6; i++)
            {
                b.ObserveTransition(va, act, true, 50); b.ObserveNext(va, "m1", act, "X");
                b.ObserveTransition(vb, act, true, 50); b.ObserveNext(vb, "m1", act, "Y");
            }
        b.ObserveTransition(va, "button1", true, 50); b.ObserveNext(va, "m1", "button1", "X");
        var made = b.RefreshSplits();
        var sp = Assert.Single(made);
        Assert.Equal("next", sp.EvidenceKind); Assert.False(sp.Resplit);
        // 取り消しの判断: 少ない経験では取り消さない
        var few = new Dictionary<string, Dictionary<string, int>> { ["button1"] = new() { ["Z"] = 3 }, ["button2"] = new() { ["Z"] = 3 }, ["button3"] = new() { ["Z"] = 3 } };
        Assert.False(SplitReview.Review(few, few, 3).Revert);
        var many = new Dictionary<string, Dictionary<string, int>> { ["button1"] = new() { ["Z"] = 30 }, ["button2"] = new() { ["Z"] = 30 }, ["button3"] = new() { ["Z"] = 30 } };
        Assert.True(SplitReview.Review(many, many, 3).Revert);
        var diff = new Dictionary<string, Dictionary<string, int>> { ["button1"] = new() { ["W"] = 30 }, ["button2"] = new() { ["Z"] = 30 }, ["button3"] = new() { ["Z"] = 30 } };
        Assert.False(SplitReview.Review(many, diff, 3).Revert);
        // 取り消した後は、取り消しの後の観測だけで分け直しを判断する（すぐには分け直さない）
        Assert.True(b.Revert(sp.Id));
        Assert.Equal("m1", b.Node("m1", sp.Id));
        Assert.Empty(b.RefreshSplits());
        foreach (var act in new[] { "button1", "button2" }) for (int i = 0; i < 6; i++) b.ObserveNext(sp.Id, "m1", act, "Y");
        var again = Assert.Single(b.RefreshSplits());
        Assert.True(again.Resplit); Assert.Equal(sp.Node, again.Node);
    }
}

internal static class TestPaths
{
    public static string SrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("GameKouryakuKun.sln が見つかりません");
    }
}
