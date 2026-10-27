namespace GameKouryakuKun.Core.Imaging;

/// <summary>
/// 「変化あり」「静止」の判定基準。どちらも『変化率』と『最大連結領域』の両方で判定する（Phase 0 の取得・変化検出の性能評価で確認した方式。U5 のシステム性能評価で、画面の意味は使っていない）。
///  - 変化あり: 基準画面からの差分が ChangeRatio を超え、かつ最大連結領域が ChangeComponent 以上
///  - 静止   : 連続フレーム間の差分が StillRatio 以下、または最大連結領域が StillComponent 未満
/// 値はノイズ較正（NoiseCalibrator）で実測ノイズより上に自動調整される。
/// </summary>
public sealed record ChangeCriteria
{
    public double ChangeRatio { get; init; } = 0.003;  // 根拠: 実機性能評価（Phase 0/1: 入力後の画面変化の検出の正否のみ・U5）＋合成テスト。ノイズ較正で上書きされる下限
    public double ChangeComponent { get; init; } = 0.015;  // 根拠: 同上（最大連結領域の下限）
    public double StillRatio { get; init; } = 0.002;  // 根拠: 同上（静止とみなす変化率）
    public double StillComponent { get; init; } = 0.006;  // 根拠: 同上（静止とみなす最大連結領域）
    public static readonly ChangeCriteria Default = new();

    public bool IsChanged(DiffResult d) => !d.Comparable || (d.ChangedRatio > ChangeRatio && d.LargestComponentRatio >= ChangeComponent);
    public bool IsStill(DiffResult d) => d.Comparable && (d.ChangedRatio <= StillRatio || d.LargestComponentRatio < StillComponent);

    public override string ToString() =>
        $"変化: 率>{ChangeRatio:0.####} かつ 連結≥{ChangeComponent:0.####} ／ 静止: 率≤{StillRatio:0.####} または 連結<{StillComponent:0.####}";
}
