using System.Globalization;
using GameKouryakuKun.Core.Exploration;

namespace GameKouryakuKun.Core.Viewer;

/// <summary>
/// 1手サマリー（v0.15.0。表示だけ）。判断の記録（DecisionRecord）から、人がリアルタイムで追える短い2行を作る。
/// 記号は色に頼らない: ○ 予測一致・× 予測外れ・★ 新発見・↻ 仮説修正・? 探索（未試行・不確かな関係の確認）・◎ 目的達成（目的の実装後）。
/// 手の選択・学習・記録からは参照しない（静的検査 Viewer_types_are_display_only）
/// </summary>
public sealed record StepSummary(long Step, string Source, string From, string Action, string? To, string? PredictedTo, double PPredicted,
    bool? Hit, bool NewState, bool Revised, bool Changed, string Rule, bool Explore, bool GoalAchieved, DecisionRecord? Detail)
{
    public string SourceTag => Source == "human" ? "[HUMAN]" : "[AI]   ";

    /// <summary>v0.21.0: システムの出来事の区切りの行（例「──── PROCESS EXIT ────」）。手ではない（成長の計器・学習には入れない）</summary>
    public string? SystemLine { get; init; }
    public static StepSummary Separator(long afterStep, string text) =>
        new(afterStep, "system", "", "", null, null, double.NaN, null, false, false, false, "system", false, false, null) { SystemLine = text };

    public string Marks =>
        (Hit == true ? "○" : Hit == false ? "×" : "") + (NewState ? "★" : "") + (Revised ? "↻" : "") + (Explore ? "?" : "") + (GoalAchieved ? "◎" : "");

    public string Line1
    {
        get
        {
            if (SystemLine != null) return $"──── {SystemLine} ────";
            string pred = Hit == true ? $"○予測一致 {Pct(PPredicted)}" : Hit == false ? $"×予測外れ（予測 {PredictedTo ?? "—"} {Pct(PPredicted)}）" : "予測なし";
            string extra = (NewState ? " ★新State" : "") + (Revised ? " ↻修正" : "") + (GoalAchieved ? " ◎目的達成" : "");
            return $"#{Step} {SourceTag} {From} → {Action} → {To ?? "—"}  {pred}{extra}";
        }
    }

    public string Line2
    {
        get
        {
            if (SystemLine != null) return "";
            string sel = Rule switch
            {
                "human" => "人間の操作",
                "momentum" => "同じ操作を続ける",
                "chord_probe" => "?同時押しのためし",
                "sequence_step" => "操作の並びの途中",
                "fallback" => "候補なし",
                _ => Explore ? (Detail?.Candidates.FirstOrDefault(c => c.Action == Action)?.DirectTries == 0 ? "?未試行の確認" : "?不確かな関係の確認") : "経験の多い関係",
            };
            string res = To == null ? "中断" : To != From ? (NewState ? "新しい画面" : "画面遷移") : Changed ? "画面変化" : "無反応";
            string learn = Revised ? "仮説修正" : Hit == true ? "確信↑" : Hit == false ? "外れを記録" : "初観測";
            return $"   選択: {sel} / 結果: {res} / 学習: {learn}";
        }
    }

    private static string Pct(double p) => double.IsFinite(p) ? (p * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";

    /// <summary>判断の記録から作る。newState は初めて着いた画面だったか</summary>
    public static StepSummary Of(long step, DecisionRecord d, string? to, bool changed, bool newState, bool goalAchieved = false)
    {
        var p = d.Check?.Prediction ?? d.Prediction;
        bool informed = p != null && p.TopNext != null && (p.DirectN + p.ReinterpretedN + p.PriorN) > 0;
        bool? hit = d.Check != null && informed ? d.Check.Hit : null;
        var me = d.Candidates.FirstOrDefault(c => c.Action == d.Chosen);
        // 探索: 点数の抽選で、未試行または予測の不確かさの項が大きい手（記録の数値の区分だけ）
        bool explore = d.Rule == "chord_probe" || (d.Rule == "scored" && me != null && (me.DirectTries == 0 || me.Uncertainty >= 0.5));
        return new StepSummary(step, d.ActionSource, d.Node, d.Chosen, to, p?.TopNext, p?.PTop ?? double.NaN, hit, newState, d.RevisionId != null || d.Check?.Revision != null,
            changed, d.Rule, explore, goalAchieved, d);
    }
}

/// <summary>
/// 1手サマリーの履歴（表示だけ）。Viewer Pause 中は表示用の一覧を止める（探索と記録は止めない）。解除すると最新へ戻る
/// </summary>
public sealed class StepHistory
{
    private readonly object _lock = new();
    private readonly List<StepSummary> _all = new();
    private IReadOnlyList<StepSummary> _frozen = Array.Empty<StepSummary>();
    public int Capacity { get; }
    public bool Paused { get; private set; }
    public StepHistory(int capacity = 500) { Capacity = capacity; }

    public void Add(StepSummary s) { lock (_lock) { _all.Add(s); if (_all.Count > Capacity) _all.RemoveRange(0, _all.Count - Capacity); } }
    public int Count { get { lock (_lock) return _all.Count; } }
    /// <summary>手の行だけの数（v0.22.1: システムの出来事の区切りの行を除く。実機 v0.21.0 で区切りを手と数えて健全性が FAIL になった）</summary>
    public int StepCount { get { lock (_lock) return _all.Count(x => x.SystemLine == null); } }

    public void SetPaused(bool on) { lock (_lock) { Paused = on; _frozen = on ? _all.ToList() : Array.Empty<StepSummary>(); } }

    /// <summary>表示する最新 k 手（Pause 中は止めた時点のもの）と、止めている間に増えた手数</summary>
    public (IReadOnlyList<StepSummary> Items, int Behind) View(int k)
    {
        lock (_lock)
        {
            var src = Paused ? _frozen : _all;
            int behind = Paused ? Math.Max(0, (int)((_all.LastOrDefault()?.Step ?? 0) - (src.LastOrDefault()?.Step ?? 0))) : 0;
            return (src.Skip(Math.Max(0, src.Count - k)).ToList(), behind);
        }
    }
}
