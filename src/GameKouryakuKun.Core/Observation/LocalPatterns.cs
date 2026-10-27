using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Observation;

/// <summary>v0.29.1: 細かい格子（32×24）の区画ごとの画素のハッシュと平均の色の符号（意味は付けない）</summary>
public sealed record FineGrid(int Cols, int Rows, ulong[] Hash, int[] Code)
{
    public const int DefaultCols = 32;   // 根拠: 画素の区画（16×12）の 2 倍の細かさ。小さな局所のパターンを区画 1〜数個でとらえる
    public const int DefaultRows = 24;   // 根拠: 同上

    public static FineGrid Of(Frame f, int cols = DefaultCols, int rows = DefaultRows)
    {
        var h = new ulong[cols * rows]; var code = new int[cols * rows];
        for (int gy = 0; gy < rows; gy++)
        for (int gx = 0; gx < cols; gx++)
        {
            int x0 = gx * f.Width / cols, x1 = Math.Max(x0 + 1, (gx + 1) * f.Width / cols), y0 = gy * f.Height / rows, y1 = Math.Max(y0 + 1, (gy + 1) * f.Height / rows);
            int sx = Math.Max(1, (x1 - x0) / 8), sy = Math.Max(1, (y1 - y0) / 8);   // 根拠: 区画あたり 8×8 点の間引き（TextReader.Cells と同じ考え方で軽く）
            ulong v = 14695981039346656037UL; long b = 0, g = 0, r = 0; int n = 0;
            for (int y = y0; y < y1 && y < f.Height; y += sy)
            for (int x = x0; x < x1 && x < f.Width; x += sx)
            {
                int i = (y * f.Width + x) * 4;
                v ^= (ulong)((f.Bgra[i] >> 4) | ((f.Bgra[i + 1] >> 4) << 4) | ((f.Bgra[i + 2] >> 4) << 8)); v *= 1099511628211UL;
                b += f.Bgra[i]; g += f.Bgra[i + 1]; r += f.Bgra[i + 2]; n++;
            }
            n = Math.Max(1, n);
            h[gy * cols + gx] = v;
            code[gy * cols + gx] = (int)(b / n >> 4) | (int)(g / n >> 4) << 4 | (int)(r / n >> 4) << 8;   // 各色 4 ビット
        }
        return new FineGrid(cols, rows, h, code);
    }

    /// <summary>v0.29.2: 区画のハッシュを取り込みのノイズに強くした格子（CellStabilizer。8×8 点）</summary>
    public static FineGrid Of(Frame f, CellStabilizer stab)
    {
        var g = Of(f, DefaultCols, DefaultRows);
        return g with { Hash = stab.Apply(f) };
    }

    public IReadOnlyList<int> ChangedFrom(FineGrid? prev)
    {
        var l = new List<int>();
        if (prev == null || prev.Hash.Length != Hash.Length) return l;
        for (int i = 0; i < Hash.Length; i++) if (prev.Hash[i] != Hash[i]) l.Add(i);
        return l;
    }
}

/// <summary>局所パターンの候補（意味のラベルなし）。座標は細かい格子の区画。AppearanceState は追跡ごとの見た目の状態（A, B, C …）</summary>
public sealed record EntityCandidate(int Id, int Track, int X0, int Y0, int X1, int Y1, double Cx, double Cy, int Cells, string Appearance, string AppearanceState,
    double Dx, double Dy, bool Moved, bool AppearanceChanged, bool New, double Confidence, int Age)
{
    /// <summary>v0.31.6: この追跡の最近の中心の位置（表示の軌跡用。古い順）</summary>
    public IReadOnlyList<(double X, double Y)> Trail { get; init; } = Array.Empty<(double, double)>();
    /// <summary>v0.32.0: この候補を続けた追跡の同一性の事後（対数オッズ。新しい追跡は null）</summary>
    public double? IdentityPosterior { get; init; }
    /// <summary>v0.32.0: 前の手にも候補として見えていた追跡の続き</summary>
    public bool Continuous { get; init; }
    /// <summary>v0.32.0: 手がかりで支えられた続き（前の手から続き、同一性の事後 &gt; 0）。P-6 の予測を「安定した番号の上」で数えるのに使う</summary>
    public bool StableIdentity => !New && Continuous && IdentityPosterior is > 0;
    /// <summary>v0.31.6: 追跡ができてからの手数</summary>
    public long Lifetime { get; init; }
    /// <summary>v0.31.4: 動かずに、この追跡で前に見た見た目の状態へ戻った（その場の周期的な見た目の変化。足踏みのような変化。意味は付けない）</summary>
    public bool PeriodicInPlace { get; init; }
}

public sealed record StaticTrack(int Track, int X0, int Y0, int X1, int Y1, string AppearanceState, int StillSteps);

public sealed record RelationObs(int TrackA, int TrackB, double Dx, double Dy, double Distance, string Change, bool Adjacent, bool Overlap, bool SameMove);

public sealed record EntityLayer(string Status, IReadOnlyList<EntityCandidate> Candidates, IReadOnlyList<int> Disappeared, IReadOnlyList<RelationObs> Relations)
{
    /// <summary>v0.30.1: 画面全体の共通のずれ（細かい区画の単位。null＝なし）。候補の Dx/Dy はこのずれを差し引いた動き</summary>
    public double[]? CommonShift { get; init; }
    /// <summary>v0.31.3（P-5）: この手で変化がなく、前に変化で見つかったまま見た目（区画のハッシュ）が変わらずに残っている追跡（静止した候補）。
    /// 出来事・学習の入力には入れない（候補 Candidates は変わった所だけのまま）</summary>
    public IReadOnlyList<StaticTrack> Static { get; init; } = Array.Empty<StaticTrack>();
    /// <summary>v0.30.1: 全部の組の数（Relations は近い組と重なり・隣接・同じ動きの組だけに絞ったもの）</summary>
    public int RelationPairs { get; init; }
    /// <summary>v0.30.4: 共通のずれを差し引いても変わった細かい区画（共通のずれがなければ null）。変化した領域の層もこれから作る</summary>
    public IReadOnlyList<int>? ResidualCells { get; init; }
    /// <summary>v0.30.4: 調べたずれ（dx, dy, 全体の差の比, 良くなった区画の割合）。共通のずれと判定しなかった手も残す</summary>
    public double[]? ShiftProbe { get; init; }
    /// <summary>v0.32.7: 場所によって違うずれ（双一次）の係数。dx: 1, X, Y, XY の係数・dy: 同じ（X, Y は −1〜1 に正規化した画面の座標、単位は細かい区画）。平行移動なら null</summary>
    public double[]? ShiftWarp { get; init; }
    /// <summary>v0.32.9 (t108u): 画面全体の 1 つの変換かどうかの証拠と確からしさ（比べなかった手は null）</summary>
    public GlobalTransformConfidence? GlobalTransform { get; init; }
    /// <summary>v0.30.4: いっしょに変わり続ける隣り合った候補のまとまり（共通の運命）。意味は付けない</summary>
    public IReadOnlyList<EntityGroup> Groups { get; init; } = Array.Empty<EntityGroup>();
}

/// <summary>v0.30.4: 隣り合い、同じ手で何度もいっしょに候補になった追跡のまとまり。Id はまとまりの中のいちばん小さい追跡 ID</summary>
public sealed record EntityGroup(int Id, IReadOnlyList<int> Tracks, int X0, int Y0, int X1, int Y1, int CoSteps);

