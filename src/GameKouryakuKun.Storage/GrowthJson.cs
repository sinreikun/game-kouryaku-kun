using System.Text.Json;
using GameKouryakuKun.Core.Growth;

namespace GameKouryakuKun.Storage;

/// <summary>成長テストの計測の JSON（growth_snapshots.json）</summary>
public static class GrowthJson
{
    public static string Write(GrowthSnapshot g)
    {
        var d = new Dictionary<string, object?>
        {
            ["run_id"] = g.RunId, ["seq"] = g.Seq, ["step"] = g.Step, ["horizon"] = g.Horizon, ["horizon_changes"] = g.HorizonChanges, ["max_achieved_length"] = g.MaxAchievedLength,
            ["skills_total"] = g.SkillsTotal, ["skills_active"] = g.SkillsActive, ["skill_uses"] = g.SkillUses, ["skill_candidates"] = g.SkillCandidates,
            ["replan_goals"] = g.ReplanGoals, ["replan_achieved"] = g.ReplanAchieved, ["states_run"] = g.StatesRun, ["states_total"] = g.StatesTotal,
            ["visit_entropy"] = Math.Round(g.VisitEntropy, 4), ["value_reach"] = Math.Round(g.ValueReach, 6), ["value_retest"] = Math.Round(g.ValueRetest, 6),
            ["outcome_keys"] = g.OutcomeKeys, ["outcome_mean_abs"] = Math.Round(g.OutcomeMeanAbs, 6), ["goals_created"] = g.GoalsCreated, ["goals_achieved"] = g.GoalsAchieved,
            ["window_created"] = g.WindowCreated, ["window_achieved"] = g.WindowAchieved,
            ["plan_candidates_by_length"] = g.PlanCandidatesByLength.ToDictionary(k => k.Key.ToString(), k => (object?)k.Value), ["reason"] = g.Reason, ["app_version"] = g.AppVersion,
        };
        return JsonSerializer.Serialize(d);
    }

    public static GrowthSnapshot Read(string json)
    {
        using var doc = JsonDocument.Parse(json); var e = doc.RootElement;
        long L(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        int I(string k) => (int)L(k);
        double D(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var pl = new Dictionary<int, int>();
        if (e.TryGetProperty("plan_candidates_by_length", out var p) && p.ValueKind == JsonValueKind.Object)
            foreach (var x in p.EnumerateObject()) if (int.TryParse(x.Name, out var k)) pl[k] = x.Value.GetInt32();
        return new GrowthSnapshot(L("run_id"), L("seq"), I("step"), I("horizon"), I("horizon_changes"), I("max_achieved_length"), I("skills_total"), I("skills_active"), I("skill_uses"), I("skill_candidates"),
            I("replan_goals"), I("replan_achieved"), I("states_run"), I("states_total"), D("visit_entropy"), D("value_reach"), D("value_retest"), I("outcome_keys"), D("outcome_mean_abs"),
            I("goals_created"), I("goals_achieved"), I("window_created"), I("window_achieved"), pl, e.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "")
            { AppVersion = e.TryGetProperty("app_version", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() ?? "" : "" };
    }
}
