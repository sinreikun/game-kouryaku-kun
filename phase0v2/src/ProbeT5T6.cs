using System.Text;

namespace GameKouryakuKun.Phase0;

public sealed partial class Probe
{
    // ------------------------------------------------------------ T5
    private sealed record Step(string Id, string Title, string Expect, Func<PostMessageBackend, string> Act, string? Caution = null);

    private List<Step> T5Steps()
    {
        KeySpec K(string s) => KeySpec.Parse(s);
        var up = K(TornekoPreset.Up); var dn = K(TornekoPreset.Down); var lf = K(TornekoPreset.Left); var rt = K(TornekoPreset.Right);
        var diag = K(TornekoPreset.Diagonal);
        int hold = Math.Max(_cfg.HoldMs, 80);
        return new()
        {
            new("S01", "足踏み（Num6 を1回）", "その場で1ターン経過（画面変化は小さいことがある）", pm => pm.Chord(_target, new[] { K(TornekoPreset.Step) }, hold)),
            new("S02", "上へ1歩（W を1回）", "上へ1マス移動", pm => pm.Chord(_target, new[] { up }, hold)),
            new("S03", "下へ1歩（S を1回）", "下へ1マス移動（元の位置へ）", pm => pm.Chord(_target, new[] { dn }, hold)),
            new("S04", "左へ1歩（A を1回）", "左へ1マス移動", pm => pm.Chord(_target, new[] { lf }, hold)),
            new("S05", "右へ1歩（D を1回）", "右へ1マス移動（元の位置へ）", pm => pm.Chord(_target, new[] { rt }, hold)),
            new("S06", "斜め補助つき右上（Num3 を押したまま W+D を同時押し 400ms）", "右上へ1マス移動", pm => pm.Chord(_target, new[] { diag, up, rt }, 400)),
            new("S07", "斜め補助つき左下（Num3 を押したまま S+A を同時押し 400ms）", "左下へ1マス移動（元の位置へ）", pm => pm.Chord(_target, new[] { diag, dn, lf }, 400)),
            new("S08", "補助なし同時押し右上（W+D を同時押し 400ms）", "右上へ移動するか、どちらか一方へ移動するか", pm => pm.Chord(_target, new[] { up, rt }, 400)),
            new("S09", "補助なし同時押し左下（S+A を同時押し 400ms）", "元の位置へ戻るか", pm => pm.Chord(_target, new[] { dn, lf }, 400)),
            new("S10", "右へ長押し（D を 1200ms 押しっぱなし、オートリピート相当を連送）", "複数マス進むか、1マスで止まるか",
                pm => pm.Chord(_target, new[] { rt }, 1200), "長押しで複数マス進む可能性があります。周囲に敵や罠がない場所で行ってください。"),
            new("S11", "左へ長押し（A を 1200ms 押しっぱなし）", "元の位置付近へ戻る", pm => pm.Chord(_target, new[] { lf }, 1200),
                "長押しで複数マス進む可能性があります。"),
            new("S12", "メニューを開く（Num0）", "メニュー（道具・倉庫…）が開く", pm => pm.Chord(_target, new[] { K(TornekoPreset.Menu) }, hold)),
            new("S13", "決定（Num1）", "メニューのカーソル位置の項目が開く（『道具』なら道具一覧）", pm => pm.Chord(_target, new[] { K(TornekoPreset.Confirm) }, hold),
                "実行前に、ゲーム画面のメニューのカーソルが『道具』にあることを目で確認してください。『中断』などにある場合はスキップ(s)してください。"),
            new("S14", "キャンセル（Num2）", "道具一覧が閉じてメニューに戻る", pm => pm.Chord(_target, new[] { K(TornekoPreset.Cancel) }, hold)),
            new("S15", "キャンセル（Num2）", "メニューが閉じる", pm => pm.Chord(_target, new[] { K(TornekoPreset.Cancel) }, hold)),
        };
    }