/// <summary>
/// v0.29.1（ブロック 5〜8）: 変化した細かい区画のつながりを局所パターンの候補にし、前の手の候補と追跡する。
/// 対応は「互いにいちばん近い」組だけ（位置の差と見た目の差の和。固定の距離のしきい値なし）。消えた追跡は見た目が完全に同じときだけ再び同じ ID にする（再同定）。
/// 見た目の状態: 候補の区画の色の符号を 4×4 にそろえたハッシュ。追跡ごとに出てきた順に A, B, C …（向き・姿勢などの意味は付けない）。
/// </summary>
public sealed class EntityTracker
{
    private sealed class Track { public int Id; public double Cx, Cy; public int[] Hist = Array.Empty<int>(); public string App = ""; public readonly List<string> States = new(); public int Age; public bool Missing; public int Still; public int Seen; public int Cells; public double Vx, Vy; public long LastStep, Born; public readonly List<(double X, double Y)> Trail = new(); public int W, H, X0, Y0, X1, Y1; public HashSet<int> CellSet = new(); }
    private readonly List<Track> _tracks = new();
    private int _next = 1;
    public int MaxTracks { get; init; } = 256;   // 根拠: 文字の読み取りのキャッシュと同じ桁（状態の数〜数百）。古い消えた追跡から捨てる
    private FineGrid? _prev;
    /// <summary>v0.29.2: 細かい区画のノイズに強いハッシュ（呼ぶ側が FineGrid.Of(f, Stabilizer) で使う）</summary>
    public CellStabilizer Stabilizer { get; } = new(FineGrid.DefaultCols, FineGrid.DefaultRows, 8);   // 根拠: FineGrid.Of と同じ区画あたり 8×8 点
    private Dictionary<int, (double Cx, double Cy)> _lastPos = new();
    public long Created { get; private set; }
    public long Continued { get; private set; }
    public long Reidentified { get; private set; }
    public long AppearanceOnly { get; private set; }

    /// <summary>v0.30.1: 共通のずれ（視点の移動のような動き）を差し引いてから追跡する</summary>
    public CommonMotion Motion { get; init; } = new();
    /// <summary>関係は候補ごとに近い順にこの数まで（重なり・隣接・同じ動きの組はいつも残す）</summary>
    public int RelationNeighbors { get; init; } = 3;   // 根拠: 平面の近さのつながり（三角分割）の平均の次数 6 の半分。両側から選ばれるので 1 候補あたり 3〜6 本
    public long CommonShiftSteps => Motion.Shifted;
    public long RelationsPruned { get; private set; }
    /// <summary>v0.30.3: 前に持っていた見た目の状態に戻った候補を同じ追跡で続けた回数（周期的な見た目）</summary>
    public long PeriodicReturns { get; private set; }
    /// <summary>v0.30.5: 画面の 1/4 を超えるので候補にしなかった塊</summary>
    public long LargeSuppressed { get; private set; }
    public const int MaxCandidateShareDen = 4;   // 根拠: 局所の候補は画面の一部（1/4 まで）。それより大きい変化は画面全体の変化として扱う
    /// <summary>v0.30.3: 対応は取れなかったが前の外接矩形と大きく重なるので同じ追跡で続けた回数</summary>
    public long OverlapContinued { get; private set; }
    public long StaticObserved { get; private set; }
    public TrackEvidence Evidence { get; } = new();
    public TrackQuality Quality { get; } = new();
    private long _step;
    public const int TrailLength = 8;   // 根拠: 表示用の軌跡の長さ（判定には使わない）
    private readonly List<int> _lifetimes = new();
    /// <summary>v0.31.6: 終わった追跡（消えた扱いのまま捨てた）の寿命（候補として見えた手数）と、今いる追跡の寿命</summary>
    public (double Mean, double Median, int N) Lifetimes()
    {
        var v = _lifetimes.Concat(_tracks.Select(t => t.Seen)).Where(x => x > 0).OrderBy(x => x).ToList();
        return v.Count == 0 ? (0, 0, 0) : (Math.Round(v.Average(), 2), v[v.Count / 2], v.Count);
    }
    public long Steps => _step;
    /// <summary>v0.31.6: 共通の運命のまとまりの中で、前の手にいた追跡が別の番号に入れ替わった回数（同じ物の割れ・付け替えの目安）</summary>
    public long GroupSwitches { get; private set; }
    private readonly Dictionary<int, HashSet<int>> _groupMembers = new();

