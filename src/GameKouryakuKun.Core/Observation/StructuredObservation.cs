using System.Text.Json;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Ocr;

namespace GameKouryakuKun.Core.Observation;

/// <summary>層の状態: この手で測った・画素が同じなので前の結果を継承した（変わっていないことは測った）・取得に失敗した・まだない層</summary>
public static class LayerStatus
{
    public const string Observed = "observed", Inherited = "inherited", NotObserved = "not_observed", NotImplemented = "not_implemented";
}

public sealed record GlobalScene(string? Node, string? Mode, string? Layout, string? VisualState, string? FgTextLayout, string Status);

/// <summary>変化した画素区画（16×12）の 4 近傍のつながり。座標は区画の番号。Causality は区画の因果の分け方の多数（意味は付けない）</summary>
public sealed record ChangeRegion(int Id, int Track, int X0, int Y0, int X1, int Y1, int Cells, string Causality, double NaturalRate, bool NewTrack);

public sealed record RegionLayer(string Status, int ChangedCells, IReadOnlyList<ChangeRegion> Regions)
{
    /// <summary>v0.30.4: 画面全体の共通のずれ（細かい区画の単位）。あれば Regions はずれを差し引いた後の変化（ずれ自体は手と結びつく大域の観測としてここに残す）</summary>
    public double[]? CommonShift { get; init; }
    /// <summary>v0.30.4: 差し引く前に変わった区画の数</summary>
    public int? RawChangedCells { get; init; }
}

public sealed record TextLineObs(string Text, int X, int Y, int W, int H, double Confidence, int Cell, string Status, string Change);

/// <summary>数字の系列（文字の配置の区画ごと・行の中の順番ごと）。何の数かは持たない</summary>
public sealed record NumberObs(int Cell, int Index, double Value, double? Previous, IReadOnlyList<double> Series);

public sealed record TextLayer(string Status, string ReadKind, string DiffKind, IReadOnlyList<TextLineObs> Lines, IReadOnlyList<NumberObs> Numbers,
    int Observed, int Inherited, double ActionCaused);

public sealed record TemporalLayer(double BoundaryMs, bool Changed, double? NaturalChange, string Status)
{
    /// <summary>v0.32.2（P-7）: 前の手が静止した後、この手の入力の前までに来た変化（遅れて来た変化・入力なしの変化）。なければ null</summary>
    public DelayedChange? Delayed { get; init; }
}

/// <summary>
/// v0.32.2（P-7）: 遅れて来た変化。FromStep の手の結果（静止と判定した画面）から AfterMs 後の画面までに変わった細かい区画。
/// Late = 前の手を「変化なし」と判定していた（判定が早すぎた）。意味は付けない
/// </summary>
public sealed record DelayedChange(long FromStep, double AfterMs, int Cells, IReadOnlyList<int> CellList, bool Late)
{
    public static DelayedChange? Of(GameKouryakuKun.Core.Imaging.Frame? settled, GameKouryakuKun.Core.Imaging.Frame? next, long fromStep, bool late)
    {
        if (settled == null || next == null) return null;
        var ch = FineGrid.Of(next).ChangedFrom(FineGrid.Of(settled));
        return ch.Count == 0 ? null : new DelayedChange(fromStep, Math.Round(next.TimestampMs - settled.TimestampMs, 1), ch.Count, ch, late);
    }
}

/// <summary>
/// v0.29.0: 1 手の構造化した観測（schema 1）。層ごとに状態を持ち、まだない層は not_implemented と明示する。
/// 旧経験（この形のない手）は legacy coarse observation として区別し、推測で埋めない
/// </summary>
public sealed record StructuredObservation(int Schema, long Step, string Provenance, string Action, string? Before, string? After,
    GlobalScene Global, RegionLayer Regions, TextLayer Text, TemporalLayer Temporal)
{
    public const int CurrentSchema = 1;   // 根拠: 形の版（構造化した観測の最初の版）
    /// <summary>v0.29.1: 局所パターンの候補・追跡・見た目の状態・関係（作っていない呼び出しでは null＝not_implemented のまま）</summary>
    public EntityLayer? EntityLayer { get; init; }
    /// <summary>v0.29.1: この手の前に出した局所の予測と、その外れ（驚き）</summary>
    public PredictionObs? Prediction { get; init; }
    public string Entities => EntityLayer?.Status ?? LayerStatus.NotImplemented;
    public string Relations => EntityLayer == null ? LayerStatus.NotImplemented : EntityLayer.Status;
    public string Appearance => EntityLayer == null ? LayerStatus.NotImplemented : EntityLayer.Status;
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
}

