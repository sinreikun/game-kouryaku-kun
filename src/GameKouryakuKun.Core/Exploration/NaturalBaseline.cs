using System.Text.Json;

namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.27.0 画面（State）ごとの「入力なしの自然な変化」の基準。ゲームの意味・画面の種類の定義は持たない。
/// 観測の源は 2 つだけ: (1) 待つ手の後の、手と手の間の入力なしの区間（NoInputGate の確認。変わった・変わらなかったの両方）、(2) 待つ手。
/// v0.27.1: キーを送った手の直後の区間は、遅れた反応と自然な変化を分けられないので率には使わない（ObserveAfterAction で数えるだけ）。
/// 変化の判定は手の「正味の変化」と同じ基準（ChangeCriteria.IsChanged）にそろえる。
/// 区間の長さが違う観測をそろえるため、区間あたりの割合 p と平均の区間の長さ d を持ち、操作の 1 手の長さ T で P = 1 − (1 − p)^(min(T,d)/d) にする（観測した長さを超えて外挿しない）。
/// 観測が少ないうちは Jeffreys の事前値（変化 0.5 回を 1 区間ぶん）に縮む（既存の NaturalRates・Attribute と同じ +0.5/+1 の規則）。
/// 観測そのものは消さない: 自然な変化の起きた区画は「入力なしで変わる区画」として数えるだけで、状態・世界モデルの観測からは除かない。
/// </summary>
public sealed class NaturalBaseline
{
    private sealed class N { public int Obs, Changes, Leaves; public double ExposureMs; public int ActTurns; public double ActMs; public int GapObs, WaitObs, DelayedObs, DelayedChanges; }
    private readonly Dictionary<string, N> _n = new(StringComparer.Ordinal);
    private N Of(string node) => _n.TryGetValue(node, out var x) ? x : _n[node] = new N();

    /// <summary>入力なしの観測を 1 つ足す（durationMs: 見ていた長さ。changed: 画面が変わった。left: 別の画面になった）</summary>
    public void ObserveNatural(string node, double durationMs, bool changed, bool left, bool fromWait)
    {
        if (node == null || !(durationMs > 0) || double.IsInfinity(durationMs)) return;
        var e = Of(node); e.Obs++; e.ExposureMs += durationMs; if (changed) e.Changes++; if (left) e.Leaves++;
        if (fromWait) e.WaitObs++; else e.GapObs++;
    }

    /// <summary>v0.27.1: キーを送った手の直後の区間の観測（前の手の遅れた反応と入力なしの変化を分けられない）。消さずに数えるが、基準の率には使わない</summary>
    public void ObserveAfterAction(string node, bool changed)
    {
        if (node == null) return;
        var e = Of(node); e.DelayedObs++; if (changed) e.DelayedChanges++;
    }
    public (int Obs, int Changes) AfterAction(string node) => _n.TryGetValue(node, out var e) ? (e.DelayedObs, e.DelayedChanges) : (0, 0);
    public int AfterActionObservations => _n.Values.Sum(x => x.DelayedObs);

    /// <summary>キーを送った手の 1 手の長さ（その画面の操作の結果を比べる長さ）</summary>
    public void ObserveActionTurn(string node, double durationMs)
    {
        if (node == null || !(durationMs > 0) || double.IsInfinity(durationMs)) return;
        var e = Of(node); e.ActTurns++; e.ActMs += durationMs;
    }

    public int Observations(string node) => _n.TryGetValue(node, out var e) ? e.Obs : 0;
    public (int Obs, int Changes, int Leaves, int Gap, int Wait, double ExposureMs) Counts(string node) =>
        _n.TryGetValue(node, out var e) ? (e.Obs, e.Changes, e.Leaves, e.GapObs, e.WaitObs, e.ExposureMs) : (0, 0, 0, 0, 0, 0);

    /// <summary>その画面の操作の 1 手の長さ（なければ全画面の平均・それもなければ入力なしの観測の平均）</summary>
    public double ActionTurnMs(string node)
    {
        if (_n.TryGetValue(node, out var e) && e.ActTurns > 0) return e.ActMs / e.ActTurns;
        int k = _n.Values.Sum(x => x.ActTurns); if (k > 0) return _n.Values.Sum(x => x.ActMs) / k;
        int o = _n.Values.Sum(x => x.Obs); return o > 0 ? _n.Values.Sum(x => x.ExposureMs) / o : double.NaN;
    }