    private List<(int X0, int Y0, int X1, int Y1, double Cx, double Cy, int[] Hist, string App, int Cells, HashSet<int> CellsSet)> MergeFragments(FineGrid g,
        List<(int X0, int Y0, int X1, int Y1, double Cx, double Cy, int[] Hist, string App, int Cells, HashSet<int> CellsSet)> obs, List<Track> live)
    {
        var parent = Enumerable.Range(0, obs.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        bool Ov(Track t, int i) { var o = obs[i]; int ix = 0, iy = 0; return t.W > 0 && Math.Min(t.X1 + ix, o.X1) >= Math.Max(t.X0 + ix, o.X0) && Math.Min(t.Y1 + iy, o.Y1) >= Math.Max(t.Y0 + iy, o.Y0); }
        bool Near(int a, int b) { var p = obs[a]; var q = obs[b]; return p.X0 - 1 <= q.X1 && q.X0 - 1 <= p.X1 && p.Y0 - 1 <= q.Y1 && q.Y0 - 1 <= p.Y1; }
        var hits = Enumerable.Range(0, obs.Count).ToDictionary(i => i, i => live.Where(t => Ov(t, i)).Select(t => t.Id).ToList());
        foreach (var t in live)
        {
            var mine = Enumerable.Range(0, obs.Count).Where(i => hits[i].Count == 1 && hits[i][0] == t.Id).ToList();
            // 割れた塊とするのは、どちらも前の枠（速度で進めた位置）の中に収まる候補だけ（枠の外へはみ出す塊はまとめない）
            int ix = 0, iy = 0;
            bool Inside(int i) { var o = obs[i]; return o.X0 >= t.X0 + ix && o.X1 <= t.X1 + ix && o.Y0 >= t.Y0 + iy && o.Y1 <= t.Y1 + iy; }
            mine = mine.Where(Inside).ToList();
            for (int a = 0; a < mine.Count; a++) for (int b = a + 1; b < mine.Count; b++) if (Near(mine[a], mine[b])) parent[Find(mine[b])] = Find(mine[a]);
        }
        var res = new List<(int X0, int Y0, int X1, int Y1, double Cx, double Cy, int[] Hist, string App, int Cells, HashSet<int> CellsSet)>();
        foreach (var grp in Enumerable.Range(0, obs.Count).GroupBy(Find).OrderBy(x => x.Key))
        {
            if (grp.Count() == 1) { res.Add(obs[grp.First()]); continue; }
            FragmentsMerged += grp.Count() - 1;
            res.Add(Describe(g, grp.SelectMany(i => obs[i].CellsSet).Distinct().OrderBy(x => x).ToList()));
        }
        return res;
    }
    public long FragmentsMerged { get; private set; }
    private static int Overlap(Track t, (int X0, int Y0, int X1, int Y1, double Cx, double Cy, int[] Hist, string App, int Cells, HashSet<int> CellsSet) o)
        => Math.Max(0, Math.Min(t.X1, o.X1) - Math.Max(t.X0, o.X0) + 1) * Math.Max(0, Math.Min(t.Y1, o.Y1) - Math.Max(t.Y0, o.Y0) + 1);

    public EntityLayer Update(FineGrid g, Frame? frame = null)
    {
        var changed = g.ChangedFrom(_prev); bool first = _prev == null; _prev = g;
        double[]? shift = null;
        if (frame != null && Motion.Estimate(frame, changed) is { } cm && !first)
        {
            changed = cm.Residual; shift = new[] { cm.Dx, cm.Dy };
            var field = Motion.LastField;   // v0.32.7: 場所によって違うずれ（なければ画面全体で同じずれ）
            (double, double) D(double x, double y) => field?.Invoke(x, y) ?? (cm.Dx, cm.Dy);
            foreach (var t in _tracks.Where(t => !t.Missing))   // 前の位置を共通のずれだけ動かしてから対応を取る
            {
                var (ddx, ddy) = D(t.Cx, t.Cy);
                int ix = (int)Math.Round(ddx), iy = (int)Math.Round(ddy);
                t.Cx += ddx; t.Cy += ddy; t.X0 += ix; t.X1 += ix; t.Y0 += iy; t.Y1 += iy;
                t.CellSet = t.CellSet.Select(c => (c / g.Cols + iy) * g.Cols + (c % g.Cols + ix)).Where(c => c >= 0 && c < g.Cols * g.Rows).ToHashSet();
            }
            _lastPos = _lastPos.ToDictionary(k => k.Key, k => { var (ddx, ddy) = D(k.Value.Cx, k.Value.Cy); return (k.Value.Cx + ddx, k.Value.Cy + ddy); });
        }
        if (first) return new EntityLayer(LayerStatus.NotObserved, Array.Empty<EntityCandidate>(), Array.Empty<int>(), Array.Empty<RelationObs>());
        // 1) 変化した区画の 4 近傍のつながり
        var set = changed.ToHashSet(); var seen = new HashSet<int>(); var comps = new List<List<int>>();
        foreach (var s in changed.OrderBy(x => x))
        {
            if (!seen.Add(s)) continue;
            var comp = new List<int>(); var q = new Queue<int>(); q.Enqueue(s);
            while (q.Count > 0)
            {
                int c = q.Dequeue(); comp.Add(c); int x = c % g.Cols, y = c / g.Cols;
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    if (nx >= 0 && ny >= 0 && nx < g.Cols && ny < g.Rows && set.Contains(ny * g.Cols + nx) && seen.Add(ny * g.Cols + nx)) q.Enqueue(ny * g.Cols + nx);
            }
            comps.Add(comp);
        }
        // 1b) 変化の塊を、手の後の画面の「その場所の背景（塊のまわりの枠でいちばん多い色の符号）でない区画」のつながりにする。
        //     動いた物の抜けた跡（背景に戻った区画）は候補にせず、変わらなかった物の部分（変わった区画と同じ色の符号の隣の区画）は候補に含める
        var fgSets = new List<HashSet<int>>();
        foreach (var comp in comps)
        {
            int x0 = comp.Min(c => c % g.Cols), x1 = comp.Max(c => c % g.Cols), y0 = comp.Min(c => c / g.Cols), y1 = comp.Max(c => c / g.Cols);
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            int ex0 = Math.Max(0, x0 - bw), ex1 = Math.Min(g.Cols - 1, x1 + bw), ey0 = Math.Max(0, y0 - bh), ey1 = Math.Min(g.Rows - 1, y1 + bh);   // 根拠: 塊の大きさぶん広げる（変わらなかった部分が塊より大きくはならない前提の上限）
            var ring = new Dictionary<int, int>();
            for (int y = ey0; y <= ey1; y++) for (int x = ex0; x <= ex1; x++)
                if (x == ex0 || x == ex1 || y == ey0 || y == ey1) { int k = g.Code[y * g.Cols + x]; ring[k] = ring.GetValueOrDefault(k) + 1; }
            int bg = ring.Count == 0 ? -1 : ring.OrderByDescending(r => r.Value).ThenBy(r => r.Key).First().Key;
            var fs = new HashSet<int>(); var q = new Queue<int>();
            foreach (var c in comp) if (g.Code[c] != bg && fs.Add(c)) q.Enqueue(c);
            var own = fs.Select(c => g.Code[c]).ToHashSet();   // v0.29.2: 広げるのは、変わった区画と同じ色の符号の区画だけ（背景の色むらへ広がらない）
            while (q.Count > 0)
            {
                int c = q.Dequeue(); int x = c % g.Cols, y = c / g.Cols;
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    if (nx >= ex0 && ny >= ey0 && nx <= ex1 && ny <= ey1 && g.Code[ny * g.Cols + nx] != bg && own.Contains(g.Code[ny * g.Cols + nx]) && fs.Add(ny * g.Cols + nx)) q.Enqueue(ny * g.Cols + nx);
            }
            if (fs.Count == 0) continue;
            var hit = fgSets.FirstOrDefault(o => o.Overlaps(fs));
            if (hit != null) hit.UnionWith(fs); else fgSets.Add(fs);
        }
        // v0.30.5: 画面の 1/4 を超える塊は局所の候補にしない（画面の大きな変化は区画・大域の観測として残る）
        int large = fgSets.Count(c => c.Count * 4 > g.Cols * g.Rows); LargeSuppressed += large;
        var obs = fgSets.Where(c => c.Count * 4 <= g.Cols * g.Rows).Select(c => Describe(g, c.OrderBy(x => x).ToList())).ToList();
        // 2) v0.31.6: 追跡と候補の対応を、観測上の手がかりの学んだ尤度比で決める（TrackEvidence）。
        //    a) 前の枠（速度で進めた位置）と重なるのがその候補だけで、その候補が重なるのもその追跡だけの組は、時間の続きとしてそのまま対応（学びの「同じ物」の材料）
        //    b) 残りは事後確率の高い組から順に（対数尤度比 + 事前の対数オッズ > 0）。見た目は手がかりの 1 つで、同一の条件ではない
        //    消えた追跡も同じ手がかり（見えなかった手数を含む）で再同定する
        _step++;
        var live = _tracks.Where(t => !t.Missing && (t.CellSet.Overlaps(set))).ToList();
        var liveIds = live.Select(t => t.Id).ToHashSet();
        // 前の 1 つの枠に重なる複数の候補（同じ物が割れた塊）は、互いに 1 区画以内なら 1 つにまとめる（共通の運命）
        obs = MergeFragments(g, obs, live);
        var pool = _tracks.ToList();
        // 予測の位置の仮説: 背景と一緒にずれる（共通のずれを足した位置）／画面に固定（ずれる前の位置）。事後確率の高い方を使う（どちらの物かは決めない）。
        // 根拠: 実機の再生（#131・#143）で、自分の速度で進める仮説は対応を悪くした（枠の大きさの揺れで中心が跳ぶ）ので使わない
        var hyps = new List<(double Dx, double Dy)> { (0, 0) };
        if (shift != null) hyps.Add((-shift[0], -shift[1]));
        TrackFeatures F1(Track t, int i, double vx, double vy, int gap)
        {
            var o = obs[i];
            int ow = o.X1 - o.X0 + 1, oh = o.Y1 - o.Y0 + 1, tw = Math.Max(1, t.W), th = Math.Max(1, t.H);
            int ix = (int)Math.Round(vx), iy = (int)Math.Round(vy);
            bool ov = t.W > 0 && Math.Min(t.X1 + ix, o.X1) >= Math.Max(t.X0 + ix, o.X0) && Math.Min(t.Y1 + iy, o.Y1) >= Math.Max(t.Y0 + iy, o.Y0);
            return new TrackFeatures(Math.Sqrt((o.Cx - t.Cx - vx) * (o.Cx - t.Cx - vx) + (o.Cy - t.Cy - vy) * (o.Cy - t.Cy - vy)),
                Math.Abs(Math.Log((double)Math.Max(1, o.Cells) / Math.Max(1, t.Cells))), Math.Abs(Math.Log((double)ow / oh) - Math.Log((double)tw / th)),
                HistDist(t.Hist, o.Hist), t.States.Contains(o.App), ov, liveIds.Contains(t.Id), gap);
        }
        TrackFeatures F(Track t, int i)
        {
            int gap = t.Missing ? (int)Math.Max(0, _step - t.LastStep - 1) : 0;
            TrackFeatures best = F1(t, i, 0, 0, gap);
            if (!t.Missing) foreach (var (vx, vy) in hyps.Skip(1)) { var f = F1(t, i, vx, vy, gap); double pf = Evidence.Posterior(f), pb = Evidence.Posterior(best); if (pf > pb || (pf == pb && f.Dist < best.Dist)) best = f; }
            return best;
        }
        var feats = new Dictionary<(int T, int O), TrackFeatures>();
        for (int i = 0; i < obs.Count; i++) foreach (var t in pool) feats[(t.Id, i)] = F(t, i);
        var assign = new Dictionary<int, (Track T, double Conf, bool Sure)>();
        var takenT = new HashSet<int>();
        // a) 重なりがお互いに 1 つだけの組
        var ovT = pool.Where(t => !t.Missing).ToDictionary(t => t.Id, t => Enumerable.Range(0, obs.Count).Where(i => feats[(t.Id, i)].Overlap).ToList());
        for (int i = 0; i < obs.Count; i++)
        {
            var ts = pool.Where(t => !t.Missing && feats[(t.Id, i)].Overlap).ToList();
            if (ts.Count == 1 && ovT[ts[0].Id].Count == 1 && liveIds.Contains(ts[0].Id)) { assign[i] = (ts[0], 1, true); takenT.Add(ts[0].Id); }
        }
        // b) 事後確率の順
        // v0.32.0: 評価用。候補ごとに、割り当ての前の「いちばん同じ物らしい追跡」とその事後（対数オッズ）
        var bestPre = new Dictionary<int, (Track T, double S)>();
        for (int i = 0; i < obs.Count; i++)
            foreach (var t in pool) { double sc0 = Evidence.Posterior(feats[(t.Id, i)]); if (!bestPre.TryGetValue(i, out var bp0) || sc0 > bp0.S) bestPre[i] = (t, sc0); }
        var wasPrev = pool.Where(t => !t.Missing && t.LastStep == _step - 1).Select(t => t.Id).ToHashSet();
        var pairs = new List<(int I, Track T, double S)>();
        for (int i = 0; i < obs.Count; i++) if (!assign.ContainsKey(i)) foreach (var t in pool) if (!takenT.Contains(t.Id)) pairs.Add((i, t, Evidence.Posterior(feats[(t.Id, i)])));
        foreach (var (i, t, sc) in pairs.OrderByDescending(p => p.S).ThenBy(p => p.I).ThenBy(p => p.T.Id))
        {
            if (sc <= 0) break;
            if (assign.ContainsKey(i) || takenT.Contains(t.Id)) continue;
            // 確かさ: 2 番目の候補との差（対数オッズ）。ln10 以上なら学びの材料にする（根拠: RevertMinLogBayesFactor と同じ）
            double second = pairs.Where(p => p.I == i && p.T.Id != t.Id).Select(p => p.S).DefaultIfEmpty(double.NegativeInfinity).Max();
            double secondT = pairs.Where(p => p.T.Id == t.Id && p.I != i).Select(p => p.S).DefaultIfEmpty(double.NegativeInfinity).Max();
            double margin = sc - Math.Max(second, secondT);
            double conf = 1 / (1 + Math.Exp(-sc));
            assign[i] = (t, Math.Round(conf, 3), margin >= Math.Log(10)); takenT.Add(t.Id);
        }
        // c) 残った「前の区画が変わった追跡」と候補は、予測位置と色の分布で互いにいちばん近い組を続きとする（学びが少ないうちの時間の続き。v0.29.x と同じ互いの最近傍）
        var restT = live.Where(t => !takenT.Contains(t.Id)).ToList(); var restO = Enumerable.Range(0, obs.Count).Where(i => !assign.ContainsKey(i)).ToList();
        double Cost(Track t, int i) { var f = feats[(t.Id, i)]; return f.Dist / Math.Sqrt(g.Cols * g.Cols + g.Rows * g.Rows) + f.Hist; }
        foreach (var i in restO)
        {
            if (restT.Count == 0) break;
            var costs = restT.Where(t => !takenT.Contains(t.Id)).Select(t => (t, c: Cost(t, i))).OrderBy(z => z.c).ThenBy(z => z.t.Id).ToList();
            if (costs.Count == 0) break;
            var best = costs[0].t;
            int back = restO.Where(j => !assign.ContainsKey(j)).OrderBy(j => Cost(best, j)).ThenBy(j => j).First();
            if (back != i) continue;
            double conf = costs.Count > 1 && costs[1].c > 0 ? 1 - costs[0].c / costs[1].c : 1;
            assign[i] = (best, Math.Round(Math.Clamp(conf, 0, 1), 3), false); takenT.Add(best.Id);
        }
        // d) まだ残った候補: 前の枠（仮説の位置）と重なる、まだ使っていない追跡のうち事後確率のいちばん高いもの（確かな組ではないので学びには使わない）
        for (int i = 0; i < obs.Count; i++)
        {
            if (assign.ContainsKey(i)) continue;
            var c2 = pool.Where(t => !takenT.Contains(t.Id) && feats[(t.Id, i)].Overlap).OrderByDescending(t => Evidence.Posterior(feats[(t.Id, i)])).ThenBy(t => t.Id).FirstOrDefault();
            if (c2 != null) { assign[i] = (c2, Math.Round(1 / (1 + Math.Exp(-Evidence.Posterior(feats[(c2.Id, i)]))), 3), false); takenT.Add(c2.Id); }
        }
        // 学び: 確かな組は「同じ物」、確かな組の相手でない組は「違う物」
        foreach (var (i, (t, _, sure)) in assign)
        {
            if (!sure) continue;
            Evidence.Learn(feats[(t.Id, i)], true);
            foreach (var u in pool) if (u.Id != t.Id) Evidence.Learn(feats[(u.Id, i)], false);
        }
        // 3) 対応のない候補は新しい追跡
        var outList = new List<EntityCandidate>(); var used = new HashSet<int>();
        for (int i = 0; i < obs.Count; i++)
        {
            var o = obs[i]; Track t; bool isNew = false; double conf;
            int sb = TrackQuality.Bin(o.Cells);
            double? post = null; bool continuous = false;
            bestPre.TryGetValue(i, out var bpi);
            bool strongPred = bpi.T != null && bpi.S > 0 && wasPrev.Contains(bpi.T.Id);
            if (strongPred) Quality.StrongPred[sb]++;
            if (assign.TryGetValue(i, out var m))
            {
                post = Math.Round(Evidence.Posterior(feats[(m.T.Id, i)]), 3);
                continuous = wasPrev.Contains(m.T.Id);
                if (strongPred && m.T.Id == bpi.T!.Id) Quality.Kept[sb]++;
                if (post <= 0) Quality.WeakContinued[sb]++;   // 手がかりの弱い続き（跳び・取り違えの疑い）
                Quality.Assigned[sb]++;
                t = m.T; conf = m.Conf; used.Add(t.Id);
                if (t.Missing) { Reidentified++; t.Missing = false; Quality.Returned[TrackQuality.Bin(t.Cells)]++; }
                else Continued++;
                if (t.States.Contains(o.App) && t.App != o.App) PeriodicReturns++;
                else if (!m.Sure && feats[(t.Id, i)].Overlap) OverlapContinued++;
            }
            else
            {
                if (bpi.T != null && bpi.S > 0) Quality.SuspectedSplit[sb]++;   // 強い手がかりで既にある追跡とつながるのに新しい番号（割れの疑い）
                Quality.Created[sb]++;
                t = new Track { Id = _next++, Cx = o.Cx, Cy = o.Cy, Born = _step }; _tracks.Add(t); isNew = true; conf = 0; used.Add(t.Id); Created++;
            }
            double dx = isNew ? 0 : o.Cx - t.Cx, dy = isNew ? 0 : o.Cy - t.Cy;
            bool moved = !isNew && (Math.Abs(dx) >= 0.5 || Math.Abs(dy) >= 0.5);   // 根拠: 区画の半分（中心の位置の丸めの幅。これより小さい差は同じ位置）
            // v0.29.2: 外接矩形の大きさが変わり、前の矩形と重なるなら、中心のずれは広がり・縮み（見た目の変化）で、移動とはしない
            if (moved && t.W > 0 && (o.X1 - o.X0 + 1 != t.W || o.Y1 - o.Y0 + 1 != t.H) && o.X0 <= t.X1 && t.X0 <= o.X1 && o.Y0 <= t.Y1 && t.Y0 <= o.Y1) moved = false;
            bool appChanged = !isNew && t.App.Length > 0 && t.App != o.App;
            if (appChanged && !moved) AppearanceOnly++;
            bool periodic = appChanged && !moved && !isNew && t.States.Contains(o.App);
            int si = t.States.IndexOf(o.App); if (si < 0) { t.States.Add(o.App); si = t.States.Count - 1; }
            int gapSteps = isNew ? 0 : (int)Math.Max(1, _step - t.LastStep);
            t.Vx = isNew ? 0 : dx / gapSteps; t.Vy = isNew ? 0 : dy / gapSteps; t.LastStep = _step;
            t.CellSet = o.CellsSet; t.Cx = o.Cx; t.Cy = o.Cy; t.Hist = o.Hist; t.App = o.App; t.Age++; t.Cells = o.Cells; t.W = o.X1 - o.X0 + 1; t.H = o.Y1 - o.Y0 + 1; t.X0 = o.X0; t.Y0 = o.Y0; t.X1 = o.X1; t.Y1 = o.Y1;
            t.Trail.Add((o.Cx, o.Cy)); if (t.Trail.Count > TrailLength) t.Trail.RemoveAt(0);
            outList.Add(new EntityCandidate(i, t.Id, o.X0, o.Y0, o.X1, o.Y1, Math.Round(o.Cx, 2), Math.Round(o.Cy, 2), o.Cells, o.App, StateName(si),
                Math.Round(dx, 2), Math.Round(dy, 2), moved, appChanged, isNew, conf, t.Age) { PeriodicInPlace = periodic, Trail = t.Trail.ToArray(), Lifetime = _step - t.Born + 1, IdentityPosterior = post, Continuous = continuous });
            t.Seen++;
        }
        var gone = new List<int>();
        foreach (var t in live.Where(t => !used.Contains(t.Id))) { t.Missing = true; gone.Add(t.Id); Quality.Lost[TrackQuality.Bin(t.Cells)]++; }
        while (_tracks.Count > MaxTracks && _tracks.FirstOrDefault(t => t.Missing) is { } old) { _tracks.Remove(old); if (old.Seen > 0) _lifetimes.Add(old.Seen); }
        var all = Relations(outList, g);
        var rel = Prune(all, outList);
        RelationsPruned += all.Count - rel.Count;
        _lastPos = outList.ToDictionary(c => c.Track, c => (c.Cx, c.Cy));
        var groups = Group(outList);
        foreach (var gr in groups)   // v0.31.6: まとまりの中の追跡の入れ替わり（同じまとまりの番号＝いちばん小さい追跡番号）
        {
            var now = gr.Tracks.ToHashSet();
            if (_groupMembers.TryGetValue(gr.Id, out var was)) GroupSwitches += now.Count(x => !was.Contains(x));
            _groupMembers[gr.Id] = now;
        }
        if (_groupMembers.Count > 4096) _groupMembers.Clear();   // 根拠: 記憶の上限（数え方の目安だけに使う）
        // v0.31.3（P-5）: 変化のなかった区画にいる追跡は、見た目が同じまま存続している（区画のハッシュが変わらない＝見た目が同じ）
        var stat = new List<StaticTrack>();
        foreach (var t in _tracks)
        {
            if (t.Missing) continue;
            if (used.Contains(t.Id)) { t.Still = 0; continue; }
            // v0.31.4: 存続を記録するのは、2 回以上候補として追跡でき、縦横とも 2 区画以上に広がった形だけ。
            // 根拠: 1 回だけ・幅 1 区画の変化は質感のちらつきや画面が移った跡の残り（実機 #138: 静止の延べの 9 割が 1〜2 区画で、草地に出た）。
            // 2 回＝追跡が続いたと言える最小、2 区画＝縦横の形がある最小（新しい調整値ではなく最小の構造）
            if (t.Seen < 2 || t.X1 <= t.X0 || t.Y1 <= t.Y0) continue;
            t.Still++;
            stat.Add(new StaticTrack(t.Id, t.X0, t.Y0, t.X1, t.Y1, StateName(Math.Max(0, t.States.IndexOf(t.App))), t.Still));
        }
        StaticObserved += stat.Count;
        return new EntityLayer(LayerStatus.Observed, outList, gone, rel) { Static = stat, CommonShift = shift, RelationPairs = all.Count, ResidualCells = shift == null ? null : changed, ShiftProbe = Motion.LastProbe, ShiftWarp = shift == null ? null : Motion.LastWarp, GlobalTransform = Motion.LastGlobal, Groups = groups };
    }

    private readonly Dictionary<(int, int), int> _co = new();
    private readonly Dictionary<int, int> _seen = new();
    public const int GroupMinCo = 3;   // 根拠: 偶然に隣り合った 1〜2 回ではまとめない（文字の変化の確かめの最小回数と同じ桁）
    public long GroupedSteps { get; private set; }

    /// <summary>
    /// v0.30.4（共通の運命）: 同じ手で候補になり、外接矩形の間が 1 区画以内の追跡の組を数える。GroupMinCo 回以上で、
    /// どちらかが候補になった回数の半分以上でいっしょなら、同じまとまりとする（体の一部だけが変わる動き・形の変わる物の部分を 1 つにまとめる）。
    /// 追跡そのものは分けたまま（記録は残す）
    /// </summary>
    private IReadOnlyList<EntityGroup> Group(List<EntityCandidate> cs)
    {
        foreach (var c in cs) _seen[c.Track] = _seen.GetValueOrDefault(c.Track) + 1;
        var parent = cs.ToDictionary(c => c.Track, c => c.Track);
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        int co = 0;
        for (int i = 0; i < cs.Count; i++)
        for (int j = i + 1; j < cs.Count; j++)
        {
            var a = cs[i]; var b = cs[j];
            bool near = a.X0 <= b.X1 + 2 && b.X0 <= a.X1 + 2 && a.Y0 <= b.Y1 + 2 && b.Y0 <= a.Y1 + 2;   // 根拠: 間が 1 区画以内
            if (!near) continue;
            var k = a.Track < b.Track ? (a.Track, b.Track) : (b.Track, a.Track);
            int n = _co[k] = _co.GetValueOrDefault(k) + 1;
            if (n >= GroupMinCo && n * 2 >= Math.Min(_seen[a.Track], _seen[b.Track])) { int ra = Find(a.Track), rb = Find(b.Track); if (ra != rb) { parent[Math.Max(ra, rb)] = Math.Min(ra, rb); co = Math.Max(co, n); } }
        }
        if (_co.Count > 20000) foreach (var k in _co.Where(x => x.Value < GroupMinCo).Select(x => x.Key).ToList()) _co.Remove(k);   // 根拠: 記憶の上限（少ない組から捨てる）
        var g = cs.GroupBy(c => Find(c.Track)).Where(x => x.Count() > 1)
            .Select(x => new EntityGroup(x.Key, x.Select(c => c.Track).OrderBy(t => t).ToList(), x.Min(c => c.X0), x.Min(c => c.Y0), x.Max(c => c.X1), x.Max(c => c.Y1),
                x.SelectMany(c => x.Where(d => d.Track > c.Track).Select(d => _co.GetValueOrDefault((c.Track, d.Track)))).DefaultIfEmpty(0).Max())).ToList();
        if (g.Count > 0) GroupedSteps++;
        return g;
    }

    /// <summary>v0.30.1: 全部の組（候補が n なら n(n−1)/2）を、候補ごとに近い RelationNeighbors 組と、重なり・隣接・同じ動きの組に絞る</summary>
    private IReadOnlyList<RelationObs> Prune(IReadOnlyList<RelationObs> all, List<EntityCandidate> cs)
    {
        if (cs.Count <= RelationNeighbors + 1) return all;
        var keep = new HashSet<(int, int)>();
        foreach (var c in cs)
            foreach (var r in all.Where(r => r.TrackA == c.Track || r.TrackB == c.Track).OrderBy(r => r.Distance).ThenBy(r => r.TrackA).ThenBy(r => r.TrackB).Take(RelationNeighbors))
                keep.Add((r.TrackA, r.TrackB));
        return all.Where(r => r.Overlap || r.Adjacent || r.SameMove || keep.Contains((r.TrackA, r.TrackB))).ToList();
    }

    private IReadOnlyList<RelationObs> Relations(List<EntityCandidate> cs, FineGrid g)
    {
        var l = new List<RelationObs>();
        for (int i = 0; i < cs.Count; i++)
        for (int j = i + 1; j < cs.Count; j++)
        {
            var a = cs[i]; var b = cs[j];
            double dx = b.Cx - a.Cx, dy = b.Cy - a.Cy, d = Math.Sqrt(dx * dx + dy * dy);
            string ch = "new";
            if (_lastPos.TryGetValue(a.Track, out var pa) && _lastPos.TryGetValue(b.Track, out var pb))
            {
                double d0 = Math.Sqrt((pb.Cx - pa.Cx) * (pb.Cx - pa.Cx) + (pb.Cy - pa.Cy) * (pb.Cy - pa.Cy));
                ch = d < d0 - 0.5 ? "approach" : d > d0 + 0.5 ? "recede" : "same";   // 根拠: 移動と同じ区画の半分
            }
            bool overlap = a.X0 <= b.X1 && b.X0 <= a.X1 && a.Y0 <= b.Y1 && b.Y0 <= a.Y1;
            bool adj = !overlap && a.X0 <= b.X1 + 1 && b.X0 <= a.X1 + 1 && a.Y0 <= b.Y1 + 1 && b.Y0 <= a.Y1 + 1;
            bool same = a.Moved && b.Moved && Math.Abs(a.Dx - b.Dx) < 0.5 && Math.Abs(a.Dy - b.Dy) < 0.5;
            l.Add(new RelationObs(a.Track, b.Track, Math.Round(dx, 2), Math.Round(dy, 2), Math.Round(d, 2), ch, adj, overlap, same));
        }
        return l;
    }

    private static string StateName(int i) => i < 26 ? ((char)('A' + i)).ToString() : "S" + i;

    private static (int X0, int Y0, int X1, int Y1, double Cx, double Cy, int[] Hist, string App, int Cells, HashSet<int> CellsSet) Describe(FineGrid g, List<int> comp)
    {
        int x0 = comp.Min(c => c % g.Cols), x1 = comp.Max(c => c % g.Cols), y0 = comp.Min(c => c / g.Cols), y1 = comp.Max(c => c / g.Cols);
        double cx = comp.Average(c => c % g.Cols), cy = comp.Average(c => c / g.Cols);
        var hist = new int[64];
        foreach (var c in comp) { int k = g.Code[c]; hist[((k >> 2) & 3) | ((k >> 6) & 3) << 2 | ((k >> 10) & 3) << 4]++; }   // 各色 2 ビットの 64 ビン
        // 見た目: 外接矩形を 4×4 にそろえた色の符号（変化していない区画も含む＝その場所の見た目）
        ulong v = 14695981039346656037UL;
        for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
        {
            int x = x0 + (x1 - x0) * sx / 3, y = y0 + (y1 - y0) * sy / 3;
            v ^= (ulong)g.Code[y * g.Cols + x]; v *= 1099511628211UL;
        }
        v ^= (ulong)((x1 - x0) * 131 + (y1 - y0)); v *= 1099511628211UL;
        return (x0, y0, x1, y1, cx, cy, hist, v.ToString("x16"), comp.Count, comp.ToHashSet());
    }

    private static double HistDist(int[] a, int[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 1;
        double sa = Math.Max(1, a.Sum()), sb = Math.Max(1, b.Sum()), s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] / sa - b[i] / sb);
        return s / 2;
    }
}

