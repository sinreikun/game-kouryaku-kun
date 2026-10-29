namespace GameKouryakuKun.Core.Throughput;

/// <summary>Action に対するフレームの位置（docs/throughput-parallel-sensing-design.md §7.1）</summary>
public enum FramePosition { Pre, Post, Settle, Gap }

/// <summary>観測の出どころ。missing は「一度も作られなかった」ものだけ（deadline 超過は late）</summary>
public enum SensorProvenance { Observed, Inherited, Pending, Late, Missing }

/// <summary>
/// すべての観測器の結果が持つ必須項目（C1）。帰属は CaptureMs と Step/Seq/FrameId。FinishMs は遅延の計測だけに使う。
/// </summary>
public sealed record SensorResult(
    long FrameId, double CaptureMs, int Step, long Seq, FramePosition Position, string Sensor,
    double StartMs, double FinishMs, SensorProvenance Provenance, string? Detail = null)
{
    public double LatencyMs => FinishMs - StartMs;
    public static string Name(FramePosition p) => p.ToString().ToLowerInvariant();
    public static string Name(SensorProvenance p) => p.ToString().ToLowerInvariant();
}

/// <summary>
/// 非同期 Observation の台帳（C1）。Action 時点で (step, sensor) を pending として開き、結果が届いたら元の手に合流させる。
/// 開いた時の frame/seq と結果の frame/seq が違えば対応違反（Action→Observation→Result の対応を壊した＝Health FAIL）。
/// 結果が届いた時点で、より後の手が既に始まっていれば late（追補）。届かずに終わったものは drain 後 pending_unresolved。
/// スレッド安全（観測器のスレッドから Complete が呼ばれる）。
/// </summary>
public sealed class ObservationLedger
{
    private sealed class Entry
    {
        public long FrameId; public double CaptureMs; public long Seq; public FramePosition Position; public double OpenedMs;
        public SensorResult? Result;
    }
    private readonly object _lock = new();
    private readonly Dictionary<(int Step, string Sensor), Entry> _open = new();
    private readonly List<SensorResult> _resolved = new();
    private int _currentStep = int.MinValue;
    private readonly Dictionary<int, (int Open, int Ok, int Bad)> _perStep = new();   // v0.40.1: 手ごとの観測器の状態（因果保持は手の数で数える）
    private void Track(int step, int open, int ok, int bad)
    {
        var c = _perStep.GetValueOrDefault(step);
        _perStep[step] = (c.Open + open, c.Ok + ok, c.Bad + bad);
    }

    public long Opened, Observed, Inherited, LateAmended, Missing, Violations, DecidedWithPending;

    /// <summary>手 step の入力を始める（これより前の手の未完の観測は、以後届いたら late）</summary>
    public void BeginAction(int step)
    {
        lock (_lock)
        {
            _currentStep = step;
            if (_open.Keys.Any(k => k.Step < step)) DecidedWithPending++;
        }
    }

    public int CurrentStep { get { lock (_lock) return _currentStep; } }

    /// <summary>手 step の観測器 sensor の結果を pending として開く</summary>
    public void Open(int step, long seq, string sensor, long frameId, double captureMs, FramePosition pos, double nowMs)
    {
        lock (_lock)
        {
            if (_open.ContainsKey((step, sensor))) { Violations++; Track(step, 0, 0, 1); }   // 同じ手・同じ観測器を二重に開いた（前の結果の対応を壊す）
            _open[(step, sensor)] = new Entry { FrameId = frameId, CaptureMs = captureMs, Seq = seq, Position = pos, OpenedMs = nowMs };
            Opened++; Track(step, 1, 0, 0);
        }
    }

    public int PendingCount { get { lock (_lock) return _open.Count; } }

    /// <summary>
    /// 結果を合流させる。開いた手と frame/seq が一致しなければ対応違反として記録し、結果は合流させない（別の手に混ぜない）。
    /// 返り値: 確定した provenance を持つ結果（違反なら null）
    /// </summary>
    public SensorResult? Complete(int step, string sensor, long frameId, long seq, double startMs, double finishMs, bool inherited = false, string? detail = null)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue((step, sensor), out var e) || e.FrameId != frameId || e.Seq != seq) { Violations++; Track(step, 0, 0, 1); return null; }
            _open.Remove((step, sensor)); Track(step, -1, 1, 0);
            var prov = _currentStep > step ? SensorProvenance.Late : inherited ? SensorProvenance.Inherited : SensorProvenance.Observed;
            if (prov == SensorProvenance.Late) LateAmended++; else if (prov == SensorProvenance.Inherited) Inherited++; else Observed++;
            var r = new SensorResult(e.FrameId, e.CaptureMs, step, e.Seq, e.Position, sensor, startMs, finishMs, prov, detail);
            _resolved.Add(r);
            return r;
        }
    }

    /// <summary>観測器が結果を作れなかった（例外など）。一度も作られなかったものだけ missing</summary>
    public SensorResult? Fail(int step, string sensor, double nowMs, string reason)
    {
        lock (_lock)
        {
            if (!_open.Remove((step, sensor), out var e)) { Violations++; Track(step, 0, 0, 1); return null; }
            Missing++; Track(step, -1, 0, 1);
            var r = new SensorResult(e.FrameId, e.CaptureMs, step, e.Seq, e.Position, sensor, e.OpenedMs, nowMs, SensorProvenance.Missing, reason);
            _resolved.Add(r);
            return r;
        }
    }

    /// <summary>drain 後もまだ開いているもの（未解決）。実行終了時に呼ぶ</summary>
    public IReadOnlyList<(int Step, string Sensor, long FrameId)> Unresolved()
    {
        lock (_lock) return _open.Select(k => (k.Key.Step, k.Key.Sensor, k.Value.FrameId)).ToList();
    }

    /// <summary>取得時刻順（完了順ではない）に並べた合流済みの結果</summary>
    public IReadOnlyList<SensorResult> Timeline()
    {
        lock (_lock) return _resolved.OrderBy(r => r.CaptureMs).ThenBy(r => r.Step).ThenBy(r => r.Sensor, StringComparer.Ordinal).ToList();
    }

    /// <summary>因果を保った観測器の結果（observed/inherited/late）の数（観測器の数だけ数える）</summary>
    public long CausallyIntactResults { get { lock (_lock) return Observed + Inherited + LateAmended; } }

    /// <summary>
    /// 因果を保った観測の数＝手の数（v0.40.1）。その手のすべての観測器の結果が合流し（未完なし）、missing・対応違反が 1 つもない手。
    /// v0.40.0 は観測器の結果の数を数えていたため、観測器が 4 つの設定 D で手の約 4 倍に膨らんでいた（実機 2026-10-29）。
    /// </summary>
    public long CausallyIntact { get { lock (_lock) return _perStep.Count(k => k.Value.Open == 0 && k.Value.Bad == 0 && k.Value.Ok > 0); } }

    /// <summary>v0.41.0: その手の観測が因果を保って完成したか（観測器が 1 つもなければ null）。手ごとの設定の比較で使う</summary>
    public bool? StepIntact(int step) { lock (_lock) return _perStep.TryGetValue(step, out var v) ? v.Open == 0 && v.Bad == 0 && v.Ok > 0 : null; }

    /// <summary>late-amend 成功率: late で合流できた / (late + 未解決)。late も未解決もなければ 1</summary>
    public double LateAmendSuccessRate()
    {
        lock (_lock)
        {
            long un = _open.Count(k => k.Key.Step < _currentStep);
            long d = LateAmended + un;
            return d == 0 ? 1 : (double)LateAmended / d;
        }
    }
}
