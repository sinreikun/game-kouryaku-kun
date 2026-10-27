using GameKouryakuKun.Core.Growth;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.22.0 ブロック B: 成長テストの計測と自動判定</summary>
public class BlockV022GrowthTests
{
    private static GrowthSnapshot S(long run, long seq, string why = "periodic", int h = 2, double vr = 0.5, double vt = 0.5, int created = 0, int achieved = 0,
        int wc = 0, int wa = 0, int states = 5, int total = 10, int rg = 0, int ra = 0, double om = 0) =>
        new(run, seq, (int)seq, h, 0, 1, 2, 1, 0, 0, rg, ra, states, total, 1.0, vr, vt, 1, om, created, achieved, wc, wa, new Dictionary<int, int> { [1] = 2, [3] = 1 }, why);

    private static GrowthLevel L(List<GrowthCheck> cs, string prefix) => cs.Single(c => c.Name.Contains(prefix)).Level;

    [Fact]
    public void Too_few_snapshots_are_not_tested()
    {
        var cs = GrowthEvaluator.Evaluate(new[] { S(1, 1) });
        Assert.Equal(GrowthLevel.NotTested, L(cs, "Horizon"));
        Assert.Equal(GrowthLevel.NotTested, L(cs, "引き継ぎ"));
        Assert.Equal(GrowthLevel.Pass, L(cs, "整合"));
    }

    [Fact]
    public void Inconsistent_records_fail()
    {
        var cs = GrowthEvaluator.Evaluate(new[] { S(1, 1), S(1, 2, rg: 1, ra: 2), S(1, 3) });
        Assert.Equal(GrowthLevel.Fail, L(cs, "整合"));
    }

    [Fact]
    public void Continuity_across_restart_passes_when_state_carried_and_fails_when_lost()
    {
        var ok = GrowthEvaluator.Evaluate(new[] { S(1, 1, "start"), S(1, 2), S(1, 3, "end", h: 3, vr: 0.2), S(2, 4, "start", h: 3, vr: 0.2), S(2, 5, "end", h: 3, vr: 0.2) });
        Assert.Equal(GrowthLevel.Pass, L(ok, "引き継ぎ"));
        var bad = GrowthEvaluator.Evaluate(new[] { S(1, 1, "start"), S(1, 3, "end", h: 3, vr: 0.2), S(2, 4, "start", h: 2, vr: 0.5), S(2, 5, "end") });
        Assert.Equal(GrowthLevel.Fail, L(bad, "引き継ぎ"));
        var lost = GrowthEvaluator.Evaluate(new[] { S(1, 1, "start"), S(1, 3, "end", total: 20), S(2, 4, "start", total: 10), S(2, 5, "end") });
        Assert.Equal(GrowthLevel.Fail, L(lost, "引き継ぎ"));
    }

    [Fact]
    public void Frozen_value_model_warns_but_changing_values_pass_regardless_of_direction()
    {
        var frozen = GrowthEvaluator.Evaluate(new[] { S(1, 1, created: 0), S(1, 2, created: 10), S(1, 3, created: 30) });
        Assert.Equal(GrowthLevel.Warn, L(frozen, "価値"));
        // 価値が下がっても PASS（大きいほど良いとしない）
        var down = GrowthEvaluator.Evaluate(new[] { S(1, 1, created: 0), S(1, 2, created: 10, vr: 0.4), S(1, 3, created: 30, vr: 0.1) });
        Assert.Equal(GrowthLevel.Pass, L(down, "価値"));
    }

    [Fact]
    public void Horizon_flapping_warns_and_steady_or_shrinking_passes()
    {
        var flap = GrowthEvaluator.Evaluate(Enumerable.Range(1, 6).Select(i => S(1, i, h: i % 2 == 0 ? 2 : 3)).ToList());
        Assert.Equal(GrowthLevel.Warn, L(flap, "Horizon"));
        var shrink = GrowthEvaluator.Evaluate(Enumerable.Range(1, 6).Select(i => S(1, i, h: i < 4 ? 3 : 2)).ToList());
        Assert.Equal(GrowthLevel.Pass, L(shrink, "Horizon"));
    }

    [Fact]
    public void Short_term_achievement_only_flags_zero()
    {
        Assert.Equal(GrowthLevel.Warn, L(GrowthEvaluator.Evaluate(new[] { S(1, 1, wc: 6), S(1, 2, wc: 6), S(1, 3) }), "短期"));
        Assert.Equal(GrowthLevel.Pass, L(GrowthEvaluator.Evaluate(new[] { S(1, 1, wc: 6, wa: 1), S(1, 2, wc: 6), S(1, 3) }), "短期"));
        Assert.Equal(GrowthLevel.NotTested, L(GrowthEvaluator.Evaluate(new[] { S(1, 1, wc: 2), S(1, 2), S(1, 3) }), "短期"));
    }