    /// <summary>操作の 1 手の長さで入力なしでも起きる、変化・別の画面への移動の確率（観測がなければ null＝基準なし）</summary>
    public (double Change, double Leave)? Rates(string node)
    {
        if (!_n.TryGetValue(node, out var e) || e.Obs == 0) return null;
        double t = ActionTurnMs(node); if (!(t > 0)) return null;
        // 1 区間で「変わったか」の 2 値の観測なので、区間あたりの割合 p（Jeffreys で縮める）を平均の区間の長さ d で持ち、
        // 長さ T に 1 − (1 − p)^(T/d) で引き直す。ただし観測した区間より長くは外挿しない（T > d では p のまま）:
        // 実機の記録で、短い区間の変化を一定の率として長い手に外挿すると、ほぼ 1 になり操作の効果がすべて消えた（v0.26.3 #95 の再計算）。観測の範囲内だけで言う。新しい定数なし
        double d = e.ExposureMs / e.Obs;
        double pc = (e.Changes + 0.5) / (e.Obs + 1.0), pl = (e.Leaves + 0.5) / (e.Obs + 1.0);
        double k = Math.Min(1, t / d);
        return (1 - Math.Pow(1 - pc, k), 1 - Math.Pow(1 - pl, k));
    }

    /// <summary>基準そのものの不確かさ（変わった／変わらなかったの 2 値の Dirichlet 情報量。TransitionModel と同じ機構）。観測が増えれば自然に下がる</summary>
    public double Uncertainty(string node)
    {
        var (o, c, _, _, _, _) = Counts(node);
        return TransitionModel.BinaryUncertainty(c, o - c);
    }

    public IEnumerable<string> Nodes => _n.Keys;
    public int TotalObservations => _n.Values.Sum(x => x.Obs);
    public int GapObservations => _n.Values.Sum(x => x.GapObs);
    public int WaitObservations => _n.Values.Sum(x => x.WaitObs);

    public string ExportState()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject(); w.WriteNumber("v", 2); w.WriteStartObject("n");
            foreach (var (k, e) in _n)
            {
                w.WriteStartArray(k);
                w.WriteNumberValue(e.Obs); w.WriteNumberValue(e.Changes); w.WriteNumberValue(e.Leaves); w.WriteNumberValue(e.ExposureMs);
                w.WriteNumberValue(e.ActTurns); w.WriteNumberValue(e.ActMs); w.WriteNumberValue(e.GapObs); w.WriteNumberValue(e.WaitObs); w.WriteNumberValue(e.DelayedObs); w.WriteNumberValue(e.DelayedChanges);
                w.WriteEndArray();
            }
            w.WriteEndObject(); w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public void ImportState(string json)
    {
        using var d = JsonDocument.Parse(json);
        if (!d.RootElement.TryGetProperty("n", out var n)) return;
        // v0.27.1: v1（0.27.0）は手の直後の区間も基準に入れていた（遅れた反応が混ざる）。手と手の間の分は区別できないので、待つ手の分だけでは数を分けられず、v1 は読み捨てて作り直す
        if (!d.RootElement.TryGetProperty("v", out var v) || v.GetInt32() < 2) return;
        foreach (var p in n.EnumerateObject())
        {
            var a = p.Value;
            _n[p.Name] = new N { Obs = a[0].GetInt32(), Changes = a[1].GetInt32(), Leaves = a[2].GetInt32(), ExposureMs = a[3].GetDouble(),
                ActTurns = a[4].GetInt32(), ActMs = a[5].GetDouble(), GapObs = a[6].GetInt32(), WaitObs = a[7].GetInt32(),
                DelayedObs = a.GetArrayLength() > 8 ? a[8].GetInt32() : 0, DelayedChanges = a.GetArrayLength() > 9 ? a[9].GetInt32() : 0 };
        }
    }
}