/// <summary>
/// v0.29.1（ブロック 10〜12）: 層ごとの出来事（候補の移動・見た目だけの変化・出現・消失・文字の変化・数字の変化）を、
/// 入力なし（待つ手）と手の後に分けて数え（RegionCausality を出来事の数で使う）、画面×手ごとに出来事の起きる確率を予測して外れを数える。
/// 予測は手の選択には使わない（v0.29.x は記録と評価だけ）。
/// </summary>
public sealed class LocalEventModel
{
    public static readonly string[] Events = { "entity_moved", "appearance_only", "entity_appeared", "entity_disappeared", "text_changed", "number_changed" };
    public RegionCausality Causality { get; } = new(Events.Length);
    private readonly Dictionary<(string, string), (int N, int[] C, Dictionary<(int, int), int> Moves)> _c = new();
    private readonly double[] _surpSum = new double[Events.Length]; private readonly long[] _surpN = new long[Events.Length];
    private readonly Dictionary<(string Node, string Action, int Ev), (double Sum, int N)> _err = new();
    public long MoveChecks { get; private set; }
    public long MoveHits { get; private set; }

    public static int[] EventsOf(EntityLayer e, TextLayer t)
    {
        var l = new List<int>();
        if (e.Candidates.Any(c => c.Moved)) l.Add(0);
        if (e.Candidates.Any(c => c.AppearanceChanged && !c.Moved)) l.Add(1);
        if (e.Candidates.Any(c => c.New)) l.Add(2);
        if (e.Disappeared.Count > 0) l.Add(3);
        if (t.DiffKind is "numbers" or "strings" or "layout") l.Add(4);
        if (t.Numbers.Any(n => n.Previous is double p && p != n.Value)) l.Add(5);
        return l.ToArray();
    }