/// <summary>v0.29.1: 出来事の予測（確率）・実際に起きた出来事・驚き（−ln p）・移動の予測。Events の名前は LocalEventModel.Events（意味のラベルではなく観測の種類）</summary>
public sealed record PredictionObs(string Status, IReadOnlyList<double>? P, IReadOnlyList<int> Occurred, IReadOnlyList<double> Surprise, int[]? PredictedMove)
{
    /// <summary>v0.29.5: 同じ手の World Model の予測（いちばん出やすい次の画面と確率・当たったか）</summary>
    public string? WorldNext { get; init; }
    public double? WorldP { get; init; }
    public bool? WorldHit { get; init; }
}

/// <summary>Structured Observation を作る（変化領域の追跡 ID と数字の系列の状態を持つ。乱数なし）</summary>
public sealed class ObservationBuilder
{
    public int Cols { get; init; } = GameKouryakuKun.Core.Ocr.TextReader.GridCols;
    public int Rows { get; init; } = GameKouryakuKun.Core.Ocr.TextReader.GridRows;
    public int SeriesLength { get; init; } = 8;   // 根拠: 記録用（「35→31→27」のような系列を表示・記録する長さ。判断には使わない）
    private List<ChangeRegion> _prevRegions = new();
    private int _nextTrack = 1;
    private readonly Dictionary<(int Cell, int Index), List<double>> _series = new();
    public long Built { get; private set; }
    public long TextObservedLines { get; private set; }
    public long TextInheritedLines { get; private set; }
    public long TextNotObserved { get; private set; }
    public long Tracks => _nextTrack - 1;
    public long ContinuedRegions { get; private set; }

    /// <summary>変化した区画を 4 近傍でつなぎ、前の手の領域と重なるものは同じ追跡 ID にする（重なりがいちばん多いもの）</summary>
    public IReadOnlyList<ChangeRegion> Regions(IReadOnlyCollection<int> changed, Func<int, (string Class, double? Natural)>? causality = null)
    {
        var set = new HashSet<int>(changed); var seen = new HashSet<int>(); var list = new List<ChangeRegion>();
        foreach (var start in changed.OrderBy(x => x))
        {
            if (!seen.Add(start)) continue;
            var comp = new List<int>(); var q = new Queue<int>(); q.Enqueue(start);
            while (q.Count > 0)
            {
                int c = q.Dequeue(); comp.Add(c); int x = c % Cols, y = c / Cols;
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    if (nx >= 0 && ny >= 0 && nx < Cols && ny < Rows && set.Contains(ny * Cols + nx) && seen.Add(ny * Cols + nx)) q.Enqueue(ny * Cols + nx);
            }
            string cls = "unknown"; double? nat = null;
            if (causality != null)
            {
                var cs = comp.Select(causality).ToList();
                cls = cs.GroupBy(z => z.Class).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
                var ns = cs.Where(z => z.Natural.HasValue).Select(z => z.Natural!.Value).ToList(); if (ns.Count > 0) nat = ns.Average();
            }
            var cellSet = comp.ToHashSet();
            var prev = _prevRegions.Select(p => (p, ov: Overlap(p, comp))).Where(z => z.ov > 0).OrderByDescending(z => z.ov).ThenBy(z => z.p.Track).FirstOrDefault();
            bool isNew = prev.p == null;
            int track = isNew ? _nextTrack++ : prev.p!.Track;
            if (!isNew) ContinuedRegions++;
            list.Add(new ChangeRegion(list.Count, track, comp.Min(c => c % Cols), comp.Min(c => c / Cols), comp.Max(c => c % Cols), comp.Max(c => c / Cols), comp.Count, cls, nat is double nv ? Math.Round(nv, 4) : -1, isNew));
        }
        // 同じ追跡 ID に 2 つ以上が重なったら、大きいほうだけ続ける（分かれた側は新しい ID）
        var dup = list.GroupBy(r => r.Track).Where(g => g.Count() > 1).SelectMany(g => g.OrderByDescending(r => r.Cells).Skip(1)).ToHashSet();
        list = list.Select(r => dup.Contains(r) ? r with { Track = _nextTrack++, NewTrack = true } : r).ToList();
        _prevRegions = list;
        return list;
    }

