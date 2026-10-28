using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameKouryakuKun.Core.Activity;
using GameKouryakuKun.Core.Capture;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Imaging;
using Frame = GameKouryakuKun.Core.Imaging.Frame;
using GameKouryakuKun.Core.Ocr;
using GameKouryakuKun.Core.Turn;
using GameKouryakuKun.Core.Viewer;
using Line = System.Windows.Shapes.Line;
using Polyline = System.Windows.Shapes.Polyline;
using GameKouryakuKun.Ocr;
using GameKouryakuKun.Windows;
using GameKouryakuKun.Storage;

namespace GameKouryakuKun.App;

public sealed record LogRow(string Text, Brush Brush);

public partial class MainWindow : Window
{
    private readonly AppEnv _env;
    private readonly ObservableCollection<LogRow> _log = new();
    private readonly DispatcherTimer _liveTimer, _panelTimer;
    private readonly RawInputActivityMonitor _activity;
    private readonly EmergencyStopHotkey _hotkey;
    private readonly StandInWindow _standIn;
    private readonly TimerResolution _timerRes = new();
    private GameConnection? _con;
    private PlaySession? _session;
    private bool _connecting;
    private WriteableBitmap? _bmp;
    private long _shownFrameId;
    private PaddleOcrEngine? _paddle;
    /// <summary>v5 を使うときも v4 が導入済みなら、OCR テストの「比較」に並べる（全エンジン比較）</summary>
    private PaddleOcrEngine? _paddleV4Compare;
    private readonly WindowsMediaOcrEngine _winOcr = new();
    private CaptureStatus? _capStatus;
    private IReadOnlyList<Setup.ComponentStatus> _compStatus = Array.Empty<Setup.ComponentStatus>();

    private TextBlock _pState = null!, _pGoal = null!, _pCands = null!, _pChoice = null!, _pResult = null!, _pHypo = null!, _pFind = null!,
        _pRevise = null!, _pTemporal = null!, _pRunStats = null!, _pGlobal = null!, _pPatterns = null!, _pBackend = null!, _pSup = null!;

