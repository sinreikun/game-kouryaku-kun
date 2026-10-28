using GameKouryakuKun.Core.Intrinsic;
using GameKouryakuKun.Core.Goals;

namespace GameKouryakuKun.Core.LongHorizon;

/// <summary>§29 長時間安全策の機械判定（ゲーム意味禁止。不足は not_tested）。</summary>
public static class SafetyBudgetChecks
{
    public static IReadOnlyList<LhCheck> Evaluate(LongHorizonInput input)
    {
        var r = new List<LhCheck>();
        string area = LongHorizonHealth.Area;

        // 3. 実験予算逼迫（尽きたこと自体は馬鹿プレイではない → WARN のみ）
        if (input.IntrinsicInterrupts <= 0 && input.IntrinsicBlockedBudget <= 0)
            r.Add(new(area, "実験予算", LhLevel.NotTested, "割込み実績なし"));
        else
        {
            double ratio = input.IntrinsicBudget <= 0 ? 1 :
                (double)input.IntrinsicInterrupts / input.IntrinsicBudget;
            var level = ratio >= 0.9 /* 根拠: 汎用（予算の 90% 以上＝逼迫 WARN） */ || input.IntrinsicBlockedBudget > 0 ? LhLevel.Warn : LhLevel.Ok;
            r.Add(new(area, "実験予算", level,
                $"interrupts={input.IntrinsicInterrupts}/{input.IntrinsicBudget} blocked_budget={input.IntrinsicBlockedBudget}（逼迫は WARN・馬鹿プレイは FAIL にしない）"));
        }

        // soft-adopt 予算
        if (input.SoftAdopts <= 0)
            r.Add(new(area, "SoftAdopt予算", LhLevel.NotTested, "soft adopt 実績なし"));
        else
        {
            double sr = input.SoftAdoptBudget <= 0 ? 1 : (double)input.SoftAdopts / input.SoftAdoptBudget;
            r.Add(new(area, "SoftAdopt予算", sr >= 0.9 /* 根拠: 汎用（soft-adopt 90% 逼迫） */ ? LhLevel.Warn : LhLevel.Ok,
                $"soft_adopts={input.SoftAdopts}/{input.SoftAdoptBudget}"));
        }

        // 4. 不可逆らしき遷移 — 代理未配線なら正直 not_tested
        r.Add(new(area, "不可逆らしき遷移", LhLevel.NotTested,
            "option-vanish 統計スタブ未配線（意味でセーブ破壊と呼ばない）"));

        // 5. 資源 / LiveRoi
        if (input.LiveRoiDeferHints > 0)
            r.Add(new(area, "資源/ROI", LhLevel.Info, $"live_roi defer hints={input.LiveRoiDeferHints}"));
        else
            r.Add(new(area, "資源/ROI", LhLevel.NotTested, "LiveRoi defer 件数なし"));

        // 6. Human
        r.Add(new(area, "Human緊急停止", input.HumanSwitches > 0 ? LhLevel.Info : LhLevel.NotTested,
            input.HumanSwitches > 0 ? $"human_switches={input.HumanSwitches}" : "Human Control 切替なし"));

        // 7. Outer ライブ入力増禁止
        if (!input.OuterLoopEnabled)
            r.Add(new(area, "Outer入力増禁止", LhLevel.Ok, "Outer OFF（ライブ入力増なし）"));
        else if (!input.OuterPromoteEnabled)
            r.Add(new(area, "Outer入力増禁止", LhLevel.Ok, "Outer evaluate-only（昇格なし・入力増なし）"));
        else
            r.Add(new(area, "Outer入力増禁止", LhLevel.Info,
                "Outer PROMOTE ON（重み適用可・ライブゲーム入力増は依然契約禁止）"));

        // 8. セーブ非操作
        r.Add(new(area, "セーブ非操作", input.SaveApiAbsent ? LhLevel.Ok : LhLevel.Fail,
            input.SaveApiAbsent ? "エージェントはセーブを操作しない（契約）" : "save API 検知"));

        // 入力ガード経路（存在 INFO）
        r.Add(new(area, "入力ガード", LhLevel.Info, "禁止キー/入力ガードは既存 Supervisor・Profile 経路（参照）"));

        return r;
    }
}
