using GameKouryakuKun.Core.Exploration;
using System.Globalization;
using System.Security.Cryptography;
using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Storage;

// ---- 記録の単位（観測・行動・結果。境界情報・差分・操作） ----

/// <summary>観測（入力前の画面）。認識結果の列は Phase 2 以降で埋まる。</summary>
public sealed record ObservationRecord(string? StatsJson = null, string? RecogConfJson = null, Frame? Thumbnail = null, string ScreenshotReason = "periodic");

/// <summary>行動（候補と選択）。手探り探索は policy_mode='explore'</summary>
public sealed record ActionRecord(string LogicalKey, string Intent, string PolicyMode, string ReasonJson, string? IntentTarget = null, double? Score = null,
    IReadOnlyList<CandidateRecord>? Candidates = null);

public sealed record CandidateRecord(int Rank, string LogicalKey, string Intent, double? Score, string? ComponentsJson = null);

/// <summary>
/// 結果（ターン境界の判定）。DiffMetricsJson は画面差分の数値（変化開始・静止・変化率など）で outcomes.diff_metrics_json に入る。
/// EntityEventsJson はトラックのできごと（認識の実装後に埋まる。スキーマ v3 で用途を分けた）。
/// </summary>
public sealed record OutcomeRecord(bool ScreenChanged, string BoundaryKind, double LatencyMs, string? DiffMetricsJson = null, string? EntityEventsJson = null);

public sealed record TurnCommit(ObservationRecord Observation, ActionRecord Action, OutcomeRecord Outcome);

public sealed record TurnIds(long TurnId, long ObsId, long ActionId, int TurnIndex);

public sealed record TurnRow(long TurnId, long RunId, int TurnIndex, string BoundaryKind, int LatencyMs, string LogicalKey, string Intent, bool ScreenChanged, string CreatedAt);

public sealed record RunRow(long RunId, string StartedAt, string? EndedAt, string? EndReason, int TurnsTotal, string? InputBackend, string? CaptureBackend, string? EndDetail = null);

/// <summary>
/// 長期経験DB（memory.sqlite）への記録API（設計書 付録A IExperienceStore の Phase 1 版）。
/// 1ターン = 観測 → 行動 → 結果 を単一トランザクションで書き、前ターンの outcome.next_turn_id をつなぐ。
/// 学習ロジックはまだ無い（stats は件数の集計だけ）。
/// </summary>
public sealed class ExperienceStore : IDisposable
{
    public const string BoundaryNormal = "normal", BoundaryNoChange = "no_change", BoundaryTimeout = "timeout", BoundaryAborted = "aborted";
    private readonly SqliteDb _db;
    private readonly Func<DateTime> _now;
    public string RootDir { get; }
    public string ScreenshotDir => Path.Combine(RootDir, "screenshots");
    public int ThumbnailRingSize { get; set; } = 300;
    public SqliteDb Db => _db;
    private long? _lastActionId;
    private readonly Dictionary<long, int> _nextIndex = new();

    private ExperienceStore(SqliteDb db, string root, Func<DateTime> now) { _db = db; RootDir = root; _now = now; }

    /// <summary>{profileDataDir}/memory.sqlite を開く（無ければ作る。古ければバックアップしてからマイグレーション）</summary>
    public static ExperienceStore Open(string profileDataDir, string profileId, string profileVersion, string appVersion, Func<DateTime>? utcNow = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(profileDataDir);
        var db = SqliteDb.Open(Path.Combine(profileDataDir, "memory.sqlite"), Schemas.MemoryMetaTable, Schemas.Memory, appVersion,
            new Dictionary<string, string> { ["profile_id"] = profileId, ["profile_version"] = profileVersion, ["created_by_app"] = appVersion },
            backupInterval: TimeSpan.FromDays(1), utcNow: now);
        var stored = db.Meta(Schemas.MemoryMetaTable, "profile_id");
        if (stored != null && stored != profileId) { db.Dispose(); throw new InvalidOperationException($"memory.sqlite は Profile「{stored}」のものです（要求: {profileId}）"); }
        return new ExperienceStore(db, profileDataDir, now);
    }

    private string Now() => SqliteDb.Iso(_now());

    /// <summary>診断（v0.14.1 までの互換性テスト・分離試験など）の版の名前か。診断の記録は memory.sqlite に入れない（U3。DiagnosticStore へ）</summary>
    public static bool IsDiagnosticPolicy(string policyVersion) =>
        policyVersion.StartsWith("diag", StringComparison.OrdinalIgnoreCase) || policyVersion.Equals("separation-test", StringComparison.OrdinalIgnoreCase);

    public long BeginRun(string profileVersion, string? inputBackendId, string? captureBackendId, string policyVersion = "explore", string? notesJson = null)
    {
        if (IsDiagnosticPolicy(policyVersion))
            throw new InvalidOperationException($"診断の記録（{policyVersion}）は学習 DB（memory.sqlite）に入れられません。DiagnosticStore に記録してください");
        _lastActionId = null;
        var id = _db.Insert("INSERT INTO runs(started_at, profile_version, recognizer_version, policy_version, input_backend_id, capture_backend_id, turns_total, notes_json) " +
                            "VALUES($t,$pv,$rv,$pol,$ib,$cb,0,$n)", null,
            ("$t", Now()), ("$pv", profileVersion), ("$rv", "none"), ("$pol", policyVersion), ("$ib", inputBackendId), ("$cb", captureBackendId), ("$n", notesJson));
        _nextIndex[id] = 0;
        Bump(null, "runs_total", 1);
        return id;
    }

    /// <summary>
    /// runs.end_reason の CHECK 制約で許される値（phase2-design.md §10.4）。ツール側の手続き上の区切りだけを表す:
    /// abort（ツール側で止めた）・crash・test_completed（指定の時間・手数を終えた）・unknown（ended は旧版の値の互換のため残す）。
    /// ゲームの中での終わり方・区切りは事前のラベルで持たない（連続経験履歴 experience_stream から系列の統計で見つける）。runs は実行単位のシステム記録で、学習の境界には使わない。
    /// </summary>
    public static readonly IReadOnlyList<string> EndReasons = new[] { "ended", "abort", "crash", "test_completed", "unknown" };

    /// <summary>
    /// セッションの終了理由を end_reason（制約内の大分類）と end_detail（元の理由）に分ける。
    /// 停止ボタン・緊急停止・入力不達などツール側の終了は abort、指定の時間・手数を終えたら test_completed、error は crash、知らない値は unknown。
    /// </summary>
    public static (string Reason, string? Detail) NormalizeEndReason(string? raw)
    {
        var r = (raw ?? "").Trim().ToLowerInvariant();
        if (EndReasons.Contains(r)) return (r, null);
        return r switch
        {
            "user_stop" or "emergency" or "stopped" or "input_unreachable" or "window_lost" => ("abort", r),
            "time_completed" or "steps_completed" => ("test_completed", r),
            "error" or "exception" => ("crash", r),
            "" => ("unknown", null),
            _ => ("unknown", r),
        };
    }

    /// <summary>
    /// notes_json の統合。両方 JSON オブジェクトならキー単位で足す（同じキーは後の値。ただし既存キーは "end_" を付けて残す）。
    /// どちらかがオブジェクトでなければ、既存を "start"・追加を "end" に入れたオブジェクトにする。
    /// </summary>
    public static string? MergeNotes(string? existing, string? add)
    {
        if (string.IsNullOrWhiteSpace(add)) return existing;
        if (string.IsNullOrWhiteSpace(existing)) return add;
        System.Text.Json.Nodes.JsonNode? a = null, b = null;
        try { a = System.Text.Json.Nodes.JsonNode.Parse(existing); } catch (System.Text.Json.JsonException) { }
        try { b = System.Text.Json.Nodes.JsonNode.Parse(add); } catch (System.Text.Json.JsonException) { }
        if (a is System.Text.Json.Nodes.JsonObject ao && b is System.Text.Json.Nodes.JsonObject bo)
        {
            foreach (var (k, v) in bo.ToList())
            {
                bo.Remove(k);
                var key = ao.ContainsKey(k) ? "end_" + k : k;
                ao[key] = v;
            }
            return ao.ToJsonString();
        }
        var o = new System.Text.Json.Nodes.JsonObject { ["start"] = a ?? existing, ["end"] = b ?? add };
        return o.ToJsonString();
    }

    public string? RunNotes(long runId) => _db.Scalar("SELECT notes_json FROM runs WHERE run_id=$id", null, ("$id", runId)) as string;

    /// <summary>冒険を閉じる。purgeProcessImages=true（既定）なら、その冒険の過程画像を削除する（決定 D8）</summary>
    public void EndRun(long runId, string endReason, string? notesJson = null, bool purgeProcessImages = true)
    {
        var (reason, detail) = NormalizeEndReason(endReason);
        _db.InTransaction(tx =>
        {
            // 開始時の notes（seed・最大手数など再現に必要な値）を消さず、終了時の値を足す
            var merged = MergeNotes(_db.Scalar("SELECT notes_json FROM runs WHERE run_id=$id", tx, ("$id", runId)) as string, notesJson);
            _db.Exec("UPDATE runs SET ended_at=$t, end_reason=$r, end_detail=$d, notes_json=$n, turns_total=(SELECT COUNT(*) FROM turns WHERE run_id=$id) WHERE run_id=$id", tx,
                ("$t", Now()), ("$r", reason), ("$d", detail), ("$n", merged), ("$id", runId));
            return 0;
        });
        _lastActionId = null;
        if (purgeProcessImages) try { PurgeRunProcessImages(runId); PruneRepresentatives(); } catch { /* 削除の失敗は記録を止めない（次回に再試行） */ }
        try { _db.Checkpoint(); } catch { /* 読み取り中などで書き戻せなくても記録自体は WAL にある */ }
    }