    private static readonly Brush BInfo = Frozen(0xE8, 0xE8, 0xEC), BOk = Frozen(0x8B, 0xE0, 0x8B), BWarn = Frozen(0xFF, 0xC8, 0x57), BErr = Frozen(0xFF, 0x7A, 0x70), BDim = Frozen(0x9A, 0x9C, 0xA6);
    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    public MainWindow(AppEnv env)
    {
        _env = env;
        InitializeComponent();
        VersionText.Text = $"v{AppEnv.Version} ／ 手探り探索（事前知識なし・キーの意味を使わない）";
        LogList.ItemsSource = _log;
        BuildPanels();
        RestoreUiState();

        _activity = new RawInputActivityMonitor(UserActivityMode.AllButOwn);
        _activity.UserActivity += ev => _session?.OnUserActivity(ev);
        _hotkey = new EmergencyStopHotkey();
        _hotkey.Pressed += () => Dispatcher.BeginInvoke(() => Emergency("Ctrl+Shift+F9"));
        _standIn = new StandInWindow();

        AddLog($"データフォルダ: {env.DataRoot}（{env.DataRootReason}）", LogLevel.Dim);
        foreach (var n in env.StartupNotes) AddLog(n, LogLevel.Warn);
        if (_ui != null) _env.Log.Write("view_settings_loaded", ("panels", _panelTitles.Count), ("collapsed", _panelTitles.Count(_ui.IsCollapsed)), ("problem", _ui.LoadProblem ?? "-"));
        if (_ui?.LoadProblem is string uip) AddLog("表示の設定（ui-settings.json）を読み込めなかったので既定の表示にしました（壊れたファイルは .broken-日時 として残しました）: " + uip, LogLevel.Warn);
        AddLog(_hotkey.Registered ? $"緊急停止ホットキー {_hotkey.Description} を登録しました（どのアプリが前面でも有効）" : "緊急停止ホットキーを登録できませんでした（他のアプリが使用中）。画面の「緊急停止」ボタンを使ってください。", _hotkey.Registered ? LogLevel.Ok : LogLevel.Warn);
        AddLog("ユーザー操作の検出: " + _activity.Status, LogLevel.Dim);
        RefreshOcrEngines();

        _liveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Render, (_, _) => UpdateLive(), Dispatcher);
        _panelTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdatePanels(), Dispatcher);
        _liveTimer.Start(); _panelTimer.Start();
        Loaded += (_, _) => { LoadLaunchUi(); UpdatePanels(); Connect(); var plan = _env.Components.Plan(); if (plan.Needed.Count > 0) AddLog($"未導入の部品があります（{string.Join("・", plan.Needed.Select(n => n.Spec.DisplayName))}、計 {Setup.ComponentManager.Mb(plan.DownloadBytes)}）。OCR を使うには「セットアップ」から導入してください（Windows 内蔵 OCR は導入なしで使えます）。", LogLevel.Info); };
        Closing += OnClosing;
    }

    // ======================== パネル ========================
    private void BuildPanels()
    {
        // v0.30.2: 1手サマリーと学習の成長も、ほかの欄と同じ畳める・並べ替えられる欄にする
        PanelHolder.Children.Remove(SummaryBlock); PanelHolder.Children.Remove(GrowthBlock);
        AddPanelCore("1手サマリー", true, null, SummaryBlock);
        AddPanelCore("学習の成長", true, "グラフ", GrowthBlock);
        _pBackend = AddPanel("バックエンド状態", true);
        _pState = AddPanel("現在状態", true);
        _pSup = AddPanel("Supervisor の記録", true, "システムの出来事（ゲームの経験・報酬とは別）");
        _pSup.Text = "（探索を開始すると表示）";
        _pGoal = AddPanel("目的", true, "自己生成（試験的）");
        _pCands = AddPanel("候補と評価", true, "点数の式の各項（構造化した記録）");
        _pChoice = AddPanel("選択行動と理由", true);
        _pResult = AddPanel("結果", true);
        _pHypo = AddPanel("仮説", true, "内部モデル・分割・並びの見込み（構造化した記録）");
        _pFind = AddPanel("新発見", true, "この回に見つけたもの");
        _pRevise = AddPanel("修正履歴", true, "予測の外れによる内部モデルの修正");
        _pRunStats = AddPanel("今回統計", true);
        _pGlobal = AddPanel("累積統計", true);
        _pPatterns = AddPanel("系列のパターン（連続履歴から発見）", true);
        _pTemporal = AddPanel("入力なしの変化", true, "画面ごとの時間的な変化（待つ手・手と手の間の観測）");
        _pTemporal.Text = "（探索を開始すると表示）";
        foreach (GrowthMetric m in Enum.GetValues<GrowthMetric>()) MetricBox.Items.Add(new ComboBoxItem { Content = GrowthTracker.MetricName(m), Tag = m });
        MetricBox.Items.Add(new ComboBoxItem { Content = "すべて重ねる（0〜100%）", Tag = "all" });
        MetricBox.SelectedIndex = 0;
        _pGoal.Text = "目的は画面から得た経験だけで立てます（開発者は与えません）。種類は「未試行の操作や揺らいでいる結果が残る知っている画面へ行く」「揺らいでいる関係を確かめ直す」の 2 つだけで、探索の手の 3 割までです。（探索を開始すると表示）";
        _pHypo.Text = "（探索を開始すると表示）";
        _pFind.Text = "（探索を開始すると表示）";
        BuildAvailabilityPanel();   // v0.32.5（t106u）: 右側の欄の最後
        BuildVNextDualAppendPanel(); // v0.33.4（t114u）: 診断・開発用 dual-append トグル（既定 OFF）
        _defaultOrder = _panelTitles.ToList();
        ApplyPanelOrder(GameKouryakuKun.Core.Ui.PanelOrder.Apply(_defaultOrder, _ui!.GetValue(GameKouryakuKun.Core.Ui.PanelOrder.Key)));

    }

    /// <summary>
    /// v0.32.5（t106u）: 自律探索で使ってよい入力を、入力ごとにキーの名前で切り替える欄。探索中も次の手の選択から効く。
    /// Human Control の入力と起動時の入力の診断には関係しない。ui-settings.json の explore_disabled_actions に保存し、次の起動で戻す
    /// </summary>
    private void BuildAvailabilityPanel()
    {
        var acts = GameKouryakuKun.Core.Exploration.Explorer.FromProfile(_env.Profile.KeyMap().All.Keys, _env.Profile.Input.Chords);
        var av = _env.Availability;
        if (_ui == null) _ui = GameKouryakuKun.Core.Ui.UiSettings.Load(_env.DataRoot);
        try { av.Load(_ui.GetValue(GameKouryakuKun.Core.Exploration.ActionAvailability.SettingsKey), acts.Select(a => a.Name)); }
        catch (Exception ex) { AddLog("入力の制限の設定を読めませんでした（すべて有効で始めます）: " + ex.Message, LogLevel.Warn); }
        var body = new StackPanel();
        var status = new TextBlock { Style = (Style)FindResource("PanelBody") };
        var wrap = new WrapPanel();
        var boxes = new List<CheckBox>();
        void Save()
        {
            try { _ui!.SetValue(GameKouryakuKun.Core.Exploration.ActionAvailability.SettingsKey, av.Serialize()); _env.Log.Write("explore_availability_saved", ("disabled", av.Disabled.OrderBy(x => x, StringComparer.Ordinal).ToList())); }
            catch (Exception ex) { AddLog("入力の制限を保存できませんでした: " + ex.Message, LogLevel.Warn); }
        }
        void Refresh()
        {
            foreach (var b in boxes) b.IsChecked = !av.Disabled.Contains((string)b.Tag);
            int off = acts.Count(a => av.Disabled.Contains(a.Name));
            status.Text = off == 0 ? $"すべての入力（{acts.Count}）を自律探索で使います。" :
                off == acts.Count ? "すべての入力が止められています。自律探索は一時停止します（Human Control は使えます）。" :
                $"{off}/{acts.Count} 個の入力を自律探索で使いません（人の側の制約。点数の内訳では「止められた」と記録します）。";
        }
        foreach (var a in acts)
        {
            var cb = new CheckBox { Content = a.Name, Tag = a.Name, Margin = new Thickness(0, 2, 12, 2), ToolTip = "チェックを外すと自律探索でこの入力を使いません（Human Control と入力の診断は別）" };
            cb.Click += (_, _) => { av.Set((string)cb.Tag, cb.IsChecked == true); Save(); Refresh(); };
            boxes.Add(cb); wrap.Children.Add(cb);
        }
        var reset = new Button { Content = "すべて有効に戻す", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 0) };
        reset.Click += (_, _) => { av.EnableAll(); Save(); Refresh(); };
        body.Children.Add(status); body.Children.Add(wrap); body.Children.Add(reset);
        Refresh();
        AddPanelCore("自律探索で使う入力", true, "人が決める（ゲームの知識ではない）", body);
    }


    /// <summary>
    /// v0.33.4（t114u）: Experience vNext dual-append の診断・開発用 ON/OFF。既定 OFF。
    /// ui-settings.json の experience_vnext_dual_append に保存。環境変数 GKK_USE_EXPERIENCE_VNEXT=1 は開発用で UI より強い。
    /// </summary>
    private TextBlock? _vNextStatus;
    private CheckBox? _vNextCheck;
    private void BuildVNextDualAppendPanel()
    {
        if (_ui == null) _ui = GameKouryakuKun.Core.Ui.UiSettings.Load(_env.DataRoot);
        try { RuntimeFlags.LoadUiPreference(_ui.GetValue(RuntimeFlags.SettingsKey)); }
        catch (Exception ex) { AddLog("Experience vNext 設定を読めませんでした（OFF で始めます）: " + ex.Message, LogLevel.Warn); RuntimeFlags.LoadUiPreference(null); }

        var body = new StackPanel();
        _vNextStatus = new TextBlock { Style = (Style)FindResource("PanelBody"), TextWrapping = TextWrapping.Wrap };
        _vNextCheck = new CheckBox
        {
            Content = "Experience vNext dual-append",
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = "ON にすると探索中に memory.sqlite 学習に加え experience-vnext.sqlite へ併記します（失敗しても探索は止めません）。既定 OFF。環境変数 GKK_USE_EXPERIENCE_VNEXT=1 があるときは常に ON（開発用）。",
        };
        _vNextCheck.IsChecked = RuntimeFlags.UiEnabled;
        _vNextCheck.Click += (_, _) =>
        {
            RuntimeFlags.UiEnabled = _vNextCheck.IsChecked == true;
            RuntimeFlags.ApplyEffective();
            try
            {
                _ui!.SetValue(RuntimeFlags.SettingsKey, RuntimeFlags.SerializeUiPreference());
                _env.Log.Write("vnext_dual_append_saved",
                    ("ui", RuntimeFlags.UiEnabled), ("effective", RuntimeFlags.UseExperienceVNextWriter), ("source", RuntimeFlags.EffectiveSource));
            }
            catch (Exception ex) { AddLog("Experience vNext 設定を保存できませんでした: " + ex.Message, LogLevel.Warn); }
            RefreshVNextStatus();
            UpdatePanels();
            AddLog(RuntimeFlags.StatusText(), RuntimeFlags.UseExperienceVNextWriter ? LogLevel.Ok : LogLevel.Info);
        };
        var note = new TextBlock
        {
            Style = (Style)FindResource("PanelBody"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 4, 0, 0),
            Text = "診断・開発用。学習は常に memory.sqlite。ON でも Sensor Stack / しきい値 / 学習ロジックは変えません。再起動後も保持されます。",
        };
        body.Children.Add(_vNextStatus);
        body.Children.Add(_vNextCheck);
        body.Children.Add(note);
        RefreshVNextStatus();
        AddPanelCore("診断・開発（Experience vNext）", true, "実機試験用。既定 OFF", body);
    }

    private void RefreshVNextStatus()
    {
        RuntimeFlags.ApplyEffective();
        if (_vNextStatus != null) _vNextStatus.Text = RuntimeFlags.StatusText()
            + (RuntimeFlags.EnvForcesOn && !RuntimeFlags.UiEnabled
                ? "\n（環境変数が優先されているため、画面のチェックを外してもこのプロセスでは ON のままです）"
                : "");
        if (_vNextCheck != null && _vNextCheck.IsChecked != RuntimeFlags.UiEnabled)
            _vNextCheck.IsChecked = RuntimeFlags.UiEnabled;
    }

    /// <summary>右側の欄を畳むかどうか（表示だけの設定。学習 DB ではなくデータフォルダの ui-settings.json）</summary>
    private GameKouryakuKun.Core.Ui.UiSettings? _ui;

    private readonly List<string> _panelTitles = new();

    /// <summary>欄を畳んだ・開いたときは、表示の設定ファイルに保存してログに残すだけ（記録・学習・行動の選択には触れない）</summary>
    private void OnPanelToggled(string title, bool collapsed)
    {
        try { _ui!.SetCollapsed(title, collapsed); _env.Log.Write("view_settings_saved", ("panel", title), ("collapsed", collapsed)); }
        catch (Exception ex) { _env.Log.Write("view_settings_error", ("panel", title), ("detail", ex.Message)); AddLog("表示の設定を保存できませんでした: " + ex.Message, LogLevel.Warn); }
    }

    private List<string> _defaultOrder = new();
    private List<string> _order = new();
    private readonly Dictionary<string, Border> _panelBoxes = new(StringComparer.Ordinal);

    private void ApplyPanelOrder(List<string> order)
    {
        _order = order;
        Panels.Children.Clear();
        foreach (var t in order) if (_panelBoxes.TryGetValue(t, out var b)) Panels.Children.Add(b);
    }

    /// <summary>v0.30.2: 欄を上下に動かして順を保存する（表示だけ。ui-settings.json の panel_order）</summary>
    private void MovePanel(string title, int delta)
    {
        ApplyPanelOrder(GameKouryakuKun.Core.Ui.PanelOrder.Move(_order, title, delta));
        try { _ui!.SetValue(GameKouryakuKun.Core.Ui.PanelOrder.Key, GameKouryakuKun.Core.Ui.PanelOrder.Serialize(_order)); _env.Log.Write("view_settings_saved", ("panel_order", title), ("delta", delta)); }
        catch (Exception ex) { AddLog("表示の設定を保存できませんでした: " + ex.Message, LogLevel.Warn); }
    }

    private void ResetPanelOrder_Click(object sender, RoutedEventArgs e)
    {
        ApplyPanelOrder(_defaultOrder.ToList());
        try { _ui!.SetValue(GameKouryakuKun.Core.Ui.PanelOrder.Key, ""); _env.Log.Write("view_settings_saved", ("panel_order", "reset")); }
        catch (Exception ex) { AddLog("表示の設定を保存できませんでした: " + ex.Message, LogLevel.Warn); }
    }

    private TextBlock AddPanel(string title, bool implemented, string? tag = null)
    {
        var body = new TextBlock { Style = (Style)FindResource("PanelBody"), Text = "—" };
        if (!implemented) body.Foreground = BDim;
        AddPanelCore(title, implemented, tag, body);
        return body;
    }

    private void AddPanelCore(string title, bool implemented, string? tag, FrameworkElement body)
    {
        if (_ui == null)
        {
            _ui = GameKouryakuKun.Core.Ui.UiSettings.Load(_env.DataRoot);
        }
        var headDock = new DockPanel { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "クリックで畳む／開く（▲▼で並べ替え）" };
        foreach (var (txt, d) in new[] { ("▼", 1), ("▲", -1) })
        {
            var bt = new Button { Content = txt, Width = 22, Height = 20, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), FontSize = 10, ToolTip = d < 0 ? "この欄を上へ" : "この欄を下へ", Cursor = System.Windows.Input.Cursors.Arrow };
            bt.Click += (_, e) => { MovePanel(title, d); e.Handled = true; };
            DockPanel.SetDock(bt, Dock.Right); headDock.Children.Add(bt);
        }
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        headDock.Children.Add(head);
        var chevron = new TextBlock { Width = 16, VerticalAlignment = VerticalAlignment.Top, Foreground = BDim, Style = (Style)FindResource("PanelTitle") };
        head.Children.Add(chevron);
        head.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("PanelTitle") });
        var badgeText = tag ?? (implemented ? null : "未実装");
        if (badgeText != null)
            head.Children.Add(new Border
            {
                Background = implemented ? new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5A)) : new SolidColorBrush(Color.FromRgb(0x7A, 0x52, 0x10)),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(8, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badgeText, FontSize = 11 },
            });
        void Show(bool collapsed) { body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible; chevron.Text = collapsed ? "▸" : "▾"; }
        _panelTitles.Add(title);
        Show(_ui.IsCollapsed(title));
        headDock.MouseLeftButtonUp += (_, e) =>
        {
            bool c = body.Visibility == Visibility.Visible;
            Show(c);
            OnPanelToggled(title, c);
            e.Handled = true;
        };
        var sp = new StackPanel(); sp.Children.Add(headDock); sp.Children.Add(body);
        var box = new Border { Style = (Style)FindResource("PanelBox"), Child = sp };
        _panelBoxes[title] = box;
        Panels.Children.Add(box);
    }

    private void SetBadges()
    {
        Badges.Children.Clear();
        var cap = _con?.Capture;
        var cs = _capStatus ?? cap?.Status;
        string capText = cap?.Current == null ? "取得: 未接続" : $"取得: {cap.Current.Id.ToUpperInvariant()} {HealthText(cs?.Health)}";
        Badge(capText, cap?.Current == null ? Kind.Off : cs?.Health == CaptureHealth.Running ? Kind.Good : cs?.RequiresPause == true ? Kind.Warn : Kind.Bad);
        var s = _session;
        if (s?.InputName != null) Badge($"入力: {s.InputBadge}", s.InputLevel == Core.Input.IsolationLevel.Isolated ? Kind.Good : Kind.Warn);
        else Badge("入力: 開始時に選択", Kind.Off);
        var ocr = (_paddle?.Availability().Ok == true) ? "OCR: PaddleOCR" : _winOcr.Availability().Ok ? "OCR: Windows内蔵" : "OCR: なし";
        Badge(ocr, ocr == "OCR: なし" ? Kind.Bad : _paddle?.Availability().Ok == true ? Kind.Good : Kind.Warn);
        Badge("DB: 記録中", Kind.Good);
        RuntimeFlags.ApplyEffective();
        Badge(RuntimeFlags.UseExperienceVNextWriter
                ? $"vNext併記: ON（{RuntimeFlags.EffectiveSource}）"
                : "vNext併記: OFF",
            RuntimeFlags.UseExperienceVNextWriter ? Kind.Warn : Kind.Off);
        Badge(_hotkey.Registered ? "緊急停止キー: 有効" : "緊急停止キー: 登録失敗", _hotkey.Registered ? Kind.Good : Kind.Bad);
        if (s != null)
        {
            var (t, k) = s.State switch
            {
                SessionState.Running => ("実行中", Kind.Good),
                SessionState.Preparing => ("準備中", Kind.Warn),
                SessionState.Paused => ("一時停止", Kind.Warn),
                SessionState.Stopping => ("停止中", Kind.Warn),
                SessionState.Stopped => (s.EmergencyStopped ? "緊急停止" : s.InputDeliveryFailed ? "中止（入力の送信の失敗）" : "停止", s.EmergencyStopped || s.InputDeliveryFailed ? Kind.Bad : Kind.Off),
                _ => ("待機", Kind.Off),
            };
            Badge("状態: " + t, k);
        }
    }

    private enum Kind { Good, Warn, Bad, Off }
    private void Badge(string text, Kind k)
    {
        var bg = k switch { Kind.Good => Color.FromRgb(0x1F, 0x6F, 0x3A), Kind.Warn => Color.FromRgb(0x8A, 0x60, 0x10), Kind.Bad => Color.FromRgb(0x9A, 0x22, 0x1C), _ => Color.FromRgb(0x44, 0x46, 0x50) };
        Badges.Children.Add(new Border { Background = new SolidColorBrush(bg), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 6, 0), Child = new TextBlock { Text = text, FontSize = 12 } });
    }

    private static string HealthText(CaptureHealth? h) => h switch
    {
        CaptureHealth.Running => "正常", CaptureHealth.Minimized => "最小化中", CaptureHealth.Cloaked => "非表示", CaptureHealth.WindowLost => "ウィンドウ消失",
        CaptureHealth.Stalled => "停滞", CaptureHealth.Error => "エラー", CaptureHealth.Stopped => "停止", null => "", _ => h.ToString()!,
    };

    private void UpdatePanels()
    {
        SetBadges();
        var s = _session; var cap = _con?.Capture;
        var sb = new StringBuilder();
        // バックエンド
        sb.AppendLine(_con == null ? "ゲームウィンドウ: 未接続" : $"ゲームウィンドウ: {_con.Window.ProcessName} [{_con.Window.ClassName}] \"{_con.Window.Title}\"");
        if (_con != null)
        {
            foreach (var p in _con.Probes) sb.AppendLine($"  取得 {p.BackendId}: {(p.Usable ? "使用可" : "使用不可")} {p.Width}x{p.Height} {p.Fps:0.#}fps");
            sb.AppendLine($"  使用中: {cap?.Current?.DisplayName ?? "なし"}（{_capStatus?.Detail ?? cap?.Status.Detail}）");
        }
        sb.AppendLine($"入力: {s?.InputName ?? "未選択（開始時に送信できる方式を選ぶ）"}{(s?.InputBadge != null ? $" [{s.InputBadge}]" : "")}");
        sb.AppendLine($"ユーザー操作の検出: {_activity.Status}");
        sb.AppendLine($"緊急停止ホットキー: {(_hotkey.Registered ? _hotkey.Description + "（登録済み）" : "登録失敗")}");
        sb.AppendLine($"OCR: {OcrStatusText()}");
        foreach (var st in _compStatus) sb.AppendLine($"部品 {st.Spec.DisplayName} {st.Spec.Version}: {st.Summary}");
        sb.AppendLine($"DB: {Path.Combine(_env.ProfileDataDir, "memory.sqlite")} ／ {Path.Combine(_env.DataRoot, "app.sqlite")}");
        RuntimeFlags.ApplyEffective();
        sb.Append(RuntimeFlags.StatusText());
        _pBackend.Text = sb.ToString();

        // 現在状態
        sb.Clear();
        if (s == null) sb.Append(_con == null ? "未接続" : "待機中（「開始」で手探り探索を始めます）");
        else
        {
            sb.AppendLine($"状態: {s.Phase}");
            sb.AppendLine(s.Supervisor.StatusText + "／起動の方法: " + s.LaunchSummary);
            var rs = s.PauseReasons;
            if (rs.Count > 0) sb.AppendLine("一時停止の理由: " + string.Join("・", rs.Select(PlaySession.ReasonText)));
            sb.AppendLine($"手数: {s.Step}{(s.RunId is long id ? $"（探索 #{id}）" : "")}・攻略くん {s.AgentSteps}／人間 {s.HumanSteps}{(s.Human?.Enabled == true ? "・Human Control 中" : "")}");
            sb.AppendLine($"入力の送信（A）: 成功 {s.Delivery.Delivered}／失敗 {s.Delivery.Failed}（{s.Delivery.MaxConsecutiveFailures} 回続けて失敗したときだけ止まります）");
            sb.AppendLine($"画面の反応（B）: {s.Delivery.Reacted}/{s.Delivery.ReactionObserved}・無反応の連続 {s.Delivery.NoReactionStreak}（未知の画面の性質として記録するだけで、止まりません）");
            sb.AppendLine($"ユーザー操作: 停止に使用 {s.UserEventsPausing} ／ 分離中のため無視 {s.UserEventsIgnored}");
            if (s.Calibration is { } c) sb.AppendLine($"較正: 変化しきい値 率{c.Criteria.ChangeRatio:0.000}／連結{c.Criteria.ChangeComponent:0.000}{(c.IdleMask != null ? "（常時動く領域を除外）" : "")}");
            if (s.StopReason != null) sb.Append("停止理由: " + s.StopReason);
        }
        _pState.Text = sb.ToString().TrimEnd();
        if (s != null)
        {
            var evs = s.SystemEvents;
            _pSup.Text = evs.Count == 0 ? s.Supervisor.StatusText + "／起動の方法: " + s.LaunchSummary + "\n（システムの出来事はまだありません）"
                : s.Supervisor.StatusText + "／起動の方法: " + s.LaunchSummary + "\n" + string.Join("\n", evs.Skip(Math.Max(0, evs.Count - 30)).Select(e => $"{TimeSpan.FromMilliseconds(e.AtMs):hh\\:mm\\:ss} {e.Name}{(e.RecoveryId is long rid ? $" [復旧 #{rid}]" : "")} {e.Detail}"));
        }

        // 候補・選択・結果
        var dec = s?.LastDecision;
        _pCands.Text = dec != null ? DecisionText.Candidates(dec)
            : s == null || s.Candidates.Count == 0 ? "（開始すると、使える操作の候補を表示します）"
            : "候補: " + string.Join("、", s.Candidates.Select(c => c.Name)) + "\n評価: まだ試していない・新しい画面に行けた・戻れたことのある操作・関係がまだ分かっていない操作を優先（キーの意味は使わない）";
        var last = s?.Last;
        _pChoice.Text = last == null ? "—" : $"[{last.Step}] {last.Op} → {last.Action}\n理由: {last.Reason}" + (dec != null ? "\n" + DecisionText.Choice(dec) : "");
        _pResult.Text = last == null ? "—" : $"{PlaySession.KindText(last.Kind)}・境界 {last.BoundaryMs:0}ms・変化開始 {(last.FirstChangeMs is double f ? $"{f:0}ms" : "-")}・{(last.Changed ? "画面変化あり" : "画面変化なし")}{(last.Note != null ? $"\n備考: {last.Note}" : "")}"
            + (dec != null ? "\n" + DecisionText.Result(dec) : "");
        if (s != null) { _pHypo.Text = HypothesisBoard.Text(s.Hypotheses); _pFind.Text = s.Discoveries.Text(); }
        _pRevise.Text = s == null ? "（探索を開始すると表示）" : $"この回の予測 {s.PredictionChecks} 回・修正 {s.ModelRevisions} 回\n" + DecisionText.Revisions(s.RecentRevisions);
        if (s != null) _pTemporal.Text = TemporalText(s);
        if (s != null && s.GoalText is { } gt) _pGoal.Text = gt;
        UpdateSummary(s);
        UpdateHumanControls(s);

        // 統計
        try
        {
            if (s?.RunId is long rid)
            {
                var r = _env.Memory.RunStats(rid);
                _pRunStats.Text = StatsText(r);
            }
            else _pRunStats.Text = "（探索を開始すると表示）";
            var g = _env.Memory.GlobalStats();
            _pGlobal.Text = StatsText(g) + $"\n記録: {Path.Combine(_env.ProfileDataDir, "memory.sqlite")}";
            var ps = _env.Memory.PatternSummaryForDisplay();
            _pPatterns.Text = ps.Count == 0 ? "まだありません（連続した経験の履歴から、くり返し現れる系列・循環・区間を統計で見つけます。区切りも経験から見つけ、名前は付けません）"
                : string.Join("\n", ps.Take(12).Select(p => $"P{p.Id}: {KindName(p.Kind)}・長さ {p.Length}・{p.Count} 回{(p.Label != null ? $"（人が付けた名前: {p.Label}）" : "")}")) + "\n※ 名前は人向けの説明だけで、AI の判断には使いません";
        }
        catch (Exception e) { _pGlobal.Text = "統計を読めません: " + e.Message; }

        // ボタン
        bool running = s != null && s.State is SessionState.Preparing or SessionState.Running or SessionState.Paused or SessionState.Stopping;
        StartButton.IsEnabled = _con != null && !running && !_connecting;
        ConnectButton.IsEnabled = !running && !_connecting;
        PauseButton.IsEnabled = running && s!.State != SessionState.Stopping;
        PauseButton.Content = s?.PauseReasons.Contains(PauseReason.Manual) == true ? "▶ 再開" : "⏸ 一時停止";
        StopButton.IsEnabled = running;
        UpdateBanner();
    }

    private static string KindName(string k) => k switch { "motif" => "反復系列", "cycle" => "循環", "segment" => "区間", _ => k };

    private static string StatsText(IReadOnlyDictionary<string, double> d)
    {
        double V(string k) => d.TryGetValue(k, out var v) ? v : 0;
        var parts = new List<string>();
        if (d.ContainsKey("runs_total")) parts.Add($"探索 {V("runs_total"):0} 回");
        parts.Add($"記録ターン {V("turns_total"):0}（キー1回＝1ターン）");
        parts.Add($"完了 {V("boundary_normal"):0}／変化なし {V("boundary_no_change"):0}／時間切れ {V("boundary_timeout"):0}／中断 {V("boundary_aborted"):0}");
        return string.Join("\n", parts);
    }

    private void UpdateBanner()
    {
        var s = _session;
        string? text = null; Color bg = Color.FromRgb(0xB3, 0x26, 0x1E);
        if (s?.EmergencyStopped == true) text = $"■ 緊急停止しました（{s.StopReason}）。入力は送っていません。再開するには「開始」を押し直してください。";
        else if (s?.InputDeliveryFailed == true) text = $"■ 中止（入力の送信の失敗）: {s.StopReason}";
        else if (s != null && s.State == SessionState.Paused)
        {
            var rs = s.PauseReasons;
            text = "⏸ 一時停止中: " + string.Join("・", rs.Select(PlaySession.ReasonText)) +
                   (rs.Contains(PauseReason.Minimized) ? "（ゲームを元に戻すと自動で再開）" : rs.Contains(PauseReason.UserActivity) ? "（操作をやめて5秒で自動で再開）" : rs.Contains(PauseReason.Manual) ? "（「再開」で続行）" : "");
            bg = Color.FromRgb(0x8A, 0x60, 0x10);
        }
        else if (_capStatus?.Health == CaptureHealth.Minimized) { text = "ゲームが最小化されています（画面を取得できません。元に戻すと再開します）"; bg = Color.FromRgb(0x8A, 0x60, 0x10); }
        else if (_capStatus?.Health == CaptureHealth.WindowLost) text = "ゲームウィンドウが見つかりません（ゲームを起動して「ゲームに接続」）";
        Banner.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text != null) { BannerText.Text = text; Banner.Background = new SolidColorBrush(bg); }
    }

    // ======================== ライブ画面 ========================
    private void UpdateLive()
    {
        var f = _con?.Capture.LatestFrame();
        // v0.30.3: 重ね表示を入にして「観測した画面」なら、観測を作った画面を止めて表示する（いまの画面と観測がずれない）
        var view = _session?.LatestView;
        bool frozen = ObsToggle.IsChecked == true && ObsFrozen.IsChecked == true && view != null;
        if (frozen) f = view!.Value.Frame;
        _drawObs = frozen ? view!.Value.Obs : _session?.LatestObservation;
        if (f == null) { LiveOverlay.Visibility = Visibility.Visible; LiveOverlay.Text = _con == null ? (_connecting ? "接続中…" : "ゲームに接続していません") : "フレーム待ち…"; return; }
        LiveOverlay.Visibility = Visibility.Collapsed;
        if (f.Id == _shownFrameId) return;
        _shownFrameId = f.Id;
        if (_bmp == null || _bmp.PixelWidth != f.Width || _bmp.PixelHeight != f.Height)
        {
            _bmp = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
            LiveImage.Source = _bmp;
        }
        _bmp.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
        LiveImage.Width = f.Width; LiveImage.Height = f.Height; ObsCanvas.Width = f.Width; ObsCanvas.Height = f.Height;
        UpdateObsOverlay(f.Width, f.Height);
        LiveInfo.Text = frozen ? $"観測した画面（手 {_drawObs?.Step} の結果・フレーム #{f.Id}）を表示中。いまの画面は「観測した画面」を切に" : $"{f.Width}x{f.Height} ／ {_con!.Capture.Current?.Id} {_con.Capture.Current?.FpsOver(1000):0.#}fps ／ フレーム #{f.Id}";
    }

    // ======================== 見ているもの（v0.30.0 P-1。監査用の重ね表示。選択・学習には使わない） ========================
    private long _obsShownStep = -1;
    private GameKouryakuKun.Core.Observation.StructuredObservation? _drawObs;
    private void ObsToggle_Click(object sender, RoutedEventArgs e) { _obsShownStep = -1; _shownFrameId = -1; ObsCanvas.Children.Clear(); if (ObsToggle.IsChecked != true) { ObsCanvas.Children.Clear(); ObsPanel.Visibility = Visibility.Collapsed; } }

    private static readonly Dictionary<string, Color> ObsColors = new()
    {
        ["region_action"] = Color.FromRgb(0xFF, 0x8C, 0x00), ["region_independent"] = Color.FromRgb(0x4F, 0xA3, 0xFF), ["region_unknown"] = Color.FromRgb(0xA0, 0xA0, 0xA0),
        ["entity"] = Color.FromRgb(0x50, 0xE0, 0x50), ["entity_static"] = Color.FromRgb(0x30, 0x90, 0x30), ["entity_periodic"] = Color.FromRgb(0x30, 0x90, 0x30), ["trail"] = Color.FromRgb(0xFF, 0xFF, 0xFF), ["entity_new"] = Color.FromRgb(0xFF, 0xFF, 0x40), ["entity_moved"] = Color.FromRgb(0x40, 0xFF, 0xC0), ["entity_appearance"] = Color.FromRgb(0xFF, 0x60, 0xFF),
        ["move"] = Color.FromRgb(0x40, 0xFF, 0xC0), ["group"] = Color.FromRgb(0xFF, 0xFF, 0xFF), ["text_observed"] = Color.FromRgb(0xFF, 0xFF, 0xFF), ["text_inherited"] = Color.FromRgb(0x90, 0x90, 0x90),
    };

    private void UpdateObsOverlay(int w, int h)
    {
        if (ObsToggle.IsChecked != true) return;
        var o = _drawObs;
        if (o == null) { ObsPanel.Visibility = Visibility.Visible; ObsText.Text = "まだ構造化した観測がありません（探索を始めると 1 手ごとに出ます）"; return; }
        if (o.Step == _obsShownStep) return;
        _obsShownStep = o.Step;
        IReadOnlyList<string> extra; try { extra = _session!.OverlayExtras(); } catch { extra = Array.Empty<string>(); }
        var m = GameKouryakuKun.Core.Observation.ObservationOverlay.Build(o, w, h, extra);
        ObsCanvas.Children.Clear();
        double th = Math.Max(1.5, w / 600.0), fs = Math.Max(10, w / 110.0);
        bool Show(string k) => k switch { "region" => ObsRegions.IsChecked == true, "entity" or "move" or "group" or "trail" => ObsEntities.IsChecked == true, "relation" => ObsRelations.IsChecked == true, "text" => ObsTextBoxes.IsChecked == true, _ => true };
        foreach (var it in m.Items.Where(i => Show(i.Kind)))
        {
            var col = ObsColors.TryGetValue(it.Style, out var c) ? c : it.Style.StartsWith("relation", StringComparison.Ordinal) ? Color.FromRgb(0xFF, 0xD0, 0x80) : Colors.White;
            var br = new SolidColorBrush(col);
            if (it.Kind == "trail")   // v0.31.6: 追跡の軌跡（細い実線。最後の区間に番号）
            {
                ObsCanvas.Children.Add(new System.Windows.Shapes.Line { X1 = it.X, Y1 = it.Y, X2 = it.X2, Y2 = it.Y2, Stroke = br, StrokeThickness = th, Opacity = 0.7 });
                if (it.Label.Length > 0) { var tl = new TextBlock { Text = it.Label, Foreground = br, FontSize = fs * 0.8 }; Canvas.SetLeft(tl, it.X2 + th * 2); Canvas.SetTop(tl, it.Y2); ObsCanvas.Children.Add(tl); }
                continue;
            }
            if (it.Kind is "move" or "relation")
            {
                var ln = new System.Windows.Shapes.Line { X1 = it.X, Y1 = it.Y, X2 = it.X2, Y2 = it.Y2, Stroke = br, StrokeThickness = th * (it.Kind == "move" ? 2 : 1) };
                if (it.Kind == "relation") ln.StrokeDashArray = new DoubleCollection { 4, 3 };
                ObsCanvas.Children.Add(ln);
                if (it.Kind == "move") { var dot = new System.Windows.Shapes.Ellipse { Width = th * 5, Height = th * 5, Fill = br }; Canvas.SetLeft(dot, it.X2 - th * 2.5); Canvas.SetTop(dot, it.Y2 - th * 2.5); ObsCanvas.Children.Add(dot); }
                var lt = new TextBlock { Text = it.Label, Foreground = br, FontSize = fs * 0.8, Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)) };
                Canvas.SetLeft(lt, (it.X + it.X2) / 2); Canvas.SetTop(lt, (it.Y + it.Y2) / 2); ObsCanvas.Children.Add(lt);
                continue;
            }
            var rc = new System.Windows.Shapes.Rectangle { Width = Math.Max(1, it.W), Height = Math.Max(1, it.H), Stroke = br, StrokeThickness = th };
            if (it.Kind == "region") { rc.Fill = new SolidColorBrush(Color.FromArgb(0x28, col.R, col.G, col.B)); rc.StrokeDashArray = new DoubleCollection { 2, 2 }; }
            if (it.Kind == "group") rc.StrokeDashArray = new DoubleCollection { 6, 2 };
            if (it.Style == "text_inherited") rc.StrokeDashArray = new DoubleCollection { 3, 3 };
            Canvas.SetLeft(rc, it.X); Canvas.SetTop(rc, it.Y); ObsCanvas.Children.Add(rc);
            var tb = new TextBlock { Text = it.Label, Foreground = br, FontSize = fs * (it.Kind == "text" ? 0.75 : 0.9), Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)) };
            Canvas.SetLeft(tb, it.X); Canvas.SetTop(tb, it.Kind == "text" ? it.Y + it.H : Math.Max(0, it.Y - fs * 1.3)); ObsCanvas.Children.Add(tb);
        }
        ObsPanel.Visibility = ObsTable.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ObsText.Text = string.Join("\n", m.Lines) + "\n凡例: 区画 橙=手で変わる 青=手によらない 灰=未判定／候補 緑・黄=新しい・水=動いた・桃=見た目だけ／文字 白実線=観測 灰点線=継承／点線=関係（意味は付けない）";
    }

    // ======================== ログ ========================
    private void AddLog(string text, LogLevel lv)
    {
        var brush = lv switch { LogLevel.Ok => BOk, LogLevel.Warn => BWarn, LogLevel.Error => BErr, LogLevel.Dim => BDim, _ => BInfo };
        _log.Add(new LogRow($"{DateTime.Now:HH:mm:ss} {text}", brush));
        while (_log.Count > 800) _log.RemoveAt(0);
        LogList.ScrollIntoView(_log[^1]);
    }
    private void AddLogAsync(string text, LogLevel lv) => Dispatcher.BeginInvoke(() => AddLog(text, lv));

    private void OnTurn(TurnLogItem t)
    {
        var lv = t.Kind == TurnKind.Normal ? LogLevel.Info : t.Kind == TurnKind.Aborted ? LogLevel.Dim : LogLevel.Warn;
        string text = $"[{t.Step,3}] 観測: フレーム#{t.BaselineFrameId} {t.Width}x{t.Height} → 行動: {t.Action} → 結果: {PlaySession.KindText(t.Kind)} {t.BoundaryMs:0}ms {(t.Changed ? "画面変化あり" : "画面変化なし")}{(t.Node != null ? $" 画面 {t.Node}" : "")}{(t.Note != null ? $"（{t.Note}）" : "")}{(t.TurnId is long id ? $" #turn{id}" : " ※記録失敗")}";
        Dispatcher.BeginInvoke(() => AddLog(text, lv));
    }

    // ======================== ボタン ========================
    private void Connect_Click(object sender, RoutedEventArgs e) => Connect();

    private async void Connect()
    {
        if (_connecting) return;
        _connecting = true; UpdatePanels();
        var old = _con; _con = null; _bmp = null; _capStatus = null;
        old?.Dispose();
        AddLog("ゲームウィンドウを探しています…", LogLevel.Info);
        var con = await Task.Run(() => GameConnection.Connect(_env, AddLogAsync));
        _connecting = false;
        if (con != null)
        {
            _con = con;
            // v0.23.1: 接続したゲームの実行ファイルを記録して、自動で起動し直す設定に出す
            PlaySession.RecordDetectedExecutable(_env.DataRoot, GameKouryakuKun.Windows.WindowHealth.ProcessIdOf(con.Hwnd), m => AddLogAsync("[Supervisor] " + m, LogLevel.Dim));
            Dispatcher.BeginInvoke(new Action(LoadLaunchUi));
            // v0.23.1 実機: アプリを閉じている間に見張るファイルが変わっていたら、接続したときにすぐ知らせる（戻さない）
            try
            {
                var wcfg = GameKouryakuKun.Core.Supervisor.LaunchSettings.Load(_env.DataRoot).Apply(GameKouryakuKun.Core.Supervisor.SupervisorConfig.Load(_env.DataRoot));
                var pf = new GameKouryakuKun.Core.Supervisor.ProtectedFiles();
                pf.LoadOrCreateBaseline(wcfg.WatchFiles, Path.Combine(_env.DataRoot, GameKouryakuKun.Core.Supervisor.ProtectedFiles.BaselineFileName));
                foreach (var (path, before, after) in pf.Changes())
                {
                    AddLogAsync($"[Supervisor] FILE_CHANGED: {Path.GetFileName(path)} が基準から変わっています（大きさ {before.Size} → {after.Size}）。戻さずに知らせるだけ", LogLevel.Warn);
                    _env.Log.Write("file_changed_at_connect", ("path", path), ("size_before", before.Size), ("size_after", after.Size), ("hash_before", before.Hash), ("hash_after", after.Hash), ("baseline", pf.BaselineOrigin));
                }
            }
            catch (Exception ex) { AddLogAsync("[Supervisor] 見張るファイルの確認に失敗: " + ex.Message, LogLevel.Warn); }
            con.Capture.StatusChanged += st =>
            {
                _capStatus = st;
                _session?.OnCaptureStatus(st);
                _env.Log.Write("capture_status", ("health", st.Health.ToString()), ("detail", st.Detail));
                AddLogAsync($"[取得状態] {HealthText(st.Health)} {st.Detail}", st.RequiresPause ? LogLevel.Warn : LogLevel.Dim);
            };
            _capStatus = con.Capture.Status;
        }
        UpdatePanels();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartSession(humanOnly: false);

    private bool SessionActive => _session != null && _session.State is not (SessionState.Stopped or SessionState.Idle);

    /// <summary>探索を始める。humanOnly のとき（v0.19.5）は Human Control で始め、自動の手の選択は動かさない（時間・手数の上限は使わず、停止ボタンで止める）</summary>
    private void StartSession(bool humanOnly)
    {
        SaveMaxSteps();
        if (_con == null || SessionActive) return;
        if (humanOnly)
        {
            // v0.19.7: 確認の画面は出さずにすぐ始める（利用者の依頼 t41u）
            BeginSession(new PlaySessionOptions { AllowSendInput = SendInputCheck.IsChecked == true, StartWithHumanControl = true, GrowthTest = GrowthTestCheck.IsChecked == true }, "開始（Human Control で開始・自動の探索は止めたまま・上限なし）");
            return;
        }
        int runSec = RunSeconds();
        if (!int.TryParse(MaxStepsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) || max < 0) { MessageBox.Show(this, "手数上限は 0 以上の整数で入力してください（0 = 上限なし）", "ゲーム攻略くん"); return; }
        bool si = SendInputCheck.IsChecked == true;
        // v0.19.7: 確認の画面は出さずにすぐ始める（利用者の依頼 t41u）。注意書きはログに残す
        string limits = runSec > 0 && max > 0 ? $"{runSec / 60}分または {max} 手の先に来たほうで終わります" : runSec > 0 ? $"{runSec / 60}分で終わります" : max > 0 ? $"{max} 手で終わります" : "上限なし（停止ボタンで止めます）";
        AddLog($"手探り探索を始めます（{limits}。止めるときは「停止」／「緊急停止」、または Ctrl+Shift+F9）{(si ? "・占有モード（SendInput）を許可" : "")}", LogLevel.Info);
        BeginSession(new PlaySessionOptions { AllowSendInput = si, MaxSteps = max, TimeLimitSec = runSec, GrowthTest = GrowthTestCheck.IsChecked == true }, $"開始（手探り探索・実行時間={(runSec > 0 ? $"{runSec / 60}分" : "なし")}・手数上限={(max == 0 ? "なし" : max)}）");
    }

    private void BeginSession(PlaySessionOptions o, string startLog)
    {
        // v0.25.2: 画面の文字の観測（導入済みのエンジンがあるときだけ。意味は解釈しない）
        try { RefreshOcrEngines(); var es = new IOcrEngine?[] { _paddle, _paddleV4Compare, _winOcr }.Where(x => x != null && x.Availability().Ok).Cast<IOcrEngine>().ToList(); if (es.Count > 0) o = new PlaySessionOptions { AllowSendInput = o.AllowSendInput, MaxSteps = o.MaxSteps, TimeLimitSec = o.TimeLimitSec, GrowthTest = o.GrowthTest, StartWithHumanControl = o.StartWithHumanControl, Seed = o.Seed, QuietMs = o.QuietMs, SelfGoals = o.SelfGoals, Ocr = new OcrEngineChain(es) }; }
        catch (Exception ex) { AddLogAsync("OCR の準備に失敗（文字は読まずに続けます）: " + ex.Message, LogLevel.Warn); }
        var s = new PlaySession(_env, _con!, _standIn, o);
        s.Log += AddLogAsync;
        s.Turn += OnTurn;
        s.Changed += () => Dispatcher.BeginInvoke(UpdatePanels);
        // v0.22.0: Supervisor が止まって診断 zip を書き出したら、手動の書き出しと同じくクリップボードへ置いて短く知らせる
        s.DiagnosticsExported += zip => Dispatcher.BeginInvoke(async () =>
        {
            var (copied, err) = await CopyZipToClipboardAsync(zip);
            try { _env.Log.Write("diag_clipboard", ("ok", copied), ("detail", err), ("auto", true)); } catch { }
            string clip = copied ? "zip ファイルをクリップボードにコピーしました。送り先に Ctrl+V で貼り付けられます。" : $"クリップボードへのコピーはできませんでした（{err}）。";
            AddLog("[Supervisor] 復旧を止めました。診断データ: " + clip, copied ? LogLevel.Ok : LogLevel.Warn);
            MessageBox.Show(this, $"復旧を止めました（人の確認が必要です）。\n{clip}\n\n{zip}", "診断データ", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
        // v0.21.0 Supervisor が接続を作り直したら、ライブ画面も新しい接続に切り替える（古い接続は閉じる）
        s.ConnectionReplaced += c => Dispatcher.BeginInvoke(() =>
        {
            var old = _con; _con = c; _bmp = null; _capStatus = c.Capture.Status;
            c.Capture.StatusChanged += st =>
            {
                _capStatus = st;
                _env.Log.Write("capture_status", ("health", st.Health.ToString()), ("detail", st.Detail));
                AddLogAsync($"[取得状態] {HealthText(st.Health)} {st.Detail}", st.RequiresPause ? LogLevel.Warn : LogLevel.Dim);
            };
            if (old != null && !ReferenceEquals(old, c)) try { old.Dispose(); } catch { }
            UpdatePanels();
        });
        if (_capStatus is { } cs) s.OnCaptureStatus(cs);
        _session = s;
        HumanToggle.IsChecked = o.StartWithHumanControl; _humanKeysFor = null;
        ViewerPauseButton.IsChecked = false; ViewerPauseButton.Content = "⏸ 表示を止める";
        AddLog(startLog, LogLevel.Ok);
        s.Start();
        UpdatePanels();
    }

    // ======================== 1手サマリー・成長（表示だけ。記録・学習・手の選択には触れない） ========================
    private long _summaryShownStep = -1;
    private void UpdateSummary(PlaySession? s)
    {
        if (s == null) return;
        var (items, behind) = s.StepHistory.View(8);
        SummaryNote.Text = s.StepHistory.Paused ? $"表示を止めています（その後 {behind} 手進みました）" : "○一致 ×外れ ★新発見 ↻修正 ?探索 ◎目的達成";
        long lastStep = items.Count > 0 ? items[^1].Step : -1;
        if (lastStep != _summaryShownStep)
        {
            _summaryShownStep = lastStep;
            SummaryList.ItemsSource = items;
        }
        MetersText.Text = s.Meters?.Text() ?? "（10 手ごとに更新）";
        DrawGraph(s);
    }

    private void ViewerPause_Click(object sender, RoutedEventArgs e)
    {
        bool on = ViewerPauseButton.IsChecked == true;
        _session?.StepHistory.SetPaused(on);
        ViewerPauseButton.Content = on ? "■ 表示を停止中（押すと最新へ戻る）" : "⏸ 表示を止める（いまは最新を表示中）";
        if (!on) { SummaryList.SelectedItem = null; SummaryDetail.Visibility = Visibility.Collapsed; }
        _summaryShownStep = -2; UpdatePanels();
    }

    private void Summary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SummaryList.SelectedItem is StepSummary ss && ss.Detail is { } d)
        {
            SummaryDetail.Text = $"#{ss.Step} の詳細\n" + DecisionText.Choice(d) + "\n" + DecisionText.Result(d) + "\n" + DecisionText.Candidates(d);
            SummaryDetail.Visibility = Visibility.Visible;
        }
        else SummaryDetail.Visibility = Visibility.Collapsed;
    }

    private void Metric_Changed(object sender, SelectionChangedEventArgs e) { _graphDrawnCount = -1; if (_session != null) DrawGraph(_session); }

    private int _graphDrawnCount = -1; private object? _graphDrawnMetric;
    // 指標ごとの線の色・線の形・点の形（色だけで見分けなくてよいように、線の形と点の形も変える）。表示だけ
    private static readonly (GrowthMetric M, Brush B, double[] Dash, string Mark)[] GraphStyles =
    {
        (GrowthMetric.PredictionAccuracy, Frozen(0x8B, 0xE0, 0x8B), Array.Empty<double>(), "●"),
        (GrowthMetric.CalibrationGap, Frozen(0xFF, 0xC8, 0x57), new double[] { 6, 3 }, "▲"),
        (GrowthMetric.UnknownRate, Frozen(0x8F, 0xC1, 0xFF), new double[] { 2, 2 }, "■"),
        (GrowthMetric.RevisionRate, Frozen(0xFF, 0x9E, 0xD8), new double[] { 8, 3, 2, 3 }, "◆"),
        (GrowthMetric.NaturalShare, Frozen(0xE8, 0xE8, 0xEC), new double[] { 1, 4 }, "✚"),
    };

    private void DrawGraph(PlaySession s)
    {
        object sel = MetricBox.SelectedItem is ComboBoxItem { Tag: { } tg } ? tg : GrowthMetric.PredictionAccuracy;
        var metrics = sel is GrowthMetric one ? new[] { one } : GraphStyles.Select(x => x.M).ToArray();
        var series = metrics.Select(m => (M: m, P: s.Growth.Series(m))).ToList();
        int count = series.Sum(x => x.P.Count);
        if (count == _graphDrawnCount && Equals(sel, _graphDrawnMetric)) return;
        _graphDrawnCount = count; _graphDrawnMetric = sel;
        GraphCanvas.Children.Clear(); GraphLegend.Children.Clear();
        double w = Math.Max(10, GraphCanvas.ActualWidth), h = Math.Max(10, GraphCanvas.ActualHeight);
        const double left = 34;   // 縦軸の目盛りの幅
        double Y(double v) => h - Math.Clamp(v, 0, 1) * (h - 22) - 16;
        foreach (var y in new[] { 0.0, 0.5, 1.0 })
        {
            GraphCanvas.Children.Add(new Line { X1 = left, X2 = w, Y1 = Y(y), Y2 = Y(y), Stroke = BDim, StrokeThickness = 0.5, StrokeDashArray = new DoubleCollection { 2, 4 } });
            var lab = new TextBlock { Text = $"{y:P0}", FontSize = 10, Foreground = BDim }; Canvas.SetLeft(lab, 2); Canvas.SetTop(lab, Y(y) - 7); GraphCanvas.Children.Add(lab);
        }
        var all = series.SelectMany(x => x.P).ToList();
        if (all.Count < 2) { GraphCanvas.Children.Add(new TextBlock { Text = "（直近の手がたまると表示）", Foreground = BDim, Margin = new Thickness(left + 6, 6, 0, 0) }); return; }
        long x0 = all.Min(p => p.Step), x1 = Math.Max(all.Max(p => p.Step), x0 + 1);
        double X(long st) => (st - x0) / (double)(x1 - x0) * (w - left - 8) + left + 4;
        foreach (var (m, pts) in series)
        {
            var st = GraphStyles.First(x => x.M == m);
            var brush = metrics.Length == 1 ? BOk : st.B;
            if (pts.Count >= 2)
            {
                var pl = new Polyline { Stroke = brush, StrokeThickness = 1.6 };
                if (metrics.Length > 1 && st.Dash.Length > 0) pl.StrokeDashArray = new DoubleCollection(st.Dash);
                foreach (var p in pts) pl.Points.Add(new Point(X(p.Step), Y(p.Value)));
                GraphCanvas.Children.Add(pl);
                if (metrics.Length > 1)
                    for (int k = 0; k < pts.Count; k += Math.Max(1, pts.Count / 6))   // 点の形の印（数か所）
                    {
                        var mk = new TextBlock { Text = st.Mark, FontSize = 9, Foreground = brush, ToolTip = $"{GrowthTracker.MetricName(m)} #{pts[k].Step}: {pts[k].Value:P0}" };
                        Canvas.SetLeft(mk, X(pts[k].Step) - 4); Canvas.SetTop(mk, Y(pts[k].Value) - 7); GraphCanvas.Children.Add(mk);
                    }
            }
            string last = pts.Count > 0 ? $"{pts[^1].Value:P0}" : "—";
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            var sample = new Line { X1 = 0, X2 = 22, Y1 = 8, Y2 = 8, Stroke = brush, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center };
            if (metrics.Length > 1 && st.Dash.Length > 0) sample.StrokeDashArray = new DoubleCollection(st.Dash);
            item.Children.Add(new Canvas { Width = 24, Height = 16, Children = { sample } });
            item.Children.Add(new TextBlock { Text = (metrics.Length > 1 ? st.Mark + " " : "") + $"{GrowthTracker.MetricName(m)} {last}", FontSize = 11, Foreground = brush, VerticalAlignment = VerticalAlignment.Center });
            GraphLegend.Children.Add(item);
        }
        foreach (var ev in s.Growth.Events().Where(e => e.Step >= x0 && e.Step <= x1).TakeLast(200))
        {
            var t = new TextBlock { Text = ev.Symbol, FontSize = 10, Foreground = BWarn, ToolTip = ev.Text, Background = Brushes.Transparent };
            Canvas.SetLeft(t, X(ev.Step) - 4); Canvas.SetTop(t, 0);
            GraphCanvas.Children.Add(t);
        }
        GraphCanvas.Children.Add(new TextBlock { Text = $"#{x0}〜#{x1}・縦軸はどの指標も 0〜100%・表示用で報酬には使いません", FontSize = 10, Foreground = BDim, Margin = new Thickness(left + 4, h - 15, 0, 0) });
    }

    private static string TemporalText(PlaySession s)
    {
        var ts = s.Temporal;
        var sb = new StringBuilder($"待つ手 {s.WaitTurns} 回・手と手の間の変化 {s.NoInputChanges} 回（別の画面へ {s.NoInputTransitions}）・時間切れを操作の効果としなかった手 {s.TimeoutWeak}");
        if (ts.Count == 0) return sb.Append("\nまだ画面ごとの観測がありません（「待つ」手と、手と手の間の変化から数えます）").ToString();
        foreach (var t in ts.Where(t => !t.Traits.Contains("unobserved")).OrderByDescending(t => t.WaitTries + t.NoInputEvents).Take(8))
            sb.Append($"\n{t.Node}: {string.Join("・", t.Traits.Select(TemporalProfile.TraitText))}（待つ手 {t.WaitTries} 回で変化 {t.WaitChangeRate:P0}・移動 {t.WaitLeaveRate:P0}）");
        return sb.ToString();
    }

    // ======================== Human Control ========================
    private PlaySession? _humanKeysFor;
    private HumanControlWindow? _humanWin;
    private void SaveHumanWindow()
    {
        if (_humanWin == null || !_humanWin.IsVisible || double.IsNaN(_humanWin.Left)) return;
        SaveUiValue("human_window_left", _humanWin.Left.ToString("0", CultureInfo.InvariantCulture));
        SaveUiValue("human_window_top", _humanWin.Top.ToString("0", CultureInfo.InvariantCulture));
    }
    private void UpdateHumanControls(PlaySession? s)
    {
        bool running = s?.Human != null && s.State is SessionState.Running or SessionState.Paused;
        HumanToggle.IsEnabled = running || (_con != null && !SessionActive);   // v0.19.5: 探索していないときも押せる（押すと Human Control で始める）
        if (s?.Human is { } hc && _humanKeysFor != s)
        {
            _humanKeysFor = s;
            if (_humanWin != null) { SaveHumanWindow(); _humanWin.PlacementChanged -= SaveHumanWindow; _humanWin.AllowClose = true; _humanWin.Close(); }
            var phys = _env.Profile.Input.Keys.ToDictionary(kv => kv.Key, kv => kv.Value.Key.Name, StringComparer.Ordinal);
            _humanWin = new HumanControlWindow(phys, hc.Allowed, name =>
            {
                bool ok = _session?.Human?.Enqueue(name) == true;
                if (!ok) AddLog($"Human Control: {name} を受け付けませんでした（Human Control でない・積みすぎ）", LogLevel.Warn);
                return ok;
            }) { Owner = this };
            if (HumanControlWindow.Saved(_ui) is var (l, t))
            {
                var area = new GameKouryakuKun.Core.Ui.ScreenArea(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (area.Overlap(l, t, 120, 80) > 0) { _humanWin.WindowStartupLocation = WindowStartupLocation.Manual; _humanWin.Left = l; _humanWin.Top = t; }
            }
            else _humanWin.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _humanWin.PlacementChanged += SaveHumanWindow;
        }
        bool on = s?.Human?.Enabled == true;
        if (_humanWin != null)
        {
            _humanWin.SetEnabledKeys(running && on);
            if (running && on) { if (!_humanWin.IsVisible) _humanWin.Show(); }
            else if (_humanWin.IsVisible) { SaveHumanWindow(); _humanWin.Hide(); }
        }
        HumanToggle.IsChecked = on;
        HumanToggle.Content = on ? "● Human Control: ON（人間が操作中・押すと自動に戻す）" : running ? "○ Human Control: OFF（自動で探索中）" : "○ Human Control: OFF（押すと Human Control で始める）";
    }

    private void Human_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionActive) { StartSession(humanOnly: true); return; }   // v0.19.5: 探索していないときは Human Control で始める
        var hc = _session?.Human; if (hc == null) { HumanToggle.IsChecked = false; return; }
        bool on = HumanToggle.IsChecked == true;
        hc.SetEnabled(on);
        AddLog(on ? "Human Control: 人間の操作に切り替えます（次の手の前に画面を観測し直します）" : "Human Control: 自動の探索に戻します（次の手の前に画面を観測し直します）", LogLevel.Ok);
        UpdatePanels();
    }

    /// <summary>手探り探索の実行時間（秒。0 = なし）</summary>
    private int RunSeconds() =>
        RunMinutesBox?.SelectedItem is ComboBoxItem { Tag: string t } && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : 0;

    private void Pause_Click(object sender, RoutedEventArgs e) { _session?.TogglePause(); UpdatePanels(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { _session?.Stop(); AddLog("停止を要求しました（今の手が終わったら止まります）", LogLevel.Warn); UpdatePanels(); }
    private void Emergency_Click(object sender, RoutedEventArgs e) => Emergency("画面のボタン");

    private void Emergency(string from)
    {
        if (_session == null || _session.State == SessionState.Stopped) { AddLog($"緊急停止（{from}）を受信しました（実行中のセッションはありません）", LogLevel.Warn); return; }
        _session.EmergencyStop(from);
        UpdatePanels();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        ExportButton.IsEnabled = false;
        try
        {
            // v0.29.2: 探索の終わりの保存中なら、session_end が書かれるまで待つ（健全性レポートの終わりの項目が抜けないように）
            if (_session is { Finishing: true } fs && fs.State != SessionState.Stopped)
            {
                AddLog("[診断データ] 探索の終わりの保存を待っています…", LogLevel.Dim);
                var t0 = DateTime.UtcNow;
                while (fs.State != SessionState.Stopped && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(120)) await Task.Delay(250);   // 根拠: 保存は実機 #110 で約 8 秒。止まった場合の上限
            }
            var r = await Task.Run(() => _env.ExportDiagnostics());
            AddLog($"診断データを書き出しました: {r.ZipPath}（{r.Bytes / 1024} KB、{r.Entries.Count} ファイル）", LogLevel.Ok);
            // zip そのものをクリップボードへ（ファイルの貼り付け＝CF_HDROP。チャットやエクスプローラーに Ctrl+V で貼れる）。失敗しても書き出しは成功のまま
            AddLog("[診断データ] zip をクリップボードにコピーしています…", LogLevel.Dim);
            var (copied, err) = await CopyZipToClipboardAsync(r.ZipPath);
            try { _env.Log.Write("diag_clipboard", ("ok", copied), ("detail", err)); } catch { }
            string clip = copied ? "zip ファイルをクリップボードにコピーしました。送り先に Ctrl+V で貼り付けられます。"
                                 : $"クリップボードへのコピーはできませんでした（{err}）。zip は上の場所にあります。";
            AddLog("[診断データ] " + clip, copied ? LogLevel.Ok : LogLevel.Warn);
            // v0.19.6: 結果の画面・ドラッグの枠・再コピーのボタンはやめ、短い知らせだけにする（利用者の依頼 t40u）
            MessageBox.Show(this, $"{clip}\n\n{r.ZipPath}", "診断データ", MessageBoxButton.OK, copied ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, "書き出しに失敗: " + ex.Message, "診断データ"); }
        finally { ExportButton.IsEnabled = true; }
    }

    /// <summary>zip をファイルとしてクリップボードに置く（ファイルの一覧＋場所の文字列）。クリップボードが使用中なら数回だけ待ってやり直す</summary>
    /// <summary>
    /// zip をファイルとしてクリップボードに置く（ファイルの一覧＋場所の文字列）。
    /// v0.19.5: 実機の失敗はどれも CLIPBRD_E_CANT_OPEN。copy=true（OleFlushClipboard）は置いた直後にほかのアプリが読みに来ると失敗しやすいので、
    /// 先に copy=false（アプリが動いている間は貼り付けられる）で置き、置けたかを読み返して確かめる。各回の結果（エラーコード・経過ミリ秒）を記録する
    /// </summary>
    private static async Task<(bool Ok, string? Error)> CopyZipToClipboardAsync(string path)
    {
        var tries = new List<string>(); var sw = System.Diagnostics.Stopwatch.StartNew();
        const int Attempts = 25;   // 根拠: 汎用（ほかのアプリが一時的にクリップボードを開いていることへのやり直し。待ちは合計約 20 秒）
        for (int i = 0; i < Attempts; i++)
        {
            bool copy = i % 2 == 1;   // copy=false と copy=true を交互に
            try
            {
                var data = new DataObject();
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
                data.SetText(path);
                Clipboard.SetDataObject(data, copy);
                bool there = false; try { there = Clipboard.ContainsFileDropList(); } catch { }
                if (there) return (true, i == 0 ? null : $"{i + 1} 回目で成功（{(copy ? "copy" : "nocopy")}・{sw.ElapsedMilliseconds}ms）・前の失敗: {string.Join(" ", tries)}");
                tries.Add($"#{i + 1}{(copy ? "c" : "n")}@{sw.ElapsedMilliseconds}:置いたが読み返せない");
            }
            catch (Exception ex) { tries.Add($"#{i + 1}{(copy ? "c" : "n")}@{sw.ElapsedMilliseconds}:{(ex is System.Runtime.InteropServices.COMException ce ? $"0x{ce.HResult:X8}" : ex.GetType().Name)}"); }
            await Task.Delay(Math.Min(1500, 150 * (i + 1)));
        }
        string summary = $"{Attempts} 回・{sw.ElapsedMilliseconds}ms: {string.Join(" ", tries)}";
        try { Clipboard.SetText(path); return (false, "zip ファイルとしては置けず、場所の文字列だけを置きました・" + summary); }
        catch (Exception ex) { return (false, $"文字列も置けませんでした（{ex.Message}）・" + summary); }
    }

    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        var w = new SetupWindow(_env) { Owner = this };
        w.ShowDialog();
        RefreshOcrEngines();
        UpdatePanels();
    }

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        var f = _con?.Capture.LatestFrame();
        var w = new OcrTestWindow(_env, Engines(), f) { Owner = this };
        w.Show();
    }

    public Frame? CurrentFrame() => _con?.Capture.LatestFrame();

    // ======================== OCR ========================
    private void RefreshOcrEngines()
    {
        _paddle?.Dispose();
        _paddleV4Compare?.Dispose(); _paddleV4Compare = null;
        _compStatus = _env.Components.CheckAll(); // 起動時とセットアップ後だけ検査（UI 更新のたびにハッシュ計算しない）
        // PP-OCRv5 が導入済みならそれを使い、なければ従来の PP-OCRv4 日本語モデル
        bool v5 = _compStatus.Any(c => c.Spec.Id == "ocr-paddle-v5" && c.Health == Setup.ComponentHealth.Installed);
        _paddle = v5
            ? new PaddleOcrEngine(_env.ComponentDir("ocr-paddle-v5"), _env.ComponentDir("onnxruntime"), PaddleOcrOptions.V5)
            : new PaddleOcrEngine(_env.ComponentDir("ocr-paddle-ja"), _env.ComponentDir("onnxruntime"), PaddleOcrOptions.V4);
        if (v5 && _compStatus.Any(c => c.Spec.Id == "ocr-paddle-ja" && c.Health == Setup.ComponentHealth.Installed))
            _paddleV4Compare = new PaddleOcrEngine(_env.ComponentDir("ocr-paddle-ja"), _env.ComponentDir("onnxruntime"), PaddleOcrOptions.V4);
    }
    /// <summary>OCR テスト用の一覧（自動選択はこの順に試す）: 主 Paddle → v4（v5 主のときだけ）→ Windows 内蔵</summary>
    private IReadOnlyList<IOcrEngine> Engines() => _paddleV4Compare != null
        ? new IOcrEngine[] { _paddle!, _paddleV4Compare, _winOcr }
        : new IOcrEngine[] { _paddle!, _winOcr };
    private string OcrStatusText()
    {
        var p = _paddle!.Availability(); var w = _winOcr.Availability();
        return $"PaddleOCR: {(p.Ok ? "利用可" : p.Detail)} ／ Windows 内蔵: {(w.Ok ? "利用可（" + w.Detail + "）" : w.Detail)}";
    }

    // ======================== 終了 ========================
    // ---- v0.19.1: 開始の設定・グラフの種類・ウィンドウの位置を ui-settings.json に保存して次の起動で戻す（表示だけ。学習・記録は参照しない） ----
    private bool _restoringUi;
    private static string MetricKey(object? item) => item is ComboBoxItem { Tag: var t } ? (t is GrowthMetric gm ? gm.ToString() : t?.ToString() ?? "") : "";

    // ---- v0.23.1 自動で起動し直す（Supervisor の環境の設定。data\launch-settings.json。攻略 AI は読まない） ----
    private bool _launchUiLoading;
    private void LoadLaunchUi()
    {
        _launchUiLoading = true;
        try
        {
            var ls = GameKouryakuKun.Core.Supervisor.LaunchSettings.Load(_env.DataRoot);
            AutoRelaunchCheck.IsChecked = ls.AutoRelaunch;
            LaunchRouteBox.SelectedIndex = ls.Route switch { "exe" => 1, "steam" => 2, _ => 0 };
            LaunchExeBox.Text = ls.Route == "exe" ? ls.ChosenExe ?? "" : ls.DetectedExe ?? "（まだ見つけていません。ゲームに接続すると記録します）";
            SteamIdBox.Text = ls.SteamAppId ?? ls.DetectedSteamAppId ?? "";
            UpdateLaunchInfo(ls);
        }
        finally { _launchUiLoading = false; }
    }

    private void UpdateLaunchInfo(GameKouryakuKun.Core.Supervisor.LaunchSettings ls)
    {
        var eff = ls.Apply(GameKouryakuKun.Core.Supervisor.SupervisorConfig.Load(_env.DataRoot));
        LaunchInfo.Text = "起動の方法: " + eff.LaunchSummary + $"・見張るファイル {eff.WatchFiles.Count}" + (ls.DetectedSteamAppId != null && ls.Route == "detected" ? "（Steam のゲームは Steam 経由で起動します）" : "");
        LaunchBrowseButton.IsEnabled = ls.Route == "exe";
        SteamIdBox.IsEnabled = ls.Route == "steam";
    }

    private void Launch_Changed(object sender, RoutedEventArgs e)
    {
        if (_launchUiLoading || !IsLoaded) return;
        var ls = GameKouryakuKun.Core.Supervisor.LaunchSettings.Load(_env.DataRoot);
        var route = (LaunchRouteBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "detected";
        var id = SteamIdBox.Text.Trim();
        ls = ls with { AutoRelaunch = AutoRelaunchCheck.IsChecked == true, Route = route, SteamAppId = id.Length > 0 && id.All(char.IsDigit) ? id : null };
        try { ls.Save(_env.DataRoot); } catch (Exception ex) { AddLog("自動で起動し直す設定の保存に失敗: " + ex.Message, LogLevel.Warn); }
        if (id.Length > 0 && !id.All(char.IsDigit)) AddLog("Steam app id は数字だけです", LogLevel.Warn);
        _env.Log.Write("launch_settings_changed", ("auto_relaunch", ls.AutoRelaunch), ("route", ls.Route));
        LoadLaunchUi();
    }

    private void WatchFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "変化を見張るファイル（記録と警告だけ。戻さない）" };
        if (dlg.ShowDialog(this) != true) return;
        var ls = GameKouryakuKun.Core.Supervisor.LaunchSettings.Load(_env.DataRoot) with { WatchFiles = dlg.FileNames.ToList() };
        try { ls.Save(_env.DataRoot); } catch (Exception ex) { AddLog("保存に失敗: " + ex.Message, LogLevel.Warn); }
        _env.Log.Write("launch_settings_changed", ("watch_files", ls.WatchFiles.Count));
        AddLog($"見張るファイル {ls.WatchFiles.Count} 件（次の開始から。基準は最初に 1 回だけ取ります）", LogLevel.Info);
        LoadLaunchUi();
    }

    private void WatchReset_Click(object sender, RoutedEventArgs e)
    {
        try { File.Delete(Path.Combine(_env.DataRoot, GameKouryakuKun.Core.Supervisor.ProtectedFiles.BaselineFileName)); AddLog("見張るファイルの基準を、次の開始のときに取り直します", LogLevel.Info); _env.Log.Write("watch_baseline_reset"); }
        catch (Exception ex) { AddLog("基準の取り直しに失敗: " + ex.Message, LogLevel.Warn); }
    }

    private void LaunchBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "実行ファイル (*.exe)|*.exe", Title = "起動し直す実行ファイル" };
        if (dlg.ShowDialog(this) != true) return;
        var ls = GameKouryakuKun.Core.Supervisor.LaunchSettings.Load(_env.DataRoot) with { ChosenExe = dlg.FileName, Route = "exe" };
        try { ls.Save(_env.DataRoot); } catch (Exception ex) { AddLog("保存に失敗: " + ex.Message, LogLevel.Warn); }
        _env.Log.Write("launch_settings_changed", ("auto_relaunch", ls.AutoRelaunch), ("route", ls.Route));
        LoadLaunchUi();
    }

    private void RestoreUiState()
    {
        _ui ??= GameKouryakuKun.Core.Ui.UiSettings.Load(_env.DataRoot);
        _restoringUi = true;
        try
        {
            if (_ui.GetValue("run_seconds") is string rs) foreach (ComboBoxItem it in RunMinutesBox.Items) if ((it.Tag as string) == rs) { RunMinutesBox.SelectedItem = it; break; }
            if (_ui.GetValue("max_steps") is string ms && int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mv) && mv >= 0) MaxStepsBox.Text = mv.ToString(CultureInfo.InvariantCulture);
            if (_ui.GetValue("graph") is string g) foreach (ComboBoxItem it in MetricBox.Items) if (MetricKey(it) == g) { MetricBox.SelectedItem = it; break; }
            var areas = new List<GameKouryakuKun.Core.Ui.ScreenArea>
            {
                new(SystemParameters.WorkArea.X, SystemParameters.WorkArea.Y, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height),   // 主モニター（画面外のときの戻り先）
                new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight),
            };
            var saved = _ui.Window;
            var fit = GameKouryakuKun.Core.Ui.WindowRestore.Fit(saved, areas, MinWidth > 0 ? MinWidth : 400, MinHeight > 0 ? MinHeight : 300);
            string win = saved == null ? "none" : fit == null ? "invalid" : (fit.Left == saved.Left && fit.Top == saved.Top && fit.Width == saved.Width && fit.Height == saved.Height) ? "restored" : "adjusted";
            // v0.19.2: 復元したことを診断で確かめられるように記録する（実機 v0.19.1 では記録が無く確かめられなかった）
            _env.Log.Write("view_settings_restored", ("run_seconds", RunMinutesBox.SelectedItem is ComboBoxItem { Tag: string rt } ? rt : "-"), ("max_steps", MaxStepsBox.Text), ("graph", MetricKey(MetricBox.SelectedItem)),
                ("window", win), ("maximized", fit?.Maximized ?? false), ("saved_run_seconds", _ui.GetValue("run_seconds") ?? "-"), ("saved_max_steps", _ui.GetValue("max_steps") ?? "-"), ("saved_graph", _ui.GetValue("graph") ?? "-"));
            if (fit != null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = fit.Left; Top = fit.Top; Width = fit.Width; Height = fit.Height;
                if (fit.Maximized) WindowState = WindowState.Maximized;
            }
        }
        catch (Exception ex) { _env.Log.Write("view_settings_error", ("panel", "restore"), ("detail", ex.Message)); }
        finally { _restoringUi = false; }
        RunMinutesBox.SelectionChanged += (_, _) => SaveUiValue("run_seconds", RunMinutesBox.SelectedItem is ComboBoxItem { Tag: string t } ? t : null);
        // v0.19.4: 1 文字ごとではなく、入力欄を離れたとき・閉じるときに保存する（実機 v0.19.3 で 1 回の入力に 6 回保存していた）
        MaxStepsBox.LostFocus += (_, _) => SaveMaxSteps();
        MetricBox.SelectionChanged += (_, _) => SaveUiValue("graph", MetricKey(MetricBox.SelectedItem));
    }

    private void SaveUiValue(string key, string? value)
    {
        if (_restoringUi || _ui == null || string.IsNullOrEmpty(value)) return;
        try { if (_ui.GetValue(key) == value) return; _ui.SetValue(key, value); _env.Log.Write("view_settings_value_saved", ("key", key), ("value", value)); } catch (Exception ex) { _env.Log.Write("view_settings_error", ("panel", key), ("detail", ex.Message)); }
    }

    private void SaveMaxSteps() { if (int.TryParse(MaxStepsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0) SaveUiValue("max_steps", v.ToString(CultureInfo.InvariantCulture)); }

    private void SaveWindowPlacement()
    {
        try
        {
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (b.IsEmpty || b.Width < 1 || b.Height < 1) return;
            _ui?.SetWindow(new GameKouryakuKun.Core.Ui.WindowPlacement { Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height, Maximized = WindowState == WindowState.Maximized });
            _env.Log.Write("view_settings_window_saved", ("width", Math.Round(b.Width)), ("height", Math.Round(b.Height)), ("maximized", WindowState == WindowState.Maximized));
        }
        catch (Exception ex) { _env.Log.Write("view_settings_error", ("panel", "window"), ("detail", ex.Message)); }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_humanWin != null) { SaveHumanWindow(); _humanWin.AllowClose = true; }
        SaveMaxSteps();
        SaveWindowPlacement();
        var s = _session;
        if (s != null && s.State is not (SessionState.Stopped or SessionState.Idle))
        {
            s.EmergencyStop("ウィンドウを閉じた");
            for (int i = 0; i < 30 && s.State != SessionState.Stopped; i++) Thread.Sleep(100);
        }
        _liveTimer.Stop(); _panelTimer.Stop();
        try { _con?.Dispose(); } catch { }
        try { _hotkey.Dispose(); } catch { }
        try { _activity.Dispose(); } catch { }
        try { _standIn.Dispose(); } catch { }
        try { _paddle?.Dispose(); _paddleV4Compare?.Dispose(); } catch { }
        _timerRes.Dispose();
    }

}
