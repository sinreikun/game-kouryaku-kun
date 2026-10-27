namespace GameKouryakuKun.Core.Growth;

/// <summary>
/// 成長テストの 1 回の計測（v0.22.0 ブロック B。src/growth-test-design.md）。記録と健全性の判定だけで、手の選択・報酬・価値には使わない。
/// 「大きいほど良い」とは扱わない: 判定は記録の整合・引き継ぎ・止まっていないか（凍結・振動）だけを見る。
/// </summary>
public sealed record GrowthSnapshot(
    long RunId, long Seq, int Step, int Horizon, int HorizonChanges, int MaxAchievedLength,
    int SkillsTotal, int SkillsActive, int SkillUses, int SkillCandidates,
    int ReplanGoals, int ReplanAchieved,
    int StatesRun, int StatesTotal, double VisitEntropy,
    double ValueReach, double ValueRetest, int OutcomeKeys, double OutcomeMeanAbs,
    int GoalsCreated, int GoalsAchieved, int WindowCreated, int WindowAchieved,
    IReadOnlyDictionary<int, int> PlanCandidatesByLength, string Reason)
{
    /// <summary>v0.25.1: 記録したアプリの版（空＝v0.25.0 まで）。版が変われば価値の定義が変わり得るので、引き継ぎの価値の比較は同じ版の組だけ</summary>
    public string AppVersion { get; init; } = "";
    /// <summary>訪れた状態の広がり（訪問の回数の分布のエントロピー。ビット）。0＝1 か所だけ</summary>
    public static double Entropy(IEnumerable<int> counts)
    {
        var c = counts.Where(x => x > 0).ToList(); double n = c.Sum(); if (n <= 0) return 0;
        return Math.Max(0, -c.Sum(x => x / n * Math.Log2(x / n)));
    }
}

public enum GrowthLevel { Pass, Warn, Fail, NotTested }
public sealed record GrowthCheck(string Name, GrowthLevel Level, string Detail);

/// <summary>
/// 成長テストの自動判定（長い実行・起動をまたぐ実行）。判定はシステムが働いているかだけ:
///  ・記録の整合（Horizon の範囲・引き直しの達成 ≤ 引き直し・短期の達成 ≤ 作成）
///  ・起動をまたいだ引き継ぎ（前の実行の最後の Horizon・価値が次の実行の最初に引き継がれているか）
///  ・凍結（目的が十分あるのに価値・結果の集計がまったく動かない）／振動（Horizon が計測のたびに変わる）
/// 数の多さ・伸びは PASS の条件にしない（推移は INFO として出すだけ）。
/// </summary>
public static class GrowthEvaluator
{
    public const int MinSnapshots = 3;          // 根拠: 汎用（推移を言える最小の点数）
    public const int FreezeMinGoals = 20;        // 根拠: 汎用（価値が動くはずの目的の数）
    public const double FlapShare = 0.5;         // 根拠: 汎用（計測の半分以上で Horizon が変わるなら落ち着いていない）
    public const int ShortTermMinCreated = 10;   // 根拠: 汎用（短期の達成を言える最小の目的の数）
    public const double ContinuityTolerance = 1e-6;

