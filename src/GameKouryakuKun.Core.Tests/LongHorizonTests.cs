using GameKouryakuKun.Core.Growth;
using GameKouryakuKun.Core.LongHorizon;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class LongHorizonTests : IDisposable
{
    public LongHorizonTests()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.LongHorizonSuiteEnvName, null);
        RuntimeFlags.EnableLongHorizonSuite = false;
        RuntimeFlags.ApplyLongHorizonSuiteEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.LongHorizonSuiteEnvName, null);
        RuntimeFlags.EnableLongHorizonSuite = false;
        RuntimeFlags.ApplyLongHorizonSuiteEffective();
    }

    [Fact]
    public void Flag_default_off()
    {
        RuntimeFlags.ApplyLongHorizonSuiteEffective();
        Assert.False(RuntimeFlags.EnableLongHorizonSuite);
        Assert.Contains("OFF", RuntimeFlags.LongHorizonSuiteStatusText());
        Environment.SetEnvironmentVariable(RuntimeFlags.LongHorizonSuiteEnvName, "1");
        RuntimeFlags.ApplyLongHorizonSuiteEffective();
        Assert.True(RuntimeFlags.EnableLongHorizonSuite);
        Assert.Contains("ON", RuntimeFlags.LongHorizonSuiteStatusText());
    }

    [Fact]
    public void Health_builds_safety_and_learning_areas()
    {
        var input = new LongHorizonInput
        {
            PeEnqueued = 12,
            PePending = 3,
            PeStatsSummary = "pe test",
            IntrinsicMixSteps = 40,
            IntrinsicInterrupts = 38,
            IntrinsicBudget = 40,
            IntrinsicBlockedBudget = 2,
            ChampionId = "champion/bootstrap@0",
            RegistrySummary = "registry champ=champion/bootstrap@0",
            RegistryRollbackAvailable = true,
            RegistryCompares = 2,
            SoftAdopts = 1,
            SoftAdoptBudget = 20,
            OuterLoopEnabled = false,
            SaveApiAbsent = true,
            HumanSwitches = 1,
            RecentPeProxies = new[] { 0.6, 0.55, 0.5 },
        };
        var rep = LongHorizonHealth.Build(input);
        Assert.Contains(rep.Checks, c => c.Name == "実験予算" && c.Level == LhLevel.Warn);
        Assert.Contains(rep.Checks, c => c.Name == "Outer入力増禁止" && c.Level == LhLevel.Ok);
        Assert.Contains(rep.Checks, c => c.Name == "セーブ非操作" && c.Level == LhLevel.Ok);
        Assert.Contains(rep.Checks, c => c.Name == "PE傾向");
        Assert.Contains(rep.Checks, c => c.Name == "不可逆らしき遷移" && c.Level == LhLevel.NotTested);
        Assert.Contains("long_horizon", rep.SummaryLine);
        Assert.Equal("display_only; not_reward; stupid_play_not_fail", rep.Note);
    }

    [Fact]
    public void Stupid_play_budget_exhausted_is_warn_not_fail()
    {
        var rep = LongHorizonHealth.Build(new LongHorizonInput
        {
            IntrinsicInterrupts = 40,
            IntrinsicBudget = 40,
            IntrinsicBlockedBudget = 5,
        });
        var budget = rep.Checks.Single(c => c.Name == "実験予算");
        Assert.Equal(LhLevel.Warn, budget.Level);
        Assert.DoesNotContain(rep.Checks, c => c.Level == LhLevel.Fail && c.Name == "実験予算");
    }

    [Fact]
    public void Growth_suite_wraps_snapshots()
    {
        var snaps = new List<GrowthSnapshot>
        {
            new(1, 1, 10, 3, 0, 2, 2, 1, 0, 0, 0, 0, 5, 10, 1.0, 0.1, 0.1, 1, 0.01, 5, 1, 2, 1, new Dictionary<int, int>(), "t") { AppVersion = "0.38.0" },
            new(1, 2, 20, 3, 0, 2, 2, 1, 1, 0, 0, 0, 6, 10, 1.1, 0.1, 0.1, 1, 0.01, 8, 2, 3, 1, new Dictionary<int, int>(), "t") { AppVersion = "0.38.0" },
            new(1, 3, 30, 3, 0, 2, 3, 2, 2, 0, 0, 0, 7, 10, 1.2, 0.1, 0.1, 1, 0.01, 10, 3, 4, 2, new Dictionary<int, int>(), "t") { AppVersion = "0.38.0" },
        };
        var checks = GrowthSuite.Evaluate(new LongHorizonInput { GrowthSnapshots = snaps });
        Assert.Contains(checks, c => c.Name.StartsWith("成長/"));
        Assert.Contains(checks, c => c.Name == "到達/Skill再利用" && c.Detail.Contains("skills_active"));
    }

    [Fact]
    public void Eval_suite_hooks_four_entries()
    {
        Assert.Equal(4, EvalSuiteHooks.All.Count);
        Assert.Contains(EvalSuiteHooks.All, e => e.Id == "synthetic");
        Assert.Contains(EvalSuiteHooks.All, e => e.Id == "replay");
        Assert.Contains(EvalSuiteHooks.All, e => e.Id == "long_run");
        Assert.Contains(EvalSuiteHooks.All, e => e.Id == "principle");
        Assert.Contains("synthetic:", EvalSuiteHooks.DescribeAll());
    }

    [Fact]
    public void Aggregator_reads_registry_store()
    {
        using var d = new TempDir();
        using var reg = ModelRegistryStore.Open(d["p"], "0.38.0");
        Assert.NotNull(reg.ChampionId());
        var input = LongHorizonAggregator.FromProfile(d["p"], outerEnabled: true);
        Assert.Equal(reg.ChampionId(), input.ChampionId);
        Assert.Contains("champ=", input.RegistrySummary);
        var rep = LongHorizonHealth.Build(input);
        Assert.Contains(rep.Checks, c => c.Name == "Outer/Registry");
    }

    [Fact]
    public void Health_parses_long_horizon_logs()
    {
        using var d = new TempDir();
        var logs = d["logs"];
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "app-1.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"long_horizon_snapshot","summary":"long_horizon checks=5 warn=1 fail=0 pe=3 mix=10 champ=c","warn_count":1}""",
            """{"type":"long_horizon_suite","summary":"suite on"}""",
        });
        using var mem = ExperienceStore.Open(d["mem"], "p", "1", "0.38.0");
        using var app = AppStore.Open(d["app"], "0.38.0");
        var r = HealthReport.Build("0.38.0", mem.Db, app.Db, null, new[] { path }, 2, null);
        var area = r.Checks.Where(c => c.Area == "長期自律").ToList();
        Assert.NotEmpty(area);
        Assert.Contains(area, c => c.Name.Contains("Snapshot"));
    }

    [Fact]
    public void Contract_and_audit_exist()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s8-long-horizon-autonomy-contract.md"));
        Assert.Contains("LongHorizonHealth", text);
        Assert.Contains("GrowthSuite", text);
        Assert.Contains("EvalSuiteHooks", text);
        Assert.Contains("GKK_LONG_HORIZON_SUITE", text);
        Assert.Contains("トルネコをクリア", text);
        Assert.Contains("馬鹿プレイ", text);
        Assert.DoesNotContain("しきい値を緩めて合格", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.38.0.md")));
        var fa = File.ReadAllText(Path.Combine(root, "future-architecture.md"));
        Assert.Contains("v0.38.0", fa);
        Assert.Contains("S8 クローズ可", fa);
        Assert.Contains("親判断", fa);
    }

    [Fact]
    public void Session_wires_long_horizon()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var ses = File.ReadAllText(Path.Combine(d!.Parent!.FullName, "src", "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("TryEmitLongHorizonSnapshot", ses);
        Assert.Contains("LongHorizonHealth.Build", ses);
        Assert.Contains("long_horizon_snapshot", ses);
    }
}
