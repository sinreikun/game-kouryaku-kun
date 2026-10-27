using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GameKouryakuKun.Core.Control;

namespace GameKouryakuKun.App;

/// <summary>
/// v0.26.0 Human Control の小さな窓。Human Control が ON のときだけ出し、OFF で隠す。位置は ui-settings.json（human_window_left/top）に残す。
/// ボタンは 8方向・テンキーの並び（Num0〜Num7）・その他（Tab）・同時押しのキー（押したままにする切り替え）。
/// 送る操作は Profile の単独キーと chords だけで、攻略くんの通常の入力経路（Action Source=human）を通る。
/// </summary>
public sealed class HumanControlWindow : Window
{
    private readonly IReadOnlyDictionary<string, string> _physical;
    private readonly HashSet<string> _allowed;
    private readonly Func<string, bool> _send;
    private readonly Grid _dirs = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly List<ToggleButton> _mods = new();
    private string? _modifier;
    private readonly TextBlock _modState = new() { Text = "同時押し: なし（方向は単独で送ります）", Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.Bold };
    private static System.Windows.Media.Brush Res(string key) => (System.Windows.Media.Brush)Application.Current.FindResource(key);
    private string Phys(string logical) => _physical.TryGetValue(logical, out var p) ? p : logical;
    public event Action? PlacementChanged;
    public bool AllowClose { get; set; }
    private readonly System.Windows.Threading.DispatcherTimer _moveTimer = new() { Interval = TimeSpan.FromSeconds(1) };   // 根拠: 汎用（窓を動かし終えたとみなす間）

    public HumanControlWindow(IReadOnlyDictionary<string, string> physical, IEnumerable<string> allowed, Func<string, bool> send)
    {
        _physical = physical; _allowed = new HashSet<string>(allowed, StringComparer.Ordinal); _send = send;
        Title = "Human Control"; SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        Background = Res("Bg"); Foreground = Res("Fg");
        var root = new StackPanel { Margin = new Thickness(10) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 3; i++) { _dirs.RowDefinitions.Add(new RowDefinition()); _dirs.ColumnDefinitions.Add(new ColumnDefinition()); }
        row.Children.Add(_dirs);

        var (num, others) = HumanPad.ButtonCells(physical, _allowed);
        var np = new Grid();
        for (int i = 0; i < 4; i++) np.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < 3; i++) np.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var c in num)
        {
            var b = Key(c.Label, c.Action);
            Grid.SetRow(b, c.Row); Grid.SetColumn(b, c.Col); Grid.SetColumnSpan(b, c.ColSpan); np.Children.Add(b);
        }
        row.Children.Add(np);
        var side = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        foreach (var c in others) side.Children.Add(Key(c.Label, c.Action));
        if (_allowed.Contains("wait")) side.Children.Add(Key("待つ", "wait"));
        row.Children.Add(side);
        root.Children.Add(row);

        var mods = HumanPad.Modifiers(_allowed);
        if (mods.Count > 0)
        {
            var mp = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            mp.Children.Add(new TextBlock { Text = "同時押し（押したまま→方向）:", Foreground = Res("Fg"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            foreach (var m in mods)
            {
                string ml = physical.TryGetValue(m, out var p) ? p : m;
                var t = new ToggleButton { Content = ml, Tag = m, MinWidth = 64, Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4), Style = (Style)Application.Current.FindResource("ModToggle"),
                    ToolTip = "押している間、方向のボタンはこのキーとの同時押し（Profile の chords にあるものだけ）を送ります" };
                t.Click += (_, _) =>
                {
                    _modifier = t.IsChecked == true ? m : null;
                    foreach (var o in _mods) { if (o != t) o.IsChecked = false; o.Content = o.IsChecked == true ? $"● {Phys((string)o.Tag)} 押したまま" : Phys((string)o.Tag); }
                    _modState.Text = _modifier == null ? "同時押し: なし（方向は単独で送ります）" : $"同時押し: {Phys(_modifier)} ＋ 方向（もう一度押すと離す）";
                    _modState.Foreground = _modifier == null ? Res("Dim") : Res("ModOnBg");
                    BuildDirections();
                };
                _mods.Add(t); mp.Children.Add(t);
            }
            root.Children.Add(mp);
            root.Children.Add(_modState);
        }
        Content = root;
        BuildDirections();
        // v0.26.1: 動かしている間は保存しない（実機 v0.26.0 で 1 回の移動に 366 回保存）。止めて 1 秒後に 1 回
        _moveTimer.Tick += (_, _) => { _moveTimer.Stop(); PlacementChanged?.Invoke(); };
        LocationChanged += (_, _) => { _moveTimer.Stop(); _moveTimer.Start(); };
        Closing += (s, e) => { if (AllowClose) return; e.Cancel = true; Hide(); PlacementChanged?.Invoke(); };   // 閉じても位置は残す（次に ON にすると同じ位置に出る）
    }

    private Button Key(string label, string action)
    {
        var b = new Button { Content = label, Tag = action, MinWidth = 48, MinHeight = 34, Margin = new Thickness(2), FontSize = 13, IsEnabled = action.Length > 0,
            ToolTip = action.Length > 0 ? $"攻略くんの入力経路で {action}（{label}）を送ります" : "Profile の chords にない組合せです" };
        if (action.Length == 0) { b.Content = "—"; b.Background = Res("PadOffBg"); b.Foreground = Res("PadOffFg"); }
        b.Click += (_, _) => { if (b.Tag is string a && a.Length > 0) _send(a); };
        return b;
    }

    private void BuildDirections()
    {
        _dirs.Children.Clear();
        foreach (var c in HumanPad.DirectionCells(_physical, _allowed, _modifier))
        {
            var b = Key(c.Label, c.Action);
            if (_modifier != null && c.Action.Length > 0) { b.Background = Res("ChordPadBg"); b.BorderBrush = Res("ChordPadBorder"); b.BorderThickness = new Thickness(2); b.Foreground = Res("FgStrong"); b.FontWeight = FontWeights.Bold; }
            Grid.SetRow(b, c.Row); Grid.SetColumn(b, c.Col); _dirs.Children.Add(b);
        }
    }

    public void SetEnabledKeys(bool on)
    {
        IsEnabled = on;
    }

    public static (double L, double T)? Saved(GameKouryakuKun.Core.Ui.UiSettings? ui)
    {
        if (ui?.GetValue("human_window_left") is string l && ui.GetValue("human_window_top") is string t
            && double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return (x, y);
        return null;
    }
}
