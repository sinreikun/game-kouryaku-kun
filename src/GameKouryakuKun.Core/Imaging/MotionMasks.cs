namespace GameKouryakuKun.Core.Imaging;

/// <summary>
/// 画面ごとの常時アニメのマスク（v0.13.1。目の改善でゲームの知識は使わない）。
/// 起動時の較正（NoiseCalibrator）は、そのとき映っていた画面の待機中の動きしか測れない。別の画面で動き続ける領域があると、
/// どの入力の後も画面が落ち着かず、すべての手が時間切れ（Timeout）になる。そこで、時間切れの手の終わりの区間で動き続けたブロックを
/// 画面（の種類）ごとに数え、MinTimeouts 回以上そろったブロックをその画面のマスクに足す（広すぎるときは足さない）。
/// </summary>
public sealed class MotionMaskBook
{
    public int MinTimeouts { get; init; } = 2;            // 根拠: 汎用（1回だけの長い画面効果ではなく、くり返し動き続ける所だけ）
    public double MaxMaskRatio { get; init; } = 0.12;     // 根拠: 起動時の較正（NoiseCalibrator）と同じ上限（画面の 12% を超える動きは隠さない）

    private sealed class Entry { public BlockGrid Grid; public int[] Counts = Array.Empty<int>(); public int Timeouts; public BlockMask? Mask; public bool TooWide; public int ActivityTurns; public double ActivitySum; public double Coverage; }

    /// <summary>
    /// 学習 DB に残す形（v0.15.0。画面の種類ごとの時間的な変化の性質）。起動のたびに学び直さず、続きから使う。
    /// Counts は「時間切れの手の終わりに動き続けた回数」のブロックごとの数。Activity は判定から外した所の中の変化の割合の和と手数
    /// </summary>
    public sealed record Stored(string Key, int Width, int Height, int Block, int Timeouts, int[] Counts, int ActivityTurns, double ActivitySum);

    public IEnumerable<Stored> Export() => _e.Select(kv => new Stored(kv.Key, kv.Value.Grid.Width, kv.Value.Grid.Height, kv.Value.Grid.Block, kv.Value.Timeouts,
        kv.Value.Counts.ToArray(), kv.Value.ActivityTurns, kv.Value.ActivitySum));

    /// <summary>残した性質を読み込む（ブロックの数が合わないものは使わない）。マスクは数え直しと同じ規則で作り直す</summary>
    public int Import(IEnumerable<Stored> rows)
    {
        int n = 0;
        foreach (var r in rows)
        {
            var grid = new BlockGrid(r.Width, r.Height, r.Block);
            if (r.Counts.Length != grid.Count) continue;
            var e = _e[r.Key] = new Entry { Grid = grid, Counts = r.Counts.ToArray(), Timeouts = r.Timeouts, ActivityTurns = r.ActivityTurns, ActivitySum = r.ActivitySum };
            Rebuild(e); n++;
        }
        return n;
    }

    private void Rebuild(Entry e)
    {
        var bits = e.Counts.Select(c => c >= MinTimeouts).ToArray();
        BlockMask? m = bits.Any(b => b) ? new BlockMask(e.Grid, bits).Dilate() : null;
        double cov = m?.CoveredRatio() ?? 0;
        e.TooWide = cov > MaxMaskRatio;
        e.Mask = e.TooWide ? null : m;
        e.Coverage = cov;
    }

    /// <summary>判定から外した所の中の変化の割合を、画面の種類ごとに足す（観測として残す）</summary>
    public void ObserveActivity(string key, double ratio)
    {
        if (!_e.TryGetValue(key, out var e)) return;   // その画面の性質をまだ数えていない（較正のマスクだけ）ときは、Session の集計だけに残す
        e.ActivityTurns++; e.ActivitySum += ratio;
    }

    public IEnumerable<GameKouryakuKun.Core.Exploration.MotionSummary> Summaries() => _e.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
        new GameKouryakuKun.Core.Exploration.MotionSummary(kv.Key, kv.Value.Timeouts, kv.Value.Coverage, kv.Value.Mask != null, kv.Value.TooWide,
            kv.Value.ActivityTurns, kv.Value.ActivityTurns > 0 ? kv.Value.ActivitySum / kv.Value.ActivityTurns : 0));
    private readonly Dictionary<string, Entry> _e = new(StringComparer.Ordinal);

    public sealed record Update(string Key, int Timeouts, int Blocks, double Coverage, bool TooWide, bool Changed);

    /// <summary>時間切れの手の終わりに動き続けたブロックを数える。マスクが変わったかを返す</summary>
    public Update Observe(string key, BlockGrid grid, bool[] lateMotion)
    {
        if (!_e.TryGetValue(key, out var e) || e.Grid != grid || e.Counts.Length != lateMotion.Length)
            _e[key] = e = new Entry { Grid = grid, Counts = new int[lateMotion.Length] };
        e.Timeouts++;
        for (int k = 0; k < lateMotion.Length; k++) if (lateMotion[k]) e.Counts[k]++;
        int before = e.Mask?.CountSet() ?? 0; bool wasWide = e.TooWide;
        Rebuild(e);
        bool changed = (e.Mask?.CountSet() ?? 0) != before || e.TooWide != wasWide;
        return new Update(key, e.Timeouts, e.Mask?.CountSet() ?? 0, e.Coverage, e.TooWide, changed);
    }

    /// <summary>その画面で使うマスク（起動時の較正のマスクと合わせる）</summary>
    public BlockMask? MaskFor(string key, BlockMask? baseMask)
    {
        if (!_e.TryGetValue(key, out var e) || e.Mask == null) return baseMask;
        if (baseMask == null) return e.Mask;
        if (baseMask.Grid != e.Mask.Grid) return baseMask;
        return new BlockMask(baseMask.Grid, baseMask.Bits.Zip(e.Mask.Bits, (a, b) => a || b).ToArray());
    }

    public int Learned => _e.Values.Count(e => e.Mask != null);
}
