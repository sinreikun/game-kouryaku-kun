using GameKouryakuKun.Core.Exploration;

namespace GameKouryakuKun.Core.Viewer;

/// <summary>グラフの指標（表示だけ。報酬にはしない）</summary>
public enum GrowthMetric { PredictionAccuracy, CalibrationGap, UnknownRate, RevisionRate, NaturalShare }

public enum GrowthEventKind { NewState, NewSequence, Revision, GoalAchieved }

public sealed record GrowthEvent(long Step, GrowthEventKind Kind, string Text)
{
    public string Symbol => Kind switch { GrowthEventKind.NewState => "★", GrowthEventKind.NewSequence => "◆", GrowthEventKind.Revision => "↻", _ => "◎" };
}

public sealed record GrowthPoint(long Step, double Value);

/// <summary>
/// 学習の成長の計器と折れ線グラフ（v0.15.0。人向けの表示だけ）。
/// ここで作る値（予測精度など）は手の選択・学習・報酬に使わない（静的検査 Viewer_types_are_display_only）。
/// 直近 Window 手の移動窓で、予測精度・較正のずれ・未知の遭遇率・モデル修正率・待つ手の割合を出す
/// </summary>
public sealed class GrowthTracker
{
    public int Window { get; }
    private readonly object _lock = new();
    private readonly List<(long Step, bool? Hit, double P, bool Unknown, bool Revised, bool Wait, string Source)> _steps = new();
    private readonly List<GrowthEvent> _events = new();
    public int MaxPoints { get; init; } = 2000;   // 根拠: 表示の都合（数時間分の手を間引かずに持てる量）

    public GrowthTracker(int window = 50) { Window = window; }   // 根拠: 表示の都合（数分の手。1手の偶然で大きく振れない幅）

    public void Add(StepSummary s, bool wait)
    {
        lock (_lock)
        {
            _steps.Add((s.Step, s.Hit, s.PPredicted, s.NewState, s.Revised, wait, s.Source));
            if (_steps.Count > MaxPoints * 2) _steps.RemoveRange(0, _steps.Count - MaxPoints * 2);
            if (s.NewState) _events.Add(new GrowthEvent(s.Step, GrowthEventKind.NewState, $"#{s.Step} ★新State {s.To}（{s.From} で {s.Action}）"));
            if (s.Revised) _events.Add(new GrowthEvent(s.Step, GrowthEventKind.Revision, $"#{s.Step} ↻モデル修正 {s.From}「{s.Action}」"));
            if (s.GoalAchieved) _events.Add(new GrowthEvent(s.Step, GrowthEventKind.GoalAchieved, $"#{s.Step} ◎目的達成"));
            if (_events.Count > MaxPoints) _events.RemoveRange(0, _events.Count - MaxPoints);
        }
    }

    public void AddEvent(GrowthEvent e) { lock (_lock) _events.Add(e); }

    public IReadOnlyList<GrowthEvent> Events() { lock (_lock) return _events.ToList(); }

    public static string MetricName(GrowthMetric m) => m switch
    {
        GrowthMetric.PredictionAccuracy => "予測精度（直近）", GrowthMetric.CalibrationGap => "予測確率の較正のずれ（直近）",
        GrowthMetric.UnknownRate => "未知遭遇率（直近）", GrowthMetric.RevisionRate => "モデル修正率（直近）", _ => "待つ手の割合（直近）",
    };

    /// <summary>指標の折れ線（手数ごと。窓に十分な手がない所は出さない）</summary>
    public IReadOnlyList<GrowthPoint> Series(GrowthMetric m)
    {
        lock (_lock)
        {
            var r = new List<GrowthPoint>();
            for (int i = 0; i < _steps.Count; i++)
            {
                int lo = Math.Max(0, i - Window + 1);
                var w = _steps.GetRange(lo, i - lo + 1);
                double? v = m switch
                {
                    GrowthMetric.PredictionAccuracy => w.Count(x => x.Hit != null) >= Math.Min(10, Window) ? w.Count(x => x.Hit == true) / (double)w.Count(x => x.Hit != null) : null,
                    GrowthMetric.CalibrationGap => w.Count(x => x.Hit != null && double.IsFinite(x.P)) >= Math.Min(10, Window)
                        ? Math.Abs(w.Where(x => x.Hit != null && double.IsFinite(x.P)).Average(x => x.P) - w.Where(x => x.Hit != null && double.IsFinite(x.P)).Average(x => x.Hit == true ? 1.0 : 0.0)) : null,
                    GrowthMetric.UnknownRate => w.Count >= Math.Min(10, Window) ? w.Count(x => x.Unknown) / (double)w.Count : null,
                    GrowthMetric.RevisionRate => w.Count >= Math.Min(10, Window) ? w.Count(x => x.Revised) / (double)w.Count : null,
                    _ => w.Count >= Math.Min(10, Window) ? w.Count(x => x.Wait) / (double)w.Count : null,
                };
                if (v is double d) r.Add(new GrowthPoint(_steps[i].Step, d));
            }
            return r.Count > MaxPoints ? r.Where((_, k) => k % (r.Count / MaxPoints + 1) == 0).ToList() : r;
        }
    }

    public (int Steps, int Agent, int Human, int Checked, int Hits) Counts()
    {
        lock (_lock) return (_steps.Count, _steps.Count(x => x.Source != "human"), _steps.Count(x => x.Source == "human"), _steps.Count(x => x.Hit != null), _steps.Count(x => x.Hit == true));
    }
}

/// <summary>成長メーターの1行分の値（表示だけ）</summary>
public sealed record GrowthMeters(long ExperienceSteps, int States, int EstablishedRelations, int Hypotheses, int ActiveSequences,
    double RecentAccuracy, double RecentCalibrationGap, double RecentUnknownRate, int HumanSteps, int Goals, int GoalsAchieved, int GoalsFailed)
{
    public string Text()
    {
        static string P(double x) => double.IsFinite(x) ? $"{x:P0}" : "—";
        return $"経験 {ExperienceSteps} 手（人間 {HumanSteps}）・認識した画面 {States}・確立した関係 {EstablishedRelations}・仮説 {Hypotheses}・使える並び {ActiveSequences}\n" +
               $"予測精度（直近） {P(RecentAccuracy)}・較正のずれ {P(RecentCalibrationGap)}・未知遭遇率 {P(RecentUnknownRate)}・自己目的 {Goals}（達成 {GoalsAchieved}／失敗・断念 {GoalsFailed}）\n" +
               "※ 表示用の計器です。攻略くんの報酬・目的には使いません";
    }
}
