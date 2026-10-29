namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// T4 / C2: Raw Experience（append-only・潰さない・後で再解釈できる根拠と参照）。
/// TurnId は memory.sqlite の turn 行（縮小フレーム・観測の行）への参照。EvidenceJson は静止判定などの観測根拠。
/// RepVersion は作った時点の表現の版（集約のシグネチャ関数の版）。Tier は保持階層（hot のみ実装。warm/cold は設計 §8）。
/// </summary>
public sealed record RawExperience(
    long Seq, int Step, long? TurnId, long FrameId, double CaptureMs, string NodeBefore, string Action, string? NodeAfter,
    bool NetChange, string Kind, string EvidenceJson, int RepVersion = 1, string Tier = "hot");

/// <summary>集約の鍵（現在の表現で同一と判断した状況・手・結果）</summary>
public readonly record struct AggKey(string State, string Action, string Outcome);

public sealed class AggStats
{
    public long Count; public long FirstSeq; public long LastSeq;
}

/// <summary>
/// T4 / C2: Aggregated Experience / Index。現在の表現（Signature）で同一と判断した経験を 1 行にまとめ count・最初/最後の seq を持つ。
/// Raw からいつでも作り直せる（Rebuild）。新しい表現の版は別の索引として並存させ、古い集約は消さない（再集約で情報は失われない）。
/// </summary>
public sealed class AggregateIndex
{
    public delegate AggKey SignatureFn(RawExperience r);

    /// <summary>表現 v1: 状態 = 手の前の node、結果 = 手の後の node と正味の変化の有無。OCR など後から合流する層は使わない（v2 で Raw から再集約）</summary>
    public static AggKey SignatureV1(RawExperience r) => new(r.NodeBefore, r.Action, (r.NodeAfter ?? "-") + "|" + (r.NetChange ? "c" : "n") + "|" + r.Kind);

    public int RepVersion { get; }
    public SignatureFn Signature { get; }
    private readonly Dictionary<AggKey, AggStats> _m = new();
    public long Added, Duplicates;

    public AggregateIndex(int repVersion = 1, SignatureFn? signature = null) { RepVersion = repVersion; Signature = signature ?? SignatureV1; }

    /// <summary>集約に足す。既出の鍵なら true（重複）</summary>
    public bool Add(RawExperience r, out AggKey key, out AggStats stats)
    {
        key = Signature(r);
        Added++;
        if (_m.TryGetValue(key, out stats!)) { stats.Count++; stats.LastSeq = r.Seq; Duplicates++; return true; }
        _m[key] = stats = new AggStats { Count = 1, FirstSeq = r.Seq, LastSeq = r.Seq };
        return false;
    }

    /// <summary>保存済みの集約を読み込む（Storage 用）</summary>
    public void Put(AggKey k, AggStats s) => _m[k] = s;

    public int Keys => _m.Count;
    public double DuplicateRatio => Added == 0 ? 0 : (double)Duplicates / Added;
    public IReadOnlyDictionary<AggKey, AggStats> Entries => _m;

    /// <summary>Raw をリプレイして新しい表現の集約を作る（新しい観測器・特徴が入った時の再分類・再集約）</summary>
    public static AggregateIndex Rebuild(IEnumerable<RawExperience> raw, int repVersion, SignatureFn signature)
    {
        var ix = new AggregateIndex(repVersion, signature);
        foreach (var r in raw.OrderBy(r => r.Seq)) ix.Add(r, out _, out _);
        return ix;
    }

    /// <summary>二つの集約が同じ内容か（Raw からの再生成の検証）</summary>
    public bool SameAs(AggregateIndex o) =>
        _m.Count == o._m.Count && _m.All(k => o._m.TryGetValue(k.Key, out var v) && v.Count == k.Value.Count && v.FirstSeq == k.Value.FirstSeq && v.LastSeq == k.Value.LastSeq);
}
