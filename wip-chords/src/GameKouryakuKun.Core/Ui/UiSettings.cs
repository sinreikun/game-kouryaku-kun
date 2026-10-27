using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameKouryakuKun.Core.Ui;

/// <summary>
/// 表示だけの設定（v0.12.0）: Viewer 右側の各欄を畳んでいるかどうか。データフォルダの ui-settings.json に保存し、次の起動で戻す。
/// 学習 DB とは別のファイルで、記録・学習・行動の選択からは参照しない（静的チェックで確かめる）
/// </summary>
public sealed class UiSettings
{
    public const string FileName = "ui-settings.json";
    private readonly string _path;
    private readonly Dictionary<string, bool> _collapsed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);   // v0.19.1: 実行時間・手数上限・グラフの種類など
    private WindowPlacement? _window;
    private readonly object _gate = new();
    /// <summary>読み込めなかったときの理由（壊れたファイルは脇へ移して既定値で始める）</summary>
    public string? LoadProblem { get; private set; }

    private UiSettings(string path) { _path = path; }

    public static UiSettings Load(string dir)
    {
        var s = new UiSettings(Path.Combine(dir, FileName));
        try
        {
            if (!File.Exists(s._path)) return s;
            var dto = JsonSerializer.Deserialize(File.ReadAllText(s._path), UiSettingsJson.Default.Dto);
            if (dto?.Collapsed != null) foreach (var (k, v) in dto.Collapsed) s._collapsed[k] = v;
            if (dto?.Values != null) foreach (var (k, v) in dto.Values) s._values[k] = v;
            s._window = dto?.Window;
        }
        catch (Exception e)
        {
            s.LoadProblem = e.Message;
            try { File.Move(s._path, s._path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
        }
        return s;
    }

    public bool IsCollapsed(string section) { lock (_gate) return _collapsed.TryGetValue(section, out var v) && v; }

    /// <summary>畳む・開くを記録してすぐ保存する（一時ファイルに書いてから置き換える。途中で止まっても前のファイルが残る）</summary>
    public void SetCollapsed(string section, bool collapsed) { lock (_gate) { _collapsed[section] = collapsed; Save(); } }

    /// <summary>表示・開始の設定の値（v0.19.1）。無ければ null</summary>
    public string? GetValue(string key) { lock (_gate) return _values.TryGetValue(key, out var v) ? v : null; }
    public void SetValue(string key, string value) { lock (_gate) { if (_values.TryGetValue(key, out var old) && old == value) return; _values[key] = value; Save(); } }
    public WindowPlacement? Window { get { lock (_gate) return _window; } }
    public void SetWindow(WindowPlacement w) { lock (_gate) { _window = w; Save(); } }

    private void Save()
    {
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dto { Collapsed = new SortedDictionary<string, bool>(_collapsed, StringComparer.Ordinal), Values = new SortedDictionary<string, string>(_values, StringComparer.Ordinal), Window = _window }, UiSettingsJson.Default.Dto));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    internal sealed class Dto
    {
        public int Version { get; set; } = 1;   // 根拠: 汎用（設定ファイルの形式の版）
        public SortedDictionary<string, bool>? Collapsed { get; set; }
        public SortedDictionary<string, string>? Values { get; set; }
        public WindowPlacement? Window { get; set; }
    }
}

/// <summary>メインウィンドウの位置・大きさ（v0.19.1、表示だけ。WPF の論理単位）。Left/Top/Width/Height は通常表示のときの値</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>画面上の長方形（作業領域など）</summary>
public readonly record struct ScreenArea(double X, double Y, double W, double H)
{
    public double Overlap(double x, double y, double w, double h) => Math.Max(0, Math.Min(X + W, x + w) - Math.Max(X, x)) * Math.Max(0, Math.Min(Y + H, y + h) - Math.Max(Y, y));
}

/// <summary>保存した位置を今のモニターに安全に戻す（v0.19.1）。いちばん重なるモニターに収め、どれとも十分に重ならなければ先頭（主モニター）の中央へ。値が壊れていれば null（既定の表示）</summary>
public static class WindowRestore
{
    public const double MinVisible = 0.25;   // 根拠: 汎用（窓の 1/4 以上が見えていなければ画面外とみなす）

    public static WindowPlacement? Fit(WindowPlacement? p, IReadOnlyList<ScreenArea> areas, double minW, double minH)
    {
        if (p == null || areas.Count == 0) return null;
        double[] v = { p.Left, p.Top, p.Width, p.Height };
        if (v.Any(d => double.IsNaN(d) || double.IsInfinity(d)) || p.Width < 1 || p.Height < 1) return null;
        int best = 0; double bestOv = -1;
        for (int i = 0; i < areas.Count; i++) { var o = areas[i].Overlap(p.Left, p.Top, p.Width, p.Height); if (o > bestOv) { bestOv = o; best = i; } }
        bool visible = bestOv >= MinVisible * p.Width * p.Height;
        var a = visible ? areas[best] : areas[0];
        double w = Math.Clamp(p.Width, Math.Min(minW, a.W), a.W), h = Math.Clamp(p.Height, Math.Min(minH, a.H), a.H);
        double l = visible ? Math.Clamp(p.Left, a.X, a.X + a.W - w) : a.X + (a.W - w) / 2;
        double t = visible ? Math.Clamp(p.Top, a.Y, a.Y + a.H - h) : a.Y + (a.H - h) / 2;
        return new WindowPlacement { Left = l, Top = t, Width = w, Height = h, Maximized = p.Maximized };
    }
}

/// <summary>表示の設定ファイルの読み書き（ソース生成。リフレクションを使わない）</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiSettings.Dto))]
internal sealed partial class UiSettingsJson : JsonSerializerContext { }
