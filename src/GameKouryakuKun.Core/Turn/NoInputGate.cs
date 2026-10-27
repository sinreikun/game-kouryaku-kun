using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Turn;

/// <summary>手と手の間（入力なし）の変化を確かめた結果</summary>
/// <remarks>v0.27.1: NetChanged＝手の「正味の変化」（TurnDetector の NetChange）と同じ基準（IsChanged）で変わったか。Changed は従来どおり「静止でない」（IsStill の否定。選び直しに使う）</remarks>
public sealed record NoInputCheck<T>(bool Changed, bool Transition, T? Classified, string? Node, bool Rechoose, double Ratio, bool NetChanged = false);

/// <summary>
/// 入力なしの変化（v0.14.1 で Session に置いたものを、合成試験のために切り出した。v0.15.0）。
/// 前の手の結果の画面と、次の入力の直前の画面を比べる。変わっていれば今の画面を割り当て直し、別の画面（入力なしの遷移）なら、
/// 前の画面向けに選んだ手は送らずに選び直す（連続 maxRechoose 回まで。その後は今の画面のまま手を送る）。ゲームの知識は使わない
/// </summary>
public static class NoInputGate
{
    public static NoInputCheck<T> Check<T>(Frame? prevAfter, Frame baseline, ChangeCriteria criteria, DiffOptions? diff, BlockMask? mask,
        string curNode, Func<Frame, T> classify, Func<T, string> nodeOf, int rechooseSoFar, int maxRechoose)
    {
        if (prevAfter == null) return new(false, false, default, null, false, 0);
        var d = FrameDiff.Compare(prevAfter, baseline, diff, mask);
        if (criteria.IsStill(d)) return new(false, false, default, null, false, d.ChangedRatio);
        var c = classify(baseline); var node = nodeOf(c);
        bool transition = node != curNode;
        return new(true, transition, c, node, transition && rechooseSoFar < maxRechoose, d.ChangedRatio, criteria.IsChanged(d) && d.Comparable);
    }
}
