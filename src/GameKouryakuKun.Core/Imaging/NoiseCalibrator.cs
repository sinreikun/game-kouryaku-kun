namespace GameKouryakuKun.Core.Imaging;

public sealed record CalibrationResult(
    ChangeCriteria Criteria, BlockMask? IdleMask, int Samples,
    double NoiseMaxRatio, double NoiseMaxComponent, double ConsecutiveMaxRatio, double ConsecutiveMaxComponent,
    IReadOnlyList<string> Notes)
{
    public override string ToString() =>
        $"較正 {Samples}枚: ノイズ最大 率={NoiseMaxRatio:0.#####} 連結={NoiseMaxComponent:0.#####}（連続フレーム 率={ConsecutiveMaxRatio:0.#####} 連結={ConsecutiveMaxComponent:0.#####}） " +
        $"→ {Criteria} ／ {(IdleMask == null ? "アニメマスクなし" : IdleMask.ToString())}";
}

/// <summary>
/// 何も入力していない間のフレーム列から、常時アニメする領域（待機モーション・水面・点滅など）と
/// 残留ノイズの大きさを測り、判定基準を自動設定する。ゲーム固有の知識は使わない。
/// </summary>
public static class NoiseCalibrator
{
    public static CalibrationResult Calibrate(IReadOnlyList<Frame> idle, DiffOptions? options = null,
        ChangeCriteria? floor = null, double maxMaskRatio = 0.12)
    {
        var o = options ?? DiffOptions.Default;
        var f = floor ?? ChangeCriteria.Default;
        var notes = new List<string>();
        var frames = idle.Where(x => x != null).ToList();
        if (frames.Count < 2)
        {
            notes.Add("較正用フレームが2枚未満のため既定値を使用");
            return new CalibrationResult(f, null, frames.Count, 0, 0, 0, 0, notes);
        }
        if (frames.Any(x => !x.SameSize(frames[0])))
        {
            notes.Add("較正中にウィンドウサイズが変わったため、最初のサイズのフレームだけを使用");
            frames = frames.Where(x => x.SameSize(frames[0])).ToList();
        }
        var grid = BlockGrid.For(frames[0], o.Block);
        var pairs = new List<(Frame A, Frame B, bool Consecutive)>();
        for (int i = 1; i < frames.Count; i++) { pairs.Add((frames[i - 1], frames[i], true)); if (i > 1) pairs.Add((frames[0], frames[i], false)); }

        // 1) どのブロックが待機中に変化するか
        var freq = new int[grid.Count];
        foreach (var (a, b, _) in pairs)
        {
            var d = FrameDiff.Compare(a, b, o);
            if (d.ChangedBlocks == null) continue;
            for (int k = 0; k < freq.Length; k++) if (d.ChangedBlocks[k]) freq[k]++;
        }
        BlockMask? mask = null;
        if (freq.Any(c => c > 0))
        {
            var m = new BlockMask(grid, freq.Select(c => c > 0).ToArray()).Dilate();
            double cov = m.CoveredRatio();
            if (cov <= maxMaskRatio) { mask = m; notes.Add($"待機中に変化する領域をマスク（画面の{cov:P1}）"); }
            else notes.Add($"待機中の変化領域が広すぎる（画面の{cov:P1} > {maxMaskRatio:P0}）ためマスクせず閾値で吸収");
        }

        // 2) マスク適用後の残留ノイズ
        double nR = 0, nC = 0, cR = 0, cC = 0;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (a, b, consecutive) = pairs[i];
            var d = FrameDiff.Compare(a, b, o, mask);
            if (!d.Comparable) continue;
            nR = Math.Max(nR, d.ChangedRatio); nC = Math.Max(nC, d.LargestComponentRatio);
            if (consecutive) { cR = Math.Max(cR, d.ChangedRatio); cC = Math.Max(cC, d.LargestComponentRatio); }
        }
        var crit = new ChangeCriteria
        {
            ChangeRatio = Math.Round(Math.Max(f.ChangeRatio, nR + 0.002), 5),
            ChangeComponent = Math.Round(Math.Max(f.ChangeComponent, nC * 2), 5),
            StillRatio = Math.Round(Math.Max(f.StillRatio, cR * 1.5 + 0.001), 5),
            StillComponent = Math.Round(Math.Max(f.StillComponent, cC * 2), 5),
        };
        if (crit.StillComponent > crit.ChangeComponent) crit = crit with { StillComponent = crit.ChangeComponent };
        if (nC * 2 > f.ChangeComponent) notes.Add($"残留ノイズが大きいため変化判定の連結閾値を {crit.ChangeComponent:0.####} に引き上げ");
        return new CalibrationResult(crit, mask, frames.Count, nR, nC, cR, cC, notes);
    }
}
