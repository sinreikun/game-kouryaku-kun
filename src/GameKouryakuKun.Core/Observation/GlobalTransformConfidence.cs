namespace GameKouryakuKun.Core.Observation;

/// <summary>
/// v0.32.9 (t108u): 画面全体の 1 つの変換（共通のずれ・ずれの場）で、離れた多くの局所のずれがどれだけ一貫して説明できるかの証拠と確からしさ。
/// Decision は none（共通の変換なし）/ zero（動かない）/ translation / field。確率は 4 つの仮説に同じ事前を置いた BIC 近似の事後
/// </summary>
public sealed record GlobalTransformConfidence
{
    public string Decision { get; init; } = "none";
    /// <summary>受け入れたか（1 つのずれ〔translation〕はこれまでの平行移動の判定だけで受け入れる。ここで translation が最も確からしくても、その判定で採られなかったので受け入れない）</summary>
    public bool Accepted { get; init; }
    /// <summary>決めた方法（model_comparison = 仮説の比べ / translation_check = これまでの平行移動の判定）</summary>
    public string Method { get; init; } = "model_comparison";
    public double PField { get; init; }
    public double PTranslation { get; init; }
    public double PZero { get; init; }
    public double PNone { get; init; }
    /// <summary>選んだ仮説と、次に良い仮説との対数オッズ（ln のベイズ因子の BIC 近似）</summary>
    public double LogOdds { get; init; }
    /// <summary>選んだ仮説の係数（縮小の点。dx の 1, X, Y, XY → dy の 1, X, Y, XY。X, Y は −1〜1 の画面の座標）</summary>
    public double[] Field { get; init; } = new double[8];
    /// <summary>局所の観測点（区画ごとのずれ）のうち、変換に従う確率が過半の点の数</summary>
    public int Inliers { get; init; }
    public int Points { get; init; }
    /// <summary>画面を 4×3 に分けたうち、従う点のある割合（偏りでないこと）</summary>
    public double Coverage { get; init; }
    /// <summary>従う点の、観測したずれと変換の予測の差（縮小の点）の中央値</summary>
    public double MedianResidual { get; init; }
    /// <summary>従う点の区画の差が補正でどれだけ減ったか（1 − 後/前）</summary>
    public double ErrorReduction { get; init; }
    /// <summary>前に受け入れた変換の中央のずれとの差（縮小の点。前がなければ null）＝時間の続き（記録だけ）</summary>
    public double? Continuity { get; init; }
    public string Note => $"gt={Decision} acc={Accepted} cont={Continuity} pF={PField} pT={PTranslation} pZ={PZero} pN={PNone} lo={LogOdds} f=({string.Join(",", Field)}) in={Inliers}/{Points} cov={Coverage} res={MedianResidual} red={ErrorReduction}";
}
