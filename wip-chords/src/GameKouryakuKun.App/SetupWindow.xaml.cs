using System.Windows;
using GameKouryakuKun.Setup;

namespace GameKouryakuKun.App;

public sealed record ComponentRow(ComponentSpec Spec, string Name, string Version, string License, string Size, string Status);

public partial class SetupWindow : Window
{
    private readonly AppEnv _env;
    private CancellationTokenSource? _cts;

    public SetupWindow(AppEnv env)
    {
        _env = env;
        InitializeComponent();
        Refresh(false);
        try { _env.Log.Write("setup_open", ("components", _env.Components.CheckAll().Select(c => $"{c.Spec.Id}@{c.Spec.Version}:{c.Health}").ToList())); } catch { }
        Closing += (_, e) => { if (_cts != null) { e.Cancel = true; MessageBox.Show(this, "導入中です。先に「中断」を押してください。", "セットアップ"); } };
    }

    private void Refresh(bool forceHash) => Show(_env.Components.CheckAll(forceHash));

    private void Show(IReadOnlyList<ComponentStatus> statuses)
    {
        var rows = statuses.Select(st => new ComponentRow(st.Spec, st.Spec.DisplayName, st.Spec.Version,
            $"{st.Spec.License.Spdx}{(st.Spec.License.Url != null ? " " + st.Spec.License.Url : "")}", ComponentManager.Mb(st.Spec.DownloadBytes), st.Summary)).ToList();
        List.ItemsSource = rows;
        var details = statuses.SelectMany(st => st.Files.Where(f => !f.Ok).Select(f => $"{st.Spec.Id}/{f.File.Path}: {f.Detail}")).ToList();
        ProgressText.Text = details.Count == 0 ? "すべての部品が正常に導入されています。" : "未導入・異常: " + string.Join("、", details.Take(6)) + (details.Count > 6 ? " …" : "");
    }

    private async Task Install(IEnumerable<ComponentSpec> specs)
    {
        var list = specs.ToList();
        if (list.Count == 0) { MessageBox.Show(this, "導入する部品を選んでください。", "セットアップ"); return; }
        if (LicenseCheck.IsChecked != true)
        {
            MessageBox.Show(this, "導入する部品のライセンスへの同意が必要です:\n\n" + string.Join("\n", list.Select(s => $"・{s.DisplayName}: {s.License.Spdx} {s.License.Url}{(s.License.Notes != null ? "\n    " + s.License.Notes : "")}")), "ライセンス", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _cts = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress.Value = p.Ratio;
            ProgressText.Text = $"{p.ComponentId}: {p.Stage}{(p.File != null ? " " + p.File : "")} {ComponentManager.Mb(p.Done)}/{ComponentManager.Mb(p.Total)}{(p.Message != null ? " — " + p.Message : "")}";
        });
        var results = new List<string>();
        try
        {
            foreach (var spec in list)
            {
                try
                {
                    var r = await _env.Components.InstallAsync(spec, true, progress, _cts.Token);
                    results.Add($"○ {spec.DisplayName}: {r.Detail}");
                    _env.Log.Write("setup_install", ("id", spec.Id), ("version", spec.Version), ("ok", r.Ok), ("bytes", r.BytesDownloaded), ("resumed", r.Resumed),
                        ("skipped_already_installed", r.BytesDownloaded == 0 && r.DownloadedUrls.Count == 0), ("detail", r.Detail), ("urls", r.DownloadedUrls.ToList()));
                }
                catch (OperationCanceledException) { results.Add($"― {spec.DisplayName}: 中断しました（次回は途中から再開）"); break; }
                catch (Exception e) { results.Add($"× {spec.DisplayName}: {e.Message}"); _env.Log.Write("setup_error", ("id", spec.Id), ("error", e.ToString())); }
            }
        }
        finally { _cts.Dispose(); _cts = null; SetBusy(false); }
        Refresh(false);
        MessageBox.Show(this, string.Join("\n", results), "セットアップの結果");
    }

    private void SetBusy(bool busy)
    {
        InstallButton.IsEnabled = InstallAllButton.IsEnabled = VerifyButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await Install(List.SelectedItems.Cast<ComponentRow>().Select(r => r.Spec));
    private async void InstallAll_Click(object sender, RoutedEventArgs e)
    {
        var plan = _env.Components.Plan();
        if (plan.Needed.Count == 0)
        {
            // 再取得しなかった証跡をログに残す
            _env.Log.Write("setup_skip", ("reason", "all_installed"), ("components", _env.Components.CheckAll().Select(c => $"{c.Spec.Id}@{c.Spec.Version}:{c.Health}").ToList()));
            MessageBox.Show(this, "すべての部品が導入済みです（検証OK・再取得しません）。", "セットアップ");
            return;
        }
        await Install(plan.Needed.Select(n => n.Spec));
    }
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true); ProgressText.Text = "全ファイルの SHA-256 を計算しています…";
        try { Show(await Task.Run(() => _env.Components.CheckAll(forceHash: true))); }
        finally { SetBusy(false); CancelButton.IsEnabled = false; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
