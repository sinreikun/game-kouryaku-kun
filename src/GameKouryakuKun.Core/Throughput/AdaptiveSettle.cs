namespace GameKouryakuKun.Core.Throughput;

/// <summary>
/// T2: 画面ごとの適応的静止判定（docs/throughput-parallel-sensing-design.md §2.1）。
/// 変えるのは「入力後この時間内に変化が始まらなければ変化なし」とする待ち時間（no-change）だけ。静止の確認（StableMs）・差分の基準・
/// GT/Track のしきい値には触れない。全体の自己調整値（AdaptiveTurnTimeout）より長くはしない（短くするだけ）。
/// その画面で「変化が始まるまでの時間」を MinSamples 回以上測れたら Factor × 上位分位まで短くできる。下限は StableMs と MinMs の大きい方。
/// 短くした後に遅れた変化（late_change）が出たら、その画面は CooldownTurns 手のあいだ全体の値に戻す（安全側に自己修正）。
/// 打ち切られた標本（late）は測れていない遅れなので、待ち時間の LateFactor 倍を標本として足す（下がり続ける偏りを防ぐ）。
/// </summary>
public sealed class AdaptiveSettle
{
    public int MinSamples { get; init; } = 20;      // 根拠: 汎用（AdaptiveTurnTimeout.MinSamples と同じ。分布を推定する最小の標本数）
    public double Quantile { get; init; } = 0.99;   // 根拠: 汎用（AdaptiveTurnTimeout.DownQuantile と同じ。下げる判定は窓のほぼ最大）
    public double Factor { get; init; } = 2;        // 根拠: 汎用（AdaptiveTurnTimeout.NoChangeFactor と同じ。分布の裾の余裕）
    public double MinMs { get; init; } = 250;       // 根拠: 汎用（TurnDetector.StableMs と同じ約15フレーム。これより短い no-change は判定として意味がない）
    public int Window { get; init; } = 200;         // 根拠: 汎用（AdaptiveTurnTimeout.Window と同じ。最近の標本だけ）
    public int CooldownTurns { get; init; } = 50;   // 根拠: 汎用（遅れた変化の後、標本の窓の 1/4 の手は全体の値に戻す）
    public double LateFactor { get; init; } = 2;    // 根拠: 汎用（打ち切られた遅れの下限推定。AdaptiveTurnTimeout の遅れた変化の扱いと同じ考え）

    private sealed class Screen { public readonly List<double> First = new(); public int Cooldown; public double LateFloor; public long Applied, Late, ShortNoChange; }
    private readonly Dictionary<string, Screen> _s = new(StringComparer.Ordinal);
    public long Applied, Reverted, ShortNoChange, LateOnShort;

    private Screen Get(string key) => _s.TryGetValue(key, out var x) ? x : _s[key] = new Screen();

    /// <summary>その画面で使う no-change の待ち時間。globalMs（全体の自己調整値）以下・下限 max(MinMs, stableMs) 以上</summary>
    public double Recommend(string key, double globalMs, double stableMs)
    {
        var s = Get(key);
        if (s.Cooldown > 0) { s.Cooldown--; return globalMs; }
        if (s.First.Count < MinSamples) return globalMs;
        var sorted = s.First.OrderBy(x => x).ToList();
        double q = sorted[(int)Math.Floor(Quantile * (sorted.Count - 1))];
        double floor = Math.Max(MinMs, stableMs);
        double r = Math.Min(globalMs, Math.Max(Math.Max(floor, s.LateFloor), Factor * q));   // 遅れた変化を見た待ち時間の LateFactor 倍より短くはしない
        if (r < globalMs) { Applied++; s.Applied++; }
        return r;
    }

    /// <summary>入力から変化が始まるまでの時間（入力の手だけ。待つ手は応答ではない）</summary>
    public void ObserveFirstChange(string key, double ms)
    {
        var l = Get(key).First;
        l.Add(ms);
        if (l.Count > Window) l.RemoveAt(0);
    }

    /// <summary>短くした待ち時間で「変化なし」と判定した手（遅れた変化の率の分母）</summary>
    public void ShortenedNoChange(string key) { ShortNoChange++; Get(key).ShortNoChange++; }

    /// <summary>「変化なし」の判定の後に変化が来た（判定が早すぎた）。shortened: その手で短くした待ち時間を使っていたか</summary>
    public void LateChange(string key, double usedMs, bool shortened)
    {
        var s = Get(key);
        s.Late++;
        ObserveFirstChange(key, LateFactor * usedMs);   // 打ち切られた遅れは標本として足す（分位が上がる＝安全側）
        // v0.40.2: 下限を上げるのは、自分が短くした待ちで早すぎたときだけ。全体の値で待って遅れた変化は標本だけに入れる
        // （v0.40.0 実機: 全体の値 1200 ms での遅れた変化で下限が 2400 ms になり、その画面は二度と短くならなかった）
        if (shortened) { s.LateFloor = Math.Max(s.LateFloor, LateFactor * usedMs); LateOnShort++; Reverted++; s.Cooldown = CooldownTurns; }
    }

    /// <summary>短くした判定のうち早すぎた割合</summary>
    public double LateRate => ShortNoChange == 0 ? 0 : (double)LateOnShort / ShortNoChange;

    /// <summary>v0.40.2: 実行・区間をまたいで持ち越す（画面×手の種類ごとの標本と下限）。データフォルダの JSON</summary>
    public sealed record Persisted(Dictionary<string, List<double>> First, Dictionary<string, double> LateFloor);

    public Persisted Export() => new(_s.ToDictionary(k => k.Key, k => k.Value.First.ToList()), _s.Where(k => k.Value.LateFloor > 0).ToDictionary(k => k.Key, k => k.Value.LateFloor));

    public void Import(Persisted? p)
    {
        if (p == null) return;
        foreach (var (k, l) in p.First) { var s = Get(k); s.First.Clear(); s.First.AddRange(l.Where(x => x >= 0 && !double.IsNaN(x)).TakeLast(Window)); }
        foreach (var (k, f) in p.LateFloor) Get(k).LateFloor = Math.Max(Get(k).LateFloor, f);
    }

    public string ScreensSummary() => string.Join(" ", _s.OrderBy(k => k.Key, StringComparer.Ordinal)
        .Select(k => $"{k.Key}:n{k.Value.First.Count}/a{k.Value.Applied}/l{k.Value.Late}"));
}