    /// <summary>出来事ごとの確率（Jeffreys）と、いちばん多い移動（区画）。観測がなければ null</summary>
    public (double[] P, (int Dx, int Dy)? Move)? Predict(string node, string action)
    {
        if (!_c.TryGetValue((node, action), out var x) || x.N == 0) return null;
        var p = x.C.Select(c => (c + 0.5) / (x.N + 1.0)).ToArray();
        (int, int)? mv = x.Moves.Count > 0 ? x.Moves.OrderByDescending(m => m.Value).ThenBy(m => m.Key).First().Key : null;
        return (p, mv);
    }

    /// <summary>予測と比べてから数える。戻り値: 出来事ごとの驚き（−ln p。予測がなければ空）</summary>
    public double[] Observe(string node, string action, bool natural, int[] events, EntityLayer e, string? ctx = null)
    {
        var surp = Array.Empty<double>();
        var pr = ctx == null ? Predict(node, action) : PredictContext(node, action, ctx);
        if (pr is { } p)
        {
            surp = new double[Events.Length];
            for (int i = 0; i < Events.Length; i++)
            {
                bool y = events.Contains(i); surp[i] = -Math.Log(y ? p.P[i] : 1 - p.P[i]);
                _surpSum[i] += surp[i]; _surpN[i]++;
                var k = (node, action, i); var v = _err.GetValueOrDefault(k); _err[k] = (v.Sum + surp[i], v.N + 1);
            }
            var moved = e.Candidates.Where(c => c.Moved).Select(c => ((int)Math.Round(c.Dx), (int)Math.Round(c.Dy))).ToList();
            if (p.Move is { } m && moved.Count > 0) { MoveChecks++; if (moved.Contains(m)) MoveHits++; }
        }
        if (!_c.TryGetValue((node, action), out var x)) _c[(node, action)] = x = (0, new int[Events.Length], new Dictionary<(int, int), int>());
        foreach (var i in events) x.C[i]++;
        foreach (var c in e.Candidates.Where(c => c.Moved)) { var k = ((int)Math.Round(c.Dx), (int)Math.Round(c.Dy)); x.Moves[k] = x.Moves.GetValueOrDefault(k) + 1; }
        _c[(node, action)] = (x.N + 1, x.C, x.Moves);
        if (natural) Causality.ObserveNatural(node, events); else Causality.ObserveAction(node, action, events);
        if (ctx != null)
        {
            var ck = (node, action, ctx);
            if (!_cx.TryGetValue(ck, out var cx)) _cx[ck] = cx = (0, new int[Events.Length]);
            foreach (var i in events.Distinct()) cx.C[i]++;
            _cx[ck] = (cx.N + 1, cx.C);
        }
        var bk = natural ? _nat : _act;
        if (!bk.TryGetValue(node, out var nb)) bk[node] = nb = (0, new int[Events.Length]);
        foreach (var i in events.Distinct()) nb.C[i]++;
        bk[node] = (nb.N + 1, nb.C);
        return surp;
    }

