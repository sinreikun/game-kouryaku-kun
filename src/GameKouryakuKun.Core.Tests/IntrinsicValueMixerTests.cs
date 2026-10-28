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
        var causal = new CausalComparisonRecord
        {
            Preferred = "undecided",
            PeRoutingTags = new[] { PeRoutingTags.RepresentationCoarse },
            Hypotheses = CausalHypothesisIds.All.Select(id => new HypothesisScore { Id = id, Status = "not_observed" }).ToList(),
        };
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
            """{"type":"intrinsic_mix","summary":"intrinsic_mix mode=parallel_log top=[a:0.40]","mode":"parallel_log","top":"a"}""",
            """{"type":"active_experiment","status":"proposed","action":"a","gain":0.2,"applied":true}""",
        });
        using var mem = ExperienceStore.Open(d["mem"], "p", "1", "0.35.0");
        using var app = AppStore.Open(d["app"], "0.35.0");
        var r = HealthReport.Build("0.35.0", mem.Db, app.Db, null, new[] { path }, 2, null);
        var area = r.Checks.Where(c => c.Area == "内発価値").ToList();
        Assert.NotEmpty(area);
        Assert.Contains(area, c => c.Name.Contains("ポートフォリオ"));
        Assert.Contains(area, c => c.Name.Contains("能動実験") && c.Detail.Contains("applied"));
        Assert.DoesNotContain(r.Text, "キャラ");
    }

    [Fact]
    public void Contract_and_audit_exist()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var root = d!.Parent!.FullName;
        var text = File.ReadAllText(Path.Combine(root, "docs", "s5-intrinsic-active-exp-contract.md"));
        Assert.Contains("IntrinsicValuePortfolio", text);
        Assert.Contains("GKK_USE_INTRINSIC_MIXER", text);
        Assert.Contains("ActiveExperiment", text);
        Assert.Contains("正解キー", text);
        Assert.True(File.Exists(Path.Combine(root, "src", "audit", "v0.35.0.md")));
    }
}
