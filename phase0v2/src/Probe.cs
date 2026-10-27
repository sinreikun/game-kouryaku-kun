using System.Diagnostics;

namespace GameKouryakuKun.Phase0;

/// <summary>検証テスト本体（共通部・T1〜T4）。T5/T6 は ProbeT5T6.cs。</summary>
public sealed partial class Probe : IDisposable
{
    private readonly Config _cfg;
    public Report Report { get; }
    private IntPtr _target;
    private VigemBackend? _vigem;
    /// <summary>①で WM_CHAR を併送するか。T2 の結果で自動決定（null = 未決定、既定はオフ）</summary>
    private bool? _charMode;
    private string _charModeReason = "未決定（T2未実施のため既定=オフ）";

    public string CharModeLabel => $"{(_charMode == true ? "オン" : "オフ")}（{_charModeReason}）";
    private bool CharMode => _charMode == true;

    public Probe(Config cfg)
    {
        _cfg = cfg;
        Report = new Report { Config = cfg };
        Report.Dir = Path.Combine(AppContext.BaseDirectory, "results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var (wgcOk, os) = WgcSession.Supported();
        Report.Environment["OS"] = os;
        Report.Environment["WGC GraphicsCaptureSession.IsSupported"] = wgcOk;
        Report.Environment["本ツールの管理者権限"] = Environment.IsPrivilegedProcess;
        Report.Environment["Runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        Report.Environment["Screen"] = Monitors();
        Report.Environment["NumLock(起動時, 参考値)"] = Native.NumLockOn ? "ON" : "OFF";
        if (cfg.TargetQuery.Length > 0)
        {
            var found = WindowFinder.Find(cfg.TargetQuery);
            if (found.Count == 1) SetTarget(found[0]);
        }
    }

    private static string Monitors()
    {
        var list = new List<string>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT r, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEXW { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEXW>() };
            if (Native.GetMonitorInfoW(h, ref mi)) list.Add($"{mi.szDevice} {mi.rcMonitor.Width}x{mi.rcMonitor.Height}{((mi.dwFlags & 1) != 0 ? " primary" : "")}");
            return true;
        }, IntPtr.Zero);
        return string.Join(", ", list);
    }

    public string TargetLabel => _target == IntPtr.Zero ? "(未選択)" : WindowFinder.Describe(_target).ToString();

    // ------------------------------------------------------------ 共通
    private string ImgPath(string name) => Path.Combine(Report.Dir, name);

    private TestResult Begin(string id, string name)
    {
        var r = new TestResult { Id = id, Name = name };
        Report.Results.RemoveAll(x => x.Id == id);
        Report.Results.Add(r);
        return r;
    }

    private void Skip(string id, string name, string why = "ユーザーがスキップ")
    {
        var r = Begin(id, name); r.Verdict = "SKIPPED"; r.Summary = why; End(r);
    }

    private void End(TestResult r)
    {
        r.FinishedAt = DateTime.Now;
        SaveReport(false);
        var line = $"[{r.Id}] {Report.VerdictJa(r.Verdict)}  {r.Summary}";
        if (r.Verdict is "OK") Con.Ok(line); else if (r.Verdict is "NG" or "ABORTED") Con.Err(line); else Con.Warn(line);
        foreach (var w in r.Warnings) Con.Warn("  警告: " + w);
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
        var all = WindowFinder.List();
        var cands = int.TryParse(q, out int idx) && idx >= 0 && idx < all.Count ? new List<WindowInfo> { all[idx] } : WindowFinder.Find(q);
        if (cands.Count == 0) { Con.Warn("該当なし"); return; }
        var pick = cands[0];
        if (cands.Count > 1)
        {
            for (int i = 0; i < cands.Count; i++) Console.WriteLine($"  {i}: {cands[i]}");
            if (!int.TryParse(Con.Prompt("複数あります。番号を選択", "0"), out int k) || k < 0 || k >= cands.Count) return;
            pick = cands[k];
        }
        if (pick.ProcessName.Equals("Phase0Probe", StringComparison.OrdinalIgnoreCase)) { Con.Warn("本ツール自身は対象にできません"); return; }
        _cfg.TargetQuery = q;
        SetTarget(pick);
    }

    public void ShowPreset()
    {
        Con.Head("トルネコ既定キー配置（ユーザーのゲーム内設定）");
        foreach (var (role, key) in TornekoPreset.All()) Console.WriteLine($"  {role,-14} {KeySpec.Parse(key)}");
        Con.Info("T2/T3/T6 の開閉キーには メニュー=Num0 / キャンセル=Num2 を使います（K で変更可）。T5 はこの配置で固定です。");
        if (Con.Confirm("開く=Num0 / 閉じる=Num2 を適用しますか", true)) { _cfg.OpenKey = TornekoPreset.Menu; _cfg.CloseKey = TornekoPreset.Cancel; }
    }

