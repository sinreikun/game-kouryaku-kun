using GameKouryakuKun.Core.Boundaries;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.23.0 ブロック C: エピソードの区切りの候補</summary>
public class BlockV023BoundaryTests
{
    // 3 種類の前の状態からそれぞれ「大きな遷移」で B に入り、その後は B→C→D→E とくり返す。それ以外は小さな変化
    private static List<StreamStep> Stream(int loops, bool large = true)
    {
        var xs = new List<StreamStep>(); long q = 0; string cur = "S";
        void Go(string to, double r) { xs.Add(new StreamStep(++q, cur, "a", true, to, r)); cur = to; }
        for (int i = 0; i < loops; i++)
        {
            string p = "P" + (i % 3);
            Go(p, 0.01); Go(p + "x", 0.02);
            Go("B", large ? 0.9 : 0.01);
            Go("C", 0.02); Go("D", 0.03); Go("E", 0.02); Go("S", 0.01);
        }
        return xs;
    }

    [Fact]
    public void Recurring_flow_after_large_transition_from_many_contexts_is_a_candidate()
    {
        var r = new FlowResetMiner().Mine(Stream(12), new HashSet<long>());
        Assert.Contains(r.Accepted, c => c.State == "B");
        var b = r.Accepted.Single(c => c.State == "B");
        Assert.Equal("C → D → E", b.Flow);
        Assert.True(b.Predecessors >= 3);
    }

    [Fact]
    public void Small_transitions_are_not_candidates()
    {
        // 大きな遷移がなければ同じ流れでも候補にしない（閾値は分布から: 上位 4 分の 1。全部小さければ B は上位に入らない）
        var r = new FlowResetMiner().Mine(Stream(12, large: false), new HashSet<long>());
        Assert.DoesNotContain(r.Accepted, c => c.State == "B");
    }

    [Fact]
    public void Restart_is_not_a_boundary()
    {
        // 流れが毎回 切れ目（再起動・復旧）をまたぐなら、到着として数えない
        var xs = Stream(12);
        var breaks = new HashSet<long>(xs.Where(s => s.State == "B").Select(s => s.Seq));   // B の後の最初の手の前で毎回切れている
        var r = new FlowResetMiner().Mine(xs, breaks);
        Assert.DoesNotContain(r.Accepted, c => c.State == "B");
    }

    [Fact]
    public void Candidates_are_recorded_with_events_and_withdrawn_without_deleting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gkk-ep-" + Guid.NewGuid().ToString("N"));
        using var mem = ExperienceStore.Open(dir, "p", "1", "t");
        var m = new FlowResetMiner();
        var ev1 = mem.SyncBoundaryCandidates(m.Mine(Stream(12), new HashSet<long>()), 10);
        Assert.Contains(ev1, e => e.State == "B" && e.Kind == "proposed");
        var ev2 = mem.SyncBoundaryCandidates(m.Mine(Stream(12, large: false), new HashSet<long>()), 20);
        Assert.Contains(ev2, e => e.State == "B" && e.Kind == "withdrawn");
        Assert.Equal(1L, Convert.ToInt64(mem.Db.Scalar("SELECT COUNT(*) FROM episode_boundary_candidates WHERE state='B' AND status='withdrawn'")));
        var ev3 = mem.SyncBoundaryCandidates(m.Mine(Stream(12), new HashSet<long>()), 30);
        Assert.Contains(ev3, e => e.State == "B" && e.Kind == "reproposed");
    }

    /// <summary>候補は報酬・価値・選択に使わない: 攻略 AI のコードから参照されない</summary>
    [Fact]
    public void Boundary_candidates_are_not_used_by_learning_code()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GameKouryakuKun.sln"))) root = root.Parent;
        var core = Path.Combine(root!.FullName, "GameKouryakuKun.Core");
        foreach (var dd in new[] { "Exploration", "Goals", "Turn", "Perception" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(core, dd), "*.cs", SearchOption.AllDirectories))
            {
                var t = File.ReadAllText(f);
                Assert.DoesNotContain("FlowResetMiner", t);
                Assert.DoesNotContain("episode_boundary", t);
            }
        // 死亡・クリアなどの定義を持たない
        var miner = File.ReadAllText(Path.Combine(core, "Boundaries", "FlowResetMiner.cs"));
        foreach (var w in new[] { "death", "dead", "gameover", "game over", "clear(", "\"死", "ゲームオーバー" }) Assert.DoesNotContain(w, miner, StringComparison.OrdinalIgnoreCase);
    }
}
