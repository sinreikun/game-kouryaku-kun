using GameKouryakuKun.Core.Growth;

namespace GameKouryakuKun.Core.LongHorizon;

/// <summary>
/// S8 GrowthSuite: Champion 非回帰・PE 傾向・到達/Skill（表示のみ。報酬にしない）。
/// </summary>
public static class GrowthSuite
{
    public const string SuiteId = "growth_suite@1";

    public static IReadOnlyList<LhCheck> Evaluate(LongHorizonInput input)
    {
        var r = new List<LhCheck>();
        string area = LongHorizonHealth.Area;

        // Wrap existing GrowthEvaluator when snapshots present
        if (input.GrowthSnapshots is { Count: > 0 } snaps)
        {
            foreach (var c in GrowthEvaluator.Evaluate(snaps))
            {
                var lvl = c.Level switch
                {
                    GrowthLevel.Pass => LhLevel.Ok,
                    GrowthLevel.Warn => LhLevel.Warn,
                    GrowthLevel.Fail => LhLevel.Fail,
                    GrowthLevel.Info => LhLevel.Info,
                    _ => LhLevel.NotTested,
                };
                r.Add(new(area, "成長/" + c.Name, lvl, c.Detail));
            }
            var last = snaps.OrderBy(x => x.Seq).ThenBy(x => x.Step).Last();
            r.Add(new(area, "到達/Skill再利用", LhLevel.Info,
                $"states_run={last.StatesRun}/{last.StatesTotal} skills_active={last.SkillsActive}/{last.SkillsTotal} uses={last.SkillUses}（数の多さは良し悪しにしない）"));
        }
        else
            r.Add(new(area, "成長スイート", LhLevel.NotTested, "growth_snapshots なし"));

        // PE trend across versions / recent proxies
        if (input.RecentPeProxies is { Count: >= 2 } pe)
        {
            double first = pe[0], last = pe[^1];
            double delta = first - last; // lower PE better
            var lvl = delta > 0.05 /* 根拠: 汎用（PE 代理の明確改善床） */ ? LhLevel.Info : delta < -0.05 /* 根拠: 汎用（PE 代理の明確悪化床） */ ? LhLevel.Warn : LhLevel.Info;
            r.Add(new(area, "PE傾向", lvl,
                $"pe_proxy {first:0.000} → {last:0.000}（下降=改善・学習停滞なら WARN）"));
        }
        else
            r.Add(new(area, "PE傾向", LhLevel.NotTested, "pe_proxy 時系列不足"));

        // §22.2 stagnation-ish: promotes==0, compares>0, mix large, pe not improving
        if (input.RegistryCompares >= 3 /* 根拠: 汎用（停滞判定の最小比較回数） */ && input.RegistryPromotes == 0 && input.IntrinsicMixSteps >= 50 /* 根拠: 汎用（停滞判定の最小 mix 手数） */
            && input.RecentPeProxies is { Count: >= 2 } pe2 && pe2[^1] >= pe2[0] - 1e-6)
        {
            r.Add(new(area, "学習停滞チケット", LhLevel.Warn,
                "compare あるが promote なし・PE 改善見えず（馬鹿プレイではなく停滞メトリクス）"));
        }

        // Champion non-regression hint
        if (input.RegistryRollbacks > 0)
            r.Add(new(area, "Champion非回帰", LhLevel.Info, $"rollbacks={input.RegistryRollbacks}（復元実績あり）"));
        else if (!string.IsNullOrEmpty(input.ChampionId))
            r.Add(new(area, "Champion非回帰", LhLevel.Info, "rollback 実績なし・ポインタ監視のみ"));

        return r;
    }
}