    public void Test5DungeonMove()
    {
        Con.Head("T5: ダンジョン内 ①背面移動（PostMessage）");
        Con.Info("内容: ゲームを背面に置いたまま、①PostMessageで移動・斜め移動（Num3押しっぱなし＋2方向同時押し）・長押し・足踏み・決定/キャンセルを1手ずつ送ります。");
        Con.Info("  各手の前に確認を表示します（y=実行 / s=スキップ / q=中止）。各手の前後のPNGと差分を保存します。");
        Con.Info("  道具の使用・投げる・装備などの危険な操作は送りません。決定(Num1)はメニュー内の『道具』一覧を開くためだけに使います。");
        Con.Warn("準備: ダンジョン内で、周囲に敵・罠・水路がなく、上下左右・斜めに1〜3マス動ける広い部屋の中央に立ってください（メニューは閉じた状態）。");
        Con.Info($"  使用するキー: {string.Join(", ", TornekoPreset.All().Select(x => $"{x.role}={x.key}"))}");
        Con.Info($"  ①の送り方: WM_CHAR {CharModeLabel}");
        if (!RequireTarget()) return;
        if (!Con.Confirm("T5を開始しますか")) { Skip("T5", "ダンジョン内①背面移動"); return; }
        var r = Begin("T5", "ダンジョン内 ①背面移動（PostMessage）");
        r.Data["①の送り方"] = $"WM_CHAR {CharModeLabel}";
        var pm = new PostMessageBackend { AlsoSendChar = CharMode };
        using var wgc = StartWgc(r);
        if (!EnsureTargetBackground()) { r.Verdict = "ABORTED"; r.Summary = "ゲームを背面にできなかった"; End(r); return; }
        Thread.Sleep(500);
        Con.Info("  無入力の揺れを測定中…");
        var nz = MeasureNoise(wgc, 3);
        r.Data["無入力の揺れ(最大)"] = nz.ToString();
        int done = 0, changed = 0, skipped = 0;
        var rows = new List<string>();
        foreach (var st in T5Steps())
        {
            Con.Head($"T5 {st.Id}: {st.Title}");
            Con.Info($"  期待: {st.Expect}");
            if (st.Caution != null) Con.Warn($"  注意: {st.Caution}");
            var ans = Con.Prompt("実行しますか？ y=実行 / s=スキップ / q=中止", "y").ToLowerInvariant();
            if (ans.StartsWith("q")) { rows.Add($"{st.Id} 中止"); break; }
            if (ans.StartsWith("s")) { skipped++; rows.Add($"{st.Id} スキップ"); r.Data[$"{st.Id} {st.Title}"] = "スキップ"; continue; }
            if (Native.GetForegroundWindow() == _target)
            {
                Con.Warn("  ゲームが前面になっています。背面にしてから送ります。");
                if (!EnsureTargetBackground()) { r.Data[$"{st.Id} {st.Title}"] = "中止: 背面にできず"; continue; }
            }
            if (Native.IsIconic(_target)) { Con.Warn("  ゲームが最小化されています。元に戻してから再実行してください。"); r.Data[$"{st.Id} {st.Title}"] = "中止: 最小化中"; continue; }
            var before = Grab(wgc);
            var fgBefore = Native.GetForegroundWindow();
            string sent = st.Act(pm);
            Thread.Sleep(_cfg.WaitAfterInputMs);
            var after = Grab(wgc);
            string tag = $"T5_{st.Id}";
            var d = FrameImage.Compare(before.Wgc, after.Wgc, _cfg.PixelThreshold, before.Wgc != null && after.Wgc != null ? ImgPath($"{tag}_diff.png") : null);
            var dp = FrameImage.Compare(before.Pw, after.Pw, _cfg.PixelThreshold);
            Save(r, before.Best, $"{tag}_1before.png"); Save(r, after.Best, $"{tag}_2after.png");
            if (before.Wgc != null && after.Wgc != null) r.Images.Add($"{tag}_diff.png");
            bool ch = Changed(d, nz, true) || Changed(dp, nz, false);
            done++; if (ch) changed++;
            Con.Info($"  送信: {sent}");
            (ch ? (Action<string>)Con.Ok : Con.Warn)($"  画面変化: {(ch ? "あり" : "なし")}  WGC {d}");
            var memo = Con.Prompt("  ゲーム画面で実際に起きたことをメモ（例: 右上へ1歩 / 動かず / 2歩進んだ）。Enterで省略", "");
            r.Data[$"{st.Id} {st.Title}"] =
                $"変化={(ch ? "あり" : "なし")} | WGC {d} | PW {dp} | 前面={(fgBefore == _target ? "ゲーム(想定外)" : "ゲーム以外")} | 送信: {sent}{(memo.Length > 0 ? $" | ユーザー観察: {memo}" : "")}";
            rows.Add($"{st.Id} 変化={(ch ? "あり" : "なし")}{(memo.Length > 0 ? " 観察=" + memo : "")}");
            SaveReport(false);
        }
        r.Data["一覧"] = string.Join("\n", rows);
        r.Verdict = "INFO";
        r.Summary = $"{done}手実行（画面変化あり {changed} / スキップ {skipped}）。移動方向・歩数は画像とユーザー観察メモで確認";
        r.Notes.Add("PostMessageはOSのキー状態（GetKeyState/GetAsyncKeyState）を変えません。押しっぱなし・同時押しの判定にキー状態を使うゲームでは、斜め移動や長押しが効かない場合があります。");
        r.Notes.Add("足踏み・方向転換は画面変化が小さいため『変化なし』でも効いている可能性があります（ターン経過はメッセージや敵の動きで確認）。");
        End(r);
    }

