using System.Text.Json;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.25.4（t52u）: ゲームの環境が消えた（ウィンドウの消失・プロセスの終了）ことを、直前の自分の操作の観測した結果として学ぶ。
/// 結果は「先のない状態」（Terminal）への遷移として内部モデルと探索の記録に入れる。負の報酬の定数は置かない:
/// 先のない状態は先の選択肢も到達もないので、地域の値打ちは下限、計画の道にもならない。キーの名前・画面の意味は見ない。
/// 人間の手の後の消失は記録だけ（人間の操作は正解にも誤りにもしない＝学習に入れない）。復旧中・待つ手・時間の離れた消失は帰属しない。
/// 再起動はエピソードの終わりでも AI の行動でもない（t44u のまま）。
/// </summary>
public sealed record LossEvent(string State, string Action, string Source, long Seq, double GapMs, string At, bool Learned);

public sealed class EnvironmentLossBook
{
    /// <summary>先のない状態の名前（システムの名前。ゲームの画面ではない）</summary>
    public const string Terminal = "⊥環境の消失";
    public int RepeatWarn { get; init; } = 2;   // 根拠: 汎用（偶然の 1 回と区別できる最小の繰り返し。学んだ後に同じ手で 2 回以上消えたら学びが効いていない）

    private readonly List<LossEvent> _events = new();
    public int Unattributed { get; private set; }
    public IReadOnlyList<LossEvent> Events => _events;

    /// <summary>消失の直前の手に帰属するか: 送った手があり、待つ手でなく、復旧中でなく、送ってから消失を知るまでが windowMs 以内</summary>
    public static bool Attributable(bool hasLast, bool lastWasWait, bool duringRecovery, double sentAtMs, double lossAtMs, double windowMs) =>
        hasLast && !lastWasWait && !duringRecovery && lossAtMs >= sentAtMs && lossAtMs - sentAtMs <= windowMs;

    public LossEvent? Record(bool attributable, string state, string action, string source, long seq, double gapMs, string at)
    {
        if (!attributable) { Unattributed++; return null; }
        var e = new LossEvent(state, action, source, seq, gapMs, at, source == "agent");
        _events.Add(e); return e;
    }

    /// <summary>学ぶもの（自分の手の後の消失だけ）</summary>
    public IEnumerable<LossEvent> Learnable => _events.Where(e => e.Learned);

    /// <summary>同じ（状態, 操作）で、学んだ後にもう一度消えた回数（学びが効いていれば増えない）</summary>
    public IReadOnlyList<(string State, string Action, int Losses, int AfterLearned)> Pairs() =>
        _events.Where(e => e.Learned).GroupBy(e => (e.State, e.Action)).Select(g => (g.Key.State, g.Key.Action, g.Count(), g.Count() - 1))
            .OrderByDescending(x => x.Item3).ToList();

    public bool RepeatsAfterLearning => Pairs().Any(p => p.AfterLearned >= RepeatWarn);

    public string Summary()
    {
        int agent = _events.Count(e => e.Learned), human = _events.Count - agent;
        return $"自分の手の後 {agent}（学んだ）・人間の手の後 {human}（記録だけ）・帰属しない {Unattributed}" +
               string.Concat(Pairs().Take(3).Select(p => $"／({p.State}, {p.Action}) 消失 {p.Losses} 回（学んだ後 {p.AfterLearned}）"));
    }

    public string ExportState() => JsonSerializer.Serialize(new { v = 1, un = Unattributed, e = _events.Select(e => new object[] { e.State, e.Action, e.Source, e.Seq, e.GapMs, e.At, e.Learned }) });

    public void ImportState(string json)
    {
        using var d = JsonDocument.Parse(json); var r = d.RootElement;
        if (!r.TryGetProperty("v", out var v) || v.GetInt32() != 1) return;
        Unattributed = r.GetProperty("un").GetInt32();
        foreach (var x in r.GetProperty("e").EnumerateArray())
        {
            var a = x.EnumerateArray().ToArray();
            _events.Add(new LossEvent(a[0].GetString()!, a[1].GetString()!, a[2].GetString()!, a[3].GetInt64(), a[4].GetDouble(), a[5].GetString()!, a[6].GetBoolean()));
        }
    }
}