    /// <summary>1ターン（観測→行動→結果）を記録する。単一トランザクション。</summary>
    public TurnIds CommitTurn(long runId, TurnCommit c)
    {
        string? shotPath = null;
        (string sha1, byte[] png, int w, int h)? shot = null;
        if (c.Observation.Thumbnail is Frame f)
        {
            var png = Png.Encode(f.Width, f.Height, f.Bgra);
            shot = (Convert.ToHexString(SHA1.HashData(png)).ToLowerInvariant(), png, f.Width, f.Height);
        }
        var ids = _db.InTransaction(tx =>
        {
            int index = NextIndex(runId, tx);
            long turnId = _db.Insert("INSERT INTO turns(run_id, turn_index, boundary_kind, latency_ms, created_at) VALUES($r,$i,$k,$l,$t)", tx,
                ("$r", runId), ("$i", index), ("$k", c.Outcome.BoundaryKind), ("$l", (int)Math.Round(c.Outcome.LatencyMs)), ("$t", Now()));
            long? shotId = null;
            if (shot is { } s)
            {
                var rel = Path.Combine("screenshots", _now().ToString("yyyy", CultureInfo.InvariantCulture), _now().ToString("MM", CultureInfo.InvariantCulture), s.sha1[..2], s.sha1 + ".png");
                var existing = _db.Scalar("SELECT screenshot_id FROM screenshots WHERE sha1=$s", tx, ("$s", s.sha1));
                if (existing != null) shotId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                else
                {
                    shotId = _db.Insert("INSERT INTO screenshots(sha1, path, width, height, format, quality, reason, retained, created_at, run_id, purpose, size_bytes) VALUES($s,$p,$w,$h,'png','thumb',$r,0,$t,$run,'process',$sz)", tx,
                        ("$s", s.sha1), ("$p", rel.Replace('\\', '/')), ("$w", s.w), ("$h", s.h), ("$r", c.Observation.ScreenshotReason), ("$t", Now()), ("$run", runId), ("$sz", s.png.Length));
                    shotPath = Path.Combine(RootDir, rel);
                }
            }
            long obsId = _db.Insert("INSERT INTO observations(turn_id, stats_json, recog_conf_json, screenshot_id) VALUES($t,$s,$r,$sh)", tx,
                ("$t", turnId), ("$s", c.Observation.StatsJson), ("$r", c.Observation.RecogConfJson), ("$sh", shotId));
            var a = c.Action;
            long actionId = _db.Insert("INSERT INTO actions(turn_id, logical_key, intent, intent_target, score, reason_json, policy_mode) VALUES($t,$k,$i,$tg,$sc,$re,$pm)", tx,
                ("$t", turnId), ("$k", a.LogicalKey), ("$i", a.Intent), ("$tg", a.IntentTarget), ("$sc", a.Score), ("$re", a.ReasonJson), ("$pm", a.PolicyMode));
            if (a.Candidates != null)
                foreach (var cand in a.Candidates)
                    _db.Exec("INSERT INTO decision_candidates(turn_id, rank, logical_key, intent, score, components_json) VALUES($t,$r,$k,$i,$s,$c)", tx,
                        ("$t", turnId), ("$r", cand.Rank), ("$k", cand.LogicalKey), ("$i", cand.Intent), ("$s", cand.Score), ("$c", cand.ComponentsJson));
            _db.Exec("INSERT INTO outcomes(action_id, screen_changed, diff_metrics_json, entity_events_json) VALUES($a,$c,$d,$e)", tx,
                ("$a", actionId), ("$c", c.Outcome.ScreenChanged ? 1 : 0), ("$d", c.Outcome.DiffMetricsJson), ("$e", c.Outcome.EntityEventsJson));
            if (_lastActionId is long prev) _db.Exec("UPDATE outcomes SET next_turn_id=$n WHERE action_id=$a", tx, ("$n", turnId), ("$a", prev));
            _db.Exec("UPDATE runs SET turns_total=COALESCE(turns_total,0)+1 WHERE run_id=$r", tx, ("$r", runId));
            Bump(tx, "turns_total", 1, runId);
            Bump(tx, "boundary_" + c.Outcome.BoundaryKind, 1, runId);
            return new TurnIds(turnId, obsId, actionId, index);
        });
        _lastActionId = ids.ActionId;
        if (shotPath != null && shot is { } sv)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(shotPath)!); var tmp = shotPath + ".tmp"; File.WriteAllBytes(tmp, sv.png); File.Move(tmp, shotPath, true); }
            catch { /* 画像の保存失敗は記録を止めない */ }
            PruneThumbnails();
        }
        return ids;
    }

    private int NextIndex(long runId, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        if (!_nextIndex.TryGetValue(runId, out int i))
            i = Convert.ToInt32(_db.Scalar("SELECT COALESCE(MAX(turn_index)+1,0) FROM turns WHERE run_id=$r", tx, ("$r", runId)), CultureInfo.InvariantCulture);
        _nextIndex[runId] = i + 1;
        return i;
    }

    private void Bump(Microsoft.Data.Sqlite.SqliteTransaction? tx, string key, double by, long? runId = null)
    {
        _db.Exec("INSERT INTO stats_global(key, value, updated_at) VALUES($k,$v,$t) ON CONFLICT(key) DO UPDATE SET value=value+$v, updated_at=$t", tx, ("$k", key), ("$v", by), ("$t", Now()));
        if (runId != null)
            _db.Exec("INSERT INTO stats_run(run_id, key, value) VALUES($r,$k,$v) ON CONFLICT(run_id,key) DO UPDATE SET value=value+$v", tx, ("$r", runId), ("$k", key), ("$v", by));
    }

    /// <summary>
    /// 過程データの自動削除（phase2-design.md §10.5、決定 D8）: 冒険の手ごとの画像（purpose='process'）は、
    /// 観測の記録（DB の行）に集約し終えたら消す。既定では冒険の終了時に、その冒険の画像をまとめて削除する。
    /// DB の行（観測・行動・結果）は残り、screenshot_id だけ NULL になる。代表画像（retained=1）は残す。
    /// </summary>
    public int PurgeRunProcessImages(long runId)
    {
        var old = _db.Query("SELECT screenshot_id, path FROM screenshots WHERE run_id=$r AND retained=0 AND COALESCE(purpose,'process')='process'",
            r => (Id: r.GetInt64(0), Path: r.IsDBNull(1) ? null : r.GetString(1)), ("$r", runId));
        return DeleteShots(old);
    }

    /// <summary>代表画像（retained=1）の総量上限。超えたら古い順に間引く（既定 200MB）</summary>
    public long RepresentativeLimitBytes { get; set; } = 200L * 1024 * 1024;

    public int PruneRepresentatives()
    {
        var rows = _db.Query("SELECT screenshot_id, path, COALESCE(size_bytes,0) FROM screenshots WHERE retained=1 ORDER BY screenshot_id DESC",
            r => (Id: r.GetInt64(0), Path: r.IsDBNull(1) ? null : r.GetString(1), Size: r.GetInt64(2)));
        long total = 0; var drop = new List<(long Id, string? Path)>();
        foreach (var x in rows) { total += x.Size; if (total > RepresentativeLimitBytes) drop.Add((x.Id, x.Path)); }
        return DeleteShots(drop);
    }

    private int DeleteShots(List<(long Id, string? Path)> old)
    {
        if (old.Count == 0) return 0;
        _db.InTransaction(tx =>
        {
            foreach (var o in old)
            {
                _db.Exec("UPDATE observations SET screenshot_id=NULL WHERE screenshot_id=$i", tx, ("$i", o.Id));
                _db.Exec("UPDATE entities SET icon_screenshot_id=NULL WHERE icon_screenshot_id=$i", tx, ("$i", o.Id));
                _db.Exec("DELETE FROM screenshots WHERE screenshot_id=$i", tx, ("$i", o.Id));
            }
            return 0;
        });
        foreach (var o in old) if (o.Path != null) try { File.Delete(Path.Combine(RootDir, o.Path)); } catch { }
        return old.Count;
    }

    /// <summary>終わった冒険に残っている過程画像を消す（前回の削除が失敗した・異常終了した場合の後始末。起動時に呼ぶ）</summary>
    public int PurgeEndedRunsProcessImages()
    {
        var old = _db.Query("SELECT s.screenshot_id, s.path FROM screenshots s JOIN runs r ON r.run_id=s.run_id WHERE r.ended_at IS NOT NULL AND s.retained=0 AND COALESCE(s.purpose,'process')='process'",
            r => (Id: r.GetInt64(0), Path: r.IsDBNull(1) ? null : r.GetString(1)));
        return DeleteShots(old);
    }

    public int ScreenshotCount(long? runId = null) =>
        Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM screenshots WHERE ($r IS NULL OR run_id=$r)", null, ("$r", runId)), CultureInfo.InvariantCulture);

    /// <summary>定期サムネイルのリングバッファ（retained=0 を新しい順に K 件だけ残す。DB の参照は NULL 化）</summary>
    public int PruneThumbnails()
    {
        var old = _db.Query("SELECT screenshot_id, path FROM screenshots WHERE retained=0 AND quality='thumb' ORDER BY screenshot_id DESC LIMIT -1 OFFSET $k",
            r => (Id: r.GetInt64(0), Path: r.IsDBNull(1) ? null : r.GetString(1)), ("$k", ThumbnailRingSize));
        return DeleteShots(old.Select(o => (o.Id, o.Path)).ToList());
    }

    // ---- 参照（UI用） ----
    public IReadOnlyDictionary<string, double> GlobalStats() =>
        _db.Query("SELECT key, value FROM stats_global", r => (r.GetString(0), r.GetDouble(1))).ToDictionary(x => x.Item1, x => x.Item2);

    public IReadOnlyDictionary<string, double> RunStats(long runId) =>
        _db.Query("SELECT key, value FROM stats_run WHERE run_id=$r", r => (r.GetString(0), r.GetDouble(1)), ("$r", runId)).ToDictionary(x => x.Item1, x => x.Item2);

    public IReadOnlyList<TurnRow> RecentTurns(int limit, long? runId = null) =>
        _db.Query("SELECT t.turn_id, t.run_id, t.turn_index, t.boundary_kind, t.latency_ms, a.logical_key, a.intent, o.screen_changed, t.created_at " +
                  "FROM turns t JOIN actions a ON a.turn_id=t.turn_id JOIN outcomes o ON o.action_id=a.action_id " +
                  "WHERE ($r IS NULL OR t.run_id=$r) ORDER BY t.turn_id DESC LIMIT $n",
            r => new TurnRow(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetInt32(7) != 0, r.GetString(8)),
            ("$r", runId), ("$n", limit));

    public IReadOnlyList<RunRow> Runs(int limit = 50) =>
        _db.Query("SELECT run_id, started_at, ended_at, end_reason, COALESCE(turns_total,0), input_backend_id, capture_backend_id, end_detail FROM runs ORDER BY run_id DESC LIMIT $n",
            r => new RunRow(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)), ("$n", limit));

    /// <summary>異常終了したまま閉じられていない冒険を 'crash' として閉じる（起動時に呼ぶ）</summary>
    public int CloseDanglingRuns() =>
        _db.Exec("UPDATE runs SET ended_at=$t, end_reason='crash', turns_total=(SELECT COUNT(*) FROM turns WHERE turns.run_id=runs.run_id) WHERE ended_at IS NULL", null, ("$t", Now()));

    // ---- 画面の種類と遷移（2b の最小版。手探り探索の記録） ----

    /// <summary>画面の種類（教師なしの群の代表）を保存する</summary>
    public void SaveScreenModes(IEnumerable<(int Id, float[] Proto, int Seen)> modes, int? firstSeenTurn = null)
    {
        _db.InTransaction(tx =>
        {
            foreach (var (id, proto, seen) in modes)
            {
                var blob = new byte[proto.Length * 4]; Buffer.BlockCopy(proto, 0, blob, 0, blob.Length);
                _db.Exec("INSERT INTO screen_modes(mode_id, prototype_blob, layout_signature, seen_count, first_seen_turn) VALUES($id,$b,'hist64',$n,$t) " +
                         "ON CONFLICT(mode_id) DO UPDATE SET prototype_blob=$b, seen_count=$n", tx, ("$id", id), ("$b", blob), ("$n", seen), ("$t", firstSeenTurn));
            }
            return 0;
        });
    }

    public IReadOnlyList<(int Id, float[] Proto, int Seen)> LoadScreenModes() =>
        _db.Query("SELECT mode_id, prototype_blob, seen_count FROM screen_modes WHERE layout_signature='hist64' ORDER BY mode_id", r =>
        {
            var blob = (byte[])r.GetValue(1); var f = new float[blob.Length / 4]; Buffer.BlockCopy(blob, 0, f, 0, f.Length * 4);
            return (r.GetInt32(0), f, r.IsDBNull(2) ? 0 : r.GetInt32(2));
        });

    /// <summary>遷移の回数を足す（from ノードで key を押したら to ノードへ）</summary>
    public void AddTransitions(IEnumerable<(string From, string Key, string To, int N)> delta)
    {
        _db.InTransaction(tx =>
        {
            foreach (var (from, key, to, n) in delta)
            {
                if (n <= 0) continue;
                _db.Exec("INSERT INTO mode_transitions(from_node, logical_key, to_node, n, success_rate, updated_at) VALUES($f,$k,$t,$n,NULL,$u) " +
                         "ON CONFLICT(from_node, logical_key, to_node) DO UPDATE SET n = n + $n, updated_at=$u", tx, ("$f", from), ("$k", key), ("$t", to), ("$n", n), ("$u", Now()));
            }
            // 成功率 = その (from, key) のうち、別のノードへ移った割合
            _db.Exec("UPDATE mode_transitions SET success_rate = (SELECT CAST(SUM(CASE WHEN m.to_node <> m.from_node THEN m.n ELSE 0 END) AS REAL) / SUM(m.n) FROM mode_transitions m " +
                     "WHERE m.from_node = mode_transitions.from_node AND m.logical_key = mode_transitions.logical_key)", tx);
            return 0;
        });
    }

    public IReadOnlyList<(string From, string Key, string To, int N)> LoadTransitions() =>
        _db.Query("SELECT from_node, logical_key, to_node, n FROM mode_transitions", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));

    // ---- 可逆性（安全規則の学習。U2） ----
    public void SaveReversibility(IEnumerable<(string Key, int Returned, int NotReturned, int UndoHits)> rows)
    {
        _db.InTransaction(tx =>
        {
            foreach (var (k, r, nr, u) in rows)
                _db.Exec("INSERT INTO action_reversibility(logical_key, returned, not_returned, undo_hits, updated_at) VALUES($k,$r,$n,$u,$t) " +
                         "ON CONFLICT(logical_key) DO UPDATE SET returned=$r, not_returned=$n, undo_hits=$u, updated_at=$t", tx, ("$k", k), ("$r", r), ("$n", nr), ("$u", u), ("$t", Now()));
            return 0;
        });
    }

    public IReadOnlyList<(string Key, int Returned, int NotReturned, int UndoHits)> LoadReversibility() =>
        _db.Query("SELECT logical_key, COALESCE(returned,0), COALESCE(not_returned,0), COALESCE(undo_hits,0) FROM action_reversibility", r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));

    // ---- 連続経験履歴（学習の区切りは与えない。run_id は記録の照合用のシステム情報） ----
    /// <summary>次に使う通し番号</summary>
    public long NextStreamSeq() => (long)(_db.Scalar("SELECT COALESCE(MAX(seq), 0) + 1 FROM experience_stream") ?? 1L);

    public void AppendStream(long systemRunId, long? turnId, StreamStep s, double surprisal, IReadOnlyList<BoundaryReason> boundary, double excess = double.NaN, int? visState = null, string actionSource = "agent") =>
        _db.Exec("INSERT INTO experience_stream(seq, run_id, turn_id, state, action, changed, next_state, change_ratio, surprisal, boundary, created_at, excess, vis_state, action_source) VALUES($q,$r,$t,$s,$a,$c,$n,$x,$u,$b,$at,$e,$v,$src)", null,
            ("$src", actionSource),
            ("$q", s.Seq), ("$r", systemRunId), ("$t", turnId), ("$s", s.State), ("$a", s.Action), ("$c", s.Changed ? 1 : 0), ("$n", s.NextState),
            ("$x", double.IsFinite(s.ChangeRatio) ? s.ChangeRatio : null), ("$u", double.IsFinite(surprisal) ? surprisal : null),
            ("$b", boundary.Count == 0 ? null : string.Join(",", boundary.Select(b => b.ToString().ToLowerInvariant()))), ("$at", Now()),
            ("$e", double.IsFinite(excess) ? excess : null), ("$v", visState));

    // ---- 入力なしの変化・画面ごとの時間的な変化の性質（v10） ----
    public void AddNoInputEvent(long? runId, long beforeSeq, string from, string to, bool transition, double ratio, double gapMs, bool rechosen) =>
        _db.Exec("INSERT INTO no_input_events(run_id, before_seq, from_node, to_node, transition, changed_ratio, gap_ms, rechosen, created_at) VALUES($r,$s,$f,$t,$x,$q,$g,$c,$n)", null,
            ("$r", (object?)runId ?? DBNull.Value), ("$s", beforeSeq), ("$f", from), ("$t", to), ("$x", transition ? 1 : 0), ("$q", ratio), ("$g", gapMs), ("$c", rechosen ? 1 : 0), ("$n", Now()));

    public void AddGoalEvent(long? runId, long seq, GameKouryakuKun.Core.Goals.GoalEvent e) =>
        _db.Exec("INSERT INTO goal_events(run_id, seq, goal_id, kind, target, action, status, reason, steps, progress, value_after, origin, plan, created_at, horizon, planned_length, replans, subgoals, subgoals_reached, outcome_json) VALUES($r,$s,$g,$k,$t,$a,$st,$re,$n,$p,$v,$o,$pl,$c,$h,$pln,$rp,$sg,$sgr,$oc)", null,
            ("$h", e.Horizon), ("$pln", e.PlannedLength), ("$rp", e.Replans), ("$sg", e.Subgoals), ("$sgr", e.SubgoalsReached), ("$oc", (object?)e.Outcome ?? DBNull.Value),
            ("$r", (object?)runId ?? DBNull.Value), ("$s", seq), ("$g", e.GoalId), ("$k", e.Kind), ("$t", e.Target), ("$a", (object?)e.Action ?? DBNull.Value), ("$st", e.Status),
            ("$re", (object?)e.Reason ?? DBNull.Value), ("$n", e.Steps), ("$p", e.Progress is double p ? p : DBNull.Value), ("$v", double.IsNaN(e.ValueAfter) ? DBNull.Value : e.ValueAfter),
            ("$o", e.Origin), ("$pl", e.Plan), ("$c", Now()));

    public IReadOnlyList<(long BeforeSeq, string From, string To, bool Transition)> LoadNoInputEvents(long limit = 100_000) =>
        _db.Query("SELECT before_seq, from_node, to_node, transition FROM no_input_events ORDER BY event_id DESC LIMIT $l",
            r => (r.IsDBNull(0) ? 0L : r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0), ("$l", limit));

    /// <summary>システムの出来事（v0.21.0）。ゲームの経験とは別の表。seqBefore は直前のゲームの経験の通し番号（時間の上の突き合わせ用）</summary>
    public void AppendSystemEvent(long? runId, long? seqBefore, string kind, long? recoveryId, string detail, string dataJson) =>
        _db.Exec("INSERT INTO system_events(run_id, seq_before, kind, recovery_id, detail, data_json, at) VALUES($r,$s,$k,$x,$d,$j,$t)", null,
            ("$r", (object?)runId ?? DBNull.Value), ("$s", (object?)seqBefore ?? DBNull.Value), ("$k", kind), ("$x", (object?)recoveryId ?? DBNull.Value), ("$d", detail), ("$j", dataJson), ("$t", Now()));
    public IReadOnlyList<(string Kind, long? RecoveryId, string Detail, string At)> LoadSystemEvents(long? runId = null) =>
        _db.Query("SELECT kind, recovery_id, detail, at FROM system_events WHERE ($r IS NULL OR run_id=$r) ORDER BY event_id",
            r => (r.GetString(0), r.IsDBNull(1) ? (long?)null : r.GetInt64(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3)), ("$r", (object?)runId ?? DBNull.Value));

    /// <summary>成長テストの計測（v0.22.0）。記録だけで、学習・選択には読まない</summary>
    public void AppendGrowthSnapshot(GameKouryakuKun.Core.Growth.GrowthSnapshot g) =>
        _db.Exec("INSERT INTO growth_snapshots(run_id, seq, step, reason, json, at) VALUES($r,$s,$p,$w,$j,$t)", null,
            ("$r", g.RunId), ("$s", g.Seq), ("$p", g.Step), ("$w", g.Reason), ("$j", GrowthJson.Write(g)), ("$t", Now()));
    public IReadOnlyList<GameKouryakuKun.Core.Growth.GrowthSnapshot> LoadGrowthSnapshots(int limit = 5000) =>
        _db.Query("SELECT json FROM (SELECT json, snapshot_id FROM growth_snapshots ORDER BY snapshot_id DESC LIMIT $l) ORDER BY snapshot_id", r => GrowthJson.Read(r.GetString(0)), ("$l", limit));

    /// <summary>連続履歴の切れ目（実行の始まり・Supervisor の復旧の後）。エピソードの区切りの候補を探すとき、そこをまたいで流れをつながない</summary>
    public HashSet<long> LoadStreamBreaks()
    {
        var b = new HashSet<long>(_db.Query("SELECT MIN(seq) FROM experience_stream GROUP BY run_id", r => r.GetInt64(0)));
        if (_db.Tables().Contains("system_events"))
            foreach (var x in _db.Query("SELECT seq_before FROM system_events WHERE kind='OBSERVATION_LOST' AND seq_before IS NOT NULL", r => r.GetInt64(0))) b.Add(x + 1);
        return b;
    }

    /// <summary>エピソードの区切りの候補を今の判定に合わせる（新しい候補・取り下げ・戻した候補を出来事として残す。行は消さない）</summary>
    public IReadOnlyList<(string State, string Kind)> SyncBoundaryCandidates(GameKouryakuKun.Core.Boundaries.FlowResetMiner.Result r, long seq)
    {
        var before = _db.Query("SELECT state, status FROM episode_boundary_candidates", x => (x.GetString(0), x.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        var evs = new List<(string, string)>();
        var acc = r.Accepted.ToDictionary(c => c.State, StringComparer.Ordinal);
        foreach (var c in r.Examined)
        {
            bool now = acc.ContainsKey(c.State);
            before.TryGetValue(c.State, out var was);
            if (!now && was == null) continue;
            string status = now ? "candidate" : "withdrawn";
            _db.Exec("INSERT INTO episode_boundary_candidates(state,status,arrivals,predecessors,large_share,flow,recurrence,predecessors_with_flow,context_dependence,first_seq,last_seq,evidence_json,updated_at) VALUES($s,$st,$a,$p,$l,$f,$r,$pw,$d,$fs,$ls,$e,$t) ON CONFLICT(state) DO UPDATE SET status=$st,arrivals=$a,predecessors=$p,large_share=$l,flow=$f,recurrence=$r,predecessors_with_flow=$pw,context_dependence=$d,first_seq=$fs,last_seq=$ls,evidence_json=$e,updated_at=$t", null,
                ("$s", c.State), ("$st", status), ("$a", c.Arrivals), ("$p", c.Predecessors), ("$l", c.LargeShare), ("$f", c.Flow), ("$r", c.Recurrence), ("$pw", c.PredecessorsWithFlow), ("$d", c.ContextDependence),
                ("$fs", c.FirstSeq), ("$ls", c.LastSeq), ("$e", Json.Obj(("why", c.Why), ("large_threshold", r.LargeThreshold), ("steps", r.Steps), ("breaks", r.Breaks))), ("$t", Now()));
            string? kind = now && was == null ? "proposed" : now && was == "withdrawn" ? "reproposed" : !now && was == "candidate" ? "withdrawn" : null;
            if (kind != null) { _db.Exec("INSERT INTO episode_boundary_events(state, kind, seq, reason, at) VALUES($s,$k,$q,$r,$t)", null, ("$s", c.State), ("$k", kind), ("$q", seq), ("$r", c.Why), ("$t", Now())); evs.Add((c.State, kind)); }
        }
        // 判定の対象から外れた（到着が少なくなった）候補も取り下げる
        foreach (var (st, was) in before) if (was == "candidate" && !r.Examined.Any(c => c.State == st))
        {
            _db.Exec("UPDATE episode_boundary_candidates SET status='withdrawn', updated_at=$t WHERE state=$s", null, ("$s", st), ("$t", Now()));
            _db.Exec("INSERT INTO episode_boundary_events(state, kind, seq, reason, at) VALUES($s,'withdrawn',$q,'判定の対象の範囲から外れた',$t)", null, ("$s", st), ("$q", seq), ("$t", Now())); evs.Add((st, "withdrawn"));
        }
        return evs;
    }

    /// <summary>v0.29.0: 1 手の構造化した観測</summary>
    public void AddObservation(long runId, long seq, long step, int schemaVersion, string provenance, string json) =>
        _db.Exec("INSERT INTO structured_observations(run_id, seq, step, schema_version, provenance, json, created_at) VALUES($r,$q,$s,$v,$p,$j,$t)", null,
            ("$r", runId), ("$q", seq), ("$s", step), ("$v", schemaVersion), ("$p", provenance), ("$j", json), ("$t", Now()));
    public int CountObservations(long runId) => _db.Query("SELECT COUNT(*) FROM structured_observations WHERE run_id=$r", r => r.GetInt32(0), ("$r", runId)).First();

    public void SaveGoalModel(string key, string json) =>
        _db.Exec("INSERT INTO goal_model(key, json, updated_at) VALUES($k,$j,$t) ON CONFLICT(key) DO UPDATE SET json=$j, updated_at=$t", null, ("$k", key), ("$j", json), ("$t", Now()));
    public string? LoadGoalModel(string key) => _db.Query("SELECT json FROM goal_model WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault();

    public void SaveScreenBackground(IEnumerable<GameKouryakuKun.Core.Turn.BackgroundActivityBook.Stored> rows)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var r in rows)
            _db.Exec("INSERT INTO screen_background(screen, timeouts, samples_json, updated_at) VALUES($k,$o,$s,$t) ON CONFLICT(screen) DO UPDATE SET timeouts=$o, samples_json=$s, updated_at=$t", null,
                ("$k", r.Screen), ("$o", r.Timeouts), ("$s", "[" + string.Join(",", r.Samples.Select(x => $"[{x.R.ToString("R", ci)},{x.C.ToString("R", ci)}]")) + "]"), ("$t", Now()));
    }

    public IReadOnlyList<GameKouryakuKun.Core.Turn.BackgroundActivityBook.Stored> LoadScreenBackground() =>
        _db.Query("SELECT screen, timeouts, samples_json FROM screen_background", r =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.GetString(2));
            var list = doc.RootElement.EnumerateArray().Select(a => (a[0].GetDouble(), a[1].GetDouble())).ToList();
            return new GameKouryakuKun.Core.Turn.BackgroundActivityBook.Stored(r.GetString(0), r.GetInt32(1), list);
        });

    public void SaveScreenMotion(IEnumerable<MotionMaskBook.Stored> rows)
    {
        _db.InTransaction(tx =>
        {
            foreach (var r in rows)
                _db.Exec("INSERT INTO screen_motion(screen, width, height, block, timeouts, counts_json, activity_turns, activity_sum, updated_at) VALUES($k,$w,$h,$b,$o,$c,$at,$as,$t) " +
                         "ON CONFLICT(screen) DO UPDATE SET width=$w, height=$h, block=$b, timeouts=$o, counts_json=$c, activity_turns=$at, activity_sum=$as, updated_at=$t", tx,
                    ("$k", r.Key), ("$w", r.Width), ("$h", r.Height), ("$b", r.Block), ("$o", r.Timeouts), ("$c", "[" + string.Join(",", r.Counts) + "]"),
                    ("$at", r.ActivityTurns), ("$as", r.ActivitySum), ("$t", Now()));
            return 0;
        });
    }

    public IReadOnlyList<MotionMaskBook.Stored> LoadScreenMotion() =>
        _db.Query("SELECT screen, width, height, block, timeouts, counts_json, activity_turns, activity_sum FROM screen_motion ORDER BY screen",
            r => new MotionMaskBook.Stored(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
                r.GetString(5).Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                r.GetInt32(6), r.GetDouble(7)));

    /// <summary>連続履歴の最後の limit 手を、通し番号の順に返す（実行単位では分けない）</summary>
    /// 分割で読み直した手は、読み直した状態で返す（stream_reinterpretations。直接の記録そのものは書き換えない）
    public IReadOnlyList<StreamStep> LoadStream(int limit) =>
        _db.Query("SELECT s.seq, COALESCE(ri.state, s.state), s.action, s.changed, COALESCE(ri.next_state, s.next_state), COALESCE(s.change_ratio, 0), ri.seq IS NOT NULL, s.vis_state FROM (SELECT * FROM experience_stream ORDER BY seq DESC LIMIT $l) s " +
                  "LEFT JOIN stream_reinterpretations ri ON ri.seq = s.seq ORDER BY s.seq",
            r => new StreamStep(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetString(4), r.GetDouble(5)) { Reinterpreted = r.GetInt64(6) != 0, VisState = r.IsDBNull(7) ? null : r.GetInt32(7) }, ("$l", limit));

    public int StreamCount() => Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM experience_stream") ?? 0);

    // ---- 区画とキーの反応の対応表（v6。記録と報告だけ） ----
    public void SaveReactionMaps(IEnumerable<(string Context, string Action, int Cell, int N, int Changed, int Translated, double SumDx, double SumDy)> rows)
    {
        _db.InTransaction(tx =>
        {
            foreach (var r in rows)
                _db.Exec("INSERT INTO reaction_maps(context, action, cell, n, changed, translated, sum_dx, sum_dy, updated_at) VALUES($c,$a,$k,$n,$ch,$tr,$dx,$dy,$t) " +
                         "ON CONFLICT(context, action, cell) DO UPDATE SET n=$n, changed=$ch, translated=$tr, sum_dx=$dx, sum_dy=$dy, updated_at=$t", tx,
                    ("$c", r.Context), ("$a", r.Action), ("$k", r.Cell), ("$n", r.N), ("$ch", r.Changed), ("$tr", r.Translated), ("$dx", r.SumDx), ("$dy", r.SumDy), ("$t", Now()));
            return 0;
        });
    }

    public IReadOnlyList<(string Context, string Action, int Cell, int N, int Changed, int Translated, double SumDx, double SumDy)> LoadReactionMaps() =>
        _db.Query("SELECT context, action, cell, COALESCE(n,0), COALESCE(changed,0), COALESCE(translated,0), COALESCE(sum_dx,0), COALESCE(sum_dy,0) FROM reaction_maps",
            r => (r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetDouble(6), r.GetDouble(7)));

    // ---- 経験から見つけた操作の並び（v6。名前は ActionSequence #番号 だけ） ----
    public void SaveActionSequences(IEnumerable<ActionSequenceCandidate> cands)
    {
        _db.InTransaction(tx =>
        {
            foreach (var c in cands) UpsertSequence(c, tx);
            return 0;
        });
    }

    private void UpsertSequence(ActionSequenceCandidate c, Microsoft.Data.Sqlite.SqliteTransaction? tx) =>
        _db.Exec("INSERT INTO action_sequences(sequence_id, steps, start_node, end_node, n_total, n_at_start, start_similarity, reproducibility, found_seq, updated_at, status, status_seq, last_reproduced_seq, recent_repro, recent_n, origin) " +
                 "VALUES($i,$s,$st,$e,$n,$na,$sim,$rep,$f,$t,$stt,$ss,$lr,$rr,$rn,$o) " +
                 "ON CONFLICT(sequence_id) DO UPDATE SET end_node=$e, n_total=$n, n_at_start=$na, start_similarity=$sim, reproducibility=$rep, updated_at=$t, status=$stt, status_seq=$ss, last_reproduced_seq=$lr, recent_repro=$rr, recent_n=$rn", tx,
            ("$i", c.Id), ("$s", string.Join(" ", c.Steps)), ("$st", c.Start), ("$e", c.End), ("$n", c.Total), ("$na", c.AtStart), ("$sim", c.StartSimilarity), ("$rep", c.Reproducibility), ("$f", c.FoundAtSeq), ("$t", Now()),
            ("$stt", c.Status == SequenceStatus.Active ? "active" : "dormant"), ("$ss", c.StatusSeq), ("$lr", c.LastReproducedSeq), ("$rr", c.RecentReproducibility), ("$rn", c.RecentCount), ("$o", c.Origin));

    /// <summary>並びの候補の状態の変化を記録する（候補の行も更新する。記録は消さない）</summary>
    public void AddSequenceEvent(SequenceEvent e)
    {
        _db.InTransaction(tx =>
        {
            UpsertSequence(e.Candidate, tx);
            _db.Exec("INSERT INTO action_sequence_events(sequence_id, seq, kind, reason, recent_repro, at_start, created_at) VALUES($i,$q,$k,$r,$rr,$a,$t)", tx,
                ("$i", e.Candidate.Id), ("$q", e.Seq), ("$k", e.Kind), ("$r", e.Reason), ("$rr", e.Candidate.RecentReproducibility), ("$a", e.Candidate.AtStart), ("$t", Now()));
            return 0;
        });
    }

    /// <summary>判断の記録を要約する区間の長さ（通し番号）</summary>
    public const long DecisionRollupBlock = 10_000;   // 根拠: 汎用（起動時に流し直す履歴の 1/2。区間ごとの要約が数百行に収まる長さ）

    /// <summary>
    /// 終わった区間（通し番号が区間の終わりを超えた区間）の判断の記録を、ノード×選んだ操作×決め方ごとに要約して追記する。
    /// すでに要約した区間は飛ばす（何度呼んでも同じ）。元の decision_records は消さない・書き換えない
    /// </summary>
    public int RollupDecisions(long block = DecisionRollupBlock)
    {
        var maxObj = _db.Scalar("SELECT MAX(seq) FROM decision_records");
        if (maxObj == null) return 0;
        long max = Convert.ToInt64(maxObj, CultureInfo.InvariantCulture);
        var done = new HashSet<long>(_db.Query("SELECT block_start FROM decision_rollup_blocks", r => r.GetInt64(0)));
        int made = 0;
        for (long b0 = 0; b0 + block <= max; b0 += block)
        {
            if (done.Contains(b0)) continue;
            long b1 = b0 + block - 1;
            _db.InTransaction(tx =>
            {
                _db.Exec("INSERT OR IGNORE INTO decision_rollups(block_start, block_end, node, chosen, rule, n, checked, hits, sum_surprisal, sum_excess, revisions, ambiguous) " +
                         "SELECT $b0, $b1, node, chosen, rule, COUNT(*), COUNT(actual_next), COALESCE(SUM(hit),0), COALESCE(SUM(surprisal),0), COALESCE(SUM(excess),0), COUNT(revision_id), COALESCE(SUM(obs_ambiguous),0) " +
                         "FROM decision_records WHERE seq BETWEEN $b0 AND $b1 GROUP BY node, chosen, rule", tx, ("$b0", b0), ("$b1", b1));
                _db.Exec("INSERT OR IGNORE INTO decision_rollup_blocks(block_start, block_end, rows, bytes, created_at) " +
                         "SELECT $b0, $b1, COUNT(*), COALESCE(SUM(LENGTH(candidates_json)),0), $t FROM decision_records WHERE seq BETWEEN $b0 AND $b1", tx, ("$b0", b0), ("$b1", b1), ("$t", Now()));
                return 0;
            });
            made++;
        }
        return made;
    }

    /// <summary>操作の並びを実行した結果（予測と照らした結果）を1件追記する。書き換えはしない</summary>
    public void AddSequenceExecution(int sequenceId, string name, long seqEnd, string start, string end, PredictionCheck chk, bool informed, bool surprised)
    {
        _db.Insert("INSERT INTO sequence_executions(sequence_id, sequence_name, seq_end, start_node, end_node, predicted_end, p_predicted, hit, surprisal, excess, informed, surprised, created_at) " +
                   "VALUES($i,$n,$q,$s,$e,$pe,$pp,$h,$su,$ex,$in,$sp,$t)", null,
            ("$i", sequenceId), ("$n", name), ("$q", seqEnd), ("$s", start), ("$e", end), ("$pe", chk.Prediction.TopNext),
            ("$pp", chk.Prediction.Top.Count > 0 ? Math.Round(chk.Prediction.Top[0].P, 4) : null), ("$h", chk.Hit ? 1 : 0),
            ("$su", Fin(chk.Surprisal)), ("$ex", Fin(chk.Excess)), ("$in", informed ? 1 : 0), ("$sp", surprised ? 1 : 0), ("$t", Now()));
    }

    /// <summary>実行の記録（並びの番号, 終わりの手の番号, 予測より明らかに驚いたか）を古い順に</summary>
    public IReadOnlyList<(int SequenceId, long SeqEnd, bool Surprised)> LoadSequenceExecutions() =>
        _db.Query("SELECT sequence_id, seq_end, surprised FROM sequence_executions ORDER BY exec_id", r => (r.GetInt32(0), r.GetInt64(1), r.GetInt32(2) == 1));

    public IReadOnlyList<ActionSequenceCandidate> LoadActionSequences() =>
        _db.Query("SELECT sequence_id, steps, start_node, COALESCE(end_node,''), COALESCE(n_total,0), COALESCE(n_at_start,0), COALESCE(start_similarity,0), COALESCE(reproducibility,0), COALESCE(found_seq,0), " +
                  "COALESCE(status,'active'), COALESCE(status_seq,0), COALESCE(last_reproduced_seq,0), COALESCE(recent_repro,0.5), COALESCE(recent_n,0), COALESCE(origin,'stream') FROM action_sequences ORDER BY sequence_id",
            r => new ActionSequenceCandidate { Id = r.GetInt32(0), Steps = r.GetString(1).Split(' ', StringSplitOptions.RemoveEmptyEntries), Start = r.GetString(2), End = r.GetString(3),
                Total = r.GetInt32(4), AtStart = r.GetInt32(5), StartSimilarity = r.GetDouble(6), Reproducibility = r.GetDouble(7), FoundAtSeq = r.GetInt64(8),
                Status = r.GetString(9) == "dormant" ? SequenceStatus.Dormant : SequenceStatus.Active, StatusSeq = r.GetInt64(10), LastReproducedSeq = r.GetInt64(11),
                RecentReproducibility = r.GetDouble(12), RecentCount = r.GetInt32(13), Origin = r.GetString(14) });

    // ---- 経験の出どころ（v7）: 分割による読み直し・弱い事前の推定 ----
    /// <summary>読み直した遷移（元の (from, to) から新しい (from, to) へ）。作った順</summary>
    public sealed record Reclassified(string OrigFrom, string Action, string OrigTo, string NewFrom, string NewTo, int N, int Changed);

    /// <summary>
    /// 見た目の状態 vis を粗いノード coarse から新しいノード newNode に分けたときの読み直し。見た目の状態の記録が残っている手（v6 以降）だけを対象に、
    /// その手の始まり（vis_state）と行き先（次の手の vis_state。通し番号が続き、状態がつながるときだけ）を見て、新しいノードとして解釈し直す。
    /// 直接の記録は書き換えず、stream_reinterpretations と derived_transitions（origin = 'reclassified'・cause = 'split'）に書く。読み直せない集計はそのまま。
    /// v0.12.0: 索引で対象の手だけを読む（全履歴を読まない）。読み直しの済み印を state_splits に付ける
    /// </summary>
    public IReadOnlyList<Reclassified> ReclassifyForSplit(string coarse, int vis, string newNode, double distance = double.NaN, int leader = 0)
    {
        // 分割の行を先に作る（読み直しの済み印を確実に残し、次の起動で二重に読み直さないため）
        _db.Exec("INSERT OR IGNORE INTO state_splits(vis_id, coarse_node, split_node, distance, leader_vis_id, decided_at) VALUES($i,$c,$n,$d,$l,$t)", null,
            ("$i", vis), ("$c", coarse), ("$n", newNode), ("$d", double.IsFinite(distance) ? distance : null), ("$l", leader), ("$t", Now()));
        var r = Reinterpret(coarse, "split", vis,
            x => x.State == coarse && x.Vis == vis ? newNode : x.State,
            x => x.Next == coarse && x.NextVis == vis ? newNode : x.Next);
        _db.Exec("UPDATE state_splits SET reclassified_at=$t WHERE vis_id=$v", null, ("$t", Now()), ("$v", vis));
        return r;
    }

    /// <summary>
    /// 分割の取り消しの読み直し: いま split_node と解釈している手（分割の後に直接その名前で記録した手を含む）を、粗いノード coarse として解釈し直す。
    /// 直接の記録は書き換えない（stream_reinterpretations・derived_transitions に cause = 'revert' で書く）
    /// </summary>
    public IReadOnlyList<Reclassified> ReclassifyForRevert(string splitNode, int vis, string coarse) =>
        Reinterpret(splitNode, "revert", vis, x => x.State == splitNode ? coarse : x.State, x => x.Next == splitNode ? coarse : x.Next);

    private sealed record StreamRow(long Seq, string RawState, string RawNext, string Action, bool Changed, int? Vis, string State, string Next, int? NextVis);

    /// <summary>今の解釈で node が始まりか行き先になっている手（と、その次の手の見た目の状態）だけを読む</summary>
    private List<StreamRow> RowsTouching(string node) =>
        _db.Query("SELECT s.seq, s.state, s.next_state, s.action, s.changed, s.vis_state, COALESCE(ri.state, s.state), COALESCE(ri.next_state, s.next_state), n.state, n.vis_state " +
                  "FROM experience_stream s LEFT JOIN stream_reinterpretations ri ON ri.seq = s.seq LEFT JOIN experience_stream n ON n.seq = s.seq + 1 " +
                  "WHERE s.seq IN (SELECT seq FROM experience_stream WHERE state = $c UNION SELECT seq FROM experience_stream WHERE next_state = $c " +
                  "UNION SELECT seq FROM stream_reinterpretations WHERE state = $c UNION SELECT seq FROM stream_reinterpretations WHERE next_state = $c) ORDER BY s.seq",
            r => new StreamRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0, r.IsDBNull(5) ? null : r.GetInt32(5), r.GetString(6), r.GetString(7),
                // 行き先の見た目: 次の手が通し番号で続き、生の状態がつながるときだけ
                !r.IsDBNull(8) && r.GetString(8) == r.GetString(2) && !r.IsDBNull(9) ? r.GetInt32(9) : null), ("$c", node));

    private IReadOnlyList<Reclassified> Reinterpret(string node, string cause, int vis, Func<StreamRow, string> newFrom, Func<StreamRow, string> newTo)
    {
        var rows = RowsTouching(node);
        var agg = new Dictionary<(string, string, string, string, string), (int N, int Ch)>();
        var re = new List<(long Seq, string State, string Next)>();
        foreach (var r in rows)
        {
            string nf = newFrom(r), nt = newTo(r);
            if (nf == r.State && nt == r.Next) continue;
            re.Add((r.Seq, nf, nt));
            var k = (r.State, r.Action, r.Next, nf, nt);
            var v = agg.TryGetValue(k, out var x) ? x : (0, 0);
            agg[k] = (v.Item1 + 1, v.Item2 + (r.Changed ? 1 : 0));
        }
        _db.InTransaction(tx =>
        {
            foreach (var (q, st, nx) in re)
                _db.Exec("INSERT INTO stream_reinterpretations(seq, state, next_state, split_vis, updated_at, cause) VALUES($q,$s,$n,$v,$t,$c) ON CONFLICT(seq) DO UPDATE SET state=$s, next_state=$n, split_vis=$v, updated_at=$t, cause=$c", tx,
                    ("$q", q), ("$s", st), ("$n", nx), ("$v", vis), ("$t", Now()), ("$c", cause));
            foreach (var ((of, a, ot, nf, nt), (n, ch)) in agg)
                _db.Exec("INSERT INTO derived_transitions(origin, from_node, logical_key, to_node, n, changed_n, orig_from, orig_to, split_vis, created_at, cause) VALUES('reclassified',$f,$k,$to,$n,$c,$of,$ot,$v,$t,$cause)", tx,
                    ("$f", nf), ("$k", a), ("$to", nt), ("$n", n), ("$c", ch), ("$of", of), ("$ot", ot), ("$v", vis), ("$t", Now()), ("$cause", cause));
            return 0;
        });
        return agg.Select(kv => new Reclassified(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Key.Item4, kv.Key.Item5, kv.Value.N, kv.Value.Ch)).ToList();
    }

    /// <summary>読み直しの済み印のない有効な分割（v0.10.0 より前に作った分割など）。起動時に読み直す</summary>
    public IReadOnlyList<(string Coarse, int Vis, string Node)> PendingSplitReclassifications() =>
        _db.Query("SELECT coarse_node, vis_id, split_node FROM state_splits WHERE status='active' AND reclassified_at IS NULL ORDER BY vis_id",
            r => (r.GetString(0), r.GetInt32(1), r.GetString(2)));

    /// <summary>分割の変化の履歴を残す（split・reclassified・reverted・resplit）。取り消しのときは状態も変える（行は消さない）</summary>
    public void AddSplitEvent(int vis, string coarse, string node, string kind, string? evidenceKind, double distance, double logBf, int moved, string reason)
    {
        _db.InTransaction(tx =>
        {
            _db.Exec("INSERT INTO state_split_events(vis_id, coarse_node, split_node, kind, evidence_kind, distance, log_bayes_factor, moved, reason, created_at) VALUES($v,$c,$n,$k,$e,$d,$b,$m,$r,$t)", tx,
                ("$v", vis), ("$c", coarse), ("$n", node), ("$k", kind), ("$e", evidenceKind), ("$d", double.IsFinite(distance) ? distance : null), ("$b", double.IsFinite(logBf) ? logBf : null), ("$m", moved), ("$r", reason), ("$t", Now()));
            if (kind == "reverted") _db.Exec("UPDATE state_splits SET status='reverted', status_at=$t WHERE vis_id=$v", tx, ("$t", Now()), ("$v", vis));
            if (kind == "resplit") _db.Exec("UPDATE state_splits SET status='active', status_at=$t, reclassified_at=NULL WHERE vis_id=$v", tx, ("$t", Now()), ("$v", vis));
            return 0;
        });
    }

    /// <summary>これまでの読み直し（作った順。探索の集計に順に当て直す）</summary>
    public IReadOnlyList<Reclassified> LoadReclassified() =>
        _db.Query("SELECT orig_from, logical_key, orig_to, from_node, to_node, n, COALESCE(changed_n,0) FROM derived_transitions WHERE origin='reclassified' ORDER BY derived_id",
            r => new Reclassified(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6)));

    /// <summary>弱い事前の推定を記録する（今の一覧で置き換える。どの出どころをどれだけの強さで使ったか）。変化は experience_prior_events に残す（v8）</summary>
    public void SavePriors(IEnumerable<(string Node, string Source, string Origin, double Strength, string Evidence)> priors)
    {
        var now = priors.ToList();
        var before = _db.Query("SELECT node, source_node, origin, strength FROM experience_priors", r => (N: r.GetString(0), S: r.GetString(1), O: r.GetString(2), W: r.GetDouble(3)))
            .ToDictionary(x => (x.N, x.S, x.O), x => x.W);
        _db.InTransaction(tx =>
        {
            var keys = new HashSet<(string, string, string)>();
            foreach (var p in now)
            {
                var k = (p.Node, p.Source, p.Origin); keys.Add(k);
                string? kind = !before.TryGetValue(k, out var w) ? "added" : Math.Abs(w - p.Strength) > 1e-9 ? "changed" : null;
                if (kind != null)
                    _db.Exec("INSERT INTO experience_prior_events(node, source_node, origin, kind, strength, evidence, created_at) VALUES($n,$s,$o,$k,$w,$e,$t)", tx,
                        ("$n", p.Node), ("$s", p.Source), ("$o", p.Origin), ("$k", kind), ("$w", p.Strength), ("$e", p.Evidence), ("$t", Now()));
            }
            foreach (var (k, w) in before)
                if (!keys.Contains(k))
                    _db.Exec("INSERT INTO experience_prior_events(node, source_node, origin, kind, strength, evidence, created_at) VALUES($n,$s,$o,'removed',NULL,NULL,$t)", tx,
                        ("$n", k.Item1), ("$s", k.Item2), ("$o", k.Item3), ("$t", Now()));
            _db.Exec("DELETE FROM experience_priors", tx);
            foreach (var p in now)
                _db.Exec("INSERT INTO experience_priors(node, source_node, origin, strength, evidence, updated_at) VALUES($n,$s,$o,$w,$e,$t)", tx,
                    ("$n", p.Node), ("$s", p.Source), ("$o", p.Origin), ("$w", p.Strength), ("$e", p.Evidence), ("$t", Now()));
            return 0;
        });
    }

    // ---- 予測・比較・修正・判断の記録（v8） ----
    /// <summary>予測の外れによる内部モデルの修正を記録する。返り値は revision_id</summary>
    public long AddModelRevision(ModelRevision r)
    {
        return _db.Insert("INSERT INTO model_revisions(seq, node, logical_key, weight_before, weight_after, recent_kept, reason, created_at) VALUES($q,$n,$k,$b,$a,$r,$why,$t)", null,
            ("$q", r.Seq), ("$n", r.Node), ("$k", r.Action), ("$b", r.WeightBefore), ("$a", r.WeightAfter), ("$r", r.RecentKept), ("$why", r.Reason), ("$t", Now()));
    }

    /// <summary>手ごとの判断の記録（選んだ規則・点数の内訳・予測と結果の比較）。返り値は decision_id</summary>
    public long AddDecision(DecisionRecord d)
    {
        var p = d.Check?.Prediction ?? d.Prediction;
        return _db.Insert("INSERT INTO decision_records(seq, turn_id, node, chosen, composite, rule, probability, score, predicted_next, p_predicted, p_change, entropy, uncertainty, " +
                     "evidence_direct, evidence_reinterpreted, evidence_prior, prior_sources, actual_next, changed, p_actual, surprisal, excess, hit, revision_id, candidates_json, created_at, " +
                     "obs_vis_ratio, obs_mode_ratio, frame_age_ms, obs_ambiguous, action_source, p_change_wait, p_action_caused) " +
                     "VALUES($q,$tu,$n,$c,$co,$ru,$pr,$sc,$pn,$pp,$pc,$en,$un,$ed,$er,$ep,$ps,$an,$ch,$pa,$su,$ex,$hi,$rv,$cj,$t,$ov,$om,$fa,$am,$src,$pw,$pac)", null,
                ("$src", d.ActionSource), ("$pw", d.Attribution is { WaitTries: > 0 } aw ? Fin(aw.PChangeWait) : null), ("$pac", d.Attribution is { } ac ? Fin(ac.PActionCaused) : null),
                ("$q", d.Seq), ("$tu", d.TurnId), ("$n", d.Node), ("$c", d.Chosen), ("$co", d.Composite), ("$ru", d.Rule), ("$pr", double.IsFinite(d.Probability) ? d.Probability : null), ("$sc", d.Score),
                ("$pn", p?.TopNext), ("$pp", p?.PTop), ("$pc", p?.PChange), ("$en", p?.Entropy), ("$un", p?.Uncertainty),
                ("$ed", p?.DirectN), ("$er", p?.ReinterpretedN), ("$ep", p?.PriorN), ("$ps", p == null ? null : Json.Arr(p.PriorSources)),
                ("$an", d.Check?.Actual), ("$ch", d.Check == null ? null : d.Check.Changed ? 1 : 0), ("$pa", d.Check?.PActual), ("$su", d.Check?.Surprisal), ("$ex", d.Check?.Excess),
                ("$hi", d.Check == null ? null : d.Check.Hit ? 1 : 0), ("$rv", d.RevisionId), ("$cj", CandidatesJson(d.Candidates, d.Composite ?? d.Chosen)), ("$t", Now()),
            ("$ov", Fin(d.ObsVisRatio)), ("$om", Fin(d.ObsModeRatio)), ("$fa", Fin(d.FrameAgeMs)), ("$am", d.Ambiguous ? 1 : 0));
    }

    private static object? Fin(double x) => double.IsFinite(x) ? Math.Round(x, 4) : null;

    /// <summary>判断の記録に残す候補の数の上限（確率の高い順。選んだ候補は必ず残す）</summary>
    public const int DecisionCandidatesKept = 8;   // 根拠: 汎用（記録の大きさを 1 手あたり 1KB 前後に抑える。全候補の数は別に残す）

    public static string CandidatesJson(IReadOnlyList<CandidateScore> cands, string chosen)
    {
        var keep = cands.OrderByDescending(c => c.Probability).Take(DecisionCandidatesKept).ToList();
        if (!keep.Any(c => c.Action == chosen) && cands.FirstOrDefault(c => c.Action == chosen) is { } me) keep.Add(me);
        static double R(double x) => Math.Round(x, 4);
        // 列の並びは fields に1回だけ書き、候補ごとは配列にする（1 手あたりの大きさを抑える）
        return Json.Obj(("total", cands.Count),
            ("fields", new[] { "action", "score", "p", "tries", "novelty", "rates", "info", "uncertainty", "rev", "flags", "priors", "dest", "text", "nat_cut" }),
            ("kept", keep.Select(c => (object?)new object?[] { c.Action, R(c.Score), R(c.Probability), c.DirectTries, R(c.Novelty), R(c.Rates), R(c.Information), R(c.Uncertainty), R(c.Reversibility),
                (c.Stagnant ? "s" : "") + (c.Deferred ? "d" : "") + (c.ExcludedByUser ? "u" : ""), c.Priors, R(c.Destination), R(c.Text), R(c.NaturalChangeCut + c.NaturalLeaveCut) }).ToList()));
    }

    public sealed record DecisionRow(long Id, long? Seq, string Node, string Chosen, string? Composite, string Rule, double? Probability, string? PredictedNext, double? PPredicted,
        double? PChange, double? Uncertainty, double? EvidenceDirect, double? EvidenceReinterpreted, double? EvidencePrior, string? PriorSources,
        string? ActualNext, bool? Changed, double? Surprisal, double? Excess, bool? Hit, long? RevisionId, string? CandidatesJson);

    public IReadOnlyList<DecisionRow> RecentDecisions(int limit) =>
        _db.Query("SELECT decision_id, seq, node, chosen, composite, rule, probability, predicted_next, p_predicted, p_change, uncertainty, evidence_direct, evidence_reinterpreted, evidence_prior, prior_sources, " +
                  "actual_next, changed, surprisal, excess, hit, revision_id, candidates_json FROM decision_records ORDER BY decision_id DESC LIMIT $l",
            r => new DecisionRow(r.GetInt64(0), N<long>(r, 1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), N<double>(r, 6),
                r.IsDBNull(7) ? null : r.GetString(7), N<double>(r, 8), N<double>(r, 9), N<double>(r, 10), N<double>(r, 11), N<double>(r, 12), N<double>(r, 13), r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetInt32(16) != 0, N<double>(r, 17), N<double>(r, 18), r.IsDBNull(19) ? null : r.GetInt32(19) != 0,
                N<long>(r, 20), r.IsDBNull(21) ? null : r.GetString(21)), ("$l", limit));

    public sealed record RevisionRow(long Id, long? Seq, string Node, string Action, double WeightBefore, double WeightAfter, string? Reason);
    public IReadOnlyList<RevisionRow> RecentRevisions(int limit) =>
        _db.Query("SELECT revision_id, seq, node, logical_key, COALESCE(weight_before,0), COALESCE(weight_after,0), reason FROM model_revisions ORDER BY revision_id DESC LIMIT $l",
            r => new RevisionRow(r.GetInt64(0), N<long>(r, 1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetDouble(5), r.IsDBNull(6) ? null : r.GetString(6)), ("$l", limit));

    private static T? N<T>(Microsoft.Data.Sqlite.SqliteDataReader r, int i) where T : struct =>
        r.IsDBNull(i) ? null : typeof(T) == typeof(long) ? (T)(object)r.GetInt64(i) : (T)(object)r.GetDouble(i);

    // ---- 見た目の状態・遷移の特徴・分割（v6） ----
    public void SaveVisualStates(VisualStateBook book)
    {
        var groups = book.TransitionGroups();
        _db.InTransaction(tx =>
        {
            foreach (var v in book.States)
            {
                var blob = new byte[v.Prototype.Length * 4]; Buffer.BlockCopy(v.Prototype, 0, blob, 0, blob.Length);
                _db.Exec("INSERT INTO visual_states(vis_id, prototype_blob, seen_count, coarse_node, trans_group, updated_at) VALUES($i,$b,$n,$c,$g,$t) " +
                         "ON CONFLICT(vis_id) DO UPDATE SET prototype_blob=$b, seen_count=$n, coarse_node=$c, trans_group=$g, updated_at=$t", tx,
                    ("$i", v.Id), ("$b", blob), ("$n", v.Seen), ("$c", v.DominantCoarse), ("$g", groups.TryGetValue(v.Id, out var g) ? g : null), ("$t", Now()));
            }
            foreach (var (id, fp) in book.Fingerprints)
                foreach (var (a, c) in fp.ByAction)
                    _db.Exec("INSERT INTO state_fingerprints(vis_id, action, n_none, n_stay, n_leave) VALUES($i,$a,$x,$y,$z) " +
                             "ON CONFLICT(vis_id, action) DO UPDATE SET n_none=$x, n_stay=$y, n_leave=$z", tx, ("$i", id), ("$a", a), ("$x", c[0]), ("$y", c[1]), ("$z", c[2]));
            // 分割の行は消さない。状態（有効／取り消し）は AddSplitEvent で変える
            foreach (var (id, sp) in book.Splits)
                _db.Exec("INSERT OR IGNORE INTO state_splits(vis_id, coarse_node, split_node, distance, leader_vis_id, decided_at) VALUES($i,$c,$n,$d,$l,$t)", tx,
                    ("$i", id), ("$c", sp.Coarse), ("$n", sp.Node), ("$d", sp.Distance), ("$l", sp.Leader), ("$t", Now()));
            return 0;
        });
    }

    public void LoadVisualStates(VisualStateBook book)
    {
        var states = _db.Query("SELECT vis_id, prototype_blob, COALESCE(seen_count,0), coarse_node FROM visual_states ORDER BY vis_id", r =>
        {
            var blob = (byte[])r.GetValue(1); var f = new float[blob.Length / 4]; Buffer.BlockCopy(blob, 0, f, 0, f.Length * 4);
            return (r.GetInt32(0), f, r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3));
        });
        var fps = _db.Query("SELECT vis_id, action, COALESCE(n_none,0), COALESCE(n_stay,0), COALESCE(n_leave,0) FROM state_fingerprints",
            r => (r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
        var splits = _db.Query("SELECT coarse_node, vis_id, split_node, COALESCE(distance,0), COALESCE(leader_vis_id,0), COALESCE(status,'active') FROM state_splits",
            r => (r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetDouble(3), r.GetInt32(4), r.GetString(5)));
        book.Load(states, fps, splits.Where(x => x.Item6 == "active").Select(x => (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5)));
        book.LoadReverted(splits.Where(x => x.Item6 == "reverted").Select(x => (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5)));
    }

    // ---- 系列から見つけたパターン（名前は持たない） ----
    public void SaveSequencePatterns(IEnumerable<SequencePattern> patterns)
    {
        _db.InTransaction(tx =>
        {
            foreach (var p in patterns)
                _db.Exec("INSERT INTO sequence_patterns(pattern_id, kind, signature, length, n, first_seq, last_seq, updated_at) VALUES($i,$k,$s,$l,$n,$f,$z,$t) " +
                         "ON CONFLICT(pattern_id) DO UPDATE SET n=MAX(n, $n), last_seq=$z, updated_at=$t", tx,
                    ("$i", p.Id), ("$k", p.Kind.ToString().ToLowerInvariant()), ("$s", p.Signature), ("$l", p.Length), ("$n", p.Count), ("$f", p.FirstSeq), ("$z", p.LastSeq), ("$t", Now()));
            return 0;
        });
    }

    public IReadOnlyList<SequencePattern> LoadSequencePatterns() =>
        _db.Query("SELECT pattern_id, kind, signature, COALESCE(length,0), COALESCE(n,0), COALESCE(first_seq,0), COALESCE(last_seq,0) FROM sequence_patterns ORDER BY pattern_id",
            r => new SequencePattern { Id = r.GetInt32(0), Kind = Enum.Parse<SequencePatternKind>(r.GetString(1), true), Signature = r.GetString(2), Length = r.GetInt32(3), Count = r.GetInt32(4), FirstSeq = r.GetInt64(5), LastSeq = r.GetInt64(6) });

    /// <summary>説明層（人向け）: パターンの種類・長さ・回数と、人が付けた名前（あれば）。本体の学習・判断からは呼ばない</summary>
    public IReadOnlyList<(int Id, string Kind, int Length, int Count, string? Label)> PatternSummaryForDisplay() =>
        _db.Query("SELECT p.pattern_id, p.kind, COALESCE(p.length,0), COALESCE(p.n,0), l.label FROM sequence_patterns p LEFT JOIN pattern_labels l ON l.pattern_id=p.pattern_id ORDER BY p.n DESC, p.pattern_id LIMIT 50",
            r => (r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)));

    /// <summary>説明層（人向け）: パターンに人が名前を付ける。本体の学習・判断はこの名前を読まない</summary>
    public void SetPatternLabelForDisplay(int patternId, string? label, string? note = null) =>
        _db.Exec("INSERT INTO pattern_labels(pattern_id, label, note, updated_at) VALUES($p,$l,$n,$t) ON CONFLICT(pattern_id) DO UPDATE SET label=$l, note=$n, updated_at=$t", null,
            ("$p", patternId), ("$l", label), ("$n", note), ("$t", Now()));

    private static string JsonArr(double[] a) => "[" + string.Join(",", a.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
    private static double[] ParseArr(string s)
    {
        using var d = System.Text.Json.JsonDocument.Parse(s);
        return d.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? d.RootElement.EnumerateArray().Select(e => e.GetDouble()).ToArray() : Array.Empty<double>();
    }

    public string Backup(string tag = "manual") => _db.Backup(tag, _now());
    public void Checkpoint() => _db.Checkpoint();
    public void ExportCopy(string dest) => _db.ExportCopy(dest);

    public void Dispose() => _db.Dispose();
}