    [Fact]
    public void Entropy_of_visits()
    {
        Assert.Equal(0, GrowthSnapshot.Entropy(new[] { 5 }));
        Assert.Equal(2, GrowthSnapshot.Entropy(new[] { 1, 1, 1, 1 }), 6);
        Assert.Equal(0, GrowthSnapshot.Entropy(Array.Empty<int>()));
    }

    [Fact]
    public void Snapshots_round_trip_through_db_and_health_report()
    {
        using var d = new TempDir();
        using var mem = ExperienceStore.Open(d["p"], "t", "0.1.0", "0.2.0");
        var a = S(7, 3, "start", h: 4, vr: 0.123456);
        mem.AppendGrowthSnapshot(a); mem.AppendGrowthSnapshot(S(7, 4)); mem.AppendGrowthSnapshot(S(7, 5, "end"));
        var back = mem.LoadGrowthSnapshots();
        Assert.Equal(3, back.Count);
        Assert.Equal(4, back[0].Horizon); Assert.Equal(0.123456, back[0].ValueReach, 6); Assert.Equal(1, back[0].PlanCandidatesByLength[3]);
        Assert.Equal("start", back[0].Reason);
    }

    /// <summary>計測は選択に使わない: Growth の型は攻略 AI のコードから参照されない</summary>
    [Fact]
    public void Growth_types_are_not_used_by_learning_code()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GameKouryakuKun.sln"))) root = root.Parent;
        var core = Path.Combine(root!.FullName, "GameKouryakuKun.Core");
        foreach (var dd in new[] { "Exploration", "Goals", "Turn", "Perception" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(core, dd), "*.cs", SearchOption.AllDirectories))
                Assert.DoesNotContain("GrowthSnapshot", File.ReadAllText(f));
    }
}

/// <summary>v0.22.1: 実機 v0.21.0 の診断の反映</summary>
public class BlockV0221Tests
{
    [Fact]
    public void Step_history_counts_steps_without_separators()
    {
        var h = new Viewer.StepHistory();
        h.Add(Viewer.StepSummary.Separator(1, "PROCESS EXIT"));
        Assert.Equal(1, h.Count); Assert.Equal(0, h.StepCount);
    }

    [Fact]
    public void Recovery_keeps_calibration_unless_resolution_changes_and_rejects_moving_screen_noise()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GameKouryakuKun.sln"))) root = root.Parent;
        var session = File.ReadAllText(Path.Combine(root!.FullName, "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("(fr0.Width, fr0.Height) != (calW, calH)", session);
        Assert.Contains("c1.Criteria.ChangeRatio <= MaxNoiseRatio", session);
        var pc = File.ReadAllText(Path.Combine(root.FullName, "GameKouryakuKun.Supervisor", "ProcessControl.cs"));
        Assert.Contains("EnableRaisingEvents = true", pc);
    }

    [Fact]
    public void Health_accepts_old_logs_with_separator_rows_and_flags_nochange_with_node_change()
    {
        // v0.21.0 の実機ログの形（区切り 10 行を含むサマリー 150・手数 140）
        string Mk(string extra) => $$"""{"t":"x","type":"session_end","reason":"user_stop","steps":142,"turns_aborted":2,"input_sent":1,"agent_steps":130,"human_steps":10,"summaries":150,"growth_points":140,"sup_events":20{{extra}}}""";
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "app-1.jsonl"), Mk("") + "\n");
        using var d = new TempDir();
        using var mem = ExperienceStore.Open(d["p"], "t", "0.1.0", "0.2.0");
        using var app = SqliteDb.Open(Path.Combine(d["a"], "app.sqlite"), "meta", Schemas.App, "t");
        var r = HealthReport.Build("t", mem.Db, app, null, new[] { Path.Combine(dir, "app-1.jsonl") }, 2, null);
        Assert.DoesNotContain(r.Checks, c => c.Name.StartsWith("1手サマリー") && c.Level == HealthReport.Level.Fail);
        File.WriteAllText(Path.Combine(dir, "app-1.jsonl"), Mk(""","system_separators":10,"nochange_node_changed":60""").Replace("\"summaries\":150", "\"summaries\":140") + "\n");
        r = HealthReport.Build("t", mem.Db, app, null, new[] { Path.Combine(dir, "app-1.jsonl") }, 2, null);
        Assert.Contains(r.Checks, c => c.Name.StartsWith("画面の種類が変わったのに") && c.Level == HealthReport.Level.Warn);
    }
}