    private int Overlap(ChangeRegion p, List<int> comp) =>
        comp.Count(c => c % Cols >= p.X0 && c % Cols <= p.X1 && c / Cols >= p.Y0 && c / Cols <= p.Y1);

    /// <summary>文字の層: 行ごとの観測・継承と前の手との差、数字の系列</summary>
    public TextLayer Text(TextSnapshot? prev, TextSnapshot? snap, string readKind, OcrBox? readBox, TextDiff? diff, double actionCaused)
    {
        if (snap == null) { TextNotObserved++; return new TextLayer(LayerStatus.NotObserved, readKind, "unknown", Array.Empty<TextLineObs>(), Array.Empty<NumberObs>(), 0, 0, 0); }
        var prevByCell = (prev?.Lines ?? Array.Empty<OcrLine>()).GroupBy(l => TextDiff.LayoutCell(l, prev!.Width, prev.Height)).ToDictionary(g => g.Key, g => g.Select(l => l.Text.Trim()).ToHashSet());
        var lines = new List<TextLineObs>(); int obs = 0, inh = 0;
        foreach (var l in snap.Lines)
        {
            int cell = TextDiff.LayoutCell(l, snap.Width, snap.Height);
            bool inherited = readKind == "reuse" || (readKind == "cache" && readBox == null) || (readBox is { } b && !Intersects(l.Box, b));
            string status = inherited ? LayerStatus.Inherited : LayerStatus.Observed;
            if (inherited) inh++; else obs++;
            string change = prev == null ? "unknown" : !prevByCell.TryGetValue(cell, out var ps) ? "new" : ps.Contains(l.Text.Trim()) ? "same" : "changed";
            lines.Add(new TextLineObs(l.Text, l.Box.X, l.Box.Y, l.Box.W, l.Box.H, Math.Round(l.Confidence ?? -1, 3), cell, status, change));
        }
        TextObservedLines += obs; TextInheritedLines += inh;
        var nums = new List<NumberObs>(); var seenKeys = new HashSet<(int, int)>();
        foreach (var g in lines.GroupBy(x => x.Cell).OrderBy(g => g.Key))
        {
            var vals = g.OrderBy(x => x.Y).ThenBy(x => x.X).SelectMany(x => Numbers(x.Text)).ToList();
            for (int i = 0; i < vals.Count; i++)
            {
                var k = (g.Key, i); seenKeys.Add(k);
                if (!_series.TryGetValue(k, out var s)) _series[k] = s = new List<double>();
                double? pv = s.Count > 0 ? s[^1] : null;
                if (pv != vals[i] || s.Count == 0) { s.Add(vals[i]); if (s.Count > SeriesLength) s.RemoveAt(0); }
                nums.Add(new NumberObs(g.Key, i, vals[i], pv, s.ToList()));
            }
        }
        string st = readKind is "full" or "region" ? (inh > 0 ? LayerStatus.Observed + "+" + LayerStatus.Inherited : LayerStatus.Observed) : LayerStatus.Inherited;
        return new TextLayer(st, readKind, diff?.Kind ?? "unpaired", lines, nums, obs, inh, Math.Round(actionCaused, 4));
    }

    /// <summary>行の中の数（整数・小数。記号は付けない）</summary>
    public static IEnumerable<double> Numbers(string t)
    {
        var m = System.Text.RegularExpressions.Regex.Matches(t, @"\d+(?:\.\d+)?");
        foreach (System.Text.RegularExpressions.Match x in m) if (double.TryParse(x.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) yield return v;
    }

    private static bool Intersects(OcrBox a, OcrBox b) => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    public StructuredObservation Build(long step, string provenance, string action, string? before, string? after, GlobalScene global, RegionLayer regions, TextLayer text, TemporalLayer temporal)
    {
        Built++;
        return new StructuredObservation(StructuredObservation.CurrentSchema, step, provenance, action, before, after, global, regions, text, temporal);
    }
}
