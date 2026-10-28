namespace GameKouryakuKun.Core.LongHorizon;

/// <summary>
/// §28.2 Evaluation suite の入口（薄い・文書化固定）。プロセス起動はしない。
/// </summary>
public static class EvalSuiteHooks
{
    public const string HooksId = "eval_suite_hooks@1";

    public sealed record Entry(string Id, string Name, string Description, IReadOnlyList<string> TestOrPathHints);

    public static Entry SyntheticEntry { get; } = new(
        "synthetic",
        "合成世界",
        "因果・追跡・遅延・重要度・能動実験・Outer（意味ラベルは試験側のみ）",
        new[]
        {
            "GameKouryakuKun.Core.Tests.PeClosedLoopTests",
            "GameKouryakuKun.Core.Tests.FeatureImportanceTests",
            "GameKouryakuKun.Core.Tests.IntrinsicValueMixerTests",
            "GameKouryakuKun.Core.Tests.StructuredGoalsTests",
            "GameKouryakuKun.Core.Tests.OuterLoopTests",
            "GameKouryakuKun.Core.Tests.LongHorizonTests",
        });

    public static Entry ReplayEntry { get; } = new(
        "replay",
        "Replay",
        "実機診断の非破壊再生（GT・追跡・欠測）。Adapter / 診断 zip",
        new[]
        {
            "GameKouryakuKun.Core.Tests.OldExperienceAdapterTests",
            "docs: AppEnv 診断 zip / pe-closed-loop・intrinsic-stats・model-registry 同梱",
        });

    public static Entry LongRunEntry { get; } = new(
        "long_run",
        "長期運転",
        "起動またぎで Champion 悪化しない・GrowthEvaluator + LongHorizonHealth",
        new[]
        {
            "GameKouryakuKun.Core.Growth.GrowthEvaluator",
            "GameKouryakuKun.Core.LongHorizon.LongHorizonHealth",
            "GameKouryakuKun.Core.LongHorizon.GrowthSuite",
            "GameKouryakuKun.Core.Tests.BlockV022GrowthTests",
            "GameKouryakuKun.Core.Tests.LongHorizonTests",
        });

    public static Entry PrincipleRegressionEntry { get; } = new(
        "principle",
        "原則回帰",
        "ConstraintAudit / ForbiddenApi / Profile 境界",
        new[]
        {
            "ConstraintAudit",
            "ForbiddenApi",
            "episode_boundary",
            "docs/s8-long-horizon-autonomy-contract.md",
        });

    public static IReadOnlyList<Entry> All { get; } = new[]
    {
        SyntheticEntry, ReplayEntry, LongRunEntry, PrincipleRegressionEntry,
    };

    /// <summary>薄いランナー: 入口が揃っていることだけを返す（実テスト実行は xunit）。</summary>
    public static string DescribeAll() =>
        string.Join(" | ", All.Select(e => $"{e.Id}:{e.Name}({e.TestOrPathHints.Count} hints)"));
}