    // ------------------------------------------------------------ T6
    public void Test6LongRun()
    {
        Con.Head("T6: 背面長時間テスト");
        Con.Info($"内容: ゲームを背面に置いたまま {_cfg.T6Minutes}分間、{_cfg.T6IntervalSec}秒ごとに①PostMessageでメニュー開閉（{_cfg.OpenKey}→{_cfg.CloseKey}）とWGC取得を行い、fps と受理率を記録します。");
        Con.Info("  メニュー開閉はターンを消費しない無害な操作です。途中でやめるときはこのコンソールで q を押してください。");
        Con.Info("  テスト中は他の作業（ブラウザ等）をして構いません。ただしゲームを最小化しないでください。");
        if (!RequireTarget()) return;
        _cfg.T6Minutes = Int("実行時間（分）", _cfg.T6Minutes, 1, 120);
        _cfg.T6IntervalSec = Int("操作間隔（秒）", _cfg.T6IntervalSec, 5, 600);
        if (!Con.Confirm($"T6を開始しますか（約{_cfg.T6Minutes}分）")) { Skip("T6", "背面長時間"); return; }
        var r = Begin("T6", $"背面長時間（{_cfg.T6Minutes}分・{_cfg.T6IntervalSec}秒間隔）");
        r.Data["①の送り方"] = $"WM_CHAR {CharModeLabel}";
        var pm = new PostMessageBackend { AlsoSendChar = CharMode };
        using var wgc = StartWgc(r);
        if (!EnsureTargetBackground()) { r.Verdict = "ABORTED"; r.Summary = "ゲームを背面にできなかった"; End(r); return; }
        Dummy.Hide(); // 背面を確保できたらダミー窓は不要（他の作業をしてよい）
        Thread.Sleep(500);
        var nz = MeasureNoise(wgc);
        r.Data["無入力の揺れ(最大)"] = nz.ToString();
        var csv = new StringBuilder("cycle,time,fps,foreground_is_game,minimized,open_accepted,closed,retry_close,open_wgc,close_wgc\n");
        int cycles = 0, accepted = 0, closedOk = 0, minimizedCycles = 0, fgCycles = 0, savedFail = 0;
        var fpsList = new List<double>();
        var start = DateTime.Now; var end = start.AddMinutes(_cfg.T6Minutes);
        bool aborted = false;
        while (DateTime.Now < end)
        {
            var cycleStart = DateTime.Now;
            cycles++;
            double fps = wgc?.FpsOver(TimeSpan.FromSeconds(Math.Min(_cfg.T6IntervalSec, 10))) ?? 0;
            fpsList.Add(fps);
            bool fgIsGame = Native.GetForegroundWindow() == _target; if (fgIsGame) fgCycles++;
            bool iconic = Native.IsIconic(_target);
            if (iconic)
            {
                minimizedCycles++;
                csv.Append($"{cycles},{cycleStart:HH:mm:ss},{fps},{fgIsGame},true,,,,,\n");
                Con.Warn($"  #{cycles} ゲームが最小化されています（このサイクルはスキップ）");
            }
            else
            {
                var b = Grab(wgc);
                pm.Press(_target, _cfg.OpenKey, _cfg.HoldMs); Thread.Sleep(_cfg.WaitAfterInputMs);
                var o = Grab(wgc);
                pm.Press(_target, _cfg.CloseKey, _cfg.HoldMs); Thread.Sleep(_cfg.WaitAfterInputMs);
                var c = Grab(wgc);
                var dO = FrameImage.Compare(b.Wgc, o.Wgc, _cfg.PixelThreshold); var dOp = FrameImage.Compare(b.Pw, o.Pw, _cfg.PixelThreshold);
                var dC = FrameImage.Compare(b.Wgc, c.Wgc, _cfg.PixelThreshold); var dCp = FrameImage.Compare(b.Pw, c.Pw, _cfg.PixelThreshold);
                bool acc = Changed(dO, nz, true) || Changed(dOp, nz, false);
                bool closed = !(Changed(dC, nz, true) || Changed(dCp, nz, false));
                bool retry = false;
                if (acc && !closed)
                {
                    retry = true;
                    pm.Press(_target, _cfg.CloseKey, _cfg.HoldMs); Thread.Sleep(_cfg.WaitAfterInputMs);
                    var c2 = Grab(wgc);
                    closed = !(Changed(FrameImage.Compare(b.Wgc, c2.Wgc, _cfg.PixelThreshold), nz, true));
                }
                if (acc) accepted++; if (closed) closedOk++;
                csv.Append($"{cycles},{cycleStart:HH:mm:ss},{fps},{fgIsGame},false,{acc},{closed},{retry},{dO.LargestComponentRatio},{dC.LargestComponentRatio}\n");
                if (cycles == 1) { Save(r, b.Best, "T6_first_1before.png"); Save(r, o.Best, "T6_first_2open.png"); Save(r, c.Best, "T6_first_3close.png"); }
                if ((!acc || !closed) && savedFail < 6) { savedFail++; Save(r, o.Best, $"T6_fail{cycles:000}_open.png"); Save(r, c.Best, $"T6_fail{cycles:000}_close.png"); }
                (acc && closed ? (Action<string>)Con.Info : Con.Warn)($"  #{cycles} {cycleStart:HH:mm:ss} fps={fps} 開={(acc ? "○" : "×")} 閉={(closed ? "○" : "×")}{(retry ? "(再送)" : "")}{(fgIsGame ? " ※ゲームが前面" : "")}  残り{Math.Max(0, (end - DateTime.Now).TotalSeconds):0}秒");
            }
            // 次サイクルまで待つ（q で中止）
            var next = cycleStart.AddSeconds(_cfg.T6IntervalSec);
            while (DateTime.Now < next && DateTime.Now < end)
            {
                if (Console.KeyAvailable && char.ToLowerInvariant(Console.ReadKey(true).KeyChar) == 'q') { aborted = true; break; }
                Thread.Sleep(100);
            }
            if (aborted) break;
        }
        File.WriteAllText(ImgPath("T6_cycles.csv"), csv.ToString(), new UTF8Encoding(true));
        int active = cycles - minimizedCycles;
        double rate = active > 0 ? (double)accepted / active : 0;
        r.Data["サイクル数(うち最小化中)"] = $"{cycles}（{minimizedCycles}）";
        r.Data["開く受理率"] = $"{accepted}/{active} = {rate:P1}";
        r.Data["閉じる成功率"] = $"{closedOk}/{active}";
        r.Data["fps 平均/最小/最大"] = fpsList.Count > 0 ? $"{fpsList.Average():0.##} / {fpsList.Min():0.##} / {fpsList.Max():0.##}" : "-";
        r.Data["fps=0 のサイクル数"] = fpsList.Count(f => f == 0);
        r.Data["ゲームが前面だったサイクル数"] = fgCycles;
        r.Data["WGC総フレーム数 / ItemClosed"] = $"{wgc?.TotalFrames} / {wgc?.ItemClosed}";
        r.Data["詳細CSV"] = "T6_cycles.csv";
        r.Data["途中中止"] = aborted;
        if (fgCycles > 0) r.Warnings.Add($"{fgCycles}サイクルでゲームが前面でした（背面の条件を満たしていない回があります）。");
        r.Verdict = active == 0 ? "NG" : rate >= 0.95 && closedOk == active && fpsList.Count(f => f == 0) == 0 ? "OK" : rate >= 0.8 ? "PARTIAL" : "NG";
        r.Summary = $"背面{(DateTime.Now - start).TotalMinutes:0.#}分: 受理率 {rate:P1}（{accepted}/{active}）、閉じ成功 {closedOk}/{active}、fps平均 {(fpsList.Count > 0 ? fpsList.Average() : 0):0.#}（最小 {(fpsList.Count > 0 ? fpsList.Min() : 0):0.#}）";
        End(r);
    }
}
