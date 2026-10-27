using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GameKouryakuKun.Core.Imaging;
using Frame = GameKouryakuKun.Core.Imaging.Frame;
using GameKouryakuKun.Core.Ocr;

namespace GameKouryakuKun.App;

/// <summary>今のゲーム画面（キャプチャ画像）の文字を読んで表示する。読み取り結果はまだ行動選択には使わない。</summary>
public partial class OcrTestWindow : Window
{
    private readonly AppEnv _env;
    private readonly IReadOnlyList<IOcrEngine> _engines;
    private Frame? _frame;

    public OcrTestWindow(AppEnv env, IReadOnlyList<IOcrEngine> engines, Frame? frame)
    {
        _env = env; _engines = engines; _frame = frame;
        InitializeComponent();
        EngineBox.Items.Add(new ComboBoxItem { Content = "自動（PaddleOCR → Windows 内蔵の順）" });
        foreach (var e in engines) { var a = e.Availability(); EngineBox.Items.Add(new ComboBoxItem { Content = $"{e.DisplayName}{(a.Ok ? "" : "（利用不可）")}", IsEnabled = true }); }
        EngineBox.SelectedIndex = 0;
        ShowFrame();
        Loaded += async (_, _) => { if (_frame != null) await RunAsync(); };
    }

    private void ShowFrame()
    {
        Overlay.Children.Clear();
        if (_frame == null) { Status.Text = "ゲーム画面がありません（ゲームに接続するか、画像ファイルを開いてください）"; Img.Source = null; return; }
        var bmp = BitmapSource.Create(_frame.Width, _frame.Height, 96, 96, PixelFormats.Bgra32, null, _frame.Bgra, _frame.Width * 4);
        Img.Source = bmp;
        ImageHost.Width = _frame.Width; ImageHost.Height = _frame.Height;
        Overlay.Width = _frame.Width; Overlay.Height = _frame.Height;
        Status.Text = $"{_frame.Width}x{_frame.Height}（{_frame.Source}）";
    }

    private async Task RunAsync()
    {
        if (_frame == null) return;
        RunButton.IsEnabled = false; Status.Text = "読み取り中…"; ResultText.Text = "";
        try
        {
            OcrResult? r; IReadOnlyList<string> tried;
            int idx = EngineBox.SelectedIndex;
            if (idx <= 0) (r, tried) = await new OcrEngineChain(_engines).RecognizeAsync(_frame);
            else
            {
                var e = _engines[idx - 1];
                var a = e.Availability();
                if (!a.Ok) { r = null; tried = new[] { $"{e.DisplayName}: {a.Detail}" }; }
                else { r = await e.RecognizeAsync(_frame); tried = new[] { $"{e.DisplayName}: 成功" }; }
            }
            var sb = new StringBuilder();
            Overlay.Children.Clear();
            if (r == null) sb.AppendLine("読み取れるエンジンがありません。");
            else
            {
                sb.AppendLine($"エンジン: {r.EngineId}／{r.ElapsedMs:0}ms／{r.Detail}／{r.Lines.Count} 行");
                sb.AppendLine();
                foreach (var l in r.Lines)
                {
                    sb.AppendLine($"{l.Text}    {(l.Confidence is double c ? $"[{c:0.00}]" : "")} {l.Box}");
                    if (OcrTokens.Split(l.Text).Any(t => t.Kind == OcrTokenKind.Number)) sb.AppendLine($"    → {OcrTokens.Describe(l.Text)}");
                    var rect = new Rectangle { Width = l.Box.W, Height = l.Box.H, Stroke = Brushes.Lime, StrokeThickness = 2 };
                    Canvas.SetLeft(rect, l.Box.X); Canvas.SetTop(rect, l.Box.Y);
                    Overlay.Children.Add(rect);
                }
            }
            sb.AppendLine();
            sb.AppendLine("試したエンジン:");
            foreach (var t in tried) sb.AppendLine("・" + t);
            ResultText.Text = sb.ToString();
            Status.Text = r == null ? "失敗" : $"{r.Lines.Count} 行を読み取りました";
            _env.Log.Write("ocr_test", ("engine", r?.EngineId), ("lines", r?.Lines.Count), ("ms", r?.ElapsedMs), ("tried", tried.ToList()), ("text", r?.Text));
        }
        catch (Exception e) { Status.Text = "エラー"; ResultText.Text = $"{e.GetType().Name}: {e.Message}"; }
        finally { RunButton.IsEnabled = true; }
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    /// <summary>使えるエンジンすべてで同じ画像を読み、行ごとに並べる（Windows 内蔵 OCR との比較用）</summary>
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_frame == null) return;
        RunButton.IsEnabled = CompareButton.IsEnabled = false; Status.Text = "比較中…"; Overlay.Children.Clear();
        var sb = new StringBuilder();
        var colors = new[] { Brushes.Lime, Brushes.Orange, Brushes.DeepSkyBlue };
        try
        {
            int k = 0;
            foreach (var eng in _engines)
            {
                var brush = colors[k++ % colors.Length];
                var a = eng.Availability();
                sb.AppendLine($"■ {eng.DisplayName}（枠の色: {(brush == Brushes.Lime ? "緑" : brush == Brushes.Orange ? "橙" : "青")}）");
                if (!a.Ok) { sb.AppendLine("   使用不可: " + a.Detail); sb.AppendLine(); continue; }
                try
                {
                    var r = await eng.RecognizeAsync(_frame);
                    sb.AppendLine($"   {r.ElapsedMs:0}ms／{r.Lines.Count} 行／{r.Detail}");
                    foreach (var l in r.Lines.OrderBy(l => l.Box.Y / 20).ThenBy(l => l.Box.X))
                    {
                        sb.AppendLine($"   {l.Text}  {(l.Confidence is double c ? $"[{c:0.00}]" : "")}  {OcrTokens.Describe(l.Text)}");
                        var rect = new Rectangle { Width = l.Box.W, Height = l.Box.H, Stroke = brush, StrokeThickness = 2, StrokeDashArray = k > 1 ? new DoubleCollection { 3, 2 } : null };
                        Canvas.SetLeft(rect, l.Box.X); Canvas.SetTop(rect, l.Box.Y);
                        Overlay.Children.Add(rect);
                    }
                    _env.Log.Write("ocr_compare", ("engine", r.EngineId), ("lines", r.Lines.Count), ("ms", r.ElapsedMs), ("text", r.Text));
                }
                catch (Exception ex) { sb.AppendLine($"   失敗: {ex.GetType().Name}: {ex.Message}"); }
                sb.AppendLine();
            }
            ResultText.Text = sb.ToString();
            Status.Text = "比較しました";
        }
        finally { RunButton.IsEnabled = CompareButton.IsEnabled = true; }
    }

    private void Recapture_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mw && mw.CurrentFrame() is Frame f) { _frame = f; ShowFrame(); }
        else Status.Text = "ゲームに接続していません";
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp|すべて|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var src = new BitmapImage();
            src.BeginInit(); src.CacheOption = BitmapCacheOption.OnLoad; src.UriSource = new Uri(dlg.FileName); src.EndInit();
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            _frame = new Frame(Frame.NextId(), w, h, px, 0, "file:" + System.IO.Path.GetFileName(dlg.FileName));
            ShowFrame();
        }
        catch (Exception ex) { Status.Text = "開けません: " + ex.Message; }
    }
}
