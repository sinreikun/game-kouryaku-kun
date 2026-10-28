namespace GameKouryakuKun.Storage;

/// <summary>
/// Session 用 dual-append。フラグ OFF なら何もしない。ON なら RuntimeKernel で vNext に追記。
/// 失敗は呼び出し側に返し、例外で学習ループを止めない。
/// </summary>
public static class VNextDualAppend
{
    /// <summary>
    /// ベストエフォートで 1 手を vNext へ併記する。
    /// 戻り値 null = 例外で失敗（onError 呼び出し済み）。Skipped は StepResult で返す。
    /// </summary>
    public static StepResult? Try(
        ref RuntimeKernel? kernel,
        string profileDataDir,
        string appVersion,
        ActionSpec action,
        StepContext context,
        Action<Exception>? onError = null)
    {
        if (!RuntimeFlags.UseExperienceVNextWriter)
            return new StepResult(Wrote: false, SkippedBecauseFlagOff: true, ExperienceId: null, Record: null);
        try
        {
            kernel ??= RuntimeKernel.Open(profileDataDir, appVersion);
            return kernel.Step(action, context);
        }
        catch (Exception e)
        {
            onError?.Invoke(e);
            return null;
        }
    }
}
