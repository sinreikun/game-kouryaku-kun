using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameKouryakuKun.Storage;

/// <summary>
/// 診断 zip に入れる健全性レポート（2026-10-18 09:36: 実機確認は診断 zip だけで行い、画像は開かない）。
/// ゲームに依らないシステムの動作だけを自動で集計する: 版、DB（版・整合性・移行前のバックアップ）、Profile、入力の到達、記録の整合、
/// 停止理由、例外、診断と学習 DB の分離、ユーザー入力との分離、ターン判定、学習の更新。プレイの内容（何をしたか・うまくいったか）は評価しない。
/// </summary>
public static class HealthReport
{
    public enum Level { Ok, Warn, Fail, Info, NotTested }
    public sealed record Check(string Area, string Name, Level Level, string Detail);
    public sealed record Result(IReadOnlyList<Check> Checks, string Text, string Json)
    {
        public Level Overall => Checks.Any(c => c.Level == Level.Fail) ? Level.Fail : Checks.Any(c => c.Level == Level.Warn) ? Level.Warn : Level.Ok;
    }

    public const int ExpectedMemorySchema = 17;  // 根拠: 診断の合格条件（この版の Schemas.Memory の最新）
    /// <summary>予測の確率と当たった割合のずれ（段ごとの差の重みつき平均）がこれを超えたら注意</summary>
    public const double CalibrationWarnGap = 0.15;   // 根拠: 診断の合格条件（確率 0.5 と言った予測が 35% か 65% しか当たらない程度のずれ）
    public const int CalibrationMinChecks = 50;       // 根拠: 汎用（10 段に分けても段ごとに数手ある最小の手数）

    /// <summary>予測の確率のずれ（10 段。期待較正誤差）・確率の平均・当たった割合</summary>
    public static (double Gap, double MeanP, double HitRate) Calibration(IReadOnlyList<(double P, bool Hit)> xs)
    {
        if (xs.Count == 0) return (0, 0, 0);
        double gap = 0;
        foreach (var g in xs.GroupBy(x => Math.Clamp((int)(x.P * 10), 0, 9)))
            gap += Math.Abs(g.Sum(x => x.P) - g.Count(x => x.Hit));
        return (gap / xs.Count, xs.Average(x => x.P), xs.Count(x => x.Hit) / (double)xs.Count);
    }

    /// <summary>後半の手のうち時間切れの割合がこれを超えたら注意</summary>
    public const double TimeoutWarnShare = 0.5;   // 根拠: 診断の合格条件（半分以上の手で画面が落ち着かないなら、変化の観測が主に時間切れに頼っている）
    public const int TimeoutMinTurns = 20;        // 根拠: 汎用（割合を言うのに少なすぎない手数）
    public const double OverheadWarnRatio = 1.5;  // 根拠: 診断の合格条件（1手あたりの処理時間の中央値が前回までの 1.5 倍を超えたら要注意。新しい処理で遅くなっていないか）
    public const double LateChangeWarnRate = 0.05;  // 根拠: 診断の合格条件（「変化なし」の判定のうち、早すぎた割合が 5% を超えたら要注意）

    public static Result Build(string appVersion, SqliteDb memory, SqliteDb app, SqliteDb? diagnostics, IReadOnlyList<string> logPathsNewestFirst,
        int? profileSchema, string? profileError)
    {
        var cs = new List<Check>();
        void Add(string area, string name, Level l, string d) => cs.Add(new Check(area, name, l, d));

        // 版
        Add("版", "アプリ", Level.Info, appVersion);

        // DB
        foreach (var (name, db, expect) in new[] { ("memory.sqlite", memory, (int?)ExpectedMemorySchema), ("app.sqlite", app, null), (DiagnosticStore.FileName, diagnostics, null) })
        {
            if (db == null) { Add("DB", name, Level.Info, "なし（診断を実行していない）"); continue; }
            string ic = Try(() => db.Scalar("PRAGMA integrity_check;") as string ?? "?", "?");
            int fk = Try(() => db.Query("PRAGMA foreign_key_check;", r => 0).Count, -1);
            var lvl = ic == "ok" && fk == 0 && (expect == null || db.SchemaVersion == expect) ? Level.Ok : Level.Fail;
            Add("DB", name, lvl, $"schema v{db.SchemaVersion}{(expect != null ? $"（期待 v{expect}）" : "")}・integrity {ic}・外部キー違反 {fk}・バックアップ {db.ListBackups().Count()} 個");
        }

        // Profile
        if (profileError != null) Add("Profile", "読み込み", Level.Fail, profileError);
        else Add("Profile", "読み込み", profileSchema == 2 ? Level.Ok : Level.Fail, $"スキーマ v{profileSchema}");

        // ログ（新しい順）
        // 判定（PASS/WARN/FAIL/NOT_TESTED）は最新のログ（今回の起動）だけで行う。それより前のログは参考（情報）として並べる
        var events = new List<JsonElement>(); var current = new HashSet<int>();
        var perLog = new List<List<JsonElement>>();
        foreach (var (p, i) in logPathsNewestFirst.Take(5).Select((p, i) => (p, i)))
        {
            var mine = new List<JsonElement>(); perLog.Add(mine);
            foreach (var line in SafeLines(p))
            {
                try { using var d = JsonDocument.Parse(line); if (i == 0) current.Add(events.Count); var el = d.RootElement.Clone(); events.Add(el); mine.Add(el); } catch (JsonException) { }
            }
        }
        var cur = events.Where((e, i) => current.Contains(i)).ToList();
        string T(JsonElement e) => e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        int migrated = events.Count(e => T(e) == "profile_migrated");
        if (migrated > 0) Add("Profile", "移行", Level.Ok, $"v1→v2 の移行 {migrated} 回（旧版はバックアップ済み）");
        // 診断データの zip のクリップボードへのコピー（v0.16.0〜）。コピーは書き出しの後なので、記録は次の書き出しの zip に入る
        var clips = events.Where(e => T(e) == "diag_clipboard").OrderBy(e => Str(e, "t"), StringComparer.Ordinal).ToList();   // ログは新しい順に読むので時刻で並べ直す（v0.19.0。最後＝いちばん新しい書き出し）
        if (clips.Count == 0) Add("診断", "zip のクリップボードへのコピー（前回までの書き出し）", Level.NotTested, "記録なし（この版で初めて書き出すときは、次の書き出しで確かめます）");
        else
        {
            var lastClip = clips[^1]; bool ok = lastClip.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
            Add("診断", "zip のクリップボードへのコピー（前回までの書き出し）", ok ? Level.Ok : Level.Warn, $"成功 {clips.Count(c => c.TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.True)}／{clips.Count} 回" + (ok ? "" : $"・最後の失敗: {Str(lastClip, "detail")}"));
        }
        int retired = events.Count(e => T(e) == "profile_retired_keys_removed"), retiredErr = events.Count(e => T(e) == "profile_retired_keys_error");
        Add("Profile", "廃止した欄の取り除き", retiredErr > 0 ? Level.Fail : retired > 0 ? Level.Ok : Level.NotTested,
            retiredErr > 0 ? $"取り除きの失敗 {retiredErr} 回" : retired > 0 ? $"廃止した欄（diagnostics など）を取り除いた回数 {retired}（元のファイルはバックアップ済み）" : "取り除く欄のある Profile はありませんでした");
        var versions = events.Where(e => T(e) == "start" && e.TryGetProperty("version", out _)).Select(e => e.GetProperty("version").GetString()).Distinct().ToList();
        if (versions.Count > 0) Add("版", "ログに現れた版", Level.Info, string.Join(", ", versions));

        // 例外
        int errors = cur.Count(e => T(e).Contains("error", StringComparison.OrdinalIgnoreCase));
        int errorsOld = events.Count(e => T(e).Contains("error", StringComparison.OrdinalIgnoreCase)) - errors;
        Add("例外", "エラーの記録（今回の起動）", errors == 0 ? Level.Ok : Level.Fail, $"{errors} 件（session_error など）" + (errorsOld > 0 ? $"・前回までのログに {errorsOld} 件" : ""));
        int nErr = cur.Count(e => T(e) == "notice" && Str(e, "level") == "error"), nWarn = cur.Count(e => T(e) == "notice" && Str(e, "level") == "warn");
        Add("例外", "画面に出た注意・エラー（今回の起動）", nErr == 0 ? Level.Ok : Level.Warn,
            $"エラー {nErr}・注意 {nWarn}" + string.Concat(cur.Where(e => T(e) == "notice").Take(3).Select(e => $"／{Str(e, "msg")}")));

        // v0.22.0 成長テスト（growth_snapshots。全実行をまたいで推移と引き継ぎを見る）
        try
        {
            if (memory.Tables().Contains("growth_snapshots"))
            {
                var snaps = memory.Query("SELECT json FROM (SELECT json, snapshot_id FROM growth_snapshots ORDER BY snapshot_id DESC LIMIT 5000) ORDER BY snapshot_id", r => GrowthJson.Read(r.GetString(0)));
                foreach (var c in GameKouryakuKun.Core.Growth.GrowthEvaluator.Evaluate(snaps))
                    Add("成長", c.Name, c.Level switch { GameKouryakuKun.Core.Growth.GrowthLevel.Pass => Level.Ok, GameKouryakuKun.Core.Growth.GrowthLevel.Warn => Level.Warn, GameKouryakuKun.Core.Growth.GrowthLevel.Fail => Level.Fail, GameKouryakuKun.Core.Growth.GrowthLevel.Info => Level.Info, _ => Level.NotTested }, c.Detail);
            }
        }
        catch (Exception ex) { Add("成長", "成長テストの計測の読込", Level.Warn, ex.Message); }

        // v0.24.0 ブロック D: 目的の種類の値打ち（目的に帰属した手の学習の量 ÷ 探索の手の学習の量）。値の良し悪しは判定しない。集計が働いているかだけ
        foreach (var e in cur.Where(x => T(x) == "session_end" && x.TryGetProperty("goal_gain_baseline", out _)).Take(1))
        {
            string bl = Str(e, "goal_gain_baseline"), gg = Str(e, "goal_gain");
            bool hasBase = bl.Length > 0 && !bl.StartsWith("0:");
            Add("目的の値打ち", "目的の種類の値打ち（探索と比べた学習の量の比）", !hasBase ? Level.NotTested : Level.Info,
                $"探索の基準 {bl}（手数:1 手あたりの量）・目的 {(gg.Length == 0 ? "まだ集計なし" : gg)}。報酬ではなく内部モデルの不確かさの減りの比");
        }

        // v0.25.0 ブロック E: 地域の進み（新しさの数ではなく、範囲の広がり・未知への道・選択肢・予測の改善）。値の良し悪しは判定しない。計算が働いているか・値が範囲内か
        foreach (var e in cur.Where(x => T(x) == "session_end" && x.TryGetProperty("frontier", out _)).Take(1))
        {
            string fs = Str(e, "frontier"); bool ok = !e.TryGetProperty("frontier_values_ok", out var vo) || vo.ValueKind != System.Text.Json.JsonValueKind.False;
            Add("地域", "地域ごとの行き直しの値打ち（進みが減れば下がり、戻れば上がる）", fs.Length == 0 ? Level.NotTested : ok ? Level.Info : Level.Warn,
                (fs.Length == 0 ? "まだ計算していない" : fs) + (ok ? "" : "／値打ちが範囲 0.05〜4 の外") + "。地域に意味は付けず、固定の除外もしない。報酬ではない");
        }

        // v0.25.2: 画面の文字の観測（OCR。意味は解釈しない。区切りの候補を人が確かめる材料。報酬・選択には使わない）
        GameKouryakuKun.Core.Ocr.StateTextBook? textBook = null;
        try { if (memory.Tables().Contains("goal_model") && memory.Query("SELECT json FROM goal_model WHERE key='state_text'", r => r.GetString(0)).FirstOrDefault() is string tj) { textBook = new(); textBook.ImportState(tj); } } catch { textBook = null; }
        foreach (var e in cur.Where(x => T(x) == "session_end" && x.TryGetProperty("ocr", out _)).Take(1))
        {
            string os = Str(e, "ocr");
            Add("文字", "画面の文字の観測（OCR。意味は解釈しない）", os.Length == 0 ? Level.NotTested : Level.Info,
                os.Length == 0 ? "OCR のエンジンがない（読まずに探索）" : os + "。文字があるか・並びと配置の種類だけを数える。報酬・選択には使わない");
        }

        // v0.25.4（t52u）: 環境の消失を直前の自分の手の結果として学んだか。同じ手で学んだ後も消え続けるなら WARN
        foreach (var e in cur.Where(x => T(x) == "session_end" && x.TryGetProperty("env_loss", out _)).Take(1))
        {
            bool rep = e.TryGetProperty("env_loss_repeats", out var rv) && rv.ValueKind == System.Text.Json.JsonValueKind.True;
            Add("環境の消失", "ゲームの消失を直前の自分の手の結果として学ぶ（負の報酬の定数は置かない）", rep ? Level.Warn : Level.Info,
                Str(e, "env_loss") + (rep ? "／学んだ後も同じ手で 2 回以上消えた" : "") + "。人間の手の後の消失は記録だけ。再起動はエピソードの終わりでも AI の行動でもない");
        }

        // v0.23.0 ブロック C: エピソードの区切りの候補（記録と表示だけ。報酬には使わない）
        try
        {
            if (memory.Tables().Contains("episode_boundary_candidates"))
            {
                var ebc = memory.Query("SELECT state, arrivals, predecessors, recurrence, flow FROM episode_boundary_candidates WHERE status='candidate' ORDER BY arrivals DESC", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetDouble(3), r.IsDBNull(4) ? "" : r.GetString(4)));
                long evs = Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM episode_boundary_events"));
                long withdrawn = Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM episode_boundary_events WHERE kind='withdrawn'"));
                // 流れが同じ状態に留まるだけの候補は「入力に反応しない画面」の可能性もある（区切りかどうかは人が画面で確かめる）
                int stay = ebc.Count(c => c.Item5.Split(" → ").All(x => x == c.Item1));
                Add("区切り", "エピソードの区切りの候補", evs == 0 ? Level.NotTested : Level.Info,
                    evs == 0 ? "まだ計算していない（または条件を満たす状態がない）" :
                    $"候補 {ebc.Count}（うち同じ状態に留まる流れ {stay}）・出来事 {evs}（取り下げ {withdrawn}）" + string.Concat(ebc.Take(3).Select(c => $"／{c.Item1} 到着 {c.Item2}・前の状態 {c.Item3} 種類・流れのくり返し {c.Item4:P0}" + (textBook == null ? "" : $"・{textBook.Describe(c.Item1)}"))) + "。ゲーム固有の区切りの定義は持たず、再起動は区切りにしない。報酬・選択には使わない");
            }
        }
        catch (Exception ex) { Add("区切り", "エピソードの区切りの候補の読込", Level.Warn, ex.Message); }

