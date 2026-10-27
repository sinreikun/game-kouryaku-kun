namespace GameKouryakuKun.Core.Observation;

/// <summary>
/// v0.32.0: 追跡の評価（追跡自身の手がかりから。意味なし・判定や学習には使わない）。候補の大きさ（区画数）で分けて数える。
/// 大きさの区分は見せ方だけで、どの候補も観測・学習から外さない
/// </summary>
public sealed class TrackQuality
{
    public static readonly string[] Bins = { "1-2", "3-5", "6-15", "16+" };
    public static int Bin(int cells) => cells <= 2 ? 0 : cells <= 5 ? 1 : cells <= 15 ? 2 : 3;   // 根拠: 見せ方の目盛り（1 区画の幅・縦横の形・小さい物・大きい物）
    public readonly long[] StrongPred = new long[4], Kept = new long[4], WeakContinued = new long[4], Assigned = new long[4], SuspectedSplit = new long[4], Created = new long[4], Lost = new long[4], Returned = new long[4];

    /// <summary>続き＝前の手に強い手がかりの相手がいた候補のうち、その相手の番号を続けた割合。割れの疑い・弱い続き・再同定の割合</summary>
    public string Summary()
    {
        static string R(long a, long b) => b == 0 ? "-" : $"{(double)a / b:0.00}";
        return string.Join("／", Enumerable.Range(0, 4).Select(i =>
            $"{Bins[i]} 区画: 続き {R(Kept[i], StrongPred[i])}（{Kept[i]}/{StrongPred[i]}）・割れの疑い {SuspectedSplit[i]}/{Created[i]}・弱い続き {WeakContinued[i]}/{Assigned[i]}・再同定 {R(Returned[i], Lost[i])}（{Returned[i]}/{Lost[i]}）"));
    }
}