    private readonly Dictionary<string, (int N, int[] C)> _nat = new(StringComparer.Ordinal), _act = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Node, string Action, string Ctx), (int N, int[] C)> _cx = new();
    public const int ContextMinN = 20;   // 根拠: 分けたときの試算（SplitEstimateMinN）と同じ
    public long ContextUsed { get; private set; }

    /// <summary>
    /// v0.30.9（P-3）: 局所の文脈（直前の手のいちばん大きい候補の位置。CoarseStateEvidence.Context）で分けた予測。
    /// 出来事ごとに、文脈で分けたほうがよいとき（観測 ContextMinN 回以上・ベイズ因子 ≥ ln10・1 つ抜きの驚きが下がる。v0.29.6 の試算と同じ式）だけ、
    /// その文脈の率（Jeffreys）を使う。それ以外・その文脈が初めてなら、分けない率のまま
    /// </summary>
    public (double[] P, (int Dx, int Dy)? Move)? PredictContext(string node, string action, string ctx)
    {
        var b = Predict(node, action); if (b == null) return null;
        if (!_cx.TryGetValue((node, action, ctx), out var c) || c.N == 0) return b;
        var p = b.Value.P.ToArray(); bool used = false;
        for (int i = 0; i < Events.Length; i++)
            if (ContextHelps(node, action, i)) { p[i] = (c.C[i] + 0.5) / (c.N + 1.0); used = true; }
        if (used) ContextUsed++;
        return (p, b.Value.Move);
    }

    public bool ContextHelps(string node, string action, int ev)
    {
        var g = _cx.Where(k => k.Key.Node == node && k.Key.Action == action).Select(k => (Y: k.Value.C[ev], N: k.Value.N)).ToList();
        int tn = g.Sum(x => x.N), ty = g.Sum(x => x.Y);
        if (tn < ContextMinN || g.Count < 2) return false;
        double sep = g.Sum(x => GameKouryakuKun.Core.Exploration.RegionCausality.LogBeta(x.Y, x.N - x.Y)), pooled = GameKouryakuKun.Core.Exploration.RegionCausality.LogBeta(ty, tn - ty);
        if (sep - pooled < Math.Log(10)) return false;   // 根拠: 状態の分け直しの戻しと同じ（RevertMinLogBayesFactor）
        double lp = 0, ls = 0;
        foreach (var (gy, gn) in g)
        {
            if (gy > 0) { lp -= gy * Math.Log((ty - 1 + 0.5) / (tn - 1 + 1.0)); ls -= gy * Math.Log((gy - 1 + 0.5) / (gn - 1 + 1.0)); }
            if (gn - gy > 0) { lp -= (gn - gy) * Math.Log((tn - ty - 1 + 0.5) / (tn - 1 + 1.0)); ls -= (gn - gy) * Math.Log((gn - gy - 1 + 0.5) / (gn - 1 + 1.0)); }
        }
        return ls < lp;
    }

    /// <summary>
    /// v0.30.8（P-2）: この画面で待つ手（入力なし）を 1 回観測して、局所の出来事について分かる量。出来事ごとに WaitInformation.Part
    /// （入力なしの観測の不確かさ × 手の後にその出来事が起きる割合）。いちばん分かる出来事の値（InformationGain と同じ取り方）。手の観測がなければ 0
    /// </summary>
    public double NaturalInformation(string node)
    {
        if (!_act.TryGetValue(node, out var a) || a.N == 0) return 0;
        var n = _nat.TryGetValue(node, out var nn) ? nn : (N: 0, C: new int[Events.Length]);
        double best = 0;
        for (int i = 0; i < Events.Length; i++) best = Math.Max(best, WaitInformation.Part(n.C[i], n.N, a.C[i], a.N));
        return best;
    }

    /// <summary>
    /// v0.29.4（ブロック 13）: この画面でこの手をもう 1 回試して、局所の出来事について分かる量。出来事ごとの 2 値の Dirichlet 情報量（試すほど下がる）を
    /// いちばん分かる出来事の量（最大）を使う（文字の区画の値打ちと同じ取り方。新しい重みなし。起きない出来事を 6 つ足し合わせて値打ちを膨らませない）。
    /// 外れが続く出来事（平均の驚きが ln2 を超え、minN 回以上）は、この画面の見分けでは学べない（隠れた条件・状態の粗さ）ので値打ちに入れない（ノイズの多い所に引き寄せられない）。
    /// 観測がなければ null（呼ぶ側は既存の不確かさだけを使う）
    /// </summary>
    public double? InformationGain(string node, string action, int minN)
    {
        if (!_c.TryGetValue((node, action), out var x) || x.N == 0) return null;
        double best = 0;
        for (int i = 0; i < Events.Length; i++)
        {
            if (_err.TryGetValue((node, action, i), out var e) && e.N >= minN && e.Sum / e.N > Math.Log(2)) { ExcludedPersistent++; continue; }
            best = Math.Max(best, TransitionModel.BinaryUncertainty(x.C[i], x.N - x.C[i]));
        }
        return best;
    }
    public long ExcludedPersistent { get; private set; }

    public double MeanSurprise(int ev) => _surpN[ev] > 0 ? _surpSum[ev] / _surpN[ev] : double.NaN;

    /// <summary>外れが続く（画面×手×出来事）: 平均の驚きが、確率の分からない（0.5）ときの驚き ln2 を超えるもの。状態の粗さ・隠れた条件・自然な変化の取り違えなどの仮説の材料（記録のみ）</summary>
    public IEnumerable<(string Node, string Action, string Event, double Mean, int N)> PersistentErrors(int minN) =>
        _err.Where(kv => kv.Value.N >= minN && kv.Value.Sum / kv.Value.N > Math.Log(2))
            .Select(kv => (kv.Key.Node, kv.Key.Action, Events[kv.Key.Ev], Math.Round(kv.Value.Sum / kv.Value.N, 3), kv.Value.N))
            .OrderByDescending(x => x.Item4).ThenBy(x => x.Node, StringComparer.Ordinal);

    public string Summary() => string.Join("・", Events.Select((e, i) => $"{e} 驚き {(double.IsNaN(MeanSurprise(i)) ? "—" : MeanSurprise(i).ToString("0.00"))}")) + $"・移動の予測 {MoveHits}/{MoveChecks}";
}

