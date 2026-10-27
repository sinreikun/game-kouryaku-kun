using System.Drawing;
using System.Windows.Forms;

namespace GameKouryakuKun.Phase0;

/// <summary>メッセージループを持つ STA スレッド。ダミー窓・RawInput受信窓・低レベルフックはここで動かす。</summary>
public static class Ui
{
    private static Control? _invoker;
    private static readonly ManualResetEventSlim Ready = new(false);

    public static void Start()
    {
        var t = new Thread(() =>
        {
            Application.EnableVisualStyles();
            _invoker = new Control();
            _ = _invoker.Handle;
            Ready.Set();
            Application.Run(new ApplicationContext());
        }) { IsBackground = true, Name = "UI" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Ready.Wait();
    }

    public static T Invoke<T>(Func<T> f) => (T)_invoker!.Invoke(f)!;
    public static void Invoke(Action a) => _invoker!.Invoke(a);
}

/// <summary>
/// テスト用の無害なダミー窓。③SendInput の背面テストや、背面テスト時の「前面アプリ役」を担う。
/// 届いたキーを記録するので「前面アプリへ漏れた入力」を検出できる。キーに反応して何かを実行することはない。
/// </summary>
public sealed class DummyForm : Form
{
    private readonly Label _label;
    private readonly List<string> _log = new();
    public int KeyDownCount { get; private set; }
    public List<string> ReceivedKeys { get { lock (_log) return _log.ToList(); } }

    public DummyForm()
    {
        Text = "ゲーム攻略くん Phase0 ダミー窓（テスト用・入力の受け皿）";
        Size = new Size(720, 300);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        BackColor = Color.FromArgb(30, 34, 48);
        ForeColor = Color.White;
        KeyPreview = true;
        ShowInTaskbar = true;
        _label = new Label
        {
            Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", 13f), TextAlign = ContentAlignment.MiddleCenter,
            Text = "これはテスト用のダミー窓です。\n背面テスト中はこの窓が前面になり、届いたキーをここに記録します。\n（キーを受けても何も実行しません）"
        };
        Controls.Add(_label);
    }

    public void ResetLog() { lock (_log) _log.Clear(); KeyDownCount = 0; Status(""); }
    public void Status(string s) => _label.Text = "ゲーム攻略くん Phase0 ダミー窓\n" + s + (KeyDownCount > 0 ? $"\n受信キー: {string.Join(" ", ReceivedKeys.TakeLast(12))}" : "");

    protected override void WndProc(ref Message m)
    {
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN)
        {
            KeyDownCount++;
            lock (_log) _log.Add(((Keys)(int)m.WParam).ToString());
            _label.Text = $"ゲーム攻略くん Phase0 ダミー窓\nキーを受信しました（何も実行しません）\n受信キー: {string.Join(" ", ReceivedKeys.TakeLast(12))}";
            m.Result = IntPtr.Zero;
            return; // 既定処理（Alt+メニュー等）へ渡さない
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}

public static class Dummy
{
    private static DummyForm? _form;
    public static DummyForm Form => _form ??= Ui.Invoke(() => { var f = new DummyForm(); f.CreateControl(); return f; });
    public static IntPtr Hwnd => Ui.Invoke(() => Form.Handle);
    public static void Show(string status) => Ui.Invoke(() => { Form.Status(status); Form.Show(); Form.WindowState = FormWindowState.Normal; Form.Activate(); });
    public static void Hide() => Ui.Invoke(() => Form.Hide());
    public static void Reset() => Ui.Invoke(() => Form.ResetLog());
    public static (int count, List<string> keys) Received() => Ui.Invoke(() => (Form.KeyDownCount, Form.ReceivedKeys));
}
