using GameKouryakuKun.Core.Intrinsic;

namespace GameKouryakuKun.Storage;

/// <summary>S5: profile 配下 intrinsic-stats.sqlite（寄与・割込み統計。memory.sqlite 非変更）。</summary>
public sealed class IntrinsicStatsStore : IDisposable
{
    public const string FileName = "intrinsic-stats.sqlite";
    public SqliteDb Db { get; }
    public string Path { get; }

    private IntrinsicStatsStore(SqliteDb db, string path) { Db = db; Path = path; }

    public static string DefaultPath(string profileDataDir) => System.IO.Path.Combine(profileDataDir, FileName);

    public static IntrinsicStatsStore Open(string profileDataDir, string appVersion = "0.35.1")
    {
        var path = DefaultPath(profileDataDir);
        Directory.CreateDirectory(profileDataDir);
        var db = SqliteDb.Open(path, Schemas.IntrinsicStatsMetaTable, Schemas.IntrinsicStats, appVersion);
        return new IntrinsicStatsStore(db, path);
    }

    public void Persist(IntrinsicContributionStats stats)
    {
        var sums = stats.ComponentSumWhenChosen();
        foreach (var (k, v) in stats.ComponentChosenCounts())
        {
            double sum = sums.GetValueOrDefault(k);
            Db.Exec(
                "INSERT INTO intrinsic_component_stats(component, chosen_count, sum_when_chosen) VALUES($c,$n,$s) " +
                "ON CONFLICT(component) DO UPDATE SET chosen_count=$n, sum_when_chosen=$s",
                null, ("$c", k), ("$n", v), ("$s", sum));
        }
        void Put(string key, long val) =>
            Db.Exec("INSERT INTO intrinsic_interrupt_stats(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v",
                null, ("$k", key), ("$v", val));
        Put("mix_steps", stats.MixSteps);
        Put("interrupts_applied", stats.InterruptsApplied);
        Put("blocked_budget", stats.BlockedBudget);
        Put("blocked_cooldown", stats.BlockedCooldown);
        Put("blocked_window", stats.BlockedWindow);
        Put("blocked_guard", stats.BlockedGuard);
        Put("aftermath_resolved", stats.AftermathResolved);
        Put("ambiguity_fell_after", stats.AmbiguityFellAfter);
        foreach (var (tag, n) in stats.TagBeforeCounts())
            Db.Exec("INSERT INTO intrinsic_tag_contrast(phase, tag, count) VALUES('before',$t,$n) ON CONFLICT(phase, tag) DO UPDATE SET count=$n",
                null, ("$t", tag), ("$n", n));
        foreach (var (tag, n) in stats.TagAfterCounts())
            Db.Exec("INSERT INTO intrinsic_tag_contrast(phase, tag, count) VALUES('after',$t,$n) ON CONFLICT(phase, tag) DO UPDATE SET count=$n",
                null, ("$t", tag), ("$n", n));
    }

    public string StatsSummary()
    {
        var keys = Db.Query("SELECT key, value FROM intrinsic_interrupt_stats ORDER BY key",
            r => $"{r.GetString(0)}={r.GetInt64(1)}");
        var comps = Db.Query("SELECT component, chosen_count FROM intrinsic_component_stats ORDER BY component",
            r => $"{r.GetString(0)}:{r.GetInt64(1)}");
        return "intrinsic_store " + string.Join(" ", keys) +
               (comps.Count > 0 ? " top_comp=[" + string.Join(" ", comps) + "]" : "");
    }

    public void Dispose() => Db.Dispose();
}