/// <summary>
/// v0.29.5（ブロック 14）: World Model（次の画面の予測）と局所の予測を同じ手で並べる。
/// 次の画面は当たるのに局所の出来事の外れが続く（画面×手×出来事）は「画面の見分けが局所には粗い」の候補（状態の分け直しの材料。記録だけで分けはしない）。
/// 次の画面も外れるなら、局所ではなく画面の移り方そのものが分かっていない（候補にしない）。
/// </summary>
public sealed class CoarseStateEvidence
{
    private readonly Dictionary<(string, string), (int N, int Hit)> _w = new();
    private readonly Dictionary<(string, string, int), (double Sum, int N)> _l = new();
    public long Joint { get; private set; }

    // v0.29.6（選択肢 A）: 局所の位置で分けたら外れが減るかの試算用。（画面, 手, 出来事）ごとに（直前の局所の位置, 起きたか）を貯める。実際には分けない
    private readonly Dictionary<(string, string, int), List<(string Ctx, bool Y)>> _ctx = new();
    public int MaxPerKey { get; init; } = 2000;   // 根拠: 1 組あたりの記憶の上限（試算に要る 20 回より十分多く、1 実行の手数の数倍）

    /// <summary>直前の手の局所パターンの候補のうち、いちばん大きいものの中心を 8×6 の区画にした位置（候補がなければ "none"）。意味は付けない</summary>
    public static string Context(EntityLayer? prev)
    {
        if (prev == null || prev.Candidates.Count == 0) return "none";
        var c = prev.Candidates.OrderByDescending(x => x.Cells).ThenBy(x => x.Track).First();
        return $"{(int)(c.Cx * 8 / FineGrid.DefaultCols)},{(int)(c.Cy * 6 / FineGrid.DefaultRows)}";   // 根拠: 8×6 は文字の配置の区画と同じ粗さ
    }

