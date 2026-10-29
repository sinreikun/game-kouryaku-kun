using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>
/// docs/throughput-parallel-sensing-design.md §9 の回帰: Prediction / Causality / Replay / Continual Learning / Outer の情報品質を落とさない配線になっているか（ソースの検査）
/// </summary>
public class ThroughputRegressionTests
{
    private static string Src()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        return d!.FullName;
    }
    private static string Session() => File.ReadAllText(Path.Combine(Src(), "GameKouryakuKun.App", "Session.cs"));

    [Fact]
    public void Flags_off_keeps_one_action_one_complete_observation()
    {
        var s = Session();
        // v0.41.0: 手ごとの設定（交互でなければ実行全体のフラグそのもの）
        Assert.Contains("if (_stepCfg.PipelineSensing) { _pendingObs = obsTask; _pendingObsStep = step; }\n            else obsTask.GetAwaiter().GetResult();", s.Replace("\r\n", "\n"));
        Assert.Contains("_stepCfg = new GameKouryakuKun.Core.Throughput.ThroughputFlags.FlagSet(\"run\", GameKouryakuKun.Core.Throughput.ThroughputFlags.StepTiming, GameKouryakuKun.Core.Throughput.ThroughputFlags.PipelineSensing", s);
    }

    [Fact]
    public void Previous_observation_is_completed_before_next_observation_starts()
    {
        var s = Session();
        int wait = s.IndexOf("double pw0 = MonoClock.Now; _pendingObs.GetAwaiter().GetResult(); _pendingObs = null;", StringComparison.Ordinal);
        int start = s.IndexOf("var obsTask = ReadTextEachStep(", StringComparison.Ordinal);
        Assert.True(wait > 0 && start > wait, "前の手の観測の回収が次の観測の開始より前にない（文字の差分の順序が壊れる）");
        Assert.Contains("// drain（§7.2-4）", s);
    }

    [Fact]
    public void Prediction_error_uses_prediction_captured_at_observation_start()
    {
        var s = Session();
        Assert.Contains("var worldPred = _lastWorldPred;", s);
        Assert.Contains("(worldPred ?? _lastWorldPred) is { } wp", s);
        Assert.DoesNotContain("if (_lastWorldPred is { } wp", s);
    }

    [Fact]
    public void Continual_learning_commit_path_unchanged_and_runs_regardless_of_flags()
    {
        var s = Session();
        Assert.Equal(1, CountOf(s, "_env.Memory.CommitTurn(RunId.Value, new TurnCommit("));
        int commit = s.IndexOf("_env.Memory.CommitTurn(RunId.Value", StringComparison.Ordinal);
        int raw = s.IndexOf("_rawAgg.Append(", StringComparison.Ordinal);
        Assert.True(raw > commit, "Raw ストアは memory への記録の後に追加するだけ");
    }

    [Fact]
    public void Adaptive_settle_touches_only_nochange_timeout()
    {
        var s = Session();
        Assert.Contains("det.Options = det.Options with { NoChangeTimeoutMs = nc };", s);
        Assert.DoesNotContain("StableMs = nc", s);
        var core = File.ReadAllText(Path.Combine(Src(), "GameKouryakuKun.Core", "Throughput", "AdaptiveSettle.cs"));
        Assert.Contains("Math.Min(globalMs,", core);   // 全体の値より長くしない＝短くするだけ、かつ下限あり
    }

    [Fact]
    public void Noise_model_is_annotation_only()
    {
        var s = Session();
        int i = s.IndexOf("private void ObserveNoise(", StringComparison.Ordinal);
        Assert.True(i > 0);
        var body = s.Substring(i, s.IndexOf("\n    }\n", i, StringComparison.Ordinal) - i);
        Assert.DoesNotContain("explorer", body);
        Assert.DoesNotContain("model.", body);
        Assert.DoesNotContain("_env.Memory", body);
    }

    private static int CountOf(string s, string sub) { int n = 0, i = 0; while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; } return n; }
}
