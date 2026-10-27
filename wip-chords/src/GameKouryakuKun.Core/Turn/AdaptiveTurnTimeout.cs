namespace GameKouryakuKun.Core.Turn;

/// <summary>
/// 1手の待ち時間を、この画面で実際にかかった時間の統計から決める（自己調整。特定のゲームの値は使わない）。
///  - 上限（MaxMs 相当）: 落ち着いた手の所要時間の 90 パーセンタイルの Factor 倍を [MinMs, MaxMs] に収める
///  - 「変化なし」とみなす待ち時間: 変化が始まるまでの時間の 95 パーセンタイルの NoChangeFactor 倍を [NoChangeMinMs, NoChangeMaxMs] に収める
/// 標本が少ないうちは初期値のまま。
/// 揺れの抑制（v0.9.0）: 統計から出した目標値（Raw*）に対し、実際に使う値は「上げるのはすぐ・下げるのは目標が
/// DownBand より下にある状態が DownPatience 回続いたときだけ」とする（ヒステリシス）。下げる判定には上げる判定より
/// 高い分位（DownQuantile）を使う。遅い応答がまれに混ざると、通常の分位は速い側と遅い側を行き来するため（v0.8.0 の実機診断）。
/// 待ち時間が短すぎると判定を誤り、長すぎると時間だけが余計にかかるため、誤りの側には寄せない。使う値は常に目標値以上。
/// </summary>
public sealed class AdaptiveTurnTimeout
{
    private readonly List<double> _settled = new(), _firstChange = new();
    public double MinMs { get; init; } = 1500;          // 根拠: 汎用（上限の下限。数フレームの演出でも時間切れにしない）
    public double MaxMs { get; init; } = 8000;          // 根拠: 汎用（上限の初期値・最大。長い画面効果でも待つ）
    public double Factor { get; init; } = 3;            // 根拠: 汎用（実測の分布の裾に余裕を持たせる倍率）
    public double NoChangeInitialMs { get; init; } = 1200;  // 根拠: 汎用（一般的な応答遅延＝数百ms の数倍）。自己調整で上書き
    public double NoChangeMinMs { get; init; } = 600;   // 根拠: 汎用（応答遅延の短い画面での下限）
    public double NoChangeMaxMs { get; init; } = 4000;  // 根拠: 汎用（遅い応答でも「変化なし」と誤らない上限）
    public double NoChangeFactor { get; init; } = 2;    // 根拠: 汎用（実測の分布の裾に余裕を持たせる倍率）
    public int MinSamples { get; init; } = 20;          // 根拠: 汎用（分布を推定する最小の標本数）
    public int Window { get; init; } = 200;             // 根拠: 汎用（最近の標本だけを使う窓）

    public double DownBand { get; init; } = 0.2;        // 根拠: 汎用（目標が今の値より2割以上低いときだけ下げる候補にする）
    public int DownPatience { get; init; } = 20;        // 根拠: 汎用（下げる前に低い目標が続くことを確かめる回数＝標本の窓の1/10）
    public double DownQuantile { get; init; } = 0.99;   // 根拠: 汎用（下げる判定は窓の上位1%を除いたほぼ最大で行う）

    /// <summary>統計から出した上限の目標値（揺れの抑制の前）</summary>
    public double RawCurrent => _settled.Count < MinSamples ? MaxMs : Math.Clamp(Factor * Percentile(_settled, 0.9), MinMs, MaxMs);
    /// <summary>統計から出した「変化なし」の待ち時間の目標値（揺れの抑制の前）</summary>
    public double RawNoChange => _firstChange.Count < MinSamples ? NoChangeInitialMs
        : Math.Clamp(NoChangeFactor * Percentile(_firstChange, 0.95), NoChangeMinMs, NoChangeMaxMs);

