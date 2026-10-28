using GameKouryakuKun.Core.Causality;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Intrinsic;
using GameKouryakuKun.Core.Observation;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

[Collection("RuntimeFlagsSerial")]
public class IntrinsicValueMixerTests : IDisposable
{
    public IntrinsicValueMixerTests()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.IntrinsicMixerEnvName, null);
        RuntimeFlags.UseIntrinsicMixer = false;
        RuntimeFlags.ApplyIntrinsicMixerEffective();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeFlags.IntrinsicMixerEnvName, null);
        RuntimeFlags.UseIntrinsicMixer = false;
        RuntimeFlags.ApplyIntrinsicMixerEffective();
    }

    private static CandidateScore Cand(string a, double novelty, double disc, double leave, double change, double info, double unc, double rev, int tries = 0) =>
        new(a, novelty + disc + leave + change + info + unc, 0, tries, novelty, disc + leave + change, info, unc, rev, false, false, Array.Empty<string>())
        {
            Discovery = disc, Leave = leave, Change = change, Text = 0, InfoTerm = info, UncTerm = unc,
        };

    private static CausalComparisonRecord UndecidedCausal() => new()
    {
        Preferred = "undecided",
        PeRoutingTags = new[] { PeRoutingTags.RepresentationCoarse },
        Hypotheses = CausalHypothesisIds.All.Select(id => new HypothesisScore { Id = id, Status = "not_observed" }).ToList(),
    };

    [Fact]
    public void Mixer_wraps_candidates_with_portfolio_components()
    {
        var cands = new[]
        {
            Cand("button1", novelty: 2, disc: 0.5, leave: 0.2, change: 0.4, info: 0.3, unc: 0.2, rev: 0.8),
            Cand("wait", novelty: 1, disc: 0, leave: 0, change: 0, info: 0.1, unc: 0.5, rev: 1.0),
        };
        var report = IntrinsicValueMixer.Mix(new IntrinsicMixInput(cands, Step: 1));
        Assert.Equal(2, report.Scores.Count);
        var b = report.Scores.Single(s => s.Action == "button1");
        Assert.Contains(IntrinsicComponents.InfoGainPe, b.Components.Keys);
        Assert.Contains(IntrinsicComponents.Controllability, b.Components.Keys);
        Assert.Equal(0, b.Components[IntrinsicComponents.SkillWmLongTerm]); // stub
        Assert.True(b.Mixed > 0);
        Assert.Contains("not_implemented", b.Note!);
        Assert.DoesNotContain("キャラ", report.SummaryLine());
        Assert.Equal("button1", report.TopMixedAction);
    }

    [Fact]
    public void Active_experiment_proposes_when_causal_undecided()
    {
        var cands = new[]
        {
            Cand("a", 2, 0.8, 0.3, 0.5, 0.4, 0.4, 0.7, tries: 0),
            Cand("b", 0.5, 0.1, 0, 0.1, 0.05, 0.05, 0.5, tries: 10),
            Cand("wait", 1, 0, 0, 0, 0.2, 0.2, 1, tries: 1),
        };
        var causal = UndecidedCausal();
        var report = IntrinsicValueMixer.Mix(new IntrinsicMixInput(cands, Step: 2, Causal: causal, PeTags: causal.PeRoutingTags));
        Assert.NotNull(report.Experiment);
        Assert.Equal("proposed", report.Experiment!.Status);
        Assert.Equal("a", report.Experiment.Action); // high novelty+ctrl, not wait
        Assert.True(report.Experiment.ExpectedInfoGain >= ActiveExperimentPlanner.MinExpectedInfoGain);
        Assert.DoesNotContain("正解", report.Experiment.Note ?? "");
    }

    [Fact]
    public void Active_experiment_budget_exhausted()
    {
        var cands = new[] { Cand("a", 2, 0.8, 0.3, 0.5, 0.4, 0.4, 0.7) };
        var causal = new CausalComparisonRecord
        {
            Preferred = "undecided",
            PeRoutingTags = Array.Empty<string>(),
            Hypotheses = CausalHypothesisIds.All.Select(id => new HypothesisScore { Id = id, Status = "not_observed" }).ToList(),
        };
        var report = IntrinsicValueMixer.Mix(new IntrinsicMixInput(
            cands, Causal: causal, ExperimentInterruptsUsed: ActiveExperimentBudget.DefaultMaxInterrupts));
        Assert.Equal("budget_exhausted", report.Experiment!.Status);
    }

    [Fact]
    public void Soft_interrupt_block_cooldown_and_window()
    {
        var cands = new[] { Cand("a", 2, 0.8, 0.3, 0.5, 0.4, 0.4, 0.7) };
        var causal = UndecidedCausal();
        // SoftInterruptBlock short-circuits to blocked
        var blocked = IntrinsicValueMixer.Mix(new IntrinsicMixInput(
            cands, Step: 10, Causal: causal, SoftInterruptBlock: "cooldown"));
        Assert.Equal("blocked", blocked.Experiment!.Status);
        Assert.Contains("cooldown", blocked.Experiment.Note!);

        Assert.Equal("budget", ActiveExperimentBudget.BlockReason(
            ActiveExperimentBudget.DefaultMaxInterrupts, ActiveExperimentBudget.DefaultMaxInterrupts,
            100, null, Array.Empty<long>(), null, "a"));
        Assert.Equal("cooldown", ActiveExperimentBudget.BlockReason(
            1, ActiveExperimentBudget.DefaultMaxInterrupts, 12, lastInterruptStep: 10,
            new long[] { 10 }, "a", "a"));
        Assert.Equal("window", ActiveExperimentBudget.BlockReason(
            3, ActiveExperimentBudget.DefaultMaxInterrupts, 40, lastInterruptStep: 30,
            new long[] { 5, 15, 25 }, "x", "y"));
        Assert.Null(ActiveExperimentBudget.BlockReason(
            0, ActiveExperimentBudget.DefaultMaxInterrupts, 100, null, Array.Empty<long>(), null, "a"));
    }

    [Fact]
    public void Contribution_stats_note_chosen_and_aftermath()
    {
        var stats = new IntrinsicContributionStats();
        var score = new IntrinsicActionScore
        {
            Action = "a",
            Mixed = 0.5,
            Components = new Dictionary<string, double>
            {
                [IntrinsicComponents.InfoGainPe] = 0.9,
                [IntrinsicComponents.Controllability] = 0.1,
            },
        };
        stats.NoteMixStep();
        stats.NoteChosen(score);
        stats.NoteInterruptApplied(1, "a", "undecided", new[] { PeRoutingTags.RepresentationCoarse });
        stats.ObserveAftermath(5, "h1_immediate", Array.Empty<string>());
        Assert.Equal(1, stats.MixSteps);
        Assert.Equal(1, stats.InterruptsApplied);
        Assert.Equal(1, stats.AftermathResolved);
        Assert.Equal(1, stats.AmbiguityFellAfter);
        Assert.Equal(1, stats.ComponentChosenCounts()[IntrinsicComponents.InfoGainPe]);
        Assert.Contains("interrupts=1", stats.SummaryLine());
    }

    [Fact]
    public void Intrinsic_stats_store_persists()
    {
        using var d = new TempDir();
        var stats = new IntrinsicContributionStats();
        stats.NoteMixStep();
        stats.NoteChosen(new IntrinsicActionScore
        {
            Action = "a", Mixed = 0.4,
            Components = new Dictionary<string, double> { [IntrinsicComponents.ReachUnknown] = 0.7 },
        });
        stats.NoteInterruptApplied(2, "a", "undecided", new[] { "representation_coarse" });
        stats.NoteBlocked("cooldown");
        using (var store = IntrinsicStatsStore.Open(d["p"], "0.35.1"))
        {
            store.Persist(stats);
            Assert.Contains("interrupts_applied=1", store.StatsSummary());
            Assert.Contains("reach_unknown:1", store.StatsSummary());
        }
        Assert.True(File.Exists(IntrinsicStatsStore.DefaultPath(d["p"])));
    }

    [Fact]
    public void Flag_default_off()
    {
        RuntimeFlags.ApplyIntrinsicMixerEffective();
        Assert.False(RuntimeFlags.UseIntrinsicMixer);
        Environment.SetEnvironmentVariable(RuntimeFlags.IntrinsicMixerEnvName, "1");
        RuntimeFlags.ApplyIntrinsicMixerEffective();
        Assert.True(RuntimeFlags.UseIntrinsicMixer);
        Assert.Contains("ON", RuntimeFlags.IntrinsicMixerStatusText());
    }

    [Fact]
    public void Health_parses_intrinsic_logs()
    {
        using var d = new TempDir();
        var logs = d["logs"];
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "app-1.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"intrinsic_mix","summary":"intrinsic_mix mode=parallel_log top=[a:0.40]","mode":"parallel_log","top":"a","stats":"intrinsic_stats mix=3 interrupts=1 blocked[b0/c0/w0/g0] aftermath=0 fell=0 top_comp=[info_gain_pe:1]"}""",
            """{"type":"active_experiment","status":"proposed","action":"a","gain":0.2,"applied":true,"stats":"intrinsic_stats mix=3 interrupts=1"}""",
            """{"type":"active_experiment","status":"blocked","action":"a","applied":false,"block":"cooldown"}""",
        });
        using var mem = ExperienceStore.Open(d["mem"], "p", "1", "0.35.1");
        using var app = AppStore.Open(d["app"], "0.35.1");
        var r = HealthReport.Build("0.35.1", mem.Db, app.Db, null, new[] { path }, 2, null);
        var area = r.Checks.Where(c => c.Area == "内発価値").ToList();
        Assert.NotEmpty(area);
        Assert.Contains(area, c => c.Name.Contains("ポートフォリオ"));
        Assert.Contains(area, c => c.Name.Contains("能動実験") && c.Detail.Contains("applied") && c.Detail.Contains("blocked"));
        Assert.Contains(area, c => c.Name.Contains("寄与") && c.Detail.Contains("interrupts=1"));
        Assert.DoesNotContain(r.Text, "キャラ");
    }

    [Fact]
    public void Contract_and_audit_exist_s5_closable()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s5-intrinsic-active-exp-contract.md"));
        Assert.Contains("IntrinsicValuePortfolio", text);
        Assert.Contains("GKK_USE_INTRINSIC_MIXER", text);
        Assert.Contains("ActiveExperiment", text);
        Assert.Contains("正解キー", text);
        Assert.Contains("CooldownSteps", text);
        Assert.Contains("intrinsic-stats.sqlite", text);
        Assert.Contains("S5 クローズ", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.35.0.md")));
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.35.1.md")));
        var fa = File.ReadAllText(Path.Combine(root, "future-architecture.md"));
        Assert.Contains("S5 クローズ可", fa);
        Assert.Contains("次 = S6", fa);
    }

    [Fact]
    public void Session_source_guards_goal_and_budget()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var ses = File.ReadAllText(Path.Combine(d!.Parent!.FullName, "src", "GameKouryakuKun.App", "Session.cs"));
        Assert.Contains("TryApplyIntrinsicMixer", ses);
        Assert.Contains("choice.Rule is \"human\" or \"goal\" or \"sequence_step\"", ses);
        Assert.Contains("ActiveExperimentBudget.BlockReason", ses);
        Assert.Contains("FlushIntrinsicStats", ses);
        Assert.Contains("IntrinsicStats.ObserveAftermath", ses);
        // Goal path must not call mixer
        Assert.Contains("Goal 経路は割込み禁止", ses);
    }
}
