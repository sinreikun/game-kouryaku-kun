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

    public static string Prompt(string q, string def = "")
    {
        if (Foreground.FocusMoved)
        {
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

    /// <summary>キー入力を受け、テンキー/上段数字のあいまいさは確認して解決する。戻り値は正規化済みの指定文字列。</summary>
    public static string PromptKey(string q, string def, bool allowEmpty = false)
    {
        while (true)
        {
            var k = Prompt(q, def);
            if (k.Length == 0 && allowEmpty) return "";
            try { var ks = KeySpec.Parse(k); Ok($"  → {ks}"); return ks.Name; }
            catch (KeySpec.AmbiguousKeyException e)
            {
                bool num = Confirm($"  「{e.Digit}」はテンキーの{e.Digit}ですか？（n = キーボード上段の数字{e.Digit}）", true);
                var ks = KeySpec.Parse(num ? $"Num{e.Digit}" : $"D{e.Digit}");
                Ok($"  → {ks}"); return ks.Name;
            }
            catch (Exception e) { Warn(e.Message); }
        }
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
                case "--t6-minutes": cfg.T6Minutes = int.Parse(args[i + 1]); break;
            }
        }
        // 引数で数字だけを指定された場合もここで確認
        foreach (var (name, get, set) in new (string, Func<string>, Action<string>)[]
                 { ("開くキー", () => cfg.OpenKey, v => cfg.OpenKey = v), ("閉じるキー", () => cfg.CloseKey, v => cfg.CloseKey = v) })
        {
            try { KeySpec.Parse(get()); }
            catch (KeySpec.AmbiguousKeyException) { set(Con.PromptKey($"{name}「{get()}」があいまいです。指定し直してください", get())); }
            catch (Exception e) { Con.Warn(e.Message); set(Con.PromptKey(name, "")); }
        }

        var probe = new Probe(cfg);
        Con.Head("ゲーム攻略くん Phase 0 検証ツール v2 (Phase0Probe)");
        Con.Info("画面取得と入力方式の実現性だけを調べます。ゲームのメモリ読み取り・ファイル解析は一切行いません。");
        Con.Info("各テストの前に内容を表示して確認を取ります。結果は exe と同じフォルダの results\\ に保存されます。");
        Con.Info($"保存先: {probe.Report.Dir}");

        while (true)
        {
            Con.Head("メニュー");
            Console.WriteLine($"  対象: {probe.TargetLabel}");
            Console.WriteLine($"  キー: 開く={Desc(cfg.OpenKey)}  閉じる={Desc(cfg.CloseKey)}  ①のWM_CHAR: {probe.CharModeLabel}");
            Console.WriteLine("  [L] ウィンドウ一覧  [S] 対象選択  [K] キー/しきい値の設定  [P] トルネコ既定キー配置の表示/適用");
            Console.WriteLine("  [1] T1 キャプチャ(前面/背面)        [2] T2 入力 ①a/①b/②/③ × 前面/背面");
            Console.WriteLine("  [3] T3 最小化                        [4] T4 ユーザー操作検出→ループ停止");
            Console.WriteLine("  [5] T5 ダンジョン内 ①背面移動        [6] T6 背面長時間（既定5分）");
            Console.WriteLine("  [A] 1→2→3→5→6 を順に実行（各テスト前に確認あり。4は個別に）");
            Console.WriteLine("  [R] レポートを保存してフォルダを開く  [Q] 終了");
            var c = Con.Prompt("選択").ToUpperInvariant();
            try
            {
                switch (c)
                {
                    case "L": probe.ListWindows(); break;
                    case "S": probe.SelectTarget(); break;
                    case "K": probe.ConfigureKeys(); break;
                    case "P": probe.ShowPreset(); break;
                    case "1": probe.Test1Capture(); break;
                    case "2": probe.Test2Input(); break;
                    case "3": probe.Test3Minimize(); break;
                    case "4": probe.Test4UserInput(); break;
                    case "5": probe.Test5DungeonMove(); break;
                    case "6": probe.Test6LongRun(); break;
                    case "A": probe.Test1Capture(); probe.Test2Input(); probe.Test3Minimize(); probe.Test5DungeonMove(); probe.Test6LongRun(); break;
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

    private static string Desc(string k) { try { return KeySpec.Parse(k).ToString(); } catch { return k.Length > 0 ? k + "(解釈不可)" : "(未設定)"; } }
}