    public void ConfigureKeys()
    {
        Con.Head("キー/しきい値の設定");
        Con.Info("キー名の例: Num0〜Num9（テンキー）, D0〜D9（上段数字）, W A S D, Escape, Enter, Space, Up, F1, VK:0x60");
        Con.Info("数字1文字だけを入力した場合は、テンキーか上段数字かを確認します。");
        _cfg.OpenKey = Con.PromptKey("メニューを開くキー", _cfg.OpenKey);
        _cfg.CloseKey = Con.PromptKey("メニューを閉じるキー", _cfg.CloseKey);
        Con.Info("仮想パッド用ボタン名の例: A, B, X, Y, Start, Back, Up, Down, Left, Right, LeftShoulder, RightShoulder");
        _cfg.PadOpenButton = Con.Prompt("パッドでメニューを開くボタン", _cfg.PadOpenButton);
        _cfg.PadCloseButton = Con.Prompt("パッドでメニューを閉じるボタン（空欄=開くと同じ）", _cfg.PadCloseButton);
        _cfg.HoldMs = Int("キー押下時間 ms", _cfg.HoldMs, 10, 2000);
        _cfg.WaitAfterInputMs = Int("入力後に画面変化を待つ時間 ms", _cfg.WaitAfterInputMs, 100, 10000);
        _cfg.RateSeconds = Int("フレームレート計測秒数", _cfg.RateSeconds, 1, 60);
        _cfg.NoiseSamples = Int("無入力の揺れを測る組数", _cfg.NoiseSamples, 2, 20);
        _cfg.MinComponentRatio = Dbl("受理に必要な最大連結変化領域の画面比（0.015=1.5%）", _cfg.MinComponentRatio, 0.001, 0.5);
        _cfg.MinChangedRatio = Dbl("受理に必要な最小変化率（0.003=0.3%）", _cfg.MinChangedRatio, 0.0001, 0.5);
        _cfg.PixelThreshold = Int("画素差しきい値(0-255)", _cfg.PixelThreshold, 1, 254);
        _cfg.T6Minutes = Int("T6 の実行時間（分）", _cfg.T6Minutes, 1, 120);
        _cfg.T6IntervalSec = Int("T6 の操作間隔（秒）", _cfg.T6IntervalSec, 5, 600);
        var cm = Con.Prompt("①の WM_CHAR 併送: auto(T2の結果) / on / off", _charMode == null ? "auto" : _charMode == true ? "on" : "off").ToLowerInvariant();
        if (cm == "on") { _charMode = true; _charModeReason = "手動指定"; } else if (cm == "off") { _charMode = false; _charModeReason = "手動指定"; }
    }

