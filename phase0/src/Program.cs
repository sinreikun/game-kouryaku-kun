using System.Text;

namespace GameKouryakuKun.Phase0;

public static class Con
{
    private static readonly object L = new();
    private static void W(ConsoleColor c, string s) { lock (L) { var o = Console.ForegroundColor; Console.ForegroundColor = c; Console.WriteLine(s); Console.ForegroundColor = o; } }
    public static void Info(string s) => W(ConsoleColor.Gray, s);
    public static void Ok(string s) => W(ConsoleColor.Green, s);
    public static void Warn(string s) => W(ConsoleColor.Yellow, s);
    public static void Err(string s) => W(ConsoleColor.Red, s);
    public static void Head(string s) => W(ConsoleColor.Cyan, "\n==== " + s + " ====");

    /// <summary>コンソールへフォーカスを戻すよう試みてから入力を求める</summary>
    public static string Prompt(string q, string def = "")
    {
        if (Foreground.FocusMoved)
        {
            // 従来のコンソール(conhost)なら前面へ戻す。Windows Terminal の場合は不可視の擬似窓なので触らない。
            var cw = Native.GetConsoleWindow();
            if (cw != IntPtr.Zero && Native.IsWindowVisible(cw)) Foreground.TryActivate(cw);
            W(ConsoleColor.DarkYellow, "  （入力する前に、このコンソールウィンドウをクリックしてください）");
            Foreground.FocusMoved = false;
        }
        W(ConsoleColor.White, $"{q}{(def.Length > 0 ? $" [{def}]" : "")}: ");
        var s = Console.ReadLine();
        return string.IsNullOrWhiteSpace(s) ? def : s.Trim();
    }

    public static bool Confirm(string q, bool def = false)
    {
        var a = Prompt(q + (def ? " (Y/n)" : " (y/N)"));
        if (a.Length == 0) return def;
        return a.StartsWith("y", StringComparison.OrdinalIgnoreCase) || a.StartsWith("はい");
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        Ui.Start();

        var cfg = new Config();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--target": cfg.TargetQuery = args[i + 1]; break;
                case "--key": cfg.OpenKey = args[i + 1]; break;
                case "--close-key": cfg.CloseKey = args[i + 1]; break;
                case "--pad": cfg.PadOpenButton = args[i + 1]; break;
                case "--pad-close": cfg.PadCloseButton = args[i + 1]; break;
                case "--wait": cfg.WaitAfterInputMs = int.Parse(args[i + 1]); break;
                case "--hold": cfg.HoldMs = int.Parse(args[i + 1]); break;
            }
        }

        var probe = new Probe(cfg);
        Con.Head("ゲーム攻略くん Phase 0 検証ツール (Phase0Probe)");
        Con.Info("このツールは画面取得と入力方式の実現性だけを調べます。ゲームのメモリ読み取り・ファイル解析は一切行いません。");
        Con.Info("各テストの前に内容を表示して確認を取ります。結果は exe と同じフォルダの results\\ に保存されます。");
        Con.Info($"保存先: {probe.Report.Dir}");

        while (true)
        {
            Con.Head("メニュー");
            Console.WriteLine($"  対象: {probe.TargetLabel}");
            Console.WriteLine($"  キー: 開く={Show(cfg.OpenKey)} 閉じる={Show(cfg.CloseKey.Length > 0 ? cfg.CloseKey : cfg.OpenKey)}  パッド: 開く={Show(cfg.PadOpenButton)} 閉じる={Show(cfg.PadCloseButton.Length > 0 ? cfg.PadCloseButton : cfg.PadOpenButton)}");
            Console.WriteLine("  [L] ウィンドウ一覧    [S] 対象ウィンドウを選択    [K] キー/パッドボタン/待ち時間の設定");
            Console.WriteLine("  [1] テスト1: WGC/PrintWindow 前面・背面キャプチャ");
            Console.WriteLine("  [2] テスト2: 入力 ①PostMessage ②仮想パッド ③SendInput × 前面・背面");
            Console.WriteLine("  [3] テスト3: 最小化時のキャプチャと①入力");
            Console.WriteLine("  [4] テスト4: 物理入力検出 → AI入力ループ停止デモ");
            Console.WriteLine("  [A] 1→2→3→4 を順に実行（各テスト前に確認あり）");
            Console.WriteLine("  [R] レポートを保存してフォルダを開く    [Q] 終了");
            var c = Con.Prompt("選択").ToUpperInvariant();
            try
            {
                switch (c)
                {
                    case "L": probe.ListWindows(); break;
                    case "S": probe.SelectTarget(); break;
                    case "K": probe.ConfigureKeys(); break;
                    case "1": probe.Test1Capture(); break;
                    case "2": probe.Test2Input(); break;
                    case "3": probe.Test3Minimize(); break;
                    case "4": probe.Test4PhysicalInput(); break;
                    case "A": probe.Test1Capture(); probe.Test2Input(); probe.Test3Minimize(); probe.Test4PhysicalInput(); break;
                    case "R": probe.SaveReport(open: true); break;
                    case "Q": probe.SaveReport(open: false); probe.Dispose(); Con.Ok($"終了します。レポート: {probe.Report.Dir}"); return 0;
                }
            }
            catch (Exception e)
            {
                Con.Err($"エラー: {e.GetType().Name}: {e.Message}");
                probe.RecordError(c, e);
            }
        }
    }

    private static string Show(string s) => s.Length > 0 ? s : "(未設定)";
}
