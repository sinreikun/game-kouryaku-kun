using System.Windows;
using System.Windows.Threading;

namespace GameKouryakuKun.App;

public partial class App : Application
{
    public static AppEnv? Env { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => TryLog("unhandled", a.ExceptionObject?.ToString());
        try { Env = AppEnv.Create(); }
        catch (Exception ex)
        {
            MessageBox.Show($"起動に失敗しました。\n\n{ex.GetType().Name}: {ex.Message}", "ゲーム攻略くん", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var w = new MainWindow(Env);
        MainWindow = w;
        w.Show();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog("ui_exception", e.Exception.ToString());
        MessageBox.Show($"エラーが発生しました（処理は続行します）。\n\n{e.Exception.GetType().Name}: {e.Exception.Message}", "ゲーム攻略くん", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void TryLog(string type, string? detail) { try { Env?.Log.Write(type, ("detail", detail)); } catch { } }

    protected override void OnExit(ExitEventArgs e)
    {
        Env?.Dispose();
        base.OnExit(e);
    }
}
