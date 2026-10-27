using System.Diagnostics;

namespace GameKouryakuKun.Phase0;

/// <summary>4つの検証テストの本体。</summary>
public sealed class Probe : IDisposable
{
    private readonly Config _cfg;
    public Report Report { get; }
    private IntPtr _target;
    private VigemBackend? _vigem;

    public Probe(Config cfg)
    {
        _cfg = cfg;
        Report = new Report { Config = cfg };
        Report.Dir = Path.Combine(AppContext.BaseDirectory, "results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var (wgcOk, os) = WgcSession.Supported();
        Report.Environment["OS"] = os;
        Report.Environment["OSVersion"] = Environment.OSVersion.Version.ToString();
        Report.Environment["64bitOS"] = Environment.Is64BitOperatingSystem;
        Report.Environment["WGC GraphicsCaptureSession.IsSupported"] = wgcOk;
        Report.Environment["本ツールの管理者権限"] = Environment.IsPrivilegedProcess;
        Report.Environment["Runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        Report.Environment["Screen"] = string.Join(", ", System.Windows.Forms.Screen.AllScreens.Select(s => $"{s.DeviceName} {s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? " primary" : "")}"));
        if (cfg.TargetQuery.Length > 0)
        {
            var found = WindowFinder.Find(cfg.TargetQuery);
            if (found.Count == 1) SetTarget(found[0]);
        }
    }

    public string TargetLabel => _target == IntPtr.Zero ? "(未選択)" : WindowFinder.Describe(_target).ToString();

    // ------------------------------------------------------------ 共通
    private string Img(string name) => name; // レポートからの相対パス
    private string ImgPath(string name) => Path.Combine(Report.Dir, name);

    private TestResult Begin(string id, string name)
    {
        var r = new TestResult { Id = id, Name = name };
        Report.Results.RemoveAll(x => x.Id == id);
        Report.Results.Add(r);
        return r;
    }

    private void End(TestResult r)
    {
        r.FinishedAt = DateTime.Now;
        SaveReport(false);
        var line = $"[{r.Id}] {Report.VerdictJa(r.Verdict)}  {r.Summary}";
        if (r.Verdict is "OK") Con.Ok(line); else if (r.Verdict is "NG" or "ABORTED") Con.Err(line); else Con.Warn(line);
    }

    public void SaveReport(bool open)
    {
        Report.Save();
        Con.Info($"レポート保存: {Path.Combine(Report.Dir, "report.md")}");
        if (open) try { Process.Start(new ProcessStartInfo("explorer.exe", Report.Dir) { UseShellExecute = true }); } catch { }
    }

    public void RecordError(string menu, Exception e)
    {
        var r = new TestResult { Id = "ERR-" + DateTime.Now.ToString("HHmmss"), Name = $"メニュー[{menu}]実行中のエラー", Verdict = "ABORTED", Summary = $"{e.GetType().Name}: {e.Message}" };
        r.Notes.Add(e.ToString());
        Report.Results.Add(r); r.FinishedAt = DateTime.Now; SaveReport(false);
    }

    private bool RequireTarget()
    {
        if (_target != IntPtr.Zero && Native.IsWindow(_target)) return true;
        Con.Warn("対象ウィンドウが未選択か、閉じられています。");
        SelectTarget();
        return _target != IntPtr.Zero;
    }

    private void SetTarget(WindowInfo w)
    {
        _target = w.Hwnd;
        Report.Target["hwnd"] = w.HwndHex; Report.Target["pid"] = w.Pid; Report.Target["process"] = w.ProcessName;
        Report.Target["title"] = w.Title; Report.Target["class"] = w.ClassName; Report.Target["rect"] = w.Rect;
        Native.GetClientRect(w.Hwnd, out var cr); Report.Target["clientSize"] = $"{cr.Width}x{cr.Height}";
        Con.Ok($"対象: {w}");
    }

    public void ListWindows()
    {
        Con.Head("ウィンドウ一覧（表示中のトップレベルウィンドウ）");
        var list = WindowFinder.List();
        for (int i = 0; i < list.Count; i++) Console.WriteLine($"  {i,3}: {list[i]}");
    }

    public void SelectTarget()
    {
        ListWindows();
        var q = Con.Prompt("一覧の番号、タイトルの一部、プロセス名（例: Torneko / トルネコ）、または 0x… のHWND", _cfg.TargetQuery);
        if (q.Length == 0) return;
        List<WindowInfo> cands;
        var all = WindowFinder.List();
        if (int.TryParse(q, out int idx) && idx >= 0 && idx < all.Count) cands = new() { all[idx] };
        else cands = WindowFinder.Find(q);
        if (cands.Count == 0) { Con.Warn("該当なし"); return; }
        WindowInfo pick = cands[0];
        if (cands.Count > 1)
        {
            for (int i = 0; i < cands.Count; i++) Console.WriteLine($"  {i}: {cands[i]}");
            var s = Con.Prompt("複数あります。番号を選択", "0");
            if (!int.TryParse(s, out int k) || k < 0 || k >= cands.Count) return;
            pick = cands[k];
        }
        if (pick.ProcessName.Equals("Phase0Probe", StringComparison.OrdinalIgnoreCase)) { Con.Warn("本ツール自身は対象にできません"); return; }
        _cfg.TargetQuery = q;
        SetTarget(pick);
    }

    public void ConfigureKeys()
    {
        Con.Head("キー設定");
        Con.Info("キー名の例: Escape, Enter, Space, Z, X, A, S, Up, Down, F1, Back, Tab, VK:0x1B");
        Con.Info("ゲーム内で「メニューを開く」キーと「閉じる」キーを指定してください（同じキーで開閉する場合は閉じるを空欄）。");
        while (true)
        {
            var k = Con.Prompt("メニューを開くキー", _cfg.OpenKey);
            try { var ks = KeySpec.Parse(k); _cfg.OpenKey = k; Con.Ok($"  → {ks}"); break; } catch (Exception e) { Con.Warn(e.Message); }
        }
        while (true)
        {
            var k = Con.Prompt("メニューを閉じるキー（空欄=開くキーと同じ）", _cfg.CloseKey);
            if (k.Length == 0) { _cfg.CloseKey = ""; break; }
            try { var ks = KeySpec.Parse(k); _cfg.CloseKey = k; Con.Ok($"  → {ks}"); break; } catch (Exception e) { Con.Warn(e.Message); }
        }
        Con.Info("仮想パッド用ボタン名の例: A, B, X, Y, Start, Back, Up, Down, Left, Right, LeftShoulder, RightShoulder");
        _cfg.PadOpenButton = Con.Prompt("パッドでメニューを開くボタン", _cfg.PadOpenButton);
        _cfg.PadCloseButton = Con.Prompt("パッドでメニューを閉じるボタン（空欄=開くと同じ）", _cfg.PadCloseButton);
        if (int.TryParse(Con.Prompt("キー押下時間 ms", _cfg.HoldMs.ToString()), out int h)) _cfg.HoldMs = Math.Clamp(h, 10, 1000);
        if (int.TryParse(Con.Prompt("入力後に画面変化を待つ時間 ms", _cfg.WaitAfterInputMs.ToString()), out int w)) _cfg.WaitAfterInputMs = Math.Clamp(w, 100, 10000);
        if (int.TryParse(Con.Prompt("フレームレート計測秒数", _cfg.RateSeconds.ToString()), out int r)) _cfg.RateSeconds = Math.Clamp(r, 1, 60);
        _cfg.PostMessageAlsoWmChar = Con.Confirm("①で WM_CHAR も併送しますか（文字キーのみ）", _cfg.PostMessageAlsoWmChar);
    }

    private bool EnsureKeys(bool needPad = false)
    {
        if (_cfg.OpenKey.Length == 0 || (needPad && _cfg.PadOpenButton.Length == 0))
        {
            Con.Warn("キー（またはパッドボタン）が未設定です。設定してください。");
            ConfigureKeys();
        }
        return _cfg.OpenKey.Length > 0;
    }

    private string CloseKey => _cfg.CloseKey.Length > 0 ? _cfg.CloseKey : _cfg.OpenKey;
    private string PadClose => _cfg.PadCloseButton.Length > 0 ? _cfg.PadCloseButton : _cfg.PadOpenButton;

    private static Dictionary<string, object?> StatsDict(FrameImage? img, string prefix)
    {
        var d = new Dictionary<string, object?>();
        if (img == null) { d[prefix + "画像"] = "取得できず"; return d; }
        var s = img.ComputeStats();
        d[prefix + "サイズ"] = $"{img.Width}x{img.Height}";
        d[prefix + "平均輝度"] = s.MeanLuma; d[prefix + "輝度標準偏差"] = s.StdLuma; d[prefix + "ほぼ黒画素率"] = s.NearBlackRatio;
        d[prefix + "真っ黒判定"] = s.LooksBlack; d[prefix + "単色判定"] = s.LooksUniform;
        return d;
    }

    private static void Merge(TestResult r, Dictionary<string, object?> d) { foreach (var kv in d) r.Data[kv.Key] = kv.Value; }

    private WgcSession? StartWgc(TestResult r)
    {
        try
        {
            var s = new WgcSession();
            s.Start(_target);
            var until = DateTime.Now.AddSeconds(3);
            while (s.TotalFrames == 0 && DateTime.Now < until) Thread.Sleep(50);
            r.Data["WGC開始"] = $"OK device={s.DeviceKind} {s.Notes} 初回フレーム={(s.TotalFrames > 0 ? "到着" : "3秒以内に未到着")}";
            return s;
        }
        catch (Exception e)
        {
            r.Data["WGC開始"] = $"失敗 {e.GetType().Name}: {e.Message}";
            r.Notes.Add("WGCのセッション開始に失敗。PrintWindowのみで判定します。");
            return null;
        }
    }

    /// <summary>WGC 最新フレームと PrintWindow の両方を取る</summary>
    private (FrameImage? wgc, FrameImage? pw, string pwDetail) Grab(WgcSession? s)
    {
        FrameImage? w = null;
        try { w = s?.Snapshot(); } catch (Exception e) { Con.Warn("WGC Snapshot失敗: " + e.Message); }
        var (p, d) = PrintWindowCapture.Capture(_target);
        return (w, p, d);
    }

    private void Save(TestResult r, FrameImage? img, string name)
    {
        if (img == null) return;
        img.SavePng(ImgPath(name)); r.Images.Add(Img(name));
    }

    // ------------------------------------------------------------ テスト1
    public void Test1Capture()
    {
        Con.Head("テスト1: WGC / PrintWindow キャプチャ（前面・背面）");
        Con.Info("内容: ①ゲームを前面にして数秒間フレームを数え、PNG保存 ②テスト用ダミー窓を前面に出し、ゲームを背面にして同じことを行う。");
        Con.Info("     WGC と比較用の PrintWindow(PW_RENDERFULLCONTENT) の両方で取得します。入力は一切送りません。");
        Con.Info("コツ: 動きのある画面（キャラが足踏みしている画面など）で実行すると、WGCのフレーム到着レートが正しく測れます。");
        if (!RequireTarget()) return;
        if (!Con.Confirm("テスト1を実行しますか")) { var sk = Begin("T1", "WGC/PrintWindow キャプチャ"); sk.Verdict = "SKIPPED"; sk.Summary = "ユーザーがスキップ"; End(sk); return; }
        var r = Begin("T1", "WGC/PrintWindow キャプチャ（前面・背面）");
        using var wgc = StartWgc(r);

        var results = new Dictionary<string, (bool wgcOk, bool pwOk, int frames)>();
        foreach (var mode in new[] { "前面", "背面" })
        {
            bool fgOk = mode == "前面" ? Foreground.Ensure(_target, "ゲームウィンドウ")
                                        : ShowDummyForeground("背面キャプチャ中…（ゲームは背面です）");
            r.Data[$"{mode}: 前面ウィンドウ"] = Foreground.Describe(Native.GetForegroundWindow());
            if (!fgOk) { r.Notes.Add($"{mode}: 想定どおりの前面状態にできなかったため、この計測は参考値です。"); }
            Thread.Sleep(500);
            var t0 = DateTime.Now;
            Con.Info($"  {mode}: {_cfg.RateSeconds}秒間フレームを数えています…");
            Thread.Sleep(_cfg.RateSeconds * 1000);
            var t1 = DateTime.Now;
            int frames = wgc?.FramesBetween(t0, t1) ?? 0;
            var (w, p, pd) = Grab(wgc);
            r.Data[$"{mode}: WGCフレーム数"] = frames;
            r.Data[$"{mode}: WGCフレームレート(fps)"] = Math.Round(frames / (t1 - t0).TotalSeconds, 2);
            r.Data[$"{mode}: WGC最終フレーム時刻"] = wgc?.LastFrameAt?.ToString("HH:mm:ss.fff");
            r.Data[$"{mode}: WGC ContentSize"] = wgc?.LastContentSize;
            r.Data[$"{mode}: PrintWindow"] = pd;
            Merge(r, StatsDict(w, $"{mode}: WGC "));
            Merge(r, StatsDict(p, $"{mode}: PW "));
            string tag = mode == "前面" ? "fg" : "bg";
            Save(r, w, $"T1_{tag}_wgc.png");
            Save(r, p, $"T1_{tag}_printwindow.png");
            bool wOk = w != null && !w.ComputeStats().LooksBlack;
            bool pOk = p != null && !p.ComputeStats().LooksBlack;
            results[mode] = (wOk, pOk, frames);
        }
        Dummy.Hide();

        var fg = results["前面"]; var bg = results["背面"];
        r.Data["判定: WGC背面で黒以外の画像"] = bg.wgcOk;
        r.Data["判定: WGC背面でフレーム更新あり"] = bg.frames > 0;
        r.Data["判定: PrintWindow背面で黒以外の画像"] = bg.pwOk;
        if (bg.wgcOk && bg.frames > 0) { r.Verdict = "OK"; r.Summary = $"WGCで背面キャプチャ可・更新継続（背面 {bg.frames}フレーム/{_cfg.RateSeconds}秒）"; }
        else if (bg.wgcOk) { r.Verdict = "PARTIAL"; r.Summary = "WGCで背面の画像は取れたが計測中にフレーム更新なし（静止画面か、背面で描画停止の可能性）"; }
        else if (bg.pwOk) { r.Verdict = "PARTIAL"; r.Summary = "WGC背面は不可、PrintWindowでは取得可"; }
        else { r.Verdict = "NG"; r.Summary = "背面キャプチャ不可（WGC・PrintWindowとも黒/取得不可）"; }
        if (fg.frames == 0) r.Notes.Add("前面でもフレーム更新が0でした。画面が静止していた可能性があります（WGCは画面が変化したときだけフレームを届けます）。動きのある画面で再実行してください。");
        r.Notes.Add("背面時はダミー窓がゲームの一部に重なっています。WGC画像にダミー窓が写っていなければ『隠れていても取得できる』ことの確認になります。");
        End(r);
    }

    private bool ShowDummyForeground(string status)
    {
        Dummy.Show(status);
        Thread.Sleep(300);
        return Foreground.Ensure(Dummy.Hwnd, "ダミー窓（ゲーム攻略くん Phase0 ダミー窓）");
    }

    // ------------------------------------------------------------ テスト2
    public void Test2Input()
    {
        Con.Head("テスト2: 入力方式 ①PostMessage ②仮想パッド ③SendInput × 前面・背面");
        Con.Info("内容: 各方式で「メニューを開くキー」を送り、送信前後のキャプチャ差分で受理を判定 → 「閉じるキー」で元に戻す。");
        Con.Info("準備: ゲームを『メニューが閉じていて、キーを押すとメニューが開く』状態（ダンジョン内で安全な場面）にしてください。");
        Con.Info("背面テストでは、テスト用ダミー窓を前面に出してから送信します（あなたの他のアプリには送りません）。");
        if (!RequireTarget() || !EnsureKeys()) return;
        Con.Info($"開くキー={KeySpec.Parse(_cfg.OpenKey)}  閉じるキー={KeySpec.Parse(CloseKey)}");

        var pm = new PostMessageBackend { AlsoSendChar = _cfg.PostMessageAlsoWmChar };
        RunBackend("T2-1", pm, _cfg.OpenKey, CloseKey,
            "①PostMessage: 対象ウィンドウのメッセージキューへ直接 WM_KEYDOWN/UP を置きます。OS全体の入力には一切影響しません。");

        // ②
        Con.Head("②仮想パッド (ViGEmBus / XInput)");
        Con.Info("ViGEmBus ドライバが導入済みなら、仮想Xbox360パッドを1台接続してボタンを押します。未導入なら利用不可としてスキップします。");
        Con.Warn("注意: 仮想パッドはシステム全体のデバイスです。パッドを読む他のアプリ（ブラウザのGamepad API等）にも見える可能性があります。");
        if (Con.Confirm("②のテストを行いますか（ViGEmBusの有無を確認し、あれば仮想パッドを接続します）"))
        {
            _vigem ??= new VigemBackend();
            var (ok, detail) = _vigem.Availability();
            if (!ok)
            {
                var r = Begin("T2-2", "入力②仮想パッド(ViGEmBus/XInput)");
                r.Verdict = "SKIPPED"; r.Summary = "利用不可: " + detail; r.Data["ViGEmBus"] = detail; End(r);
            }
            else
            {
                Con.Ok(detail);
                if (_cfg.PadOpenButton.Length == 0) _cfg.PadOpenButton = Con.Prompt("パッドでメニューを開くボタン（例: Y, B, Start）", "Y");
                if (_cfg.PadCloseButton.Length == 0) _cfg.PadCloseButton = Con.Prompt("パッドでメニューを閉じるボタン（空欄=開くと同じ）", "");
                RunBackend("T2-2", _vigem, _cfg.PadOpenButton, PadClose, "②仮想パッド: 接続済みの仮想Xbox360パッドのボタンを押します。", skipConfirm: true);
            }
        }
        else { var r = Begin("T2-2", "入力②仮想パッド(ViGEmBus/XInput)"); r.Verdict = "SKIPPED"; r.Summary = "ユーザーがスキップ"; End(r); }

        // ③
        var si = new SendInputBackend();
        RunBackend("T2-3", si, _cfg.OpenKey, CloseKey,
            "③SendInput: OS全体の入力キューにキーを注入します（前面ウィンドウに届く方式）。\n" +
            "  前面テスト: ゲームを前面にして送信。背面テスト: ダミー窓を前面にして送信（＝ゲームには届かないはず、ダミー窓が受け取ることを確認）。\n" +
            "  送信直前に前面ウィンドウを再確認し、想定外のウィンドウが前面なら送信を中止します。テスト中はキーボード・マウスに触れないでください。");
    }

    private void RunBackend(string id, IProbeInputBackend be, string openKey, string closeKey, string explain, bool skipConfirm = false)
    {
        Con.Head(be.Label);
        Con.Info(explain);
        if (!skipConfirm && !Con.Confirm($"{be.Label} のテスト（前面→背面）を実行しますか"))
        {
            var sk = Begin(id, "入力" + be.Label); sk.Verdict = "SKIPPED"; sk.Summary = "ユーザーがスキップ"; End(sk); return;
        }
        var r = Begin(id, "入力" + be.Label);
        r.Data["方式の可用性"] = be.Availability().Item2;
        using var mon = InputMonitor.StartOnUi();
        using var wgc = StartWgc(r);
        var verdicts = new Dictionary<string, string>();
        foreach (var mode in new[] { "前面", "背面" })
        {
            if (mode == "背面" && verdicts.GetValueOrDefault("前面") == "要復旧")
            {
                if (!Con.Confirm("前面テスト後にゲーム画面が元に戻っていない可能性があります。ゲームを元の状態に戻してから y を入力してください。続行しますか")) break;
            }
            verdicts[mode] = RunInputCase(r, be, mode, openKey, closeKey, wgc, mon);
        }
        Dummy.Hide();
        r.Data["物理入力検出(テスト中)"] = $"LLフック物理キー={mon.Snapshot().HookKeyPhysical} 物理マウス={mon.Snapshot().HookMousePhysical}";
        if (mon.Snapshot().HookKeyPhysical + mon.Snapshot().HookMousePhysical > 0) r.Notes.Add("テスト中に物理入力が検出されました。判定に影響している可能性があります。");
        bool fgAcc = verdicts.GetValueOrDefault("前面") is "受理" or "要復旧";
        bool bgAcc = verdicts.GetValueOrDefault("背面") is "受理" or "要復旧";
        r.Data["判定: 前面で受理"] = fgAcc; r.Data["判定: 背面で受理"] = bgAcc;
        if (be is SendInputBackend)
        {
            r.Verdict = fgAcc ? (bgAcc ? "PARTIAL" : "OK") : "NG";
            r.Summary = fgAcc ? (bgAcc ? "前面で受理。背面でもゲームが変化（要目視確認：ダミー窓が前面だったか）" : "前面で受理・背面ではゲームに届かず（想定どおり：SendInputは前面専用）")
                              : "前面でも受理されず（UIPI/入力取得方式の問題の可能性）";
        }
        else
        {
            r.Verdict = bgAcc ? "OK" : fgAcc ? "PARTIAL" : "NG";
            r.Summary = bgAcc ? $"背面でも受理（前面={(fgAcc ? "受理" : "不受理")}）" : fgAcc ? "前面のみ受理・背面では反応なし" : "前面・背面とも反応なし";
        }
        End(r);
    }

    /// <returns>"受理" / "要復旧"(受理したが元に戻らず) / "不受理" / "中止"</returns>
    private string RunInputCase(TestResult r, IProbeInputBackend be, string mode, string openKey, string closeKey, WgcSession? wgc, InputMonitor mon)
    {
        string tag = mode == "前面" ? "fg" : "bg";
        string P(string s) => $"{mode}: {s}";
        Dummy.Reset();
        bool ok = mode == "前面" ? Foreground.Ensure(_target, "ゲームウィンドウ")
                                 : ShowDummyForeground($"{be.Label} 背面テスト中…（キーボード・マウスに触れないでください）");
        var expectedFg = mode == "前面" ? _target : Dummy.Hwnd;
        r.Data[P("前面ウィンドウ(開始時)")] = Foreground.Describe(Native.GetForegroundWindow());
        if (!ok)
        {
            r.Data[P("結果")] = "中止: 想定どおりの前面状態にできなかった";
            Con.Err($"  {mode}: 前面状態を準備できなかったため中止しました。");
            return "中止";
        }
        if (be is SendInputBackend sib) sib.RequiredForeground = expectedFg;
        var before0 = mon.Snapshot();
        Thread.Sleep(700);
        var (wA, pA, _) = Grab(wgc);
        Thread.Sleep(500);
        var (wB, pB, _) = Grab(wgc);
        var noiseW = FrameImage.Compare(wA, wB, _cfg.PixelThreshold);
        var noiseP = FrameImage.Compare(pA, pB, _cfg.PixelThreshold);
        double thrW = Math.Max(_cfg.MinChangedRatio, noiseW.ChangedRatio * 3 + 0.002);
        double thrP = Math.Max(_cfg.MinChangedRatio, noiseP.ChangedRatio * 3 + 0.002);

        Con.Info($"  {mode}: 開くキー/ボタン [{openKey}] を送信");
        var t0 = DateTime.Now;
        r.Data[P("送信(開)")] = be.Press(_target, openKey, _cfg.HoldMs);
        Thread.Sleep(_cfg.WaitAfterInputMs);
        var (wC, pC, _) = Grab(wgc);
        r.Data[P("送信後WGCフレーム数")] = wgc?.FramesBetween(t0, DateTime.Now);
        var dOpenW = FrameImage.Compare(wB, wC, _cfg.PixelThreshold, wB != null && wC != null ? ImgPath($"{be.Id}_{tag}_diff_open_wgc.png") : null);
        var dOpenP = FrameImage.Compare(pB, pC, _cfg.PixelThreshold);
        if (wB != null && wC != null) r.Images.Add(Img($"{be.Id}_{tag}_diff_open_wgc.png"));

        Con.Info($"  {mode}: 閉じるキー/ボタン [{closeKey}] を送信");
        r.Data[P("送信(閉)")] = be.Press(_target, closeKey, _cfg.HoldMs);
        Thread.Sleep(_cfg.WaitAfterInputMs);
        var (wD, pD, _) = Grab(wgc);
        var dCloseW = FrameImage.Compare(wB, wD, _cfg.PixelThreshold);
        var dCloseP = FrameImage.Compare(pB, pD, _cfg.PixelThreshold);

        var fgEnd = Native.GetForegroundWindow();
        var dummyRecv = Dummy.Received();
        var after0 = mon.Snapshot();

        Save(r, wB ?? pB, $"{be.Id}_{tag}_1before.png");
        Save(r, wC ?? pC, $"{be.Id}_{tag}_2after_open.png");
        Save(r, wD ?? pD, $"{be.Id}_{tag}_3after_close.png");

        r.Data[P("ノイズ(無入力)変化率 WGC/PW")] = $"{noiseW.ChangedRatio} / {noiseP.ChangedRatio}";
        r.Data[P("判定しきい値 WGC/PW")] = $"{Math.Round(thrW, 5)} / {Math.Round(thrP, 5)}";
        r.Data[P("開いた後の変化率 WGC/PW")] = $"{Fmt(dOpenW)} / {Fmt(dOpenP)}";
        r.Data[P("閉じた後(送信前比)の変化率 WGC/PW")] = $"{Fmt(dCloseW)} / {Fmt(dCloseP)}";
        r.Data[P("前面ウィンドウ(終了時)")] = Foreground.Describe(fgEnd);
        r.Data[P("前面状態が維持された")] = fgEnd == expectedFg;
        r.Data[P("ダミー窓が受け取ったキー")] = dummyRecv.count > 0 ? string.Join(" ", dummyRecv.keys) : "なし";
        r.Data[P("LLフックで見えた自分の注入キー数")] = after0.HookKeyInjectedOwn - before0.HookKeyInjectedOwn;
        r.Data[P("テスト中の物理入力(キー/マウス)")] = $"{after0.HookKeyPhysical - before0.HookKeyPhysical}/{after0.HookMousePhysical - before0.HookMousePhysical}";

        bool openW = dOpenW.Comparable && dOpenW.ChangedRatio > thrW;
        bool openP = dOpenP.Comparable && dOpenP.ChangedRatio > thrP;
        bool accepted = openW || openP;
        bool revertedW = !dCloseW.Comparable || dCloseW.ChangedRatio <= thrW;
        bool revertedP = !dCloseP.Comparable || dCloseP.ChangedRatio <= thrP;
        bool reverted = (openW ? revertedW : true) && (openP ? revertedP : true);
        string v = !accepted ? "不受理" : reverted ? "受理" : "要復旧";
        r.Data[P("結果")] = v switch
        {
            "受理" => $"受理（画面が変化し、閉じるで元に戻った）検出={(openW ? "WGC " : "")}{(openP ? "PW" : "")}",
            "要復旧" => "受理（画面は変化したが元に戻らなかった → 画像で目視確認）",
            _ => "反応なし（しきい値を超える画面変化なし）",
        };
        if (mode == "背面" && dummyRecv.count > 0 && be is not SendInputBackend)
            r.Notes.Add($"{mode}: {be.Label} の送信中にダミー窓（前面）がキーを受信しました: {string.Join(" ", dummyRecv.keys)}（前面アプリへの漏れの疑い）");
        if (fgEnd != expectedFg) r.Notes.Add($"{mode}: テスト中に前面ウィンドウが変わりました（{Foreground.Describe(fgEnd)}）。");
        (v == "不受理" ? (Action<string>)Con.Warn : Con.Ok)($"  {mode}: {r.Data[P("結果")]}");
        return v;
    }

    private static string Fmt(FrameImage.Diff d) => d.Comparable ? d.ChangedRatio.ToString("0.#####") : $"比較不可({d.Note})";

    // ------------------------------------------------------------ テスト3
    public void Test3Minimize()
    {
        Con.Head("テスト3: 最小化時の挙動");
        Con.Info("内容: ゲームを最小化し、その間の WGC フレーム到着・画像（黒画面/停止）と PrintWindow の結果を記録。");
        Con.Info("     さらに最小化中に①PostMessageで開くキーを送り、元に戻した後の画面でメニューが開いたかを確認、最後に閉じるキーを送ります。");
        Con.Info("準備: テスト2と同じく『メニューが閉じた安全な場面』にしてください。");
        if (!RequireTarget() || !EnsureKeys()) return;
        if (!Con.Confirm("テスト3を実行しますか")) { var sk = Begin("T3", "最小化時の挙動"); sk.Verdict = "SKIPPED"; sk.Summary = "ユーザーがスキップ"; End(sk); return; }
        bool auto = Con.Confirm("最小化をツールが自動で行いますか（n = 自分で最小化ボタンを押す）", true);
        var r = Begin("T3", "最小化時の挙動（WGC/PrintWindow/①入力）");
        r.Data["最小化方法"] = auto ? "ShowWindow(SW_MINIMIZE)" : "ユーザー手動";
        if (Native.IsIconic(_target)) Native.ShowWindow(_target, Native.SW_RESTORE);
        Foreground.Ensure(_target, "ゲームウィンドウ");
        using var wgc = StartWgc(r);
        Thread.Sleep(800);

        var t0 = DateTime.Now; Thread.Sleep(_cfg.RateSeconds * 1000); var t1 = DateTime.Now;
        int framesNormal = wgc?.FramesBetween(t0, t1) ?? 0;
        var (w0, p0, pd0) = Grab(wgc);
        r.Data["通常時: WGCフレーム数"] = framesNormal;
        r.Data["通常時: PrintWindow"] = pd0;
        Save(r, w0, "T3_0normal_wgc.png"); Save(r, p0, "T3_0normal_printwindow.png");

        if (auto) Native.ShowWindow(_target, Native.SW_MINIMIZE);
        else
        {
            Con.Warn("20秒以内にゲームウィンドウを最小化してください…");
            var until = DateTime.Now.AddSeconds(20);
            while (!Native.IsIconic(_target) && DateTime.Now < until) Thread.Sleep(100);
        }
        if (!Native.IsIconic(_target)) { r.Verdict = "ABORTED"; r.Summary = "最小化を確認できなかった"; End(r); return; }
        var tMin = DateTime.Now;
        r.Data["最小化確認時刻"] = tMin.ToString("HH:mm:ss.fff");
        Thread.Sleep(1000);
        var t2 = DateTime.Now; Con.Info($"  最小化中: {_cfg.RateSeconds}秒間フレームを数えています…"); Thread.Sleep(_cfg.RateSeconds * 1000); var t3 = DateTime.Now;
        int framesMin = wgc?.FramesBetween(t2, t3) ?? 0;
        var (w1, p1, pd1) = Grab(wgc);
        r.Data["最小化中: WGCフレーム数"] = framesMin;
        r.Data["最小化中: WGC最終フレーム時刻"] = wgc?.LastFrameAt?.ToString("HH:mm:ss.fff");
        r.Data["最小化中: WGC ContentSize"] = wgc?.LastContentSize;
        r.Data["最小化中: WGCセッション終了(ItemClosed)"] = wgc?.ItemClosed;
        r.Data["最小化中: PrintWindow"] = pd1;
        Merge(r, StatsDict(w1, "最小化中: WGC "));
        Merge(r, StatsDict(p1, "最小化中: PW "));
        Save(r, w1, "T3_1minimized_wgc.png"); Save(r, p1, "T3_1minimized_printwindow.png");

        Con.Info($"  最小化中に①PostMessageで [{_cfg.OpenKey}] を送信");
        var pm = new PostMessageBackend { AlsoSendChar = _cfg.PostMessageAlsoWmChar };
        var t4 = DateTime.Now;
        r.Data["最小化中: ①送信(開)"] = pm.Press(_target, _cfg.OpenKey, _cfg.HoldMs);
        Thread.Sleep(Math.Max(_cfg.WaitAfterInputMs, 1500));
        r.Data["最小化中: ①送信後のWGCフレーム数"] = wgc?.FramesBetween(t4, DateTime.Now);
        var (_, p2, pd2) = Grab(wgc);
        r.Data["最小化中: ①送信後PrintWindow"] = pd2;
        Save(r, p2, "T3_2minimized_after_key_printwindow.png");

        Native.ShowWindow(_target, Native.SW_SHOWNOACTIVATE);
        Thread.Sleep(1500);
        var (w3, p3, _) = Grab(wgc);
        var dRestW = FrameImage.Compare(w0, w3, _cfg.PixelThreshold, w0 != null && w3 != null ? ImgPath("T3_3restored_diff_wgc.png") : null);
        var dRestP = FrameImage.Compare(p0, p3, _cfg.PixelThreshold);
        if (w0 != null && w3 != null) r.Images.Add(Img("T3_3restored_diff_wgc.png"));
        Save(r, w3 ?? p3, "T3_3restored.png");
        r.Data["復元後: 最小化前との変化率 WGC/PW"] = $"{Fmt(dRestW)} / {Fmt(dRestP)}";
        r.Data["復元後: WGCフレーム到着(復元以降)"] = wgc?.FramesBetween(t4, DateTime.Now);

        Con.Info($"  復元後に①PostMessageで閉じるキー [{CloseKey}] を送信（メニューが開いていれば閉じる）");
        bool menuLikelyOpen = (dRestW.Comparable && dRestW.ChangedRatio > _cfg.MinChangedRatio * 2) || (dRestP.Comparable && dRestP.ChangedRatio > _cfg.MinChangedRatio * 2);
        if (menuLikelyOpen) r.Data["復元後: ①送信(閉)"] = pm.Press(_target, CloseKey, _cfg.HoldMs);
        else r.Data["復元後: ①送信(閉)"] = "画面変化が小さいため送信せず（メニューは開いていないと推定）";
        Thread.Sleep(_cfg.WaitAfterInputMs);
        var (w4, p4, _) = Grab(wgc);
        Save(r, w4 ?? p4, "T3_4after_close.png");

        bool wgcStopped = framesMin == 0;
        bool wgcBlack = w1 == null || w1.ComputeStats().LooksBlack;
        bool pwBlack = p1 == null || p1.ComputeStats().LooksBlack;
        r.Data["判定: 最小化中WGCフレーム停止"] = wgcStopped;
        r.Data["判定: 最小化中WGC画像が黒/取得不可"] = wgcBlack;
        r.Data["判定: 最小化中PrintWindow画像が黒/取得不可"] = pwBlack;
        r.Data["判定: 最小化中の①入力が処理された(復元後に画面変化)"] = menuLikelyOpen;
        r.Verdict = "INFO";
        r.Summary = $"最小化中 WGC={(wgcStopped ? "フレーム停止" : $"{framesMin}フレーム到着")}{(wgcBlack ? "・黒/取得不可" : "")}, PrintWindow={(pwBlack ? "黒/取得不可" : "取得可")}, ①入力={(menuLikelyOpen ? "処理された可能性あり（画像で確認）" : "反映なし")}";
        r.Notes.Add("最小化中は多くのゲームが描画を止めます。WGCが最後のフレームを保持し続けるだけの場合、画像は黒くなくても『止まった画』です（最終フレーム時刻と最小化時刻を比較してください）。");
        r.Notes.Add("①入力判定は『復元後の画面が最小化前と異なるか』で推定しています。ゲーム自身のアニメーションでも変化するため、T3_3restored.png を目視確認してください。");
        End(r);
    }

    // ------------------------------------------------------------ テスト4
    public void Test4PhysicalInput()
    {
        Con.Head("テスト4: 物理入力検出 → AI入力ループ停止デモ");
        Con.Info("内容: 低レベルフック(WH_KEYBOARD_LL/WH_MOUSE_LL)と RawInput(RIDEV_INPUTSINK) を同時に使い、物理入力と自分の注入を区別します。");
        Con.Info("  1) 校正(5秒): ダミー窓を前面にし、AI入力ループが無害キー F24 を署名付き SendInput で注入。キーボード・マウスに触れないでください。");
        Con.Info("     → 注入が『自分の注入』と分類され、物理入力として誤検知されないことを確認します。");
        Con.Info("  2) 本番(最大30秒): ループ継続中に、あなたが任意のキーを押すかマウスを動かすと、即座にループが停止することを確認します。");
        Con.Info("  フックは観測のみで、あなたの入力を遮ったり書き換えたりはしません。");
        if (!Con.Confirm("テスト4を実行しますか")) { var sk = Begin("T4", "物理入力検出"); sk.Verdict = "SKIPPED"; sk.Summary = "ユーザーがスキップ"; End(sk); return; }
        var r = Begin("T4", "物理入力検出 → AI入力ループ停止");
        using var mon = InputMonitor.StartOnUi();
        r.Data["フック"] = mon.HookStatus; r.Data["RawInput"] = mon.RawStatus;

        volatile_bool stop = new();
        string? firstSource = null, firstDesc = null; DateTime? detectedAt = null;
        bool armed = false;
        int falsePositives = 0;
        mon.PhysicalInput += (src, desc) =>
        {
            if (!armed) { Interlocked.Increment(ref falsePositives); return; }
            if (stop.Value) return;
            detectedAt = DateTime.Now; firstSource = src; firstDesc = desc; stop.Value = true;
        };

        bool dummyFg = ShowDummyForeground("テスト4: 校正中（5秒間キーボード・マウスに触れないでください）");
        r.Data["ダミー窓が前面"] = dummyFg;
        if (!dummyFg) r.Notes.Add("ダミー窓を前面にできなかったため、SendInputによる注入は行わずPostMessageのみでループします。");
        var dh = Dummy.Hwnd;
        int injected = 0, posted = 0;
        void LoopStep()
        {
            if (dummyFg && Native.GetForegroundWindow() == dh) { SendInputBackend.InjectHarmless(); injected++; }
            Native.PostMessageW(dh, Native.WM_KEYDOWN, (IntPtr)0x87, PostMessageBackend.MakeLParam(KeySpec.Parse("F24"), false));
            Native.PostMessageW(dh, Native.WM_KEYUP, (IntPtr)0x87, PostMessageBackend.MakeLParam(KeySpec.Parse("F24"), true));
            posted++;
        }
        Thread.Sleep(500);
        Interlocked.Exchange(ref falsePositives, 0); // 前面化のためのクリック等はカウントしない
        var c0 = mon.Snapshot();
        Con.Info("  校正中… 5秒間キーボード・マウスに触れないでください");
        var end = DateTime.Now.AddSeconds(5);
        while (DateTime.Now < end) { LoopStep(); Thread.Sleep(200); }
        Thread.Sleep(200);
        var c1 = mon.Snapshot();
        r.Data["校正: 注入回数(SendInput F24)"] = injected;
        r.Data["校正: LLフック 自分の注入(キー)"] = c1.HookKeyInjectedOwn - c0.HookKeyInjectedOwn;
        r.Data["校正: LLフック 他者の注入(キー)"] = c1.HookKeyInjectedOther - c0.HookKeyInjectedOther;
        r.Data["校正: LLフック 物理(キー/マウス)"] = $"{c1.HookKeyPhysical - c0.HookKeyPhysical}/{c1.HookMousePhysical - c0.HookMousePhysical}";
        r.Data["校正: RawInput hDevice=0(注入)キー"] = c1.RawKeyNoDevice - c0.RawKeyNoDevice;
        r.Data["校正: RawInput hDevice≠0(実デバイス) キー/マウス"] = $"{c1.RawKeyDevice - c0.RawKeyDevice}/{c1.RawMouseDevice - c0.RawMouseDevice}";
        r.Data["校正: 物理入力として検知された回数"] = falsePositives;
        bool calibClean = falsePositives == 0;
        bool ownRecognized = !dummyFg || (c1.HookKeyInjectedOwn - c0.HookKeyInjectedOwn) >= injected; // down+up で2倍になるのが正常

        Dummy.Show("テスト4: 本番 — 任意のキーを押すか、マウスを動かしてください（AIループが止まります）");
        Con.Warn("  本番: 今から30秒以内に、キーボードのキーを1つ押すか、マウスを動かしてください。");
        armed = true;
        var loopStart = DateTime.Now; int loops = 0;
        var deadline = DateTime.Now.AddSeconds(30);
        DateTime? stoppedAt = null;
        while (DateTime.Now < deadline)
        {
            if (stop.Value) { stoppedAt = DateTime.Now; break; }
            LoopStep(); loops++;
            for (int i = 0; i < 10 && !stop.Value; i++) Thread.Sleep(20); // 200ms周期だが20ms毎に停止確認
        }
        armed = false;
        if (stop.Value && stoppedAt == null) stoppedAt = DateTime.Now;
        var c2 = mon.Snapshot();
        Dummy.Hide();

        r.Data["本番: ループ回数"] = loops;
        r.Data["本番: 最初に検知した系統"] = firstSource;
        r.Data["本番: 検知内容"] = firstDesc;
        r.Data["本番: 検知→ループ停止の遅延(ms)"] = detectedAt != null && stoppedAt != null ? Math.Round((stoppedAt.Value - detectedAt.Value).TotalMilliseconds, 1) : null;
        r.Data["本番: LLフック初回物理検知"] = mon.FirstPhysicalHook?.ToString("HH:mm:ss.fff");
        r.Data["本番: RawInput初回物理検知"] = mon.FirstPhysicalRaw?.ToString("HH:mm:ss.fff");
        r.Data["累計: LLフック 物理キー/物理マウス/自分の注入キー/他者の注入キー"] = $"{c2.HookKeyPhysical}/{c2.HookMousePhysical}/{c2.HookKeyInjectedOwn}/{c2.HookKeyInjectedOther}";
        r.Data["累計: RawInput キー(実/注入) マウス(実/注入)"] = $"{c2.RawKeyDevice}/{c2.RawKeyNoDevice} {c2.RawMouseDevice}/{c2.RawMouseNoDevice}";
        r.Data["累計: RawInputで見えたデバイス"] = string.Join(", ", c2.RawDevices);

        if (!stop.Value) { r.Verdict = "NG"; r.Summary = "30秒以内に物理入力を検知できなかった（または入力がなかった）"; }
        else if (calibClean && ownRecognized) { r.Verdict = "OK"; r.Summary = $"物理入力を検知しループ停止（{firstSource}: {firstDesc}）。自分の注入は物理と誤認せず"; }
        else { r.Verdict = "PARTIAL"; r.Summary = $"物理入力でループ停止はできたが、校正中に誤検知{falsePositives}件/自分の注入の識別が不完全"; }
        if (!calibClean) r.Notes.Add("校正中に物理入力が検知されました。触れていない場合は、他ソフト（マウスユーティリティ等）による入力か、ノイズの可能性があります。");
        r.Notes.Add("①PostMessage はOSの入力キューを通らないため、フックにもRawInputにも現れません（＝物理入力と混同する余地がない）。");
        End(r);
    }

    private sealed class volatile_bool { private volatile bool _v; public bool Value { get => _v; set => _v = value; } }

    public void Dispose() { _vigem?.Dispose(); }
}