        // セッション（停止理由・止まらなかったか・分離）
        foreach (var (e, isCur) in events.Select((e, i) => (e, current.Contains(i))).Where(x => T(x.e) == "session_end").Take(5).Select(x => (x.e, x.Item2)))
        {
            string reason = Str(e, "reason"), stop = Str(e, "stop_reason");
            bool diag = Bool(e, "diagnostic");   // v0.14.1 までの分離試験のログ（参考）
            var lvl = !isCur ? Level.Info : reason is "error" or "input_delivery_failed" or "input_action_table_failed" ? Level.Fail : reason is "input_unreachable" or "window_lost" or "abort" or "recovery_aborted" ? Level.Warn : Level.Ok;
            Add("停止", $"{(diag ? "診断" : "探索")} {Str(e, "t")}{(isCur ? "" : "（前回まで）")}", lvl, $"理由 {reason}（{stop}）・{Num(e, "steps")} 手・{Num(e, "duration_s")} 秒");
            if (e.TryGetProperty("ua_pauses_while_bg", out _))
            {
                double uaBg = Num(e, "ua_pauses_while_bg"), fg = Num(e, "turns_game_fg"), bg = Num(e, "turns_other_fg");
                Add("分離", $"ユーザー入力 {Str(e, "t")}", !isCur ? Level.Info : uaBg == 0 ? Level.Ok : Level.Fail,
                    $"背面中のユーザー操作で止まった回数 {uaBg}・無視したユーザー操作 {Num(e, "user_events_ignored")}・止めたユーザー操作 {Num(e, "user_events_pausing")}・ゲーム前面の手 {fg}／背面の手 {bg}");
            }
            if (isCur && !diag && Num(e, "steps") > 0)
            {
                // 仮説・新発見の欄の材料（件数だけ。内容は評価しない）。探索した手があるのに仮説の記録がなければ、表示の材料の作成が働いていない
                string Dict(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object ? string.Join("・", v.EnumerateObject().Select(p => $"{p.Name} {p.Value}")) : "";
                bool has = e.TryGetProperty("hypotheses_by_status", out var hv) && hv.ValueKind == JsonValueKind.Object;
                if (e.TryGetProperty("discoveries_by_kind", out _) || has)
                    Add("表示", $"仮説・新発見の欄の材料 {Str(e, "t")}", has && hv.EnumerateObject().Any() ? Level.Ok : Level.Warn,
                        $"仮説 {(has ? Dict("hypotheses_by_status") : "なし")}／新発見 {Dict("discoveries_by_kind")}／境目の近くの観測 {Num(e, "ambiguous_observations")} 手");
            }
            if (isCur && !diag && e.TryGetProperty("no_input_changes", out _))
            {
                // 入力なしの変化と、判定から外した所の中の変化（観測として残っているかの件数だけ。良し悪しは判定しない）
                Add("観測", $"入力なしの変化 {Str(e, "t")}", Level.Info,
                    $"手と手の間に画面が変わった {Num(e, "no_input_changes")} 回・そのうち別の画面になっていた {Num(e, "no_input_transitions")} 回・選び直した {Num(e, "rechosen")} 回");
                Add("観測", $"判定から外した所の中の変化 {Str(e, "t")}", Level.Info,
                    $"外した所があった手 {Num(e, "masked_turns")}・その中の変わったブロックの割合の平均 {Str(e, "masked_activity_mean")}・外した所だけが変わった手 {Num(e, "masked_only_turns")}");
            }
            if (isCur && !diag && e.TryGetProperty("input_sent", out _))
            {
                double sent = Num(e, "input_sent"), failed = Num(e, "input_failed"), obs = Num(e, "input_reaction_observed"), re = Num(e, "input_reacted");
                Add("入力", $"送信（A: OS・入力方式として送れたか） {Str(e, "t")}", reason == "input_delivery_failed" ? Level.Fail : failed == 0 ? (sent > 0 ? Level.Ok : Level.NotTested) : Level.Warn,
                    $"送信 {sent}・失敗 {failed}（続けて失敗したときだけ止まる）");
                Add("入力", $"画面の反応（B: 未知の画面の性質。合否に使わない） {Str(e, "t")}", Level.Info,
                    $"反応 {re}/{obs}・無反応の最長の連続 {Num(e, "longest_no_reaction")} 手");
                // 中断した手（停止ボタン・一時停止の途中で打ち切った手）は記録しないので、手数から引いて比べる（v0.17.0。実機 v0.16.0 で停止時の 1 手が FAIL になっていた）
                double aborted = e.TryGetProperty("turns_aborted", out _) ? Num(e, "turns_aborted") : (reason is "user_stop" or "emergency" ? 1 : 0);
                double steps = Num(e, "steps") - aborted, ag = Num(e, "agent_steps"), hu = Num(e, "human_steps");
                if (!e.TryGetProperty("turns_aborted", out _) && aborted > 0 && ag + hu == steps + aborted) steps += aborted;   // 旧版のログで最後の手が記録されていた場合
                Add("記録", $"行動の出どころ（agent／human） {Str(e, "t")}", ag + hu == steps ? Level.Ok : Level.Fail, $"攻略くん {ag}・人間 {hu}・合計 {ag + hu}／手数 {steps}");
                Add("操作", $"Human Control {Str(e, "t")}", hu > 0 ? Level.Ok : Level.NotTested, hu > 0 ? $"人間の手 {hu}・切替 {Num(e, "human_switches")} 回" : "この回は人間の操作なし");
                double sum = Num(e, "summaries"), gp = Num(e, "growth_points");
                // v0.21.0 のログは区切りの行（システムの出来事）もサマリーに数えていた: 区切りの数（システムの出来事の数以下）だけ多いのは合っている
                if (!e.TryGetProperty("system_separators", out _) && sum > Math.Min(steps, 500) && sum - Math.Min(steps, 500) <= Num(e, "sup_events")) sum = Math.Min(steps, 500);
                Add("表示", $"1手サマリー・成長グラフの材料 {Str(e, "t")}", steps == 0 ? Level.NotTested : sum == Math.Min(steps, 500 - Num(e, "system_separators")) && gp == Math.Min(steps, 4000) ? Level.Ok : Level.Fail,
                    $"サマリー {sum}・グラフの点 {gp}（手数 {steps}。サマリーは最新 500 手まで保持）");
                if (e.TryGetProperty("nochange_node_changed", out _))
                {
                    double nn = Num(e, "nochange_node_changed");
                    Add("ターン判定", $"画面の種類が変わったのに「変化なし」の手 {Str(e, "t")}", steps < 20 ? Level.NotTested : nn > 0.1 * steps ? Level.Warn : Level.Ok,
                        $"{nn}/{steps} 手（多いと変化の判定の基準が合っていない）");
                }
                double waits = Num(e, "wait_turns");
                Add("観測", $"待つ手（入力なしの中立の観測） {Str(e, "t")}", steps < 50 ? Level.NotTested : waits > 0 ? Level.Ok : Level.Warn,
                    $"待つ手 {waits}・時間切れを操作の効果としなかった手 {Num(e, "timeout_weak")}・時間的な変化を数えた画面 {Num(e, "temporal_screens")}");
                // v0.27.0 入力なしの自然な変化の基準: 手が 3 回以上ある画面すべてに入力なしの観測があるか。見分け: 操作の効果と入力なしの変化を比べられた組があるか
                if (e.TryGetProperty("nb_nodes", out _) && Num(e, "nb_nodes") >= 0)
                {
                    double nn2 = Num(e, "nb_nodes"), nc = Num(e, "nb_covered");
                    Add("観測", $"入力なしの基準（画面ごとの自然な変化） {Str(e, "t")}", nn2 == 0 ? Level.NotTested : nc >= nn2 ? Level.Ok : Level.Warn,
                        $"基準のある画面 {nc}/{nn2}（手が {3} 回以上の画面）・手と手の間の観測 {Num(e, "nb_gap_obs")}・待つ手 {Num(e, "nb_wait_obs")}");
                    double ap = Num(e, "attr_pairs"), ak = Num(e, "attr_known"), an = Num(e, "attr_natural");
                    Add("観測", $"操作と入力なしの変化の見分け {Str(e, "t")}", ak == 0 ? Level.NotTested : an > ak || ak > ap ? Level.Fail : Level.Ok,
                        $"比べられた組 {ak}/{ap}・入力なしの変化と区別できない組（操作の効果 {0.3:0.0} 未満） {an}");
                }
                // v0.27.2 毎手の文字の観測: 読めた割合・速さと手のペースへの影響・手ごとの文字の変化の検出・前景の鍵
                if (e.TryGetProperty("ocr_each_step", out var oes))
                {
                    double rq = Num(e, "ocr_requested"), sk = Num(e, "ocr_skipped"), fu = Num(e, "ocr_full"), rg = Num(e, "ocr_region"), ru = Num(e, "ocr_reused"), ca = Num(e, "ocr_cache"), fl = Num(e, "ocr_failed_steps");
                    double cov = fu + rg + ru + ca;
                    Add("文字", $"毎手の文字の読み取り {Str(e, "t")}", oes.ValueKind != JsonValueKind.True || rq == 0 ? Level.NotTested : cov == 0 && fl > 0 ? Level.Fail : cov >= MinTextCoverage * rq ? Level.Ok : Level.Warn,
                        $"読んだ手 {cov}/{rq}（全体 {fu}・一部 {rg}・変化なしで前の結果 {ru}・同じ見た目の結果 {ca}）・読み取り中で読まなかった {sk}・失敗 {fl}・読んだ面積の平均 {Str(e, "ocr_area_mean")}・速さ 中央値 {Str(e, "ocr_ms_median")} ms／p90 {Str(e, "ocr_ms_p90")}／最大 {Str(e, "ocr_ms_max")}");
                    double bt = Num(e, "busy_turns"), it = Num(e, "idle_turns"), bb = Num(e, "busy_boundary_ms"), ib = Num(e, "idle_boundary_ms"), bg = Num(e, "busy_gap_ms"), ig = Num(e, "idle_gap_ms");
                    bool slow = bb > PaceRatio * ib || bg > PaceRatio * ig;
                    Add("文字", $"文字の読み取りによる手のペースへの影響 {Str(e, "t")}", bt < PaceMinTurns || it < PaceMinTurns ? Level.NotTested : slow ? Level.Warn : Level.Ok,
                        $"読み取り中の手 {bt}・そうでない手 {it}／静止の判定まで 平均 {bb} ms 対 {ib} ms／前の手の静止から次の手の開始まで 平均 {bg} ms 対 {ig} ms（静止の判定を含まない）（{PaceRatio:0.00} 倍を超えたら WARN）");
                    double tp = Num(e, "text_pairs"), tc = Num(e, "text_changed"), th = Num(e, "text_hits"), tk = Num(e, "text_checks");
                    Add("文字", $"手ごとの文字の変化の検出 {Str(e, "t")}", tp == 0 ? Level.NotTested : tc > tp || th > tk ? Level.Fail : Level.Ok,
                        $"前後を比べた手 {tp}（文字が変わった {tc}・画素のハッシュが変わっていない所の差 {Num(e, "text_suppressed")}（v0.28.0 から文字の変化として数える。v0.27.3〜v0.27.4 は除いていた））・比べられなかった手 {Num(e, "text_unpaired")}・文字の変化の予測の当たり {th}/{tk}・手を選んだ時点の文字 届いていた {Num(e, "text_fresh")}／古い {Num(e, "text_stale")}／未観測 {Num(e, "text_pending")}");
                    Add("文字", $"前景の鍵（背景違いの同じ前景） {Str(e, "t")}", Level.Info, Str(e, "foreground"));
                    // v0.28.0: 文字の変化（観測）と、手が起こした文字の変化（評価）を分けて持つ。手が起こした分が変わった数を超えたら矛盾
                    if (e.TryGetProperty("text_caused_sum", out _))
                    {
                        double tcs = Num(e, "text_caused_sum"), bc = Num(e, "text_book_changed");
                        Add("文字", $"文字の変化のうち手が起こした分と手によらない分 {Str(e, "t")}", bc == 0 ? Level.NotTested : tcs > bc + 1e-6 || tcs < 0 ? Level.Fail : Level.Ok,
                            $"手の後に文字が変わった {bc}・うち手が起こしたと見込める分 {tcs}（残り {Math.Round(bc - tcs, 1)} は手によらない分。観測からは除かない）・入力なしの文字の観測 {Num(e, "text_natural_obs")}・文字の区画 {Str(e, "text_regions")}");
                        Add("観測", $"画面の区画の見分け（手で変わる所・手によらず変わる所） {Str(e, "t")}", Level.Info,
                            $"画素の区画 {Str(e, "pixel_regions")}・文字の区画 {Str(e, "text_regions")}・前景の鍵から外した手によらない行 {Num(e, "fg_natural_lines_excluded")}");
                        Add("予測", $"文字の変化が次の遷移の手がかりになるか {Str(e, "t")}", Level.Info, Str(e, "text_link") + "（予測にはまだ使わない。記録のみ）");
                        if (e.TryGetProperty("obs_built", out _))
                        {
                            double ob = Num(e, "obs_built"), os_ = Num(e, "obs_saved"), tn = Num(e, "obs_text_not_observed"), stp = Num(e, "turns");
                            Add("観測", $"1 手 = 1 完全な観測（構造化した観測） {Str(e, "t")}", ob == 0 ? Level.NotTested : os_ < ob || Num(e, "obs_failed") > 0 ? Level.Fail : Level.Ok,
                                $"作った観測 {ob}・保存 {os_}・失敗 {Num(e, "obs_failed")}／文字の層 観測できなかった手 {tn}・行 観測 {Num(e, "obs_lines_observed")}／継承（画素が同じ区画） {Num(e, "obs_lines_inherited")}・読み取り中で読まなかった手 {Num(e, "ocr_skipped")}・画素が同じ行で前の読みを使った（切り出しの読み直しの揺れ） {Num(e, "ocr_kept_unchanged_lines")}");
                            Add("観測", $"変化領域の追跡（画素の区画のつながり） {Str(e, "t")}", ob == 0 ? Level.NotTested : Level.Info,
                                $"追跡 ID {Num(e, "obs_tracks")}・前の手から続いた領域 {Num(e, "obs_continued_regions")}（意味は付けない）");
                            if (e.TryGetProperty("entity_created", out _))
                            {
                                Add("観測", $"局所パターンの候補と追跡・見た目の状態 {Str(e, "t")}", Level.Info,
                                    $"ノイズとして変化から外した区画 {Num(e, "cells_stabilized")}／変わった区画 {Num(e, "cells_changed")}（細かい格子 {Num(e, "fine_cells_stabilized")}／{Num(e, "fine_cells_changed")}）・新しい追跡 {Num(e, "entity_created")}・続いた {Num(e, "entity_continued")}・再同定 {Num(e, "entity_reidentified")}・位置が同じで見た目だけ変わった {Num(e, "entity_appearance_only")}（意味は付けない）" +
            (e.TryGetProperty("common_shift_steps", out _) ? $"／画面全体の共通のずれを差し引いた手 {Num(e, "common_shift_steps")}・絞って記録しなかった関係の組 {Num(e, "relations_pruned")}" : "") +
            (e.TryGetProperty("common_shift_checked", out _) ? $"（画面の過半が変わり共通のずれを調べた手 {Num(e, "common_shift_checked")}・ずれで説明できなかった {Num(e, "common_shift_unexplained")}{(e.TryGetProperty("common_shift_unexplained_floor", out _) ? $"〔ずらしてもそろう区画が少ない {Num(e, "common_shift_unexplained_floor")}・良くなった区画が過半でない {Num(e, "common_shift_unexplained_improved")}〕" : "")}{(e.TryGetProperty("common_shift_warp", out _) ? $"・場所によって違うずれ（行ごとのずれ・拡大縮小）で説明できた {Num(e, "common_shift_warp")}〔{Str(e, "common_shift_warp_dir")}〕" : "")}{(e.TryGetProperty("global_transform_compared", out _) ? $"・画面全体の変換の仮説を比べた {Num(e, "global_transform_compared")}〔最も確からしい仮説 {Str(e, "global_transform_decisions")}〕" : "")}）／前の見た目の状態に戻って同じ追跡で続けた {Num(e, "entity_periodic_returns")}・前の枠と大きく重なるので同じ追跡で続けた {Num(e, "entity_overlap_continued")}" : "") +
            (e.TryGetProperty("entity_grouped_steps", out _) ? $"／いっしょに変わり続ける隣の候補のまとまりがあった手 {Num(e, "entity_grouped_steps")}" : "") +
            (e.TryGetProperty("entity_large_suppressed", out _) ? $"・画面の 1/4 を超えるので候補にしなかった塊 {Num(e, "entity_large_suppressed")}" : ""));
                                Add("予測", $"局所の出来事の予測と外れ {Str(e, "t")}", Level.Info,
                                    $"{Str(e, "local_prediction")}・外れが続く（画面×手×出来事） {Num(e, "local_persistent_errors")}／手の選び方への入力（v0.29.4）: 呼んだ {Num(e, "local_info_calls")}・不確かさを上げた {Num(e, "local_info_raised")}・平均 {Str(e, "local_info_mean")}・外れが続くので外した {Num(e, "local_info_excluded")}" + (e.TryGetProperty("local_context_used", out _) ? $"／局所の文脈（直前の候補の位置）で分けた予測を使った手 {Num(e, "local_context_used")}（v0.30.9）" : "") + (e.TryGetProperty("entity_static_observed", out _) ? $"／静止したまま見た目が変わらず存続した候補（手ごとの延べ・v0.31.3） {Num(e, "entity_static_observed")}" : "") + (e.TryGetProperty("track_frag", out var tf) && tf.GetString() is { } tfs ? $"／追跡の分断（v0.31.6）: {tfs}" : "") + (e.TryGetProperty("track_quality", out var tq) && tq.GetString() is { } tqs ? $"／追跡の評価（v0.32.0。大きさ別・見せ方だけ）: {tqs}" : "") + (e.TryGetProperty("entity_exp", out var ex) && ex.GetString() is { } exs ? $"／Entity ごとの経験（v0.32.0 P-6。予測の当たり）: {exs}" : "") + (e.TryGetProperty("app_table", out var at) && at.GetString() is { } ats ? $"／見た目の表（v0.31.5。数えた/予測した/当たった/組）: {ats}" : "") + (e.TryGetProperty("num_table", out var nt) && nt.GetString() is { } nts ? $"／数字の差の表（数えた/予測した/当たった）: {nts}" : "") + (e.TryGetProperty("covary_groups", out var cg) && cg.ValueKind == System.Text.Json.JsonValueKind.String ? $"／一緒に変わる区画の群（v0.31.0）: {cg.GetString()}" : ""));
                                if (e.TryGetProperty("joint_predictions", out _))
                                    Add("予測", $"World Model と局所の予測の突き合わせ {Str(e, "t")}", Level.Info,
                                        $"並べた手 {Num(e, "joint_predictions")}・次の画面は当たるのに局所の外れが続く組（画面の見分けが局所には粗い候補。分けはしない） {Num(e, "coarse_candidates")}：{Str(e, "coarse_top")}");
                                if (e.TryGetProperty("split_estimates", out _))
                                {
                                    Add("予測", $"局所の位置で分けたときの試算（分けない） {Str(e, "t")}", Level.Info,
                                        $"観測 20 回以上の候補 {Num(e, "split_estimates")}・分けると外れが減る（log BF ≥ ln10 かつ 1 つ抜きの驚きが下がる） {Num(e, "split_would_help")}：{Str(e, "split_top")}");
                                    Add("目的", $"操作の並びの局所の効果 {Str(e, "t")}", Level.Info,
                                        $"記録した並び {Num(e, "skill_sequences")}・効果のある並び（出来事の割合が半分を超え 6 回以上） {Num(e, "skill_with_effect")}：{Str(e, "skill_effects")}");
                                }
                            }
                        }
                        if (e.TryGetProperty("wait_info_n", out _))
                        {
                            double wn = Num(e, "wait_info_n");
                            Add("観測", $"待つ手の情報の値打ち（入力なしの基準の、手の効果の見分けに要る不確かさ） {Str(e, "t")}", wn == 0 ? Level.NotTested : Level.Info,
                                $"記録した手 {wn}・平均 {Num(e, "wait_info_mean")}（始め {Num(e, "wait_info_first")} → 終わり {Num(e, "wait_info_last")}）・待つ手 {Num(e, "nb_wait_obs")}（固定の確率では増やさない）");
                            Add("探索", $"点数の項ごとの短期の変化と長期の寄与 {Str(e, "t")}", Num(e, "score_outcomes") == 0 ? Level.NotTested : Level.Info,
                                $"{Str(e, "score_contribution")}／{Str(e, "score_halves")}（重みは変えない。記録のみ）");
                        }
                        Add("探索", $"手の選び方の内訳の記録 {Str(e, "t")}", Num(e, "score_logged") == 0 ? Level.NotTested : Level.Info,
                            $"記録した手 {Num(e, "score_logged")}・点数で選んだ手 {Num(e, "score_scored")}・文字の項がなければ 1 位が入れ替わった手 {Num(e, "score_text_top_flips")}・{Str(e, "score_breakdown")}");
                    }
                }
                // v0.16.0 自己生成の目的: 作られたか（なければ NOT_TESTED）・目的の手が探索を占めていないか（3 割まで。少し余裕を見て 0.35 を超えたら WARN）。達成の数は評価しない
                if (e.TryGetProperty("goals_created", out _))
                {
                    double gc = Num(e, "goals_created"), share = Num(e, "goal_share");
                    Add("目的", $"自己生成の目的（経験から作る到達・確かめ直し） {Str(e, "t")}", gc == 0 || ag < 50 ? Level.NotTested : share > 0.35 ? Level.Warn : Level.Ok,   // v0.27.4: 攻略くんの手が 50 未満（待つ手の項目と同じ最小の手数）では割合を評価しない
                        $"作成 {gc}・達成 {Num(e, "goals_achieved")}・失敗 {Num(e, "goals_failed")}・放棄 {Num(e, "goals_abandoned")}・直近の手に占める割合 {share:0.00}・スキルを使った道筋 {Num(e, "goal_skill_uses")}・目的から作った並びの候補 {Num(e, "skill_candidates")}（達成の数は良し悪しに使わない）");
                }
                // v0.20.0 計画: Horizon・引き直し・小目的・区間の再利用（記録の整合だけを見る。値の大きさは良し悪しに使わない）
                if (e.TryGetProperty("planning_horizon", out _))
                {
                    double h = Num(e, "planning_horizon"), rg = Num(e, "replan_goals"), ra = Num(e, "replan_achieved"), sgt = Num(e, "subgoals"), sgr = Num(e, "subgoals_reached");
                    bool consistent = h >= 1 && ra <= rg && sgr <= sgt;
                    Add("目的", $"計画の届く手数・引き直し・小目的 {Str(e, "t")}", !consistent ? Level.Warn : Num(e, "goals_created") == 0 ? Level.NotTested : Level.Ok,
                        $"Horizon {h} 手（変更 {Num(e, "horizon_changes")} 回）・達成した最長 {Num(e, "max_goal_length")} 手・引き直し {Num(e, "replans")} 回（引き直した目的の達成 {ra}/{rg}）・小目的 {sgr}/{sgt}・既存の並びを目的で使った区間 {Num(e, "goal_segment_support")}");
                }
            }
            if (isCur && e.TryGetProperty("supervisor_state", out _)) AddSupervisor(e, Add);
            if (isCur && T(e) == "session_end" && e.TryGetProperty("input_sent", out _))
            {
                var sep = cur.LastOrDefault(x => T(x) == "separation_check");
                if (sep.ValueKind == JsonValueKind.Object)
                {
                    string v = Str(sep, "verdict");
                    Add("分離", $"ユーザーの作業との分離（10分以上・半分以上背面・別アプリで作業） {Str(sep, "t")}", v == "pass" ? Level.Ok : v == "fail" ? Level.Fail : Level.NotTested, Str(sep, "notes"));
                }
            }
            if (e.TryGetProperty("late_changes", out _) && Num(e, "steps") > 0)
            {
                double late = Num(e, "late_changes"), steps = Num(e, "steps");
                Add("ターン判定", $"判定が早すぎた手 {Str(e, "t")}", !isCur ? Level.Info : late / steps > LateChangeWarnRate ? Level.Warn : Level.Ok, $"{late}/{steps}（「変化なし」の後に画面が変わった）");
            }
            if (e.TryGetProperty("user_restricted_steps", out _) && Num(e, "steps") > 0)
                Add("探索", $"人が止めた入力のある手（自律探索で使わない入力） {Str(e, "t")}", Level.Info, $"{Num(e, "user_restricted_steps")}/{Num(e, "steps")} 手・いま止めている入力 {Num(e, "user_disabled_now")}（人の側の制約。ゲームの知識ではない）");
            if (e.TryGetProperty("delayed_recorded", out _) && Num(e, "steps") > 0)
                Add("Temporal", $"遅れて来た変化の記録（temporal.delayed） {Str(e, "t")}", Level.Info, $"{Num(e, "delayed_recorded")}/{Num(e, "steps")} 手（前の手の静止の後、次の入力の前に変わった区画を記録）");
        }

        // 入力方式の選択（v0.15.0: 送達＝A だけで選ぶ。起動時にゲームへキーは送らない）
        var sel = cur.LastOrDefault(e => T(e) == "input_selected");
        if (sel.ValueKind == JsonValueKind.Object)
            Add("入力", "入力方式の選択（今回の起動）", Str(sel, "backend") is { Length: > 0 } bk && bk != "null" ? Level.Ok : Level.Fail, $"{Str(sel, "backend")}・{Str(sel, "reason")}");
        else Add("入力", "入力方式の選択（今回の起動）", Level.NotTested, "今回の起動では探索を開始していません");
        var adapt = cur.Where(e => T(e) == "turn_timeout_adapted").Select(e => Num(e, "nochange_ms")).Reverse().ToList();   // ログは新しい順に読んでいるので時刻順に戻す
        if (adapt.Count > 0)
        {
            int flips = adapt.Zip(adapt.Skip(1)).Count(z => Math.Abs(z.First - z.Second) > 0.3 * Math.Max(z.First, z.Second));
            Add("ターン判定", "「変化なし」の待ち時間の推移（今回の起動）", Level.Info, $"調整 {adapt.Count} 回・最小 {adapt.Min()}ms・最大 {adapt.Max()}ms・最後 {adapt[^1]}ms・大きく変わった回数 {flips}");
        }
        var turns = cur.Where(e => T(e) == "turn").ToList();
        if (turns.Count > 0)
        {
            var kinds = turns.GroupBy(e => Str(e, "kind")).ToDictionary(g => g.Key, g => g.Count());
            int changed = turns.Count(e => Bool(e, "net_change"));
            // v0.22.2: 画面が変わった手が 0 でも、取得のフレームが手ごとに届いているなら、取得は働いている（どの操作にも反応しない未知の画面の性質かもしれない）。
            // 実機 v0.22.1 で、反応しない画面のまま起動し直した 23 手が FAIL になった。フレームが届いていないときだけ FAIL（システムの失敗）
            double frames = turns.Where(e => e.TryGetProperty("frames", out _)).Select(e => Num(e, "frames")).DefaultIfEmpty(double.NaN).Average();
            if (double.IsNaN(frames)) frames = Try(() => Convert.ToDouble(memory.Scalar("SELECT AVG(json_extract(o.diff_metrics_json,'$.frames')) FROM outcomes o JOIN actions a ON a.action_id=o.action_id JOIN turns t ON t.turn_id=a.turn_id WHERE t.run_id=(SELECT MAX(run_id) FROM runs)") ?? double.NaN), double.NaN);
            bool flowing = !double.IsNaN(frames) && frames > 1;
            Add("入力", "今回の起動の手（ログ）", changed > 0 ? Level.Ok : flowing ? Level.Warn : Level.Fail,
                $"{turns.Count} 手・画面が変わった手 {changed}・判定の内訳 {string.Join(" ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}" +
                (changed == 0 ? (flowing ? $"・取得は 1 手あたり {frames:0} フレーム届いている（どの操作にも反応しない画面かもしれない。人が画面を確かめる）" : "・取得のフレームが届いていない（システムの失敗）") : ""));
            // 時間切れの手の割合: 画面が落ち着かない（常時アニメなど）と、どの手も時間切れになり「変わった／変わらない」の観測が働かない。目の不具合でありプレイの良し悪しではない
            int timeouts = kinds.GetValueOrDefault("Timeout");
            if (turns.Count >= TimeoutMinTurns)
            {
                double share = (double)timeouts / turns.Count;
                var byScreen = turns.Where(e => Str(e, "kind") == "Timeout").GroupBy(e => Str(e, "node").Split('|')[0]).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} {g.Count()}");
                // 後半の割合: 常時アニメのマスクを学んだ後は減るはず
                var recent = turns.Take(turns.Count / 2).ToList();   // ログは新しい順に読んでいる＝先頭の半分が後半
                double recentShare = recent.Count == 0 ? 0 : (double)recent.Count(e => Str(e, "kind") == "Timeout") / recent.Count;
                int masks = cur.Count(e => T(e) == "motion_mask" && !Bool(e, "too_wide")), wide = cur.Count(e => T(e) == "motion_mask" && Bool(e, "too_wide"));
                Add("ターン判定", "時間切れの手の割合（今回の起動）", recentShare > TimeoutWarnShare ? Level.Warn : Level.Ok,
                    $"全体 {timeouts}/{turns.Count}（{share:P0}）・後半 {recentShare:P0}・画面ごと {string.Join(" ", byScreen)}・常時アニメのマスクの学習 {masks} 回・広すぎて隠さなかった {wide} 回");
            }
        }
        // 1手あたりの処理時間（手と手の間隔 − 画面が落ち着くまでの待ち）。今回の起動を前回までの起動と比べる（値の良し悪しはゲームに依らない）
        double? Overhead(List<JsonElement> log)
        {
            // v0.19.7: 人間の手（Human Control）は人の押す間隔を含むので除く。source の無い古いログは control_switch の手番から判断する
            var sw = log.Where(e => T(e) == "control_switch").Select(e => (Step: (int)Num(e, "step"), Human: Bool(e, "human"))).OrderBy(x => x.Step).ToList();
            bool IsHuman(JsonElement e)
            {
                var src = Str(e, "source"); if (src.Length > 0) return src == "human";
                int st = (int)Num(e, "step"); bool h = false; foreach (var x in sw) { if (x.Step <= st) h = x.Human; else break; } return h;
            }
            var ts = log.Where(e => T(e) == "turn" && !IsHuman(e)).Select(e => (At: DateTimeOffset.TryParse(Str(e, "t"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ? a : (DateTimeOffset?)null, B: Num(e, "boundary_ms")))
                .Where(x => x.At != null).OrderBy(x => x.At).ToList();   // ログは新しい順に読んでいるので時刻順に並べ直す
            var d = ts.Zip(ts.Skip(1)).Select(z => (z.Second.At!.Value - z.First.At!.Value).TotalMilliseconds - z.Second.B).Where(x => x >= 0 && x < 60_000).OrderBy(x => x).ToList();
            return d.Count >= 10 ? d[d.Count / 2] : null;
        }
        if (perLog.Count > 0 && Overhead(perLog[0]) is double ohNow)
        {
            var prev = perLog.Skip(1).Select(Overhead).OfType<double>().ToList();
            double? prevMed = prev.Count > 0 ? prev.OrderBy(x => x).ElementAt(prev.Count / 2) : null;
            Add("ターン判定", "1手あたりの処理時間（今回の起動・中央値）", prevMed is double pm && ohNow > OverheadWarnRatio * pm ? Level.Warn : Level.Ok,
                $"{ohNow:0} ms（手と手の間隔から画面が落ち着くまでの待ちを引いた時間）" + (prevMed is double pm2 ? $"・前回までの起動の中央値 {pm2:0} ms" : "・比べる前回の記録なし"));
        }
        // v0.19.2: 開始の設定・グラフの種類・ウィンドウの位置の復元（表示だけ）。保存していた値と起動時に戻した値が合うかを見る
        var rest = cur.FirstOrDefault(e => T(e) == "view_settings_restored");
        if (rest.ValueKind == JsonValueKind.Object)
        {
            var mism = new List<string>();
            foreach (var (k, sk) in new[] { ("run_seconds", "saved_run_seconds"), ("max_steps", "saved_max_steps"), ("graph", "saved_graph") })
                if (Str(rest, sk) is { } sv && sv != "-" && sv != Str(rest, k)) mism.Add(k);
            string win = Str(rest, "window") ?? "-";
            bool anySaved = win != "none" || new[] { "saved_run_seconds", "saved_max_steps", "saved_graph" }.Any(k => Str(rest, k) is { } v && v != "-");
            Add("表示", "開始の設定・グラフ・ウィンドウの復元（今回の起動）", mism.Count > 0 || win == "invalid" ? Level.Warn : anySaved ? Level.Ok : Level.NotTested,
                $"実行時間 {Str(rest, "run_seconds")} 秒（保存 {Str(rest, "saved_run_seconds")}）・手数上限 {Str(rest, "max_steps")}（保存 {Str(rest, "saved_max_steps")}）・グラフ {Str(rest, "graph")}（保存 {Str(rest, "saved_graph")}）・ウィンドウ {win}" +
                (mism.Count > 0 ? "・合わない: " + string.Join(", ", mism) : "") + (anySaved ? "" : "（前回までの保存なし。次の起動で確かめる）"));
        }
        // 表示の設定（右側の欄の畳む・開く）。表示だけの設定なので、読み込み・保存ができたかだけを見る
        var uiEv = cur.Where(e => T(e) is "view_settings_loaded" or "view_settings_saved" or "view_settings_error").ToList();
        if (uiEv.Count > 0)
        {
            var ld = uiEv.FirstOrDefault(e => T(e) == "view_settings_loaded");
            int saved = uiEv.Count(e => T(e) == "view_settings_saved"), errs = uiEv.Count(e => T(e) == "view_settings_error");
            bool broken = ld.ValueKind == JsonValueKind.Object && Str(ld, "problem") is { Length: > 0 } pr && pr != "-";
            // 前回の起動で最後に保存した状態（欄ごとの最後の保存）と、今回の起動で読み込んだ畳んだ欄の数が合うか（復元の確認）
            var prevSaves = perLog.Skip(1).FirstOrDefault(l => l.Any(e => T(e) == "view_settings_saved"));
            int? expectCollapsed = null;
            if (prevSaves != null)
            {
                var last = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var e in prevSaves.Where(e => T(e) == "view_settings_saved").Reverse()) last[Str(e, "panel")] = Bool(e, "collapsed");   // 新しい順に読んでいるので時刻順に戻す
                expectCollapsed = last.Count(kv => kv.Value);
            }
            double loadedCollapsed = ld.ValueKind == JsonValueKind.Object ? Num(ld, "collapsed") : 0;
            bool restoredOk = expectCollapsed == null || loadedCollapsed >= expectCollapsed;
            Add("表示", "右側の欄の畳む・開くの設定（今回の起動）", errs > 0 || broken || !restoredOk ? Level.Warn : Level.Ok,
                $"起動時に畳んでいた欄 {loadedCollapsed}／{(ld.ValueKind == JsonValueKind.Object ? Num(ld, "panels") : 0)}" +
                (expectCollapsed is int ex ? $"（前回の起動で畳んだまま保存した欄 {ex}・{(restoredOk ? "復元した" : "復元されていない")}）" : "（前回の起動の保存なし）") +
                $"・保存 {saved} 回・保存の失敗 {errs}" + (broken ? "・壊れた設定ファイルを退避して既定の表示で始めた" : ""));
        }
        int rejected = cur.Count(e => T(e) == "input_rejected");
        if (rejected > 0) Add("入力", "送信拒否", Level.Warn, $"{rejected} 件");

        // 記録の整合（実行単位ごと）
        var runs = Try(() => memory.Query("SELECT r.run_id, r.turns_total, (SELECT COUNT(*) FROM turns t WHERE t.run_id=r.run_id), " +
            "(SELECT COUNT(*) FROM experience_stream s WHERE s.run_id=r.run_id), r.ended_at IS NOT NULL, COALESCE(r.policy_version,''), " +
            "(SELECT COUNT(*) FROM turns t WHERE t.run_id=r.run_id AND t.boundary_kind='aborted') FROM runs r ORDER BY r.run_id DESC LIMIT 5",
            r => (Id: r.GetInt64(0), Total: r.IsDBNull(1) ? 0 : r.GetInt32(1), Turns: r.GetInt32(2), Stream: r.GetInt32(3), Ended: r.GetInt32(4) == 1, Policy: r.GetString(5), Aborted: r.GetInt32(6))), new());
        foreach (var r in runs)
        {
            // 連続履歴に入るのは結果の出た手だけ（停止で打ち切った手は入れない）。v5 より前の探索以外の実行は 0
            bool streamOk = r.Stream == 0 || r.Stream == r.Turns - r.Aborted;
            bool ok = r.Ended && r.Total == r.Turns && streamOk;
            Add("記録", $"実行 #{r.Id}", ok ? Level.Ok : Level.Warn, $"終了記録 {(r.Ended ? "あり" : "なし")}・turns_total {r.Total}・turns {r.Turns}（打ち切り {r.Aborted}）・連続履歴 {r.Stream}");
        }
        var gaps = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM experience_stream a WHERE a.seq > (SELECT MIN(seq) FROM experience_stream) AND NOT EXISTS (SELECT 1 FROM experience_stream b WHERE b.seq = a.seq - 1)") ?? 0L), -1L);
        long streamRows = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM experience_stream") ?? 0L), -1L);
        Add("記録", "連続経験履歴", gaps == 0 ? Level.Ok : Level.Warn, $"{streamRows} 手・通し番号の欠け {gaps}");

        // 学習の更新（量だけ。内容の良し悪しは見ない）
        long trans = Try(() => Convert.ToInt64(memory.Scalar("SELECT COALESCE(SUM(n),0) FROM mode_transitions") ?? 0L), -1L);
        long pats = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM sequence_patterns") ?? 0L), -1L);
        long rev = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM action_reversibility") ?? 0L), -1L);
        long modes = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM screen_modes") ?? 0L), -1L);
        Add("学習", "更新された量", trans >= 0 && pats >= 0 ? Level.Info : Level.Fail, $"遷移の合計 {trans}・画面の種類 {modes}・可逆性の行 {rev}・系列のパターン {pats}");
        var byKind = Try(() => memory.Query("SELECT kind, COUNT(*), COALESCE(SUM(n),0) FROM sequence_patterns GROUP BY kind ORDER BY kind", r => $"{r.GetString(0)} {r.GetInt32(1)} 個（回数の計 {r.GetInt64(2)}）"), new List<string>());
        if (byKind.Count > 0) Add("学習", "系列のパターンの内訳（件数だけ。意味は解釈しない）", Level.Info, string.Join("・", byKind));
        // v6 の汎用の機構（量だけ。意味は解釈しない）
        long rmRows = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM reaction_maps WHERE cell >= 0") ?? 0L), -1L);
        long rmPairs = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM reaction_maps WHERE cell < 0") ?? 0L), -1L);
        long seqs = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM action_sequences") ?? 0L), -1L);
        long seqTries = Try(() => Convert.ToInt64(memory.Scalar("SELECT COALESCE(SUM(n),0) FROM mode_transitions WHERE logical_key LIKE 'ActionSequence #%'") ?? 0L), -1L);
        long vis = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM visual_states") ?? 0L), -1L);
        long tg = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(DISTINCT trans_group) FROM visual_states WHERE trans_group IS NOT NULL") ?? 0L), -1L);
        long splits = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM state_splits") ?? 0L), -1L);
        Add("学習", "反応の対応表・操作の並び・見た目の状態（件数だけ）", rmRows >= 0 && seqs >= 0 && vis >= 0 ? Level.Info : Level.Fail,
            $"反応の対応表 {rmPairs} 組（反応のあった区画の行 {rmRows}）・操作の並び {seqs} 個（試した回数 {seqTries}）・見た目の状態 {vis} 個・遷移の群 {tg} 個・ノードの分割 {splits} 件");
        long seqActive = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM action_sequences WHERE status='active'") ?? 0L), -1L);
        var seqEv = Try(() => memory.Query("SELECT kind, COUNT(*) FROM action_sequence_events GROUP BY kind ORDER BY kind", r => $"{r.GetString(0)} {r.GetInt32(1)}"), new List<string>());
        var seqOrigin = Try(() => memory.Query("SELECT origin, COUNT(*), SUM(status='active') FROM action_sequences GROUP BY origin ORDER BY origin", r => $"{(r.GetString(0) == "goal" ? "目的の達成から" : "連続履歴から")} {r.GetInt32(1)}（選択中 {r.GetInt32(2)}）"), new List<string>());
        Add("学習", "操作の並びの出どころ（スキルの芽。件数だけ）", Level.Info, seqOrigin.Count == 0 ? "なし" : string.Join("・", seqOrigin));
        long reN = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM stream_reinterpretations") ?? 0L), -1L);
        long dtN = Try(() => Convert.ToInt64(memory.Scalar("SELECT COALESCE(SUM(n),0) FROM derived_transitions") ?? 0L), -1L);
        var pri = Try(() => memory.Query("SELECT origin, COUNT(*) FROM experience_priors GROUP BY origin ORDER BY origin", r => $"{r.GetString(0)} {r.GetInt32(1)}"), new List<string>());
        // 読み直した手は直接の記録（連続履歴）の中にある手だけ（存在しない手を指していたら不整合）
        long orphan = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM stream_reinterpretations ri WHERE NOT EXISTS (SELECT 1 FROM experience_stream s WHERE s.seq = ri.seq)") ?? 0L), -1L);
        Add("学習", "経験の出どころ（直接・読み直し・弱い事前の推定。件数だけ）", orphan == 0 && seqActive >= 0 ? Level.Ok : Level.Warn,
            $"並びの候補 選択中 {seqActive}／記録 {seqs}（状態の変化 {(seqEv.Count == 0 ? "なし" : string.Join("・", seqEv))}）・読み直した手 {reN}（読み直した遷移 {dtN}・指す手のない読み直し {orphan}）・弱い事前の推定 {(pri.Count == 0 ? "なし" : string.Join("・", pri))}");
        // v8: 予測・比較・修正・判断の記録（件数と整合だけ。当たりの割合などの成績は見ない）
        long decN = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM decision_records") ?? 0L), -1L);
        long decChecked = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM decision_records WHERE actual_next IS NOT NULL") ?? 0L), -1L);
        long revN = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM model_revisions") ?? 0L), -1L);
        long decOrphan = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM decision_records d WHERE d.seq IS NOT NULL AND NOT EXISTS (SELECT 1 FROM experience_stream s WHERE s.seq = d.seq)") ?? 0L), -1L);
        long revDangling = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM decision_records d WHERE d.revision_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM model_revisions m WHERE m.revision_id = d.revision_id)") ?? 0L), -1L);
        var decRules = Try(() => memory.Query("SELECT rule, COUNT(*) FROM decision_records GROUP BY rule ORDER BY rule", r => $"{r.GetString(0)} {r.GetInt32(1)}"), new List<string>());
        var priEv = Try(() => memory.Query("SELECT kind, COUNT(*) FROM experience_prior_events GROUP BY kind ORDER BY kind", r => $"{r.GetString(0)} {r.GetInt32(1)}"), new List<string>());
        // 長期の運用: 記録の大きさ（件数と平均の大きさだけ）
        double decBytes = Try(() => Convert.ToDouble(memory.Scalar("SELECT COALESCE(AVG(COALESCE(length(candidates_json),0) + 200), 0) FROM decision_records") ?? 0.0), -1.0);
        double dbMb = Try(() => Convert.ToDouble(memory.Scalar("SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()") ?? 0L) / (1024.0 * 1024.0), -1.0);
        Add("DB", "memory.sqlite の大きさ", Level.Info, $"{dbMb:0.0} MB・判断の記録 1 件あたり約 {decBytes:0} バイト（{decN} 件）");
        Add("学習", "判断・予測・修正の記録（件数と整合）", decN >= 0 && decOrphan == 0 && revDangling == 0 ? Level.Ok : Level.Warn,
            $"判断の記録 {decN}（結果と比べたもの {decChecked}・規則 {(decRules.Count == 0 ? "なし" : string.Join("・", decRules))}）・内部モデルの修正 {revN}・指す手のない判断の記録 {decOrphan}・指す修正のない記録 {revDangling}・弱い事前の推定の変化 {(priEv.Count == 0 ? "なし" : string.Join("・", priEv))}");
        if (runs.Count > 0 && runs[0].Stream > 0)
        {
            long rid0 = runs[0].Id;
            long decRun = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM decision_records d JOIN experience_stream s ON s.seq = d.seq WHERE s.run_id=$r", null, ("$r", rid0)) ?? 0L), -1L);
            // v0.11.0 より前の版の実行には判断の記録がない（情報だけ）。この版の実行では手ごとに1件
            Add("学習", $"実行 #{rid0} の判断の記録", decRun == runs[0].Stream ? Level.Ok : decRun == 0 ? Level.Info : Level.Warn,
                decRun == 0 ? "なし（判断の記録を残す前の版の実行）" : $"{decRun}/{runs[0].Stream} 手");
            if (memory.SchemaVersion >= 10)
            {
                // v10: 行動の出どころが連続履歴と判断の記録で一致しているか・入力なしの変化の記録（件数と整合だけ）
                long srcMismatch = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM decision_records d JOIN experience_stream s ON s.seq = d.seq WHERE s.run_id=$r AND d.action_source <> s.action_source", null, ("$r", rid0)) ?? 0L), -1L);
                var bySrc = Try(() => memory.Query("SELECT action_source, COUNT(*), SUM(action='wait') FROM experience_stream WHERE run_id=$r GROUP BY action_source ORDER BY action_source", r => $"{r.GetString(0)} {r.GetInt32(1)}（待つ手 {r.GetInt32(2)}）", ("$r", rid0)), new List<string>());
                Add("記録", $"実行 #{rid0} の行動の出どころ（DB）", srcMismatch == 0 ? Level.Ok : Level.Fail, $"{string.Join("・", bySrc)}・連続履歴と判断の記録の不一致 {srcMismatch}");
                long ni = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM no_input_events WHERE run_id=$r", null, ("$r", rid0)) ?? 0L), -1L);
                long niBad = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM no_input_events WHERE run_id=$r AND (from_node IS NULL OR to_node IS NULL)", null, ("$r", rid0)) ?? 0L), -1L);
                long ms = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM screen_motion") ?? 0L), -1L);
                long ge = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM goal_events WHERE run_id=$r", null, ("$r", rid0)) ?? 0L), -1L);
                long geBad = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM goal_events WHERE run_id=$r AND (origin IS NULL OR origin = '')", null, ("$r", rid0)) ?? 0L), -1L);
                Add("記録", $"実行 #{rid0} の目的の記録（DB）", ge < 0 || geBad != 0 ? Level.Fail : Level.Ok, $"目的の出来事 {ge} 件・出どころのない行 {geBad}");
                Add("記録", $"実行 #{rid0} の入力なしの変化（DB）", ni < 0 || niBad != 0 ? Level.Fail : Level.Ok, $"手と手の間の変化 {ni} 件・欠けた行 {niBad}・動き続ける所を数えた画面 {ms}");
            }
            // 予測の確率のずれ: 「次の画面は X（確率 p）」と予測した手を p の大きさで 10 段に分け、段ごとの p の平均と当たった割合の差を手数で重みづけて平均する。
            // 予測の確率が当たる割合を表していれば小さい。ゲームの内容ではなく予測の仕組みの健全性
            var cal = Try(() => memory.Query("SELECT d.p_predicted, d.hit FROM decision_records d JOIN experience_stream s ON s.seq = d.seq WHERE s.run_id=$r AND d.hit IS NOT NULL AND d.p_predicted IS NOT NULL AND d.predicted_next IS NOT NULL",
                r => (r.GetDouble(0), r.GetInt32(1) != 0), ("$r", rid0)), new List<(double, bool)>());
            if (cal.Count >= CalibrationMinChecks)
            {
                var (ece, mp, hr) = Calibration(cal);
                Add("予測", $"実行 #{rid0} の予測の確率と当たった割合", ece > CalibrationWarnGap ? Level.Warn : Level.Ok,
                    $"ずれ {ece:0.000}（{cal.Count} 手。予測の確率の平均 {mp:0.00}・当たった割合 {hr:0.00}）");
            }
        }
        // v9: 分割の一生（分割・読み直し・取り消し・分け直し）・並びの実行・観測の確かさ・判断の記録の要約（件数と整合だけ）
        if (memory.SchemaVersion >= 9)
        {
            long pendingSplits = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM state_splits WHERE status='active' AND reclassified_at IS NULL") ?? 0L), -1L);
            var splitSt = Try(() => memory.Query("SELECT status, COUNT(*) FROM state_splits GROUP BY status ORDER BY status", r => $"{r.GetString(0)} {r.GetInt32(1)}"), new List<string>());
            var splitEv = Try(() => memory.Query("SELECT kind, COUNT(*) FROM state_split_events GROUP BY kind ORDER BY kind", r => $"{r.GetString(0)} {r.GetInt32(1)}"), new List<string>());
            var reCause = Try(() => memory.Query("SELECT cause, COUNT(*) FROM stream_reinterpretations GROUP BY cause ORDER BY cause", r => $"{r.GetString(0)} {r.GetInt32(1)}"), new List<string>());
            Add("学習", "画面の分割・読み直し・取り消し（件数と整合）", pendingSplits == 0 ? Level.Ok : Level.Warn,
                $"分割 {(splitSt.Count == 0 ? "なし" : string.Join("・", splitSt))}・出来事 {(splitEv.Count == 0 ? "なし" : string.Join("・", splitEv))}・読み直した手の理由 {(reCause.Count == 0 ? "なし" : string.Join("・", reCause))}・まだ読み直していない分割 {pendingSplits}");
            long execN = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM sequence_executions") ?? 0L), -1L);
            long execDangling = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM sequence_executions e WHERE NOT EXISTS (SELECT 1 FROM action_sequences a WHERE a.sequence_id = e.sequence_id)") ?? 0L), -1L);
            long blocks = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM decision_rollup_blocks") ?? 0L), -1L);
            long rollRows = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM decision_rollups") ?? 0L), -1L);
            long maxSeq = Try(() => Convert.ToInt64(memory.Scalar("SELECT COALESCE(MAX(seq), -1) FROM decision_records") ?? -1L), -1L);
            long dueBlocks = maxSeq < 0 ? 0 : (maxSeq + 1) / 10_000;
            // 1 万手あたりの増え方の見込み（判断の記録だけ。元の記録は消さない）
            double perBlockMb = decBytes > 0 ? decBytes * 10_000 / (1024.0 * 1024.0) : 0;
            Add("DB", "並びの実行の記録・判断の記録の要約（件数と整合）", execDangling == 0 && blocks >= dueBlocks ? Level.Ok : Level.Warn,
                $"並びの実行の記録 {execN}（指す並びのないもの {execDangling}）・要約した区間 {blocks}/{dueBlocks}（要約の行 {rollRows}）・判断の記録は 1 万手あたり約 {perBlockMb:0.0} MB 増える（元の記録は消さない）");
            if (runs.Count > 0 && runs[0].Stream > 0)
            {
                long rid9 = runs[0].Id;
                var obs = Try(() => memory.Query("SELECT COUNT(d.obs_vis_ratio), COUNT(d.frame_age_ms), COALESCE(SUM(d.obs_ambiguous),0), COUNT(*) FROM decision_records d JOIN experience_stream s ON s.seq = d.seq WHERE s.run_id=$r",
                    r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)), ("$r", rid9)).First(), (0L, 0L, 0L, 0L));
                // 境目の近くの観測の数は見るだけ（多い・少ないで良し悪しは決めない）。記録があるかどうかだけ確かめる
                Add("観測", $"実行 #{rid9} の観測の確かさの記録", obs.Item4 == 0 ? Level.Info : obs.Item2 == obs.Item4 ? Level.Ok : obs.Item2 == 0 ? Level.Info : Level.Warn,
                    obs.Item2 == 0 ? "なし（観測の確かさを残す前の版の実行）" : $"判断 {obs.Item4} 件のうち 見た目の割り当ての距離 {obs.Item1}・フレームの古さ {obs.Item2}・境目の近く {obs.Item3}");
            }
        }
        if (runs.Count > 0 && runs[0].Stream > 0)
        {
            long rid = runs[0].Id;
            var range = Try(() => memory.Query("SELECT MIN(seq), MAX(seq) FROM experience_stream WHERE run_id=$r", r => (r.GetInt64(0), r.GetInt64(1)), ("$r", rid)).First(), (0L, -1L));
            long newPats = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM sequence_patterns WHERE first_seq BETWEEN $a AND $b", null, ("$a", range.Item1), ("$b", range.Item2)) ?? 0L), -1L);
            var bnd = Try(() => memory.Query("SELECT COALESCE(boundary,'-'), COUNT(*) FROM experience_stream WHERE run_id=$r GROUP BY boundary ORDER BY boundary", r => $"{r.GetString(0)}={r.GetInt32(1)}", ("$r", rid)), new List<string>());
            long withSup = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(surprisal) FROM experience_stream WHERE run_id=$r", null, ("$r", rid)) ?? 0L), -1L);
            long withEx = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(excess) FROM experience_stream WHERE run_id=$r", null, ("$r", rid)) ?? 0L), -1L);
            long withVis = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(vis_state) FROM experience_stream WHERE run_id=$r", null, ("$r", rid)) ?? 0L), -1L);
            Add("学習", $"実行 #{rid} の系列の更新", withSup == runs[0].Stream ? Level.Ok : Level.Warn,
                $"履歴 seq {range.Item1}〜{range.Item2}・予測の外れの記録 {withSup}/{runs[0].Stream}（ふだんとの差 {withEx}・見た目の状態 {withVis}）・新しいパターン {newPats}・境界の候補 {string.Join(" ", bnd)}");
        }

        // 診断と学習 DB の分離
        long diagInMemory = Try(() => Convert.ToInt64(memory.Scalar("SELECT COUNT(*) FROM runs WHERE (policy_version LIKE 'diag%' OR policy_version='separation-test') AND started_at >= COALESCE((SELECT applied_at FROM migrations WHERE version=4),'')") ?? 0L), -1L);
        Add("分離", "診断の記録が学習 DB に無い", diagInMemory == 0 ? Level.Ok : Level.Fail, $"分離の導入（DB v4）以降に学習 DB に入った診断の実行 {diagInMemory} 件" +
            (diagnostics != null ? $"・diagnostics.sqlite の実行 {Try(() => Convert.ToInt64(diagnostics.Scalar("SELECT COUNT(*) FROM diag_runs") ?? 0L), -1L)} 件" : ""));

        // 取得
        foreach (var e in events.Where(e => T(e) == "capture_probe").Take(2))
            Add("取得", Str(e, "backend"), Bool(e, "usable") ? Level.Ok : Level.Warn, $"fps {Num(e, "fps")}・{Num(e, "w")}x{Num(e, "h")}");

        // S3 PE 閉ループ（ログ集計。学習報酬に使わない）
        AddPeClosedLoopFromLogs(cur, Add);

        // S4 Importance / Attention（advisory。学習報酬に使わない）
        AddImportanceFromLogs(cur, Add);

        var res = new Result(cs, "", "");
        var sb = new StringBuilder();
        sb.AppendLine($"健全性レポート（ゲームに依らないシステム動作だけ。プレイ内容は評価しない）: 総合 {Mark(res.Overall)}");
        foreach (var g in cs.GroupBy(c => c.Area))
        {
            sb.AppendLine($"[{g.Key}]");
            foreach (var c in g) sb.AppendLine($"  {Mark(c.Level)} {c.Name}: {c.Detail}");
        }
        var json = "{\"overall\":\"" + res.Overall.ToString().ToLowerInvariant() + "\",\"checks\":[" + string.Join(",", cs.Select(c =>
            $"{{\"area\":{Q(c.Area)},\"name\":{Q(c.Name)},\"level\":\"{c.Level.ToString().ToLowerInvariant()}\",\"detail\":{Q(c.Detail)}}}")) + "]}";
        return res with { Text = sb.ToString(), Json = json };
    }

    /// <summary>
    /// v0.21.0 Game Supervisor の健全性（システムの動作だけ。ゲームの内容・終了の理由の意味は評価しない）。
    /// 1 回の復旧を「終了 → 再起動 → 取得 → 観測し直し → 再開」の取引として段ごとに見る
    /// </summary>
    /// <summary>続けて同じ段を「段 ×回数」にまとめる（待つ間の探し直しで表示が長くならないように）</summary>
    internal static IEnumerable<string> Collapse(IEnumerable<string> xs)
    {
        string? prev = null; int n = 0;
        foreach (var x in xs)
        {
            if (x == prev) { n++; continue; }
            if (prev != null) yield return n > 1 ? $"{prev} ×{n}" : prev;
            prev = x; n = 1;
        }
        if (prev != null) yield return n > 1 ? $"{prev} ×{n}" : prev;
    }

    internal static void AddSupervisor(JsonElement e, Action<string, string, Level, string> add)
    {
        string t = Str(e, "t"), st = Str(e, "supervisor_state"), ab = Str(e, "supervisor_abort_reason");
        if (st == "ABORTED") st = "RECOVERY_ABORTED";   // v0.23.1 実機: 状態の名前（列挙の ABORTED）を表示の名前にそろえる（以前は FAIL にならなかった）
        double exits = Num(e, "sup_exits"), abn = Num(e, "sup_abnormal_exits"), rs = Num(e, "sup_restarts"), ok = Num(e, "sup_recoveries_ok"), ng = Num(e, "sup_recoveries_failed"),
               trips = Num(e, "sup_breaker_trips"), lost = Num(e, "sup_observation_lost_s"), crc = Num(e, "sup_capture_reconnects"), irc = Num(e, "sup_input_reconnects"),
               hang = Num(e, "sup_suspected_hangs"), cpo = Num(e, "sup_db_checkpoints_ok"), cpf = Num(e, "sup_db_checkpoints_failed"), aex = Num(e, "sup_ai_exceptions"),
               reo = Num(e, "sup_resumed_reobserved"), stale = Num(e, "sup_stale_discarded"), files = Num(e, "sup_file_changes");
        add("Supervisor", $"状態 {t}", st == "RECOVERY_ABORTED" ? Level.Fail : st == "SUSPECTED_HANG" ? Level.Warn : Level.Ok, $"{st}{(ab != "" ? $"（{ab}）" : "")}・起動の方法 {Str(e, "supervisor_launch_method")}");
        add("Supervisor", $"プロセスの終了 {t}", exits == 0 ? Level.NotTested : abn > 0 ? Level.Warn : Level.Ok, exits == 0 ? "この回は終了なし" : $"終了 {exits}・異常（0 以外の終了コード） {abn}（OS の上の分類。ゲームの結果とは結びつけない）");
        // v0.23.1: 起動の方法と自動の起動の成否
        if (e.TryGetProperty("launch_summary", out _))
        {
            double la = Num(e, "launch_attempts"), lok = Num(e, "launch_ok");
            string ls = Str(e, "launch_summary"); bool none = Str(e, "supervisor_launch_method") == "none";
            add("Supervisor", $"起動の方法 {t}", none && exits > 0 ? Level.Warn : Level.Ok, ls + (none && exits > 0 ? "（ゲームが終了したが自動では起動しない設定。画面の「ゲームが消えたら自動で起動し直す」を確かめる）" : ""));
            add("Supervisor", $"自動の起動 {t}", la == 0 ? Level.NotTested : lok == 0 ? Level.Fail : lok < la ? Level.Warn : Level.Ok,
                la == 0 ? "この回は起動していない" : $"試行 {la}・起動できた {lok}");
        }
        if (e.TryGetProperty("sup_input_losses", out _))
        {
            double il = Num(e, "sup_input_losses");
            add("Supervisor", $"入力の方式の失敗 {t}", il == 0 ? Level.Ok : st == "RECOVERY_ABORTED" && ab == "INPUT_LOOP" ? Level.Fail : Level.Warn, il == 0 ? "なし" : $"{il} 回（Supervisor が入力をつなぎ直した）");
        }
        add("Supervisor", $"再起動の試行 {t}", rs == 0 ? Level.NotTested : Level.Ok, $"{rs} 回");
        int nTx = e.TryGetProperty("sup_transactions", out var txs) && txs.ValueKind == JsonValueKind.Array ? txs.GetArrayLength() : 0;
        add("Supervisor", $"復旧の成否 {t}", nTx == 0 ? Level.NotTested : ng > 0 ? Level.Fail : Level.Ok, $"成功 {ok}・失敗 {ng}・復旧の取引 {nTx}");
        add("Supervisor", $"回路の遮断 {t}", trips > 0 ? Level.Warn : rs == 0 ? Level.NotTested : Level.Ok, trips > 0 ? $"{trips} 回（再起動の繰り返しを止めた。人の確認が必要）" : "なし");
        add("Supervisor", $"観測を失った時間 {t}", nTx == 0 ? Level.NotTested : Level.Ok, $"{lost} 秒");
        add("Supervisor", $"取得・入力のつなぎ直し {t}", crc + irc == 0 ? Level.NotTested : Level.Ok, $"取得 {crc}・入力 {irc}");
        add("Supervisor", $"応答なしの疑い {t}", hang > 0 ? Level.Warn : Level.Ok, hang > 0 ? $"{hang} 回（終了させずに記録と警告だけ）" : "なし");
        add("Supervisor", $"DB の保存（復旧の前） {t}", cpf > 0 ? Level.Fail : cpo > 0 ? Level.Ok : Level.NotTested, $"成功 {cpo}・失敗 {cpf}");
        add("Supervisor", $"経験の連続性（観測し直してから再開） {t}", ok == 0 ? Level.NotTested : reo == ok ? Level.Ok : Level.Fail,
            $"再開 {ok}・観測し直してから再開 {reo}・捨てた古い手 {stale}（中断の前後を 1 つの経験にしない）");
        add("Supervisor", $"AI の例外 {t}", aex > 0 ? Level.Warn : Level.Ok, $"{aex} 件");
        if (files > 0) add("Supervisor", $"見張るファイルの変化 {t}", Level.Warn, $"{files} 件（戻さずに記録と警告だけ）");
        if (nTx > 0)
            foreach (var x in txs.EnumerateArray().Take(10))
            {
                string oc = Str(x, "outcome");
                string stages = x.TryGetProperty("stages", out var sg) && sg.ValueKind == JsonValueKind.Array ? string.Join(" → ", Collapse(sg.EnumerateArray().Select(v => v.GetString() ?? ""))) : "";
                add("Supervisor", $"復旧 #{Str(x, "id")} {t}", oc == "resumed" ? Level.Ok : oc == "stopped" ? Level.Info : Level.Fail, $"{Str(x, "reason")}・{oc}・試行 {Str(x, "attempts")}・{Str(x, "duration_s")} 秒・{stages}");
            }
    }

    private static string Mark(Level l) => l switch { Level.Ok => "PASS", Level.Warn => "WARN", Level.Fail => "FAIL", Level.NotTested => "NOT_TESTED", _ => "INFO" };
    private static string Q(string s) => JsonSerializer.Serialize(s);
    private const double MinTextCoverage = 0.5;   // 根拠: 汎用（半分以上の手で文字の状態が分かれば、手ごとの文字の変化を学べる）
    private const double PaceRatio = 1.25;        // 根拠: 汎用（1 手の時間の 25% の遅れ＝はっきり分かる遅れの目安）
    private const int PaceMinTurns = 20;          // 根拠: 汎用（平均を比べる最小の手の数）
    private static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
    private static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static double Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDouble(), 2) : 0;
    private static T Try<T>(Func<T> f, T fallback = default!) { try { return f(); } catch { return fallback; } }


    /// <summary>S3: causal_wm / pe_feature_candidate / pe_closed_loop_summary を件数だけ表示（意味ラベルなし）。</summary>
    internal static void AddPeClosedLoopFromLogs(IReadOnlyList<JsonElement> cur, Action<string, string, Level, string> add)
    {
        static bool IsType(JsonElement e, string name) =>
            e.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == name;
        var wm = cur.Where(e => IsType(e, "causal_wm")).ToList();
        var summaries = cur.Where(e => IsType(e, "pe_closed_loop_summary")).ToList();
        var cands = cur.Where(e => IsType(e, "pe_feature_candidate")).ToList();
        if (wm.Count == 0 && summaries.Count == 0 && cands.Count == 0)
        {
            add("因果WM", "PE閉ループ", Level.NotTested, "今回のログに causal_wm / pe_closed_loop_summary なし（フラグ OFF か未実行）");
            return;
        }
        var pref = new Dictionary<string, int>(StringComparer.Ordinal);
        var tags = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in wm)
        {
            string p = Str(e, "preferred"); if (p != "") pref[p] = pref.GetValueOrDefault(p) + 1;
            foreach (var tag in Str(e, "tags").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (tag.Length > 0) tags[tag] = tags.GetValueOrDefault(tag) + 1;
        }
        string prefS = pref.Count == 0 ? "なし" : string.Join("・", pref.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"));
        string tagS = tags.Count == 0 ? "なし" : string.Join("・", tags.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"));
        add("因果WM", "H0–H4 採用（件数）", Level.Info, $"比較 {wm.Count}・{prefS}");
        add("因果WM", "PE ルーティングタグ（件数）", Level.Info, tagS);
        add("因果WM", "特徴候補 enqueue", cands.Count > 0 ? Level.Ok : summaries.Count > 0 ? Level.Info : Level.NotTested,
            $"pe_feature_candidate {cands.Count}・summary {summaries.Count}" + (summaries.Count > 0 ? "／" + Str(summaries[^1], "summary") : ""));
    }


    /// <summary>S4: feature_importance / attention_weights / pe_importance_recommend を件数表示。</summary>
    internal static void AddImportanceFromLogs(IReadOnlyList<JsonElement> cur, Action<string, string, Level, string> add)
    {
        static bool IsType(JsonElement e, string name) =>
            e.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == name;
        var fi = cur.Where(e => IsType(e, "feature_importance")).ToList();
        var at = cur.Where(e => IsType(e, "attention_weights")).ToList();
        var rec = cur.Where(e => IsType(e, "pe_importance_recommend")).ToList();
        if (fi.Count == 0 && at.Count == 0 && rec.Count == 0)
        {
            add("重要度", "Importance/Attention", Level.NotTested, "今回のログに feature_importance / attention_weights なし（フラグ OFF か未実行）");
            return;
        }
        add("重要度", "特徴重要度レコード", fi.Count > 0 ? Level.Ok : Level.Info, $"feature_importance {fi.Count}");
        string attn = at.Count == 0 ? "なし" : Str(at[^1], "summary");
        if (attn == "") attn = $"attention_weights {at.Count}";
        add("重要度", "注意重み（advisory）", at.Count > 0 ? Level.Info : Level.NotTested, attn);
        add("重要度", "PE 推奨", rec.Count > 0 ? Level.Info : Level.NotTested, $"pe_importance_recommend {rec.Count}");
    }

    private static IEnumerable<string> SafeLines(string path)
    {
        List<string> lines = new();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? l; while ((l = sr.ReadLine()) != null) if (l.Length > 0) lines.Add(l);
        }
        catch (IOException) { }
        lines.Reverse();   // 新しい行から
        return lines;
    }
}