    public static List<GrowthCheck> Evaluate(IReadOnlyList<GrowthSnapshot> all)
    {
        var r = new List<GrowthCheck>();
        if (all.Count == 0) { r.Add(new("成長テスト: 計測", GrowthLevel.NotTested, "計測なし")); return r; }
        var xs = all.OrderBy(x => x.Seq).ThenBy(x => x.Step).ToList();
        var runs = xs.Select(x => x.RunId).Distinct().ToList();
        string Trend(Func<GrowthSnapshot, string> f) => string.Join(" → ", Sample(xs, 8).Select(f));

        // 整合（計測の間の達成は、間の始まりに進行中だった目的 1 つの分だけ作った数を超えてよい: v0.24.0 実機）
        var bad = xs.Where(x => x.Horizon < 1 || x.Horizon > 12 || x.ReplanAchieved > x.ReplanGoals || x.GoalsAchieved > x.GoalsCreated || x.WindowAchieved > x.WindowCreated + 1
                                || x.SkillsActive > x.SkillsTotal || x.StatesRun > x.StatesTotal || x.MaxAchievedLength > 12).ToList();
        r.Add(new("成長テスト: 記録の整合", bad.Count == 0 ? GrowthLevel.Pass : GrowthLevel.Fail, bad.Count == 0 ? $"計測 {xs.Count}・実行 {runs.Count}" : $"整合しない計測 {bad.Count}（最初 #{bad[0].Seq}）"));

        bool enough = xs.Count >= MinSnapshots;
        // Horizon の推移（振動だけを見る）
        int changes = 0; for (int i = 1; i < xs.Count; i++) if (xs[i].Horizon != xs[i - 1].Horizon) changes++;
        r.Add(new("成長テスト: Horizon の推移", !enough ? GrowthLevel.NotTested : changes > FlapShare * (xs.Count - 1) && xs.Count >= 5 ? GrowthLevel.Warn : GrowthLevel.Pass,
            $"{Trend(x => x.Horizon.ToString())}（計測の間の変化 {changes} 回・達成した最長 {xs[^1].MaxAchievedLength} 手）"));
        // スキルの数と再利用
        r.Add(new("成長テスト: スキルの数と再利用", !enough ? GrowthLevel.NotTested : GrowthLevel.Pass,
            $"並び（選択の候補／全体） {Trend(x => $"{x.SkillsActive}/{x.SkillsTotal}")}・目的で使った {xs[^1].SkillUses}・目的から作った候補 {xs[^1].SkillCandidates}（数の多さは良し悪しにしない）"));
        // 引き直しの成功率
        var last = xs[^1];
        r.Add(new("成長テスト: 引き直しの成功率", last.ReplanGoals < 3 ? GrowthLevel.NotTested : GrowthLevel.Pass,
            last.ReplanGoals == 0 ? "引き直しなし" : $"{last.ReplanAchieved}/{last.ReplanGoals}（{last.ReplanAchieved / (double)last.ReplanGoals:P0}）"));
        // 訪れた状態の広がり
        r.Add(new("成長テスト: 訪れた状態の広がり", !enough ? GrowthLevel.NotTested : GrowthLevel.Pass,
            $"この実行の状態／全体 {Trend(x => $"{x.StatesRun}/{x.StatesTotal}")}・広がり {Trend(x => x.VisitEntropy.ToString("0.00"))} ビット"));
        // 価値・結果の集計の変化（凍結の検出）
        var withGoals = xs.Where(x => x.GoalsCreated > 0).ToList();
        bool frozen = false;
        foreach (var run in runs)
        {
            var rs = xs.Where(x => x.RunId == run).ToList();
            if (rs.Count >= 2 && rs[^1].GoalsCreated - rs[0].GoalsCreated >= FreezeMinGoals
                && Math.Abs(rs[^1].ValueReach - rs[0].ValueReach) < ContinuityTolerance && Math.Abs(rs[^1].ValueRetest - rs[0].ValueRetest) < ContinuityTolerance
                && Math.Abs(rs[^1].OutcomeMeanAbs - rs[0].OutcomeMeanAbs) < ContinuityTolerance) frozen = true;
        }
        r.Add(new("成長テスト: 目的の価値・結果の集計の変化", !enough ? GrowthLevel.NotTested : frozen ? GrowthLevel.Warn : GrowthLevel.Pass,
            $"価値 到達 {Trend(x => x.ValueReach.ToString("0.000"))}・確かめ直し {Trend(x => x.ValueRetest.ToString("0.000"))}・結果の集計 {last.OutcomeKeys} 区分{(frozen ? "（目的が進んでも価値がまったく動いていない）" : "")}"));
        // 短期の目的の達成（窓の中。壊れていないかだけ）
        int wc = xs.Sum(x => x.WindowCreated), wa = xs.Sum(x => x.WindowAchieved);
        r.Add(new("成長テスト: 短期の目的の達成", wc < ShortTermMinCreated ? GrowthLevel.NotTested : wa == 0 ? GrowthLevel.Warn : GrowthLevel.Pass,
            $"計測の間に作った目的 {wc}・達成 {wa}{(wc > 0 ? $"（{wa / (double)wc:P0}）" : "")}（0 件だけを異常とする。率の高さは良し悪しにしない）"));
        // 道筋の候補の手数（3 手以上が候補になりうるか。v0.20.0 の調査の継続）
        var pl = last.PlanCandidatesByLength;
        r.Add(new("成長テスト: 到達の目的の候補の手数", pl.Count == 0 ? GrowthLevel.NotTested : GrowthLevel.Pass,
            pl.Count == 0 ? "候補なし" : string.Join("・", pl.OrderBy(k => k.Key).Select(k => k.Key < 0 ? $"確かさ不足 {k.Value}" : k.Key == 0 ? $"道筋なし {k.Value}" : $"{k.Key} 手 {k.Value}")) + $"（Horizon {last.Horizon} の中で選べるのはその手数まで）"));
        // 起動をまたいだ引き継ぎ
        if (runs.Count < 2) r.Add(new("成長テスト: 起動をまたいだ引き継ぎ", GrowthLevel.NotTested, "実行が 1 つだけ"));
        else
        {
            var probs = new List<string>(); int pairs = 0, crossVersion = 0;
            for (int i = 1; i < runs.Count; i++)
            {
                var prevEnd = xs.Last(x => x.RunId == runs[i - 1]); var nextStart = xs.First(x => x.RunId == runs[i]);
                if (nextStart.Reason != "start" || prevEnd.Reason is not ("end" or "recovery")) continue;
                pairs++;
                if (prevEnd.Horizon != nextStart.Horizon) probs.Add($"#{runs[i]} Horizon {prevEnd.Horizon}→{nextStart.Horizon}");
                bool sameVersion = prevEnd.AppVersion.Length > 0 && prevEnd.AppVersion == nextStart.AppVersion;
                if (!sameVersion) crossVersion++;
                else if (Math.Abs(prevEnd.ValueReach - nextStart.ValueReach) > 1e-4 || Math.Abs(prevEnd.ValueRetest - nextStart.ValueRetest) > 1e-4) probs.Add($"#{runs[i]} 価値が引き継がれていない");
                if (nextStart.StatesTotal < prevEnd.StatesTotal) probs.Add($"#{runs[i]} 知っている状態が減った {prevEnd.StatesTotal}→{nextStart.StatesTotal}");
            }
            r.Add(new("成長テスト: 起動をまたいだ引き継ぎ", pairs == 0 ? GrowthLevel.NotTested : probs.Count == 0 ? GrowthLevel.Pass : GrowthLevel.Fail,
                (pairs == 0 ? "始まりと終わりの計測がそろった実行の組なし" : probs.Count == 0 ? $"Horizon・知っている状態は {pairs} 組、価値は同じ版の {pairs - crossVersion} 組で引き継がれた" : string.Join("・", probs.Take(5))) + (crossVersion > 0 ? $"（うち {crossVersion} 組は版の記録がない・版をまたぐため価値の比較をしない）" : "")));
        }
        return r;
    }

    private static IEnumerable<GrowthSnapshot> Sample(List<GrowthSnapshot> xs, int k)
    {
        if (xs.Count <= k) return xs;
        return Enumerable.Range(0, k).Select(i => xs[(int)Math.Round(i * (xs.Count - 1) / (double)(k - 1))]);
    }
}
