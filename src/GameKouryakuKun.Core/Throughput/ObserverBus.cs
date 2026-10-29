using System.Collections.Concurrent;

namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// T5: 複数の観測器を並列に動かし、時刻付きの結果（SensorResult）を共有のバスで Supervisor へ送る（§5）。
/// ・各観測器は自分の順番で 1 つずつ処理（同じ観測器の結果の順序を保つ）。観測器どうしは並列。
/// ・背圧: 観測器ごとの待ち行列の容量を超えたとき、間引けるのは途中（Post）のフレームの解析だけ（coalesced として件数と frame ID を残す）。
///   静止確定（Settle）・基準（Pre）・入力なし区間（Gap）は落とさず、空くまで待つ（待った回数を数える）。
/// ・deadline 超過は late（落とさない。台帳で元の手へ合流）。
/// ・完了順ではなく取得時刻で並べ直して Supervisor に渡す（Timeline）。同じ観測器で取得時刻が逆転して届いた件数を reordered として数える。
/// </summary>
public sealed class ObserverBus
{
    public sealed record SensorConfig(int Capacity, double DeadlineMs);

    public static readonly IReadOnlyDictionary<string, SensorConfig> Defaults = new Dictionary<string, SensorConfig>(StringComparer.Ordinal)
    {
        ["ocr"] = new(4, 1000),          // 根拠: 汎用（#159 の OCR p90 711・最大 1034 ms を上回る締め切り。容量は数手分）
        ["frame_diff"] = new(8, 200),    // 根拠: 汎用（1 フレームの差分は数 ms〜数十 ms）
        ["noise"] = new(8, 200),         // 根拠: 汎用（区画の差分と数え上げだけ）
        ["noise_gap"] = new(8, 200),     // 根拠: 汎用（同上。入力なし区間の観測は別の観測器として台帳に開く）
    };

    private sealed class Lane
    {
        public SensorConfig Config = new(4, 1000);
        public Task Tail = Task.CompletedTask;
        public int Queued, MaxQueued;
        public long Done, Late, Coalesced, Waited, Reordered, Failed;
        public double LastCapture = double.NegativeInfinity;
        public readonly List<double> Latency = new();
        public readonly List<long> CoalescedFrames = new();
    }

    private readonly ConcurrentDictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
    private readonly ObservationLedger _ledger;
    private readonly Func<double> _now;
    private readonly object _lock = new();
    private readonly List<SensorResult> _published = new();
    public long SettleDropped;   // 静止確定の結果を落とした数（常に 0 のはず。0 以外は Health FAIL）
    public event Action<SensorResult>? Published;

    public ObserverBus(ObservationLedger ledger, Func<double> now, IReadOnlyDictionary<string, SensorConfig>? configs = null)
    {
        _ledger = ledger; _now = now;
        foreach (var (k, v) in configs ?? Defaults) _lanes[k] = new Lane { Config = v };
    }

    private Lane L(string sensor) => _lanes.GetOrAdd(sensor, _ => new Lane());

    /// <summary>
    /// 観測器 sensor に (step, seq, frame) の解析を出す。work は結果の詳細（ログ用）を返す。例外は missing。
    /// 返り値: 受け付けたか（途中フレームを間引いたときだけ false）
    /// </summary>
    public bool Submit(string sensor, int step, long seq, long frameId, double captureMs, FramePosition pos, Func<string?> work)
    {
        var lane = L(sensor);
        lock (lane)
        {
            if (lane.Queued >= lane.Config.Capacity)
            {
                if (pos == FramePosition.Post) { lane.Coalesced++; if (lane.CoalescedFrames.Count < 64) lane.CoalescedFrames.Add(frameId); return false; }
                lane.Waited++;
            }
        }
        // 静止確定などは落とさない: 容量を超えていれば前の処理が終わるのを待つ（背圧）
        while (true) { lock (lane) if (lane.Queued < lane.Config.Capacity) break; lane.Tail.Wait(50); }
        _ledger.Open(step, seq, sensor, frameId, captureMs, pos, _now());
        lock (lane)
        {
            lane.Queued++; lane.MaxQueued = Math.Max(lane.MaxQueued, lane.Queued);
            var prev = lane.Tail;
            lane.Tail = prev.ContinueWith(_ =>
            {
                double start = _now();
                SensorResult? r;
                try
                {
                    var detail = work();
                    r = _ledger.Complete(step, sensor, frameId, seq, start, _now(), detail: detail);
                }
                catch (Exception e) { lock (lane) lane.Failed++; r = _ledger.Fail(step, sensor, _now(), e.GetType().Name); }
                lock (lane) lane.Queued--;
                if (r != null) Publish(r);
            }, TaskScheduler.Default);
        }
        return true;
    }

    /// <summary>結果をバスに載せる（観測器の外で作られた結果、例えば OCR の台帳の結果も同じ口から）</summary>
    public void Publish(SensorResult r)
    {
        var lane = L(r.Sensor);
        lock (lane)
        {
            lane.Done++;
            lane.Latency.Add(r.FinishMs - r.CaptureMs);
            if (r.FinishMs - r.CaptureMs > lane.Config.DeadlineMs) lane.Late++;
            if (r.CaptureMs < lane.LastCapture) lane.Reordered++;
            lane.LastCapture = Math.Max(lane.LastCapture, r.CaptureMs);
        }
        lock (_lock) _published.Add(r);
        Published?.Invoke(r);
    }

    /// <summary>すべての観測器の処理が終わるまで待つ（実行の終わりの drain）</summary>
    public void Drain(int timeoutMs = 10000)
    {
        var tails = _lanes.Values.Select(l => { lock (l) return l.Tail; }).ToArray();
        Task.WaitAll(tails, timeoutMs);
    }

    /// <summary>取得時刻順の結果（Supervisor へ渡す並び）</summary>
    public IReadOnlyList<SensorResult> Timeline()
    {
        lock (_lock) return _published.OrderBy(r => r.CaptureMs).ThenBy(r => r.Step).ThenBy(r => r.Sensor, StringComparer.Ordinal).ToList();
    }

    public long Late => _lanes.Values.Sum(l => l.Late);
    public long Coalesced => _lanes.Values.Sum(l => l.Coalesced);
    public long Reordered => _lanes.Values.Sum(l => l.Reordered);
    public long Waited => _lanes.Values.Sum(l => l.Waited);

    private static double P(List<double> xs, double q)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s[(int)Math.Floor(q * (s.Count - 1))];
    }

    public string SensorsSummary() => string.Join(" ", _lanes.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k =>
    {
        lock (k.Value) return $"{k.Key}:n{k.Value.Done}/p50 {P(k.Value.Latency, 0.5):0}/p90 {P(k.Value.Latency, 0.9):0}ms/late {k.Value.Late}/q{k.Value.MaxQueued}/wait {k.Value.Waited}/fail {k.Value.Failed}";
    }));
}
