using GameKouryakuKun.Core.LongHorizon;
using GameKouryakuKun.Core.Growth;

namespace GameKouryakuKun.Storage;

/// <summary>S8: profile 配下の既存ストアから LongHorizonInput を組み立てる。</summary>
public static class LongHorizonAggregator
{
    public static LongHorizonInput FromProfile(
        string profileDataDir,
        SqliteDb? memory = null,
        bool outerEnabled = false,
        bool outerPromote = false,
        int softAdopts = 0,
        int humanSwitches = 0,
        bool supervisorCircuit = false,
        int supervisorRecoveries = 0,
        int liveRoiDeferHints = 0,
        IReadOnlyList<double>? recentPeProxies = null)
    {
        string? peSum = null; long peEnq = 0, pePend = 0;
        double undec = -1, coarse = -1;
        try
        {
            if (File.Exists(PeClosedLoopStore.DefaultPath(profileDataDir)))
            {
                using var pe = PeClosedLoopStore.Open(profileDataDir);
                peSum = pe.StatsSummary();
                peEnq = pe.CountCandidates();
            }
        }
        catch { /* honest partial */ }

        string? inSum = null; long mix = 0, interrupts = 0, blocked = 0, fell = 0;
        try
        {
            if (File.Exists(IntrinsicStatsStore.DefaultPath(profileDataDir)))
            {
                using var ist = IntrinsicStatsStore.Open(profileDataDir);
                inSum = ist.StatsSummary();
            }
        }
        catch { }

        string? regSum = null; string? champ = null; bool rb = false; int comps = 0, prom = 0, rolls = 0;
        try
        {
            if (File.Exists(ModelRegistryStore.DefaultPath(profileDataDir)))
            {
                using var reg = ModelRegistryStore.Open(profileDataDir);
                regSum = reg.StatsSummary();
                champ = reg.ChampionId();
                rb = reg.RollbackAvailable();
                comps = Convert.ToInt32(reg.Db.Scalar("SELECT COUNT(*) FROM registry_comparisons") ?? 0);
                prom = reg.CountEvents("promote");
                rolls = reg.CountEvents("rollback");
            }
        }
        catch { }

        List<GrowthSnapshot>? snaps = null;
        try
        {
            if (memory != null && memory.Tables().Contains("growth_snapshots"))
            {
                snaps = memory.Query(
                    "SELECT json FROM (SELECT json, snapshot_id FROM growth_snapshots ORDER BY snapshot_id DESC LIMIT 5000) ORDER BY snapshot_id",
                    r => GrowthJson.Read(r.GetString(0))).ToList();
            }
        }
        catch { }

        return new LongHorizonInput
        {
            PeStatsSummary = peSum,
            PeEnqueued = peEnq,
            PePending = pePend,
            PeUndecidedRate = undec,
            PeCoarseRate = coarse,
            IntrinsicStatsSummary = inSum,
            IntrinsicMixSteps = mix,
            IntrinsicInterrupts = interrupts,
            IntrinsicBlockedBudget = blocked,
            IntrinsicAmbiguityFell = fell,
            RegistrySummary = regSum,
            ChampionId = champ,
            RegistryRollbackAvailable = rb,
            RegistryCompares = comps,
            RegistryPromotes = prom,
            RegistryRollbacks = rolls,
            SoftAdopts = softAdopts,
            HumanSwitches = humanSwitches,
            OuterLoopEnabled = outerEnabled,
            OuterPromoteEnabled = outerPromote,
            SupervisorCircuitTripped = supervisorCircuit,
            SupervisorRecoveries = supervisorRecoveries,
            LiveRoiDeferHints = liveRoiDeferHints,
            GrowthSnapshots = snaps,
            RecentPeProxies = recentPeProxies,
            SaveApiAbsent = true,
        };
    }

    public static LongHorizonReport BuildForProfile(string profileDataDir, SqliteDb? memory = null, LongHorizonInput? overlay = null)
    {
        var input = overlay ?? FromProfile(profileDataDir, memory);
        return LongHorizonHealth.Build(input);
    }
}
