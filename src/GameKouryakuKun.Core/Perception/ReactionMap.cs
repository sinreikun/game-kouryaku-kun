using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Perception;

/// <summary>1つの区画の、入力への反応の集計（変わった回数・平行移動として説明できた回数・その移動量の和）</summary>
public sealed class ReactionCell
{
    public int Changed;
    public int Translated;
    public double SumDx, SumDy;
}

/// <summary>ある画面の種類・入力での、区画ごとの集計</summary>
public sealed class ReactionTable
{
    public int N;
    public ReactionCell[] Cells { get; }
    public ReactionTable(int cells) { Cells = Enumerable.Range(0, cells).Select(_ => new ReactionCell()).ToArray(); }
}

/// <summary>ほかの入力と比べて、この入力に反応する区画（統計の量だけ。何が映っているかは扱わない）</summary>
public sealed record ReactiveCell(int Cell, int Col, int Row, double Rate, double OtherRate, double Z, double TranslatedShare, double MeanDx, double MeanDy);

/// <summary>
/// 区画とキーの反応の対応表（v0.9.0）。画面を Cols×Rows の区画（画面の大きさに対する割合なので解像度に依らない）に分け、
/// 1手ごとに各区画が「変わらない／変わった／平行移動で説明できる変化」のどれだったかを、画面の種類（ノード）と入力ごとに数える。
/// 同じ画面の種類のほかの入力と比べて反応する割合が統計的に高い区画を「その入力に反応する区画」とする。
/// 物体・操作対象・自分などの前提は置かない（区画は位置の番号だけ。何が反応したかの意味は付けない）。
/// v0.10.0 から、操作の選択に「まだ分かっていない反応の関係の情報の量」（InformationGain）としてだけ使う。変化が大きいことを良いとはしない。
/// </summary>
public sealed class ReactionMap
{
    public int Cols { get; init; } = 16;               // 根拠: 汎用（横の区画数。16:9 の画面をほぼ正方形の区画に分ける）
    public int Rows { get; init; } = 9;                // 根拠: 汎用（縦の区画数）
    public int TargetWidth { get; init; } = 160;       // 根拠: 汎用（比べる前に縮小する横幅。計算量との釣り合い）
    public double PixelTau { get; init; } = 24;        // 根拠: 汎用（画素が変わったとみなす輝度の差。FrameDiff の画素の閾値と同じ）
    public double MinChangedFraction { get; init; } = 0.05;   // 根拠: 汎用（区画の 5% 以上の画素が変われば「変わった」）
    public int MaxShift { get; init; } = 3;            // 根拠: 汎用（平行移動を探す範囲。縮小後の画素で ±3）
    public double ExplainRatio { get; init; } = 0.5;   // 根拠: 汎用（ずらすと変わった画素が半分以下になれば平行移動で説明できる。区画の端ではみ出す分を許す）
    public double Z { get; init; } = 3;                // 根拠: 汎用（2つの割合の差の検定で、正規分布のおよそ上位 0.1%）
    public int MinObservations { get; init; } = 5;     // 根拠: 汎用（割合を比べる最小の手数）

    private readonly Dictionary<(string Context, string Action), ReactionTable> _t = new();
    public IReadOnlyDictionary<(string Context, string Action), ReactionTable> Tables => _t;
    public int CellCount => Cols * Rows;

    private ReactionTable Table(string ctx, string action) => _t.TryGetValue((ctx, action), out var t) ? t : _t[(ctx, action)] = new ReactionTable(CellCount);

    /// <summary>保存してあった集計を足す（cell = -1 の行は手数）</summary>
    public void Load(IEnumerable<(string Context, string Action, int Cell, int N, int Changed, int Translated, double SumDx, double SumDy)> rows)
    {
        foreach (var r in rows)
        {
            var t = Table(r.Context, r.Action);
            if (r.Cell < 0) { t.N += r.N; continue; }
            if (r.Cell >= CellCount) continue;
            var c = t.Cells[r.Cell]; c.Changed += r.Changed; c.Translated += r.Translated; c.SumDx += r.SumDx; c.SumDy += r.SumDy;
        }
    }