    public void ObserveContext(string node, string action, string ctx, IReadOnlyCollection<int> occurred)
    {
        for (int i = 0; i < LocalEventModel.Events.Length; i++)
        {
            var k = (node, action, i); if (!_ctx.TryGetValue(k, out var l)) _ctx[k] = l = new();
            if (l.Count < MaxPerKey) l.Add((ctx, occurred.Contains(i)));
        }
    }

    /// <summary>
    /// 分けたときの試算: ベイズ因子（位置ごとの率 対 1 つの率。RegionCausality と同じ周辺尤度）と、1 つ抜きの予測の驚きの平均（分けない／分ける。Jeffreys）。
    /// 対象は候補（Candidates）のうち観測が minN 回以上の組
    /// </summary>
    public IReadOnlyList<(string Node, string Action, string Event, int N, int Contexts, double LogBf, double LooPooled, double LooSplit)> SplitEstimates(int candidateMinN, int minN)
    {
        var res = new List<(string, string, string, int, int, double, double, double)>();
        foreach (var c in Candidates(candidateMinN))
        {
            int ev = Array.IndexOf(LocalEventModel.Events, c.Event);
            if (!_ctx.TryGetValue((c.Node, c.Action, ev), out var l) || l.Count < minN) continue;
            var g = l.GroupBy(x => x.Ctx).Select(x => (Y: x.Count(z => z.Y), N: x.Count())).ToList();
            int ty = g.Sum(x => x.Y), tn = l.Count;
            double sep = g.Sum(x => GameKouryakuKun.Core.Exploration.RegionCausality.LogBeta(x.Y, x.N - x.Y));
            double pooled = GameKouryakuKun.Core.Exploration.RegionCausality.LogBeta(ty, tn - ty);
            double lp = 0, ls = 0;
            foreach (var (gy, gn) in g)
            {
                // 1 つ抜き: 起きた観測 gy 個・起きなかった観測 gn-gy 個それぞれを、自分を除いた数で予測
                if (gy > 0) { lp -= gy * Math.Log((ty - 1 + 0.5) / (tn - 1 + 1.0)); ls -= gy * Math.Log((gy - 1 + 0.5) / (gn - 1 + 1.0)); }
                if (gn - gy > 0) { lp -= (gn - gy) * Math.Log((tn - ty - 1 + 0.5) / (tn - 1 + 1.0)); ls -= (gn - gy) * Math.Log((gn - gy - 1 + 0.5) / (gn - 1 + 1.0)); }
            }
            res.Add((c.Node, c.Action, c.Event, tn, g.Count, Math.Round(sep - pooled, 3), Math.Round(lp / tn, 4), Math.Round(ls / tn, 4)));
        }
        return res;
    }

    public void Observe(string node, string action, bool worldHit, IReadOnlyList<double> localSurprise)
    {
        var w = _w.GetValueOrDefault((node, action)); _w[(node, action)] = (w.N + 1, w.Hit + (worldHit ? 1 : 0));
        for (int i = 0; i < localSurprise.Count; i++) { var k = (node, action, i); var v = _l.GetValueOrDefault(k); _l[k] = (v.Sum + localSurprise[i], v.N + 1); }
        if (localSurprise.Count > 0) Joint++;
    }

    public double WorldHitRate(string node, string action) { var w = _w.GetValueOrDefault((node, action)); return (w.Hit + 0.5) / (w.N + 1.0); }

    /// <summary>次の画面の当たる割合（Jeffreys）が半分を超え、局所の平均の驚きが ln2 を超える組（minN 回以上）</summary>
    public IEnumerable<(string Node, string Action, string Event, double WorldHit, double LocalSurprise, int N)> Candidates(int minN) =>
        _l.Where(kv => kv.Value.N >= minN && kv.Value.Sum / kv.Value.N > Math.Log(2) && WorldHitRate(kv.Key.Item1, kv.Key.Item2) > 0.5)
          .Select(kv => (kv.Key.Item1, kv.Key.Item2, LocalEventModel.Events[kv.Key.Item3], Math.Round(WorldHitRate(kv.Key.Item1, kv.Key.Item2), 3), Math.Round(kv.Value.Sum / kv.Value.N, 3), kv.Value.N))
          .OrderByDescending(x => x.Item5).ThenBy(x => x.Item1, StringComparer.Ordinal);
}

/// <summary>
/// v0.29.6（Skill）: 操作の並び（スキルの候補）を最後まで送ったときに起きた局所の出来事を数え、並びの「局所の効果」として持つ（意味は付けない）。
/// 効果がある＝その出来事の起きる割合（Jeffreys）が半分を超え、minN 回以上。記録だけで、並びの選び方は変えない
/// </summary>
public sealed class SkillLocalEffects
{
    private readonly Dictionary<string, (int N, int[] C)> _s = new(StringComparer.Ordinal);
    public void Observe(string sequence, IReadOnlyCollection<int> occurred)
    {
        if (!_s.TryGetValue(sequence, out var x)) x = (0, new int[LocalEventModel.Events.Length]);
        foreach (var i in occurred) x.C[i]++;
        _s[sequence] = (x.N + 1, x.C);
    }
    public IEnumerable<(string Sequence, int N, IReadOnlyList<string> Effects)> Effects(int minN) =>
        _s.Where(kv => kv.Value.N >= minN)
          .Select(kv => (kv.Key, kv.Value.N, (IReadOnlyList<string>)LocalEventModel.Events.Where((e, i) => (kv.Value.C[i] + 0.5) / (kv.Value.N + 1.0) > 0.5).ToList()))
          .OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.Ordinal);
    public int Sequences => _s.Count;
}
