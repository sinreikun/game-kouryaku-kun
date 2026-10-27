namespace GameKouryakuKun.Core.Exploration;

/// <summary>
/// v0.28.1: 待つ手（入力なしの対照の観測）の情報の値打ち。固定の確率・固定の価値は置かない。
/// 各成分 = 「入力なしで何が起きるかの今の不確かさ」×「その不確かさが手の効果の見分けに要る割合（この画面で手の後にその変化が起きる割合）」。
/// 不確かさは既存の 2 値の Dirichlet 情報量（TransitionModel.BinaryUncertainty）で、待つ手の観測が増えれば自然に下がる。
/// 要る割合は手の後の変化の割合（Jeffreys）。手の後に変化がなければ、見分ける必要がないので 0 に近い。
/// 成分: 画面の変化（Visual）・画面の移り（Leave＝遷移）・画素の区画（Pixel）・文字の区画（Text）。
/// 合計 = 1 − Π(1 − 成分)（別々の問いの少なくとも 1 つが分かる見込み。0〜1 で、既存の不確かさの項にそのまま入る。新しい重みなし）
/// </summary>
public sealed record WaitInformation(double Visual, double Leave, double Pixel, double Text)
{
    /// <summary>v0.30.8（P-2）: 局所の出来事（候補の移動・見た目・出現・消失・文字・数字）の成分。6 つのうちいちばん分かる出来事の値（足し合わせて膨らませない）</summary>
    public double Local { get; init; }
    public double Total => 1 - (1 - C(Visual)) * (1 - C(Leave)) * (1 - C(Pixel)) * (1 - C(Text)) * (1 - C(Local));
    private static double C(double x) => Math.Clamp(double.IsFinite(x) ? x : 0, 0, 1);
    public static readonly WaitInformation None = new(0, 0, 0, 0);

    /// <summary>成分 1 つ: 入力なしの観測（yes/n）の不確かさ × 手の後の割合（actYes/actN。手の観測がなければ 0）</summary>
    public static double Part(int natYes, int natN, int actYes, int actN) =>
        actN <= 0 ? 0 : TransitionModel.BinaryUncertainty(natYes, natN - natYes) * ((actYes + 0.5) / (actN + 1.0));
}