    public IEnumerable<(string Context, string Action, int Cell, int N, int Changed, int Translated, double SumDx, double SumDy)> Export()
    {
        foreach (var ((ctx, a), t) in _t)
        {
            yield return (ctx, a, -1, t.N, 0, 0, 0, 0);
            for (int i = 0; i < t.Cells.Length; i++)
            {
                var c = t.Cells[i];
                if (c.Changed + c.Translated > 0) yield return (ctx, a, i, 0, c.Changed, c.Translated, c.SumDx, c.SumDy);
            }
        }
    }

    /// <summary>1手の前後の画面から、区画ごとの反応を数える。結果は区画ごとの 0=変わらない・1=変わった・2=平行移動</summary>
    public int[] Observe(string context, string action, Frame before, Frame after)
    {
        var res = Classify(before, after);
        var t = Table(context, action); t.N++;
        for (int i = 0; i < res.Kind.Length; i++)
        {
            if (res.Kind[i] == 1) t.Cells[i].Changed++;
            else if (res.Kind[i] == 2) { t.Cells[i].Translated++; t.Cells[i].SumDx += res.Dx[i]; t.Cells[i].SumDy += res.Dy[i]; }
        }
        return res.Kind;
    }

    public (int[] Kind, double[] Dx, double[] Dy) Classify(Frame before, Frame after)
    {
        var kind = new int[CellCount]; var dxs = new double[CellCount]; var dys = new double[CellCount];
        if (!before.SameSize(after)) { Array.Fill(kind, 1); return (kind, dxs, dys); }
        int scale = Math.Max(1, before.Width / TargetWidth);
        var a = GrayImage.FromFrame(before, scale); var b = GrayImage.FromFrame(after, scale);
        int w = a.Width, h = a.Height;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                int x0 = c * w / Cols, x1 = (c + 1) * w / Cols, y0 = r * h / Rows, y1 = (r + 1) * h / Rows;
                if (x1 <= x0 || y1 <= y0) continue;
                double d0 = Cost(a, b, x0, x1, y0, y1, 0, 0);
                int k = r * Cols + c;
                if (d0 < MinChangedFraction) continue;
                double best = double.MaxValue; int bx = 0, by = 0;
                for (int dy = -MaxShift; dy <= MaxShift; dy++)
                    for (int dx = -MaxShift; dx <= MaxShift; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        double d = Cost(a, b, x0, x1, y0, y1, dx, dy);
                        if (d < best) { best = d; bx = dx; by = dy; }
                    }
                // 模様のない所（ずらしても自分と同じ）は、ずれたのか変わったのか区別できないので平行移動とはしない（窓の問題）
                bool textured = Cost(a, a, x0, x1, y0, y1, bx, by) >= MinChangedFraction;
                if (textured && best <= ExplainRatio * d0) { kind[k] = 2; dxs[k] = bx * scale; dys[k] = by * scale; }
                else kind[k] = 1;
            }
        return (kind, dxs, dys);
    }

    /// <summary>区画の中で before(x) と after(x+d) の輝度の差が PixelTau 以上の画素の割合（画面の外にはみ出す画素は数えない）</summary>
    private double Cost(GrayImage a, GrayImage b, int x0, int x1, int y0, int y1, int dx, int dy)
    {
        double s = 0; int n = 0;
        for (int y = y0; y < y1; y++)
        {
            int yy = y + dy; if (yy < 0 || yy >= b.Height) continue;
            for (int x = x0; x < x1; x++)
            {
                int xx = x + dx; if (xx < 0 || xx >= b.Width) continue;
                if (Math.Abs(a.Px[y * a.Width + x] - b.Px[yy * b.Width + xx]) >= PixelTau) s++;
                n++;
            }
        }
        return n == 0 ? double.MaxValue : s / n;
    }

    /// <summary>同じ画面の種類のほかの入力と比べて、この入力に反応する割合が高い区画（2つの割合の差の検定）</summary>
    public IReadOnlyList<ReactiveCell> Reactive(string context, string action)
    {
        var list = new List<ReactiveCell>();
        if (!_t.TryGetValue((context, action), out var mine) || mine.N < MinObservations) return list;
        var others = _t.Where(kv => kv.Key.Context == context && kv.Key.Action != action).Select(kv => kv.Value).ToList();
        int no = others.Sum(o => o.N);
        if (no < MinObservations) return list;
        for (int i = 0; i < CellCount; i++)
        {
            var c = mine.Cells[i];
            int ka = c.Changed + c.Translated, ko = others.Sum(o => o.Cells[i].Changed + o.Cells[i].Translated);
            double pa = ka / (double)mine.N, po = ko / (double)no, p = (ka + ko) / (double)(mine.N + no);
            double se = Math.Sqrt(Math.Max(1e-12, p * (1 - p) * (1.0 / mine.N + 1.0 / no)));
            double z = (pa - po) / se;
            if (z < Z) continue;
            list.Add(new ReactiveCell(i, i % Cols, i / Cols, pa, po, z, ka == 0 ? 0 : c.Translated / (double)ka,
                c.Translated == 0 ? 0 : c.SumDx / c.Translated, c.Translated == 0 ? 0 : c.SumDy / c.Translated));
        }
        return list;
    }

    /// <summary>
    /// この入力をもう1回試したときに、区画ごとの「反応するか」の関係について得られる情報の量の平均を、何も分かっていないとき（観測 0 回）を 1 として返す（0〜1）。
    /// 区画ごとの反応の確率をベータ分布（一様な事前分布）で持ち、次の観測と確率の相互情報量（ベルヌーイ・ベータの閉じた式）を使う。
    /// 変化の大きさや反応する区画の多さの価値ではない: 毎回大きく変わる入力も、毎回まったく反応しない入力も、観測が増えれば同じように 0 に近づく
    /// （反応しなかった観測も情報として数える）。反応が混ざる（ときどき反応する）区画ほど、情報が残る
    /// </summary>
    public double InformationGain(string context, string action)
    {
        _t.TryGetValue((context, action), out var t);
        int n = t?.N ?? 0; double sum = 0;
        for (int i = 0; i < CellCount; i++)
        {
            int k = t == null ? 0 : t.Cells[i].Changed + t.Cells[i].Translated;
            sum += BernoulliBetaInformation(k + 1, n - k + 1);
        }
        return sum / CellCount / BernoulliBetaInformation(1, 1);
    }

    /// <summary>ベータ(a, b) の確率 p と、次の 0/1 の観測との相互情報量（nats）: H(E[p]) − E[H(p)]</summary>
    public static double BernoulliBetaInformation(double a, double b)
    {
        double s = a + b, m = a / s;
        double hm = -(m > 0 ? m * Math.Log(m) : 0) - (m < 1 ? (1 - m) * Math.Log(1 - m) : 0);
        double eh = Digamma(s + 1) - m * Digamma(a + 1) - (1 - m) * Digamma(b + 1);
        return Math.Max(0, hm - eh);
    }

    public static double Digamma(double x)
    {
        double r = 0;
        while (x < 6) { r -= 1 / x; x += 1; }
        double f = 1 / (x * x);
        return r + Math.Log(x) - 0.5 / x - f * (1.0 / 12 - f * (1.0 / 120 - f * (1.0 / 252 - f * (1.0 / 240 - f / 132))));
    }

    /// <summary>反応する区画のある (画面の種類, 入力) の組の数と、反応する区画の総数</summary>
    public (int Pairs, int Cells) Summary()
    {
        int pairs = 0, cells = 0;
        foreach (var (ctx, a) in _t.Keys) { int k = Reactive(ctx, a).Count; if (k > 0) { pairs++; cells += k; } }
        return (pairs, cells);
    }
}