    private static int Int(string q, int def, int min, int max) => int.TryParse(Con.Prompt(q, def.ToString()), out int v) ? Math.Clamp(v, min, max) : def;
    private static double Dbl(string q, double def, double min, double max) =>
        double.TryParse(Con.Prompt(q, def.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? Math.Clamp(v, min, max) : def;

    private static Dictionary<string, object?> StatsDict(FrameImage? img, string prefix, string? whyNull = null)
    {
        var d = new Dictionary<string, object?>();
        if (img == null) { d[prefix + "画像"] = whyNull ?? "取得できず"; return d; }
        var s = img.ComputeStats();
        d[prefix + "サイズ"] = $"{img.Width}x{img.Height}";
        d[prefix + "平均輝度/標準偏差"] = $"{s.MeanLuma} / {s.StdLuma}";
        d[prefix + "真っ黒判定"] = s.LooksBlack;
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
            r.Warnings.Add("WGCのセッション開始に失敗。PrintWindowのみで判定します。");
            return null;
        }
    }

    private sealed record Shot(FrameImage? Wgc, FrameImage? Pw, string PwDetail)
    {
        public FrameImage? Best => Wgc ?? Pw;
    }

    private Shot Grab(WgcSession? s)
    {
        FrameImage? w = null;
        try { w = s?.Snapshot(); } catch (Exception e) { Con.Warn("WGC Snapshot失敗: " + e.Message); }
        var (p, d) = PrintWindowCapture.Capture(_target);
        return new Shot(w, p, d);
    }

    private void Save(TestResult r, FrameImage? img, string name)
    {
        if (img == null) return;
        img.SavePng(ImgPath(name)); r.Images.Add(name);
    }

    // ------------------------------------------------------------ 判定（v2）
    /// <summary>無入力時の揺れ。複数組の最大値を使う。</summary>
    private sealed record Noise(double WgcRatio, double WgcComp, double PwRatio, double PwComp, int Pairs)
    {
        public override string ToString() => $"WGC 変化率max={WgcRatio:0.#####} 連結max={WgcComp:0.#####} / PW 変化率max={PwRatio:0.#####} 連結max={PwComp:0.#####}（{Pairs}組）";
    }

    private Noise MeasureNoise(WgcSession? wgc, int? pairs = null, int? intervalMs = null)
    {
        int n = pairs ?? _cfg.NoiseSamples, iv = intervalMs ?? Math.Min(_cfg.WaitAfterInputMs, 1000);
        var shots = new List<Shot> { Grab(wgc) };
        for (int i = 0; i < n; i++) { Thread.Sleep(iv); shots.Add(Grab(wgc)); }
        double wr = 0, wc = 0, pr = 0, pc = 0;
        // 連続する組と、2つ離れた組（入力後の待ち時間に近い間隔）の両方を見る
        for (int i = 0; i < shots.Count; i++)
        for (int j = i + 1; j <= Math.Min(i + 2, shots.Count - 1); j++)
        {
            var dw = FrameImage.Compare(shots[i].Wgc, shots[j].Wgc, _cfg.PixelThreshold);
            var dp = FrameImage.Compare(shots[i].Pw, shots[j].Pw, _cfg.PixelThreshold);
            if (dw.Comparable) { wr = Math.Max(wr, dw.ChangedRatio); wc = Math.Max(wc, dw.LargestComponentRatio); }
            if (dp.Comparable) { pr = Math.Max(pr, dp.ChangedRatio); pc = Math.Max(pc, dp.LargestComponentRatio); }
        }
        return new Noise(wr, wc, pr, pc, n);
    }

    private (double ratio, double comp) Thresholds(Noise nz, bool wgc)
    {
        double nr = wgc ? nz.WgcRatio : nz.PwRatio, nc = wgc ? nz.WgcComp : nz.PwComp;
        return (Math.Max(_cfg.MinChangedRatio, nr + 0.002), Math.Max(_cfg.MinComponentRatio, nc * 2));
    }

    /// <summary>変化あり判定: 変化率が揺れの最大値を超え、かつ最大連結変化領域が十分大きい。</summary>
    private bool Changed(FrameImage.Diff d, Noise nz, bool wgc)
    {
        if (!d.Comparable) return false;
        var (tr, tc) = Thresholds(nz, wgc);
        return d.ChangedRatio > tr && d.LargestComponentRatio >= tc;
    }

    // ------------------------------------------------------------ T1
    public void Test1Capture()
    {
        Con.Head("T1: WGC / PrintWindow キャプチャ（前面・背面）");
        Con.Info("内容: ゲームを前面にして数秒間フレームを数えPNG保存 → テスト用ダミー窓を前面に出し、ゲームを背面にして同じことを行う。入力は送りません。");
        if (!RequireTarget()) return;
        if (!Con.Confirm("T1を実行しますか")) { Skip("T1", "WGC/PrintWindow キャプチャ"); return; }
        var r = Begin("T1", "WGC/PrintWindow キャプチャ（前面・背面）");
        using var wgc = StartWgc(r);
        var res = new Dictionary<string, (bool wgcOk, bool pwOk, int frames)>();
        foreach (var mode in new[] { "前面", "背面" })
        {
            bool fgOk = mode == "前面" ? Foreground.Ensure(_target, "ゲームウィンドウ") : ShowDummyForeground("背面キャプチャ中…（ゲームは背面です）");
            r.Data[$"{mode}: 前面ウィンドウ"] = Foreground.Describe(Native.GetForegroundWindow());
            if (!fgOk) r.Warnings.Add($"{mode}: 想定どおりの前面状態にできなかったため参考値です。");
            Thread.Sleep(500);
            var t0 = DateTime.Now; Con.Info($"  {mode}: {_cfg.RateSeconds}秒間フレームを数えています…");
            Thread.Sleep(_cfg.RateSeconds * 1000); var t1 = DateTime.Now;
            int frames = wgc?.FramesBetween(t0, t1) ?? 0;
            var s = Grab(wgc);
            r.Data[$"{mode}: WGCフレーム数 / fps"] = $"{frames} / {Math.Round(frames / (t1 - t0).TotalSeconds, 2)}";
            r.Data[$"{mode}: PrintWindow"] = s.PwDetail;
            Merge(r, StatsDict(s.Wgc, $"{mode}: WGC "));
            Merge(r, StatsDict(s.Pw, $"{mode}: PW ", s.PwDetail));
            string tag = mode == "前面" ? "fg" : "bg";
            Save(r, s.Wgc, $"T1_{tag}_wgc.png"); Save(r, s.Pw, $"T1_{tag}_printwindow.png");
            res[mode] = (s.Wgc != null && !s.Wgc.ComputeStats().LooksBlack, s.Pw != null && !s.Pw.ComputeStats().LooksBlack, frames);
        }
        Dummy.Hide();
        var bg = res["背面"];
        if (bg.wgcOk && bg.frames > 0) { r.Verdict = "OK"; r.Summary = $"WGCで背面キャプチャ可・更新継続（背面 {bg.frames}フレーム/{_cfg.RateSeconds}秒）"; }
        else if (bg.wgcOk) { r.Verdict = "PARTIAL"; r.Summary = "WGC背面で画像は取れたが更新なし（静止画面か描画停止）"; }
        else if (bg.pwOk) { r.Verdict = "PARTIAL"; r.Summary = "WGC背面は不可、PrintWindowでは取得可"; }
        else { r.Verdict = "NG"; r.Summary = "背面キャプチャ不可"; }
        End(r);
    }

    private bool ShowDummyForeground(string status)
    {
        Dummy.Show(status);
        Thread.Sleep(300);
        return Foreground.Ensure(Dummy.Hwnd, "ダミー窓（ゲーム攻略くん Phase0 ダミー窓）");
    }

    /// <summary>ゲームが前面でない状態を確保（コンソールやダミー窓など、ゲーム以外が前面ならOK）</summary>
    private bool EnsureTargetBackground()
    {
        if (Native.GetForegroundWindow() != _target) return true;
        return ShowDummyForeground("ゲームを背面にするための前面窓です");
    }

    // ------------------------------------------------------------ T2
    public void Test2Input()
    {
        Con.Head("T2: 入力 ①a/①b PostMessage ②仮想パッド ③SendInput × 前面・背面");
        Con.Info("内容: 各方式で「開くキー」を送り、キャプチャ差分で受理を判定 → 「閉じるキー」で元に戻す。");
        Con.Info("  ①a = WM_KEYDOWN/UP のみ（先に実施）, ①b = WM_CHAR も併送。結果から以降の①の送り方を自動決定します。");
        Con.Info("準備: メニューが閉じた安全な場面にしてください。背面テストではダミー窓を前面に出して送ります。");
        if (!RequireTarget()) return;
        Con.Info($"開くキー={KeySpec.Parse(_cfg.OpenKey)}  閉じるキー={KeySpec.Parse(_cfg.CloseKey)}  NumLock(参考値)={(Native.NumLockOn ? "ON" : "OFF")}");

        var a = RunBackend("T2-1a", new PostMessageBackend { AlsoSendChar = false }, _cfg.OpenKey, _cfg.CloseKey,
            "①a PostMessage: 対象ウィンドウへ WM_KEYDOWN/WM_KEYUP だけを直接置きます。OS全体の入力には影響しません。");
        bool hasChar = KeySpec.Parse(_cfg.OpenKey).Char != null;
        (bool fg, bool bg)? b = null;
        if (hasChar)
            b = RunBackend("T2-1b", new PostMessageBackend { AlsoSendChar = true }, _cfg.OpenKey, _cfg.CloseKey,
                "①b PostMessage: WM_KEYDOWN/UP に加えて WM_CHAR も送ります（①aとの比較用）。");
        else Skip("T2-1b", "入力①b PostMessage(+WM_CHAR)", "開くキーに対応する文字がないため WM_CHAR 併送は不要");

        if (_charModeReason.StartsWith("未決定") || _charModeReason.StartsWith("T2"))
        {
            if (a is { bg: true }) { _charMode = false; _charModeReason = "T2で①a(WM_CHARなし)が背面受理"; }
            else if (b is { bg: true }) { _charMode = true; _charModeReason = "T2で①aは不可・①b(WM_CHARあり)が背面受理"; }
            else { _charMode = false; _charModeReason = "T2で①a/①bとも背面受理なし（既定=オフ）"; }
        }
        Con.Ok($"以降の①の送り方: WM_CHAR {CharModeLabel}");

        Con.Head("②仮想パッド (ViGEmBus / XInput)");
        Con.Warn("仮想パッドはシステム全体のデバイスです。パッドを読む他のアプリにも見える可能性があります。");
        if (Con.Confirm("②のテストを行いますか（ViGEmBusの有無を確認し、あれば仮想パッドを接続します）"))
        {
            _vigem ??= new VigemBackend();
            var (ok, detail) = _vigem.Availability();
            if (!ok) Skip("T2-2", "入力②仮想パッド(ViGEmBus/XInput)", "利用不可: " + detail);
            else
            {
                if (_cfg.PadOpenButton.Length == 0) _cfg.PadOpenButton = Con.Prompt("パッドでメニューを開くボタン", "Y");
                RunBackend("T2-2", _vigem, _cfg.PadOpenButton, _cfg.PadCloseButton.Length > 0 ? _cfg.PadCloseButton : _cfg.PadOpenButton,
                    "②仮想パッド: 仮想Xbox360パッドのボタンを押します。", skipConfirm: true);
            }
        }
        else Skip("T2-2", "入力②仮想パッド(ViGEmBus/XInput)");

        RunBackend("T2-3", new SendInputBackend(), _cfg.OpenKey, _cfg.CloseKey,
            "③SendInput(VK送信): OS全体の入力キューにキーを注入します。背面テストではダミー窓を前面にして送ります（ゲームには届かないはず）。\n" +
            "  送信直前に前面ウィンドウを再確認し、想定外なら送信を中止します。テスト中はキーボード・マウスに触れないでください。");
    }

    private (bool fg, bool bg)? RunBackend(string id, IProbeInputBackend be, string openKey, string closeKey, string explain, bool skipConfirm = false)
    {
        Con.Head(be.Label);
        Con.Info(explain);
        if (!skipConfirm && !Con.Confirm($"{be.Label} のテスト（前面→背面）を実行しますか")) { Skip(id, "入力" + be.Label); return null; }
        var r = Begin(id, "入力" + be.Label);
        r.Data["方式の可用性"] = be.Availability().Item2;
        using var mon = InputMonitor.StartNew(_cfg.StopMode);
        r.Data["入力監視"] = $"{mon.HookStatus} / {mon.RawStatus}";
        using var wgc = StartWgc(r);
        var v = new Dictionary<string, string>();
        foreach (var mode in new[] { "前面", "背面" })
        {
            if (mode == "背面" && v.GetValueOrDefault("前面") == "要復旧" &&
                !Con.Confirm("前面テスト後にゲームが元に戻っていない可能性があります。ゲームを元の状態に戻してから続行しますか")) break;
            v[mode] = RunInputCase(r, be, mode, openKey, closeKey, wgc, mon);
        }
        Dummy.Hide();
        var c = mon.Snapshot();
        r.Data["入力監視の集計"] = c.Summary();
        bool fgAcc = v.GetValueOrDefault("前面") is "受理" or "要復旧", bgAcc = v.GetValueOrDefault("背面") is "受理" or "要復旧";
        r.Data["判定: 前面で受理"] = fgAcc; r.Data["判定: 背面で受理"] = bgAcc;
        if (be is SendInputBackend)
        {
            r.Verdict = fgAcc && !bgAcc ? "OK" : fgAcc ? "PARTIAL" : "NG";
            r.Summary = fgAcc ? (bgAcc ? "前面で受理。背面でもゲームが変化（矛盾の可能性、画像確認）" : "前面で受理・背面ではゲームに届かず（想定どおり）") : "前面でも受理されず";
        }
        else
        {
            r.Verdict = bgAcc ? "OK" : fgAcc ? "PARTIAL" : "NG";
            r.Summary = bgAcc ? $"背面でも受理（前面={(fgAcc ? "受理" : "不受理")}）" : fgAcc ? "前面のみ受理・背面では反応なし" : "前面・背面とも反応なし";
        }
        End(r);
        return (fgAcc, bgAcc);
    }

    /// <returns>"受理" / "要復旧" / "不受理" / "中止"</returns>
    private string RunInputCase(TestResult r, IProbeInputBackend be, string mode, string openKey, string closeKey, WgcSession? wgc, InputMonitor mon)
    {
        string tag = mode == "前面" ? "fg" : "bg";
        string P(string s) => $"{mode}: {s}";
        Dummy.Reset();
        bool ok = mode == "前面" ? Foreground.Ensure(_target, "ゲームウィンドウ") : ShowDummyForeground($"{be.Label} 背面テスト中…（キーボード・マウスに触れないでください）");
        var expectedFg = mode == "前面" ? _target : Dummy.Hwnd;
        r.Data[P("前面ウィンドウ(開始時)")] = Foreground.Describe(Native.GetForegroundWindow());
        if (!ok) { r.Data[P("結果")] = "中止: 前面状態を準備できなかった"; Con.Err($"  {mode}: 前面状態を準備できず中止"); return "中止"; }
        if (be is SendInputBackend sib) sib.RequiredForeground = expectedFg;
        var c0 = mon.Snapshot();
        Thread.Sleep(500);
        Con.Info($"  {mode}: 無入力の揺れを測定中（{_cfg.NoiseSamples}組）…");
        var nz = MeasureNoise(wgc);
        var before = Grab(wgc);

        Con.Info($"  {mode}: 開く [{openKey}] を送信");
        var t0 = DateTime.Now;
        r.Data[P("送信(開)")] = be.Press(_target, openKey, _cfg.HoldMs);
        Thread.Sleep(_cfg.WaitAfterInputMs);
        var after = Grab(wgc);
        r.Data[P("送信後WGCフレーム数")] = wgc?.FramesBetween(t0, DateTime.Now);
        var dOpenW = FrameImage.Compare(before.Wgc, after.Wgc, _cfg.PixelThreshold, before.Wgc != null && after.Wgc != null ? ImgPath($"{be.Id}_{tag}_diff_open.png") : null);
        var dOpenP = FrameImage.Compare(before.Pw, after.Pw, _cfg.PixelThreshold);
        if (before.Wgc != null && after.Wgc != null) r.Images.Add($"{be.Id}_{tag}_diff_open.png");

        Con.Info($"  {mode}: 閉じる [{closeKey}] を送信");
        r.Data[P("送信(閉)")] = be.Press(_target, closeKey, _cfg.HoldMs);
        Thread.Sleep(_cfg.WaitAfterInputMs);
        var closed = Grab(wgc);
        var dCloseW = FrameImage.Compare(before.Wgc, closed.Wgc, _cfg.PixelThreshold);
        var dCloseP = FrameImage.Compare(before.Pw, closed.Pw, _cfg.PixelThreshold);

        var fgEnd = Native.GetForegroundWindow();
        var recv = Dummy.Received();
        var c1 = mon.Snapshot();
        Save(r, before.Best, $"{be.Id}_{tag}_1before.png");
        Save(r, after.Best, $"{be.Id}_{tag}_2after_open.png");
        Save(r, closed.Best, $"{be.Id}_{tag}_3after_close.png");

        var (twr, twc) = Thresholds(nz, true); var (tpr, tpc) = Thresholds(nz, false);
        r.Data[P("無入力の揺れ(最大)")] = nz.ToString();
        r.Data[P("判定しきい値 WGC/PW")] = $"変化率>{twr:0.#####} かつ 連結≥{twc:0.#####} / 変化率>{tpr:0.#####} かつ 連結≥{tpc:0.#####}";
        r.Data[P("開いた後 WGC")] = dOpenW.ToString(); r.Data[P("開いた後 PW")] = dOpenP.ToString();
        r.Data[P("閉じた後(送信前比) WGC")] = dCloseW.ToString(); r.Data[P("閉じた後(送信前比) PW")] = dCloseP.ToString();
        r.Data[P("前面状態が維持された")] = fgEnd == expectedFg;
        r.Data[P("ダミー窓が受け取ったキー")] = recv.count > 0 ? string.Join(" ", recv.keys) : "なし";
        r.Data[P("フック/Rawで見えた自分の注入キー")] = $"{c1.H(0, InputClass.Own) - c0.H(0, InputClass.Own)} / {c1.R(0, InputClass.Own) - c0.R(0, InputClass.Own)}";
        r.Data[P("テスト中のユーザー操作イベント")] = c1.UserEvents(_cfg.StopMode) - c0.UserEvents(_cfg.StopMode);

        bool openW = Changed(dOpenW, nz, true), openP = Changed(dOpenP, nz, false);
        bool accepted = openW || openP;
        bool reverted = (!openW || !Changed(dCloseW, nz, true)) && (!openP || !Changed(dCloseP, nz, false));
        string v = !accepted ? "不受理" : reverted ? "受理" : "要復旧";
        r.Data[P("結果")] = v switch
        {
            "受理" => $"受理（大きな連結変化が現れ、閉じるで元に戻った）検出={(openW ? "WGC " : "")}{(openP ? "PW" : "")}",
            "要復旧" => "受理（変化したが元に戻らなかった → 画像で目視確認）",
            _ => "反応なし（しきい値を超える連結変化なし）",
        };
        if (mode == "背面" && recv.count > 0)
        {
            if (accepted) r.Warnings.Add($"{be.Label} 背面: ダミー窓がキー({string.Join(" ", recv.keys)})を受信したのにゲーム画面も変化しました。矛盾しているため、NPCの動きなどによる誤判定の可能性があります（画像で確認）。");
            else if (be is not SendInputBackend) r.Warnings.Add($"{be.Label} 背面: 前面のダミー窓へキーが漏れました: {string.Join(" ", recv.keys)}");
        }
        if (fgEnd != expectedFg) r.Warnings.Add($"{be.Label} {mode}: テスト中に前面ウィンドウが変わりました（{Foreground.Describe(fgEnd)}）。");
        if (c1.UserEvents(_cfg.StopMode) > c0.UserEvents(_cfg.StopMode)) r.Warnings.Add($"{be.Label} {mode}: テスト中にユーザー操作（リモート操作を含む）が検出されました。");
        (v == "不受理" ? (Action<string>)Con.Warn : Con.Ok)($"  {mode}: {r.Data[P("結果")]}");
        return v;
    }

    // ------------------------------------------------------------ T3
    public void Test3Minimize()
    {
        Con.Head("T3: 最小化時の挙動");
        Con.Info("内容: ゲームを最小化し、WGC/PrintWindow の結果を記録。最小化中に①で開くキーを送り、復元後にメニューが開いたかを確認し、閉じるキーを送ります。");
        if (!RequireTarget()) return;
        if (!Con.Confirm("T3を実行しますか")) { Skip("T3", "最小化時の挙動"); return; }
        bool auto = Con.Confirm("最小化をツールが自動で行いますか（n = 自分で最小化ボタンを押す）", true);
        var r = Begin("T3", "最小化時の挙動（WGC/PrintWindow/①入力）");
        r.Data["最小化方法"] = auto ? "ShowWindow(SW_MINIMIZE)" : "ユーザー手動";
        r.Data["①の送り方"] = $"WM_CHAR {CharModeLabel}";
        if (Native.IsIconic(_target)) Native.ShowWindow(_target, Native.SW_RESTORE);
        Foreground.Ensure(_target, "ゲームウィンドウ");
        using var wgc = StartWgc(r);
        Thread.Sleep(800);
        var nz = MeasureNoise(wgc);
        r.Data["通常時: 無入力の揺れ(最大)"] = nz.ToString();
        var t0 = DateTime.Now; Thread.Sleep(_cfg.RateSeconds * 1000); var t1 = DateTime.Now;
        r.Data["通常時: WGCフレーム数"] = wgc?.FramesBetween(t0, t1);
        var s0 = Grab(wgc);
        Save(r, s0.Wgc, "T3_0normal_wgc.png"); Save(r, s0.Pw, "T3_0normal_printwindow.png");

        if (auto) Native.ShowWindow(_target, Native.SW_MINIMIZE);
        else
        {
            Con.Warn("20秒以内にゲームウィンドウを最小化してください…");
            var until = DateTime.Now.AddSeconds(20);
            while (!Native.IsIconic(_target) && DateTime.Now < until) Thread.Sleep(100);
        }
        if (!Native.IsIconic(_target)) { r.Verdict = "ABORTED"; r.Summary = "最小化を確認できなかった"; End(r); return; }
        r.Data["最小化確認時刻"] = DateTime.Now.ToString("HH:mm:ss.fff");
        Thread.Sleep(1000);
        var t2 = DateTime.Now; Con.Info($"  最小化中: {_cfg.RateSeconds}秒間フレームを数えています…"); Thread.Sleep(_cfg.RateSeconds * 1000); var t3 = DateTime.Now;
        int framesMin = wgc?.FramesBetween(t2, t3) ?? 0;
        var s1 = Grab(wgc);
        r.Data["最小化中: WGCフレーム数"] = framesMin;
        r.Data["最小化中: WGC最終フレーム時刻"] = wgc?.LastFrameAt?.ToString("HH:mm:ss.fff");
        r.Data["最小化中: WGCセッション終了(ItemClosed)"] = wgc?.ItemClosed;
        r.Data["最小化中: PrintWindow"] = s1.PwDetail;
        Merge(r, StatsDict(s1.Wgc, "最小化中: WGC(保持中の最終フレーム) "));
        Save(r, s1.Wgc, "T3_1minimized_wgc_lastframe.png");
        var raw = PrintWindowCapture.CaptureRaw(_target);
        Save(r, raw, "T3_1minimized_printwindow_raw.png");

        Con.Info($"  最小化中に①PostMessageで [{_cfg.OpenKey}] を送信");
        var pm = new PostMessageBackend { AlsoSendChar = CharMode };
        var t4 = DateTime.Now;
        r.Data["最小化中: ①送信(開)"] = pm.Press(_target, _cfg.OpenKey, _cfg.HoldMs);
        Thread.Sleep(Math.Max(_cfg.WaitAfterInputMs, 1500));
        r.Data["最小化中: ①送信後のWGCフレーム数"] = wgc?.FramesBetween(t4, DateTime.Now);

        Native.ShowWindow(_target, Native.SW_SHOWNOACTIVATE);
        Thread.Sleep(1500);
        var s3 = Grab(wgc);
        var dW = FrameImage.Compare(s0.Wgc, s3.Wgc, _cfg.PixelThreshold, s0.Wgc != null && s3.Wgc != null ? ImgPath("T3_3restored_diff.png") : null);
        var dP = FrameImage.Compare(s0.Pw, s3.Pw, _cfg.PixelThreshold);
        if (s0.Wgc != null && s3.Wgc != null) r.Images.Add("T3_3restored_diff.png");
        Save(r, s3.Best, "T3_3restored.png");
        r.Data["復元後: 最小化前との差 WGC"] = dW.ToString(); r.Data["復元後: 最小化前との差 PW"] = dP.ToString();
        bool menuOpen = Changed(dW, nz, true) || Changed(dP, nz, false);
        if (menuOpen)
        {
            r.Data["復元後: ①送信(閉)"] = pm.Press(_target, _cfg.CloseKey, _cfg.HoldMs);
            Thread.Sleep(_cfg.WaitAfterInputMs);
            var s4 = Grab(wgc);
            Save(r, s4.Best, "T3_4after_close.png");
            r.Data["閉じた後: 最小化前との差 WGC"] = FrameImage.Compare(s0.Wgc, s4.Wgc, _cfg.PixelThreshold).ToString();
        }
        else r.Data["復元後: ①送信(閉)"] = "大きな連結変化なし → メニューは開いていないと推定し送信せず";

        bool pwUnusable = s1.Pw == null;
        r.Data["判定: 最小化中WGCフレーム停止"] = framesMin == 0;
        r.Data["判定: 最小化中PrintWindow取得不可(極小/最小化)"] = pwUnusable;
        r.Data["判定: 最小化中の①入力が処理された(復元後にメニュー相当の変化)"] = menuOpen;
        r.Verdict = "INFO";
        r.Summary = $"最小化中 WGC={(framesMin == 0 ? "フレーム停止(最終フレーム保持)" : $"{framesMin}フレーム到着")}, PrintWindow={(pwUnusable ? "取得不可" : "取得可")}, ①入力={(menuOpen ? "処理された（復元後にメニュー）" : "反映なし")}";
        r.Notes.Add("最小化中に処理されたのか、復元時にまとめて処理されたのかは区別できません（最小化中は観測手段がないため）。");
        End(r);
    }

    // ------------------------------------------------------------ T4
    public void Test4UserInput()
    {
        Con.Head("T4: ユーザー操作検出 → AI入力ループ停止デモ");
        Con.Info("v2: フックと RawInput は専用スレッドで動きます。既定モードは『自分の署名付き注入以外はすべてユーザー操作』です（リモート操作ツールの入力も含む）。");
        Con.Info("  1) 校正(5秒、触らない): ダミー窓を前面にし、無害キー F24 を署名付き SendInput で注入 → 自分の注入として分類されるか確認");
        Con.Info("  2) 本番(最大30秒): キーを押すかマウスを動かすと、ループが止まることを確認");
        if (!Con.Confirm("T4を実行しますか")) { Skip("T4", "ユーザー操作検出"); return; }
        var mode = Con.Confirm("停止条件を『物理デバイスのみ』にしますか（n = 既定: 自分以外すべて）", false) ? StopMode.PhysicalOnly : StopMode.AllButOwn;
        var r = Begin("T4", "ユーザー操作検出 → AI入力ループ停止");
        r.Data["停止モード"] = mode == StopMode.AllButOwn ? "自分以外すべて(既定)" : "物理デバイスのみ";
        using var mon = InputMonitor.StartNew(mode);
        r.Data["フック"] = mon.HookStatus; r.Data["RawInput"] = mon.RawStatus;

        int stop = 0; InputMonitor.Ev? first = null; DateTime? detectedAt = null; bool armed = false; int falsePos = 0;
        mon.UserInput += ev =>
        {
            if (!Volatile.Read(ref armed)) { Interlocked.Increment(ref falsePos); return; }
            if (Interlocked.Exchange(ref stop, 1) == 1) return;
            detectedAt = DateTime.Now; first = ev;
        };

        bool dummyFg = ShowDummyForeground("T4: 校正中（5秒間キーボード・マウスに触れないでください）");
        r.Data["ダミー窓が前面"] = dummyFg;
        var dh = Dummy.Hwnd; int injected = 0;
        var f24 = KeySpec.Parse("F24");
        var pm = new PostMessageBackend();
        void LoopStep()
        {
            if (dummyFg && Native.GetForegroundWindow() == dh) { SendInputBackend.InjectHarmless(); injected++; }
            pm.Down(dh, f24); pm.Up(dh, f24);
        }
        Thread.Sleep(500);
        Interlocked.Exchange(ref falsePos, 0);
        var c0 = mon.Snapshot();
        Con.Info("  校正中… 5秒間キーボード・マウスに触れないでください");
        var end = DateTime.Now.AddSeconds(5);
        while (DateTime.Now < end) { LoopStep(); Thread.Sleep(200); }
        Thread.Sleep(300);
        var c1 = mon.Snapshot();
        long hookOwn = c1.H(0, InputClass.Own) - c0.H(0, InputClass.Own), rawOwn = c1.R(0, InputClass.Own) - c0.R(0, InputClass.Own);
        r.Data["校正: 注入回数(F24 押下+離し=2イベント/回)"] = injected;
        r.Data["校正: 自分の注入として分類 Hook/Raw"] = $"{hookOwn} / {rawOwn}（期待値 {injected * 2}）";
        r.Data["校正: 集計"] = $"{Diff(c0, c1)}";
        r.Data["校正: ユーザー操作として誤検知"] = falsePos;
        bool ownOk = !dummyFg || (hookOwn >= injected * 2 && rawOwn >= injected * 2);
        if (dummyFg && hookOwn < injected * 2) r.Warnings.Add($"フックで自分の注入を取りこぼし（{hookOwn}/{injected * 2}）。");
        if (dummyFg && rawOwn < injected * 2) r.Warnings.Add($"RawInputで自分の注入を取りこぼし（{rawOwn}/{injected * 2}）。");

        Dummy.Show("T4: 本番 — 任意のキーを押すか、マウスを動かしてください（AIループが止まります）");
        Con.Warn("  本番: 30秒以内に、キーを1つ押すか、マウスを動かしてください。");
        Volatile.Write(ref armed, true);
        int loops = 0; DateTime? stoppedAt = null;
        var deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            if (Volatile.Read(ref stop) == 1) { stoppedAt = DateTime.Now; break; }
            LoopStep(); loops++;
            for (int i = 0; i < 10 && Volatile.Read(ref stop) == 0; i++) Thread.Sleep(20);
        }
        if (Volatile.Read(ref stop) == 1 && stoppedAt == null) stoppedAt = DateTime.Now;
        Volatile.Write(ref armed, false);
        var c2 = mon.Snapshot();
        Dummy.Hide();
        mon.SaveCsv(ImgPath("T4_input_events.csv"));
        r.Data["本番: ループ回数"] = loops;
        r.Data["本番: 検知イベント"] = first == null ? null : $"{first.Source} {first.Device} {first.Kind} 分類={first.Class} {first.Detail}";
        r.Data["本番: 検知→停止の遅延(ms)"] = detectedAt != null && stoppedAt != null ? Math.Round((stoppedAt.Value - detectedAt.Value).TotalMilliseconds, 1) : null;
        r.Data["累計"] = c2.Summary();
        r.Data["RawInputで見えたデバイス"] = string.Join(", ", c2.RawDevices);
        r.Data["イベントログ"] = "T4_input_events.csv";
        if (first?.Class == InputClass.OtherInjected) r.Notes.Add("検知した入力は『他プロセスの注入』でした。リモート操作ツール経由の操作はこの分類になります（既定モードではユーザー操作として扱う）。");
        if (Volatile.Read(ref stop) == 0) { r.Verdict = "NG"; r.Summary = "30秒以内にユーザー操作を検知できなかった（または操作がなかった）"; }
        else if (falsePos == 0 && ownOk) { r.Verdict = "OK"; r.Summary = $"ユーザー操作を検知して停止（{first!.Source}/{first.Class}）。自分の注入は正しく識別"; }
        else { r.Verdict = "PARTIAL"; r.Summary = $"停止はできたが、校正中の誤検知{falsePos}件、または自分の注入の取りこぼしあり"; }
        End(r);
    }

    private static string Diff(InputMonitor.Counters a, InputMonitor.Counters b)
    {
        var d = new InputMonitor.Counters();
        for (int i = 0; i < 2; i++) for (int j = 0; j < 3; j++) { d.Hook[i, j] = b.Hook[i, j] - a.Hook[i, j]; d.Raw[i, j] = b.Raw[i, j] - a.Raw[i, j]; }
        return d.Summary();
    }

    public void Dispose() { _vigem?.Dispose(); }
}