    private double? _cur, _noChange; private int _curLow, _noChangeLow; private double _curLowMax, _noChangeLowMax;
    /// <summary>1手の上限（実際に使う値）</summary>
    /// v0.20.0: 「変化なし」の待ち時間より短いと、変化の無い手がすべて時間切れになる（実機 v0.19.7 で max 3015ms＜変化なし 3600ms、時間切れ 56%）。
    /// 上限は「変化なし」の待ち時間の NoChangeHeadroom 倍を下回らないようにする（MaxMs まで）
    public double Current => Math.Min(MaxMs, Math.Max(_cur ?? RawCurrent, NoChangeHeadroom * NoChangeCurrent));
    public double NoChangeHeadroom { get; init; } = 1.25;   // 根拠: 汎用（「変化なし」の判定を上限より先に必ず終えられる余裕）
    /// <summary>入力後、この時間内に変化が始まらなければ「変化なし」とする待ち時間（実際に使う値）</summary>
    public double NoChangeCurrent => _noChange ?? RawNoChange;
    /// <summary>実際に使う値を変えた回数（揺れの目安）</summary>
    public int AppliedChanges { get; private set; }

    // 初期値（標本が足りない間の値）から統計の値へは、最初の1回はすぐ移る（初期値は観測に基づかないため）
    private double Damp(double? applied, double target, double downTarget, ref int low, ref double lowMax, bool enoughNow, ref bool hadEnough)
    {
        downTarget = Math.Max(target, downTarget);
        if (applied is not double a || (enoughNow && !hadEnough)) { hadEnough = enoughNow; low = 0; return target; }
        if (target >= a) { low = 0; if (target > a) AppliedChanges++; return target; }   // 上げるのはすぐ
        if (downTarget > a * (1 - DownBand)) { low = 0; return a; }
        lowMax = low == 0 ? downTarget : Math.Max(lowMax, downTarget);
        if (++low < DownPatience) return a;
        low = 0; AppliedChanges++;
        return lowMax;   // 低い目標が続いた間の最大値まで下げる
    }

    private bool _curStat, _noChangeStat;
    private void UpdateCurrent() => _cur = Damp(_cur, RawCurrent,
        _settled.Count < MinSamples ? MaxMs : Math.Clamp(Factor * Percentile(_settled, DownQuantile), MinMs, MaxMs), ref _curLow, ref _curLowMax, _settled.Count >= MinSamples, ref _curStat);
    private void UpdateNoChange() => _noChange = Damp(_noChange, RawNoChange,
        _firstChange.Count < MinSamples ? NoChangeInitialMs : Math.Clamp(NoChangeFactor * Percentile(_firstChange, DownQuantile), NoChangeMinMs, NoChangeMaxMs), ref _noChangeLow, ref _noChangeLowMax, _firstChange.Count >= MinSamples, ref _noChangeStat);

    private static double Percentile(List<double> xs, double q)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[(int)Math.Floor(q * (s.Count - 1))];
    }

    /// <summary>落ち着いて終わった手（時間切れ・中断以外で、画面が変わったもの）の所要時間を足す</summary>
    public void Add(double boundaryMs) { Push(_settled, boundaryMs); UpdateCurrent(); }

    /// <summary>変化が始まるまでの時間（入力から）を足す</summary>
    public void AddFirstChange(double ms) { Push(_firstChange, ms); UpdateNoChange(); }

    /// <summary>
    /// 「変化なし」と判定した後に画面が変わった（判定が早すぎた）。実際の遅れは待ち時間より長かったが、測れていない（打ち切られた標本）。
    /// 測れた標本だけで分位点を取ると待ち時間が下がり続ける偏りになるので、待ち時間の LateFactor 倍を下限の推定として足す
    /// </summary>
    public void AddLateChange(double nochangeMs) { LateChanges++; Push(_firstChange, nochangeMs * LateFactor); UpdateNoChange(); }
    public double LateFactor { get; init; } = 1.5;  // 根拠: 汎用（打ち切られた標本の下限の推定。待ち時間より確実に長い値）
    public int LateChanges { get; private set; }

    private void Push(List<double> list, double v)
    {
        if (!double.IsFinite(v) || v <= 0) return;
        list.Add(v);
        if (list.Count > Window) list.RemoveAt(0);
    }
}
