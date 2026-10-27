using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gkk-test-" + Guid.NewGuid().ToString("N")[..10]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string this[string rel] => System.IO.Path.Combine(Path, rel);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(Path, true); } catch { } }
}

public class StorageTests
{
    private static readonly string[] MemoryTables =
    {
        "actions", "goal_events", "screen_background", "goal_model", "decision_candidates", "entities", "entity_sightings", "hypotheses", "hypothesis_evidence",
        "hypothesis_revisions", "llm_calls", "message_templates", "migrations", "observation_messages", "observations", "outcomes",
        "runs", "sa_stats", "schema_meta", "screenshots", "stats_global", "stats_run", "turns",
        // v3（phase2-design.md §10.4）
        "layout_versions", "layout_reviews", "grid_estimates", "screen_modes", "ve_prototypes", "ve_links", "ve_stats", "token_slots", "token_values",
        "token_links", "panels", "noise_events", "mode_transitions",
        // v4（最上位原則に沿った整理。U1・U2）
        "action_reversibility", "pattern_labels",
        // v5（連続経験履歴と系列の統計的発見。区切りに依存した表は削除）
        "experience_stream", "sequence_patterns",
        // v6（反応の対応表・操作の並び・見た目の状態と遷移の特徴）
        "reaction_maps", "action_sequences", "visual_states", "state_fingerprints", "state_splits",
        // v7（経験の出どころ: 読み直し・弱い事前の推定・並びの候補の状態の履歴）
        "action_sequence_events", "derived_transitions", "stream_reinterpretations", "experience_priors",
        // v8（判断の記録・予測の外れによる修正・弱い事前の推定の変化の履歴）
        "decision_records", "model_revisions", "experience_prior_events",
        // v9（分割の状態と履歴・操作の並びの実行の記録・判断の記録の要約）
        "state_split_events", "sequence_executions", "decision_rollups", "decision_rollup_blocks",
    };
    // v4・v5 で削除した表（ゲームの意味・学習の区切りを前提にしたもの）
    private static readonly string[] RemovedTables = { "deaths", "hazard_model", "floor_maps", "transition_patterns", "transition_pattern_stats", "transition_events", "run_end_features" };

    [Fact]
    public void Memory_db_is_created_with_full_design_schema_and_version()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "torneko-remaster", "0.1.0", "0.2.0");
        var tables = s.Db.Tables();
        foreach (var t in MemoryTables) Assert.Contains(t, tables);
        foreach (var t in RemovedTables) Assert.DoesNotContain(t, tables);
        Assert.Equal(16, s.Db.SchemaVersion);
        Assert.Equal("16", s.Db.Meta("schema_meta", "schema_version"));
        Assert.Equal("torneko-remaster", s.Db.Meta("schema_meta", "profile_id"));
        Assert.Equal(16L, s.Db.Scalar("PRAGMA user_version;"));
        Assert.Equal("wal", s.Db.Scalar("PRAGMA journal_mode;"));
        Assert.Equal(1L, s.Db.Scalar("PRAGMA foreign_keys;"));
    }

    [Fact]
    public void App_db_is_created_independently()
    {
        using var d = new TempDir();
        using var a = AppStore.Open(d.Path, "0.2.0");
        var tables = a.Db.Tables();
        foreach (var t in new[] { "app_schema_meta", "hardware_snapshots", "llm_models", "llm_runtimes", "llm_active_config", "llm_switch_events",
                                  "backend_probe_results", "compat_test_steps", "backend_selection_history", "component_installs" })
            Assert.Contains(t, tables);
        Assert.DoesNotContain("turns", tables);
        Assert.Equal("1", a.Db.Meta("app_schema_meta", "schema_version"));
    }

    [Fact]
    public void Migration_takes_backup_first_and_applies_forward_only()
    {
        using var d = new TempDir();
        var path = d["x.sqlite"];
        var v1 = new[] { new Migration(1, "v1", "CREATE TABLE schema_meta(key TEXT PRIMARY KEY, value TEXT); CREATE TABLE a(x INTEGER);") };
        using (var db = SqliteDb.Open(path, "schema_meta", v1, "t")) { db.Exec("INSERT INTO a VALUES(42)"); Assert.Empty(db.BackupsMade); }
        var v2 = v1.Append(new Migration(2, "v2", "ALTER TABLE a ADD COLUMN y TEXT; CREATE TABLE b(z INTEGER);")).ToArray();
        using (var db = SqliteDb.Open(path, "schema_meta", v2, "t"))
        {
            Assert.Equal(2, db.SchemaVersion);
            Assert.Single(db.BackupsMade);
            Assert.Contains(".bak-v1-", db.BackupsMade[0]);
            Assert.Contains("b", db.Tables());
            Assert.Equal(2L, db.Scalar("SELECT COUNT(*) FROM migrations"));
        }
        // バックアップは旧版のまま読める
        var bak = Directory.GetFiles(d.Path, "x.sqlite.bak-*").Single();
        using (var old = SqliteDb.Open(bak, "schema_meta", v1, "t"))
        {
            Assert.Equal(42L, old.Scalar("SELECT x FROM a"));
            Assert.Equal(1, old.SchemaVersion);
        }
        // 新しすぎるDBは開かない
        Assert.Throws<InvalidOperationException>(() => SqliteDb.Open(path, "schema_meta", v1, "t"));
    }

    [Fact]
    public void Periodic_backup_happens_once_per_interval_and_keeps_limit()
    {
        using var d = new TempDir();
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var path = d["m.sqlite"];
        var mig = Schemas.Memory;
        SqliteDb.Open(path, "schema_meta", mig, "t", utcNow: () => t).Dispose();
        for (int day = 1; day <= 8; day++)
        {
            var now = t.AddDays(day);
            using var db = SqliteDb.Open(path, "schema_meta", mig, "t", backupInterval: TimeSpan.FromDays(1), keepBackups: 5, utcNow: () => now);
            Assert.Single(db.BackupsMade);
            using var again = SqliteDb.Open(d["other.sqlite"], "schema_meta", mig, "t", utcNow: () => now); // 別DB
        }
        var now2 = t.AddDays(8).AddHours(1);
        using (var db = SqliteDb.Open(path, "schema_meta", mig, "t", backupInterval: TimeSpan.FromDays(1), utcNow: () => now2)) Assert.Empty(db.BackupsMade);
        Assert.Equal(5, Directory.GetFiles(d.Path, "m.sqlite.bak-*").Length);
    }

    private static TurnCommit Turn(string key, string kind, bool changed, Frame? thumb = null) =>
        new(new ObservationRecord(RecogConfJson: Json.Obj(("peak_ratio", 0.1)), Thumbnail: thumb),
            new ActionRecord(key, "random_safe", "random_safe", Json.Obj(("type", "random")), Candidates: new[] { new CandidateRecord(0, key, "random_safe", null) }),
            new OutcomeRecord(changed, kind, 590, Json.Obj(("first_change_ms", 90))));

    [Fact]
    public void CommitTurn_links_outcome_to_next_turn_and_counts_stats()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        long run = s.BeginRun("1", "window_message", "wgc");
        var a = s.CommitTurn(run, Turn("dpad_up", ExperienceStore.BoundaryNormal, true));
        var b = s.CommitTurn(run, Turn("up+left", ExperienceStore.BoundaryNoChange, false));
        Assert.Equal(0, a.TurnIndex); Assert.Equal(1, b.TurnIndex);
        Assert.Equal(b.TurnId, s.Db.Scalar("SELECT next_turn_id FROM outcomes WHERE action_id=$a", null, ("$a", a.ActionId)));
        Assert.Null(s.Db.Scalar("SELECT next_turn_id FROM outcomes WHERE action_id=$a", null, ("$a", b.ActionId)));
        s.EndRun(run, "abort");
        var rs = s.RunStats(run);
        Assert.Equal(2, rs["turns_total"]); Assert.Equal(1, rs["boundary_normal"]); Assert.Equal(1, rs["boundary_no_change"]);
        Assert.Equal(1, s.GlobalStats()["runs_total"]);
        var recent = s.RecentTurns(10);
        Assert.Equal("up+left", recent[0].LogicalKey); Assert.False(recent[0].ScreenChanged);
        Assert.Equal(2, s.Runs()[0].TurnsTotal); Assert.Equal("abort", s.Runs()[0].EndReason);
        // 2回目の冒険では前の冒険の行動をつながない
        long run2 = s.BeginRun("1", null, null);
        var c = s.CommitTurn(run2, Turn("down", ExperienceStore.BoundaryNormal, true));
        Assert.Null(s.Db.Scalar("SELECT next_turn_id FROM outcomes WHERE action_id=$a", null, ("$a", b.ActionId)));
        Assert.Equal(0, c.TurnIndex);
        Assert.Equal(3, s.GlobalStats()["turns_total"]);
    }

    [Fact]
    public void Thumbnails_are_content_addressed_and_ring_buffered()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        s.ThumbnailRingSize = 3;
        long run = s.BeginRun("1", null, null);
        for (int i = 0; i < 6; i++)
        {
            var f = new Canvas(16, 8).Rect(0, 0, 2 + i, 2, (byte)(i * 30), 0, 0).ToFrame(0);
            s.CommitTurn(run, Turn("dpad_up", "normal", true, f));
        }
        // 同じ画像は1つにまとめる
        s.CommitTurn(run, Turn("dpad_up", "normal", true, new Canvas(16, 8).Rect(0, 0, 7, 2, 150, 0, 0).ToFrame(0)));
        Assert.Equal(3L, s.Db.Scalar("SELECT COUNT(*) FROM screenshots"));
        Assert.Equal(3, Directory.GetFiles(s.ScreenshotDir, "*.png", SearchOption.AllDirectories).Length);
        Assert.Equal(3L, s.Db.Scalar("SELECT COUNT(*) FROM observations WHERE screenshot_id IS NULL"));
        var p = (string)s.Db.Scalar("SELECT path FROM screenshots ORDER BY screenshot_id DESC LIMIT 1")!;
        Assert.Matches(@"^screenshots/\d{4}/\d{2}/[0-9a-f]{2}/[0-9a-f]{40}\.png$", p);
        var png = Png.DecodeBgra(File.ReadAllBytes(Path.Combine(s.RootDir, p)));
        Assert.Equal(16, png.Width);
    }

    [Fact]
    public void Profile_mismatch_is_rejected_and_dangling_runs_are_closed()
    {
        using var d = new TempDir();
        using (var s = ExperienceStore.Open(d["p"], "a", "1", "t")) s.BeginRun("1", null, null);
        Assert.Throws<InvalidOperationException>(() => ExperienceStore.Open(d["p"], "b", "1", "t"));
        using var s2 = ExperienceStore.Open(d["p"], "a", "1", "t");
        Assert.Equal(1, s2.CloseDanglingRuns());
        Assert.Equal("crash", s2.Runs()[0].EndReason);
    }

    [Fact]
    public void Probe_and_selection_and_component_records_round_trip()
    {
        using var d = new TempDir();
        using var a = AppStore.Open(d.Path, "t");
        long hw = a.RecordHardware(new HardwareSnapshot("Windows 10.0.26200", null, 16, 32768, 20000, 100000));
        long p = a.RecordProbe(new ProbeRecord("startup", "input", "window_message", "torneko-remaster", "Game", "UnrealWindow", true, null, null, true, null,
            null, null, 91, 108, 3, 3, "Isolated", "none", null, Json.Obj(("role", "OtherForeground"))), hw,
            new[] { new CompatStepRecord(0, "background", Json.Arr(new object?[] { "button8", "button2" }), 0.13, true, 91, "accepted") });
        a.RecordSelection("wgc", "window_message", "background", "auto", false, "背面で合格");
        a.RecordComponentInstall(new ComponentInstallRecord("ocr", "1", "components/ocr/1", "[]", "Apache-2.0", "2026-10-17T00:00:00Z", "installed", null));
        var rows = a.LatestProbes();
        Assert.Single(rows); Assert.Equal(p, rows[0].ProbeId); Assert.True(rows[0].BackgroundOk); Assert.Equal(108, rows[0].LatencyP95);
        Assert.Equal(1, a.Count("compat_test_steps")); Assert.Equal(1, a.Count("backend_selection_history")); Assert.Equal(1, a.Count("component_installs"));
    }

    [Fact]
    public void Broken_app_db_is_moved_aside_and_rebuilt_without_touching_memory_db()
    {
        using var d = new TempDir();
        using (var m = ExperienceStore.Open(d["profiles/x"], "x", "1", "t")) m.BeginRun("1", null, null);
        File.WriteAllText(d["app.sqlite"], "これは SQLite ではありません。壊れたファイルの再現。".PadRight(4096, '#'));
        using (var a = AppStore.Open(d.Path, "t")) Assert.Contains("hardware_snapshots", a.Db.Tables());
        Assert.Single(Directory.GetFiles(d.Path, "app.sqlite.broken-*"));
        using var m2 = ExperienceStore.Open(d["profiles/x"], "x", "1", "t");
        Assert.Single(m2.Runs());
    }

    [Fact]
    public void Json_helper_writes_valid_json()
    {
        var s = Json.Obj(("a", 1), ("b", "日本語"), ("c", null), ("d", new object?[] { 1.5, true }), ("e", double.NaN));
        using var doc = System.Text.Json.JsonDocument.Parse(s);
        Assert.Equal("日本語", doc.RootElement.GetProperty("b").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("e").ValueKind);
    }
}

public class PngTests
{
    [Fact]
    public void Encode_then_decode_round_trips_and_downscale_crop_work()
    {
        var f = new Canvas(37, 21).Rect(3, 4, 10, 5, 10, 200, 30).ToFrame(0);
        var (w, h, bgra) = Png.DecodeBgra(Png.Encode(f.Width, f.Height, f.Bgra));
        Assert.Equal((37, 21), (w, h));
        for (int i = 0; i < bgra.Length; i += 4) Assert.Equal((f.Bgra[i], f.Bgra[i + 1], f.Bgra[i + 2]), (bgra[i], bgra[i + 1], bgra[i + 2]));
        var t = f.Downscale(4);
        Assert.Equal((9, 5), (t.Width, t.Height));
        var c = f.Crop(3, 4, 10, 5);
        Assert.Equal((10, 5), (c.Width, c.Height));
        Assert.Equal(200, c.Bgra[1]);
        var c2 = f.Crop(30, 15, 100, 100);
        Assert.Equal((7, 6), (c2.Width, c2.Height));
    }

    // ---- 実機 #1〜#5 で「冒険の終了記録に失敗: CHECK constraint failed: end_reason」になった件の回帰テスト ----
    [Theory]
    [InlineData("steps_completed", "test_completed", "steps_completed")]   // 指定の手数に到達
    [InlineData("time_completed", "test_completed", "time_completed")]
    [InlineData("user_stop", "abort", "user_stop")]
    [InlineData("emergency", "abort", "emergency")]
    [InlineData("input_unreachable", "abort", "input_unreachable")]
    [InlineData("window_lost", "abort", "window_lost")]
    [InlineData("error", "crash", "error")]
    [InlineData("abort", "abort", null)]
    [InlineData("test_completed", "test_completed", null)]
    [InlineData("何か新しい理由", "unknown", "何か新しい理由")]
    public void EndRun_never_violates_end_reason_constraint(string sessionReason, string expectedReason, string? expectedDetail)
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var id = s.BeginRun("1", "sendinput", "wgc");
        s.EndRun(id, sessionReason, "{}");
        var r = s.Runs()[0];
        Assert.NotNull(r.EndedAt);
        Assert.Equal(expectedReason, r.EndReason);
        Assert.Equal(expectedDetail, r.EndDetail);
        Assert.Contains(r.EndReason, ExperienceStore.EndReasons);
    }

    [Fact]
    public void EndRun_keeps_start_notes_such_as_seed()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var id = s.BeginRun("1", "window_message", "wgc", notesJson: "{\"seed\":952290023,\"moves\":false,\"max_steps\":50}");
        s.EndRun(id, "steps_completed", "{\"stop_reason\":\"指定の 50 手を終えました\",\"steps\":50}");
        var n = s.RunNotes(id)!;
        Assert.Contains("\"seed\":952290023", n);
        Assert.Contains("\"max_steps\":50", n);
        Assert.Contains("\"stop_reason\"", n);
        Assert.Contains("\"steps\":50", n);
    }

    [Theory]
    [InlineData(null, "{\"a\":1}", "{\"a\":1}")]
    [InlineData("{\"a\":1}", null, "{\"a\":1}")]
    [InlineData("{\"seed\":5}", "{\"steps\":3}", "{\"seed\":5,\"steps\":3}")]
    [InlineData("{\"steps\":50}", "{\"steps\":3}", "{\"steps\":50,\"end_steps\":3}")]
    [InlineData("[1]", "{\"b\":2}", "{\"start\":[1],\"end\":{\"b\":2}}")]
    public void MergeNotes_combines_objects(string? a, string? b, string expected)
        => Assert.Equal(expected, ExperienceStore.MergeNotes(a, b));

    [Fact]
    public void Every_session_end_reason_maps_into_the_check_constraint()
    {
        // Session.Run が返しうる値（App/Session.cs）。ここに無い値が増えても unknown になり、記録は失敗しない
        foreach (var raw in new[] { "steps_completed", "time_completed", "user_stop", "emergency", "input_unreachable", "window_lost", "error", "abort", "", null })
            Assert.Contains(ExperienceStore.NormalizeEndReason(raw).Reason, ExperienceStore.EndReasons);
    }

    [Fact]
    public void Migration_v2_backs_up_and_closes_runs_left_open_by_old_version()
    {
        using var d = new TempDir();
        var path = d["p/memory.sqlite"];
        Directory.CreateDirectory(d["p"]);
        // 旧版（v1 スキーマのみ）で冒険を2つ作り、終了記録なし（旧版の失敗を再現）
        using (var db = SqliteDb.Open(path, Schemas.MemoryMetaTable, Schemas.Memory.Take(1).ToList(), "old"))
        {
            db.Exec("INSERT INTO runs(run_id, started_at, turns_total) VALUES(1,'2026-10-17T13:00:00.000Z',0),(2,'2026-10-17T14:00:00.000Z',0)");
            db.Exec("INSERT INTO turns(run_id, turn_index, boundary_kind, created_at) VALUES(1,0,'normal','2026-10-17T13:00:05.000Z'),(1,1,'normal','2026-10-17T13:00:09.000Z')");
            Assert.ThrowsAny<Exception>(() => db.Exec("UPDATE runs SET end_reason='completed' WHERE run_id=1")); // 旧版で起きていた失敗
        }
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "new");
        Assert.Equal(16, s.Db.SchemaVersion);
        Assert.NotEmpty(s.Db.BackupsMade);               // マイグレーション前の自動バックアップ
        var runs = s.Runs().OrderBy(r => r.RunId).ToList();
        Assert.All(runs, r => { Assert.Equal("unknown", r.EndReason); Assert.StartsWith("backfill_v2", r.EndDetail); });
        Assert.Equal("2026-10-17T13:00:09.000Z", runs[0].EndedAt);   // 最終ターンの時刻
        Assert.Equal(2, runs[0].TurnsTotal);
        Assert.Equal("2026-10-17T14:00:00.000Z", runs[1].EndedAt);   // ターンなし → 開始時刻
        Assert.Equal(0, s.CloseDanglingRuns());
    }

    [Fact]
    public void Checkpoint_empties_wal_and_export_copy_contains_uncheckpointed_rows()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var id = s.BeginRun("1", null, null);
        // 実機の zip は -wal を含まず、本体だけでは最新の行が欠けていた → 書き出しは Backup API で WAL ごと
        s.ExportCopy(d["export.sqlite"]);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={d["export.sqlite"]};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM runs";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
        s.EndRun(id, "completed");
        var wal = d["p/memory.sqlite-wal"];
        Assert.True(!File.Exists(wal) || new FileInfo(wal).Length == 0);
    }
}

public class DiagnosticExportTests
{
    [Fact]
    public void Export_contains_consistent_db_copies_logs_and_summary()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var id = s.BeginRun("1", null, null);
        s.EndRun(id, "steps_completed");
        File.WriteAllText(d["app.jsonl"], "{\"type\":\"start\"}\n");
        using var open = new StreamWriter(d["live.jsonl"], append: true) { AutoFlush = true }; // 書き込み中のログ
        open.WriteLine("{\"type\":\"turn\"}");
        var r = DiagnosticExport.Write(d["out/diag.zip"], new[] { ("memory.sqlite", s.Db) },
            new[] { ("logs/app.jsonl", d["app.jsonl"]), ("logs/live.jsonl", d["live.jsonl"]), ("missing.txt", d["nope.txt"]) }, "テスト");
        Assert.Equal(new[] { "memory.sqlite", "logs/app.jsonl", "logs/live.jsonl", "summary.txt" }, r.Entries);
        using var z = System.IO.Compression.ZipFile.OpenRead(r.ZipPath);
        var db = d["check.sqlite"];
        System.IO.Compression.ZipFileExtensions.ExtractToFile(z.GetEntry("memory.sqlite")!, db);
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False");
        c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT end_reason || '/' || end_detail FROM runs";
        Assert.Equal("test_completed/steps_completed", cmd.ExecuteScalar());
        using var sr = new StreamReader(z.GetEntry("summary.txt")!.Open());
        Assert.Contains("schema v16", sr.ReadToEnd());
    }
}

/// <summary>スキーマ v3（phase2-design.md §10.4）への移行と、過程データの自動削除（§10.5）</summary>
public class SchemaV3Tests
{
    [Fact]
    public void Migration_v3_moves_diff_metrics_marks_test_completion_and_keeps_references()
    {
        using var d = new TempDir();
        var path = d["p/memory.sqlite"];
        Directory.CreateDirectory(d["p"]);
        // v0.4.2 相当（v2）の DB: 分離試験が正常に終わっても abort/completed、差分の数値が entity_events_json に入っていた
        using (var db = SqliteDb.Open(path, Schemas.MemoryMetaTable, Schemas.Memory.Take(2).ToList(), "0.4.2"))
        {
            db.Exec("INSERT INTO runs(run_id, started_at, ended_at, end_reason, end_detail, policy_version, turns_total) VALUES" +
                    "(12,'2026-10-17T19:51:31Z','2026-10-17T19:52:57Z','abort','nochange_stop','phase1-random-safe',1)," +
                    "(13,'2026-10-17T20:05:56Z','2026-10-17T20:15:57Z','abort','completed','separation-test',1)," +
                    "(14,'2026-10-17T21:00:00Z','2026-10-17T21:01:00Z','death',NULL,'x',0)");
            db.Exec("INSERT INTO turns(turn_id, run_id, turn_index, boundary_kind, created_at) VALUES(1,12,0,'no_change','t'),(2,13,0,'normal','t')");
            db.Exec("INSERT INTO screenshots(screenshot_id, sha1, path, reason, retained) VALUES(7,'ab','screenshots/x.png','turn',0)");
            db.Exec("INSERT INTO observations(obs_id, turn_id, screenshot_id) VALUES(1,1,7),(2,2,NULL)");
            db.Exec("INSERT INTO actions(action_id, turn_id, logical_key) VALUES(1,1,'up'),(2,2,'menu')");
            db.Exec("INSERT INTO outcomes(action_id, screen_changed, entity_events_json) VALUES" +
                    "(1,0,'{\"first_change_ms\":null,\"frames\":55,\"peak_ratio\":0.00083,\"input_backend\":\"window_message\"}'),(2,1,'[{\"track\":1}]')");
        }
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.4.3");
        Assert.Equal(16, s.Db.SchemaVersion);
        Assert.NotEmpty(s.Db.BackupsMade);
        Assert.Equal("abort", s.Db.Scalar("SELECT end_reason FROM runs WHERE run_id=12"));
        Assert.Equal("test_completed", s.Db.Scalar("SELECT end_reason FROM runs WHERE run_id=13"));
        Assert.Equal("ended", s.Db.Scalar("SELECT end_reason FROM runs WHERE run_id=14"));
        Assert.DoesNotContain("end_kind_est", s.Db.Query("PRAGMA table_info(runs);", r => r.GetString(1)));   // v4: 事前ラベルの列は削除
        // 差分の数値は diff_metrics_json へ、本来のできごとは残る
        Assert.Contains("peak_ratio", (string)s.Db.Scalar("SELECT diff_metrics_json FROM outcomes WHERE action_id=1")!);
        Assert.Null(s.Db.Scalar("SELECT entity_events_json FROM outcomes WHERE action_id=1"));
        Assert.Equal("[{\"track\":1}]", s.Db.Scalar("SELECT entity_events_json FROM outcomes WHERE action_id=2"));
        Assert.Null(s.Db.Scalar("SELECT diff_metrics_json FROM outcomes WHERE action_id=2"));
        // 作り直した runs を turns が参照し続けている。外部キーは有効のまま
        Assert.Equal(1L, s.Db.Scalar("PRAGMA foreign_keys;"));
        Assert.Equal(0, s.Db.Query("PRAGMA foreign_key_check;", r => r.GetValue(0)).Count);
        Assert.ThrowsAny<Exception>(() => s.Db.Exec("INSERT INTO turns(run_id, turn_index) VALUES(999,0)"));
        // サムネイルは冒険に結び付けられる
        Assert.Equal(12L, s.Db.Scalar("SELECT run_id FROM screenshots WHERE screenshot_id=7"));
        Assert.Equal("process", s.Db.Scalar("SELECT purpose FROM screenshots WHERE screenshot_id=7"));
        // runs は実行単位としてだけ残る（学習の集計用の見方は v5 で廃止。学習は連続経験履歴だけを読む）
        Assert.DoesNotContain("learning_runs", s.Db.Query("SELECT name FROM sqlite_master WHERE type='view'", r => r.GetString(0)));
        Assert.Equal(0L, s.Db.Scalar("SELECT COUNT(*) FROM experience_stream"));   // 旧キー名の記録は履歴に移さない
    }

    [Theory]
    [InlineData("enemy")]
    [InlineData("item")]
    [InlineData("stairs")]
    public void Entity_kind_guess_accepts_only_generic_values(string semantic)
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        s.Db.Exec("INSERT INTO entities(ve_id, kind_guess) VALUES(1,'mobile'),(2,'static'),(3,'unknown'),(4,NULL)");
        Assert.ThrowsAny<Exception>(() => s.Db.Exec("INSERT INTO entities(ve_id, kind_guess) VALUES(10,$k)", null, ("$k", semantic)));
    }

    [Fact]
    public void End_reason_is_constrained_and_no_end_kind_label_exists()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var id = s.BeginRun("1", null, null);
        Assert.ThrowsAny<Exception>(() => s.Db.Exec("UPDATE runs SET end_reason='x_label' WHERE run_id=$i", null, ("$i", id)));
        var cols = s.Db.Query("PRAGMA table_info(runs);", r => r.GetString(1));
        Assert.DoesNotContain(cols, c => c.StartsWith("end_kind", StringComparison.Ordinal));
        Assert.DoesNotContain("max_depth_value", cols);
    }

    [Fact]
    public void Diff_metrics_go_to_their_own_column()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var run = s.BeginRun("1", null, null);
        var ids = s.CommitTurn(run, new TurnCommit(new ObservationRecord(), new ActionRecord("dpad_up", "x", "random_safe", "{}"),
            new OutcomeRecord(true, "normal", 500, Json.Obj(("peak_ratio", 0.3)))));
        Assert.Contains("peak_ratio", (string)s.Db.Scalar("SELECT diff_metrics_json FROM outcomes WHERE action_id=$a", null, ("$a", ids.ActionId))!);
        Assert.Null(s.Db.Scalar("SELECT entity_events_json FROM outcomes WHERE action_id=$a", null, ("$a", ids.ActionId)));
    }

    [Fact]
    public void Process_images_are_deleted_when_the_run_ends_but_turn_records_remain()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var run = s.BeginRun("1", null, null);
        for (int i = 0; i < 4; i++)
            s.CommitTurn(run, new TurnCommit(new ObservationRecord(Thumbnail: new Canvas(16, 8).Rect(0, 0, 2 + i, 2, (byte)(i * 40), 0, 0).ToFrame(0)),
                new ActionRecord("dpad_up", "x", "random_safe", "{}"), new OutcomeRecord(true, "normal", 500)));
        Assert.Equal(4, s.ScreenshotCount(run));
        var files = Directory.GetFiles(d["p/screenshots"], "*.png", SearchOption.AllDirectories);
        Assert.Equal(4, files.Length);
        // 代表画像（retained=1）は残す
        s.Db.Exec("UPDATE screenshots SET retained=1, purpose='representative' WHERE screenshot_id=(SELECT MIN(screenshot_id) FROM screenshots)");
        s.EndRun(run, "user_stop");
        Assert.Equal(1, s.ScreenshotCount(run));
        Assert.Single(Directory.GetFiles(d["p/screenshots"], "*.png", SearchOption.AllDirectories));
        Assert.Equal(4L, s.Db.Scalar("SELECT COUNT(*) FROM observations o JOIN turns t ON t.turn_id=o.turn_id WHERE t.run_id=$r", null, ("$r", run)));
        Assert.Equal(3L, s.Db.Scalar("SELECT COUNT(*) FROM observations WHERE screenshot_id IS NULL"));
    }

    [Fact]
    public void Representative_images_are_capped()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var run = s.BeginRun("1", null, null);
        for (int i = 0; i < 5; i++)
            s.CommitTurn(run, new TurnCommit(new ObservationRecord(Thumbnail: new Canvas(16, 8).Rect(0, 0, 2 + i, 2, (byte)(i * 40), 0, 0).ToFrame(0)),
                new ActionRecord("dpad_up", "x", "random_safe", "{}"), new OutcomeRecord(true, "normal", 500)));
        s.Db.Exec("UPDATE screenshots SET retained=1, purpose='representative', size_bytes=100");
        s.RepresentativeLimitBytes = 250;
        Assert.Equal(3, s.PruneRepresentatives());
        Assert.Equal(2, s.ScreenshotCount());
    }

    [Fact]
    public void Images_left_by_ended_runs_are_cleaned_at_startup()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        var run = s.BeginRun("1", null, null);
        s.CommitTurn(run, new TurnCommit(new ObservationRecord(Thumbnail: new Canvas(16, 8).ToFrame(0)), new ActionRecord("dpad_up", "x", "random_safe", "{}"), new OutcomeRecord(true, "normal", 500)));
        s.EndRun(run, "user_stop", purgeProcessImages: false);
        var live = s.BeginRun("1", null, null);
        s.CommitTurn(live, new TurnCommit(new ObservationRecord(Thumbnail: new Canvas(16, 8, 90).ToFrame(0)), new ActionRecord("dpad_up", "x", "random_safe", "{}"), new OutcomeRecord(true, "normal", 500)));
        Assert.Equal(1, s.PurgeEndedRunsProcessImages());
        Assert.Equal(0, s.ScreenshotCount(run));
        Assert.Equal(1, s.ScreenshotCount(live));     // 記録中の冒険の画像は残す
    }
}

public class RecordingStoreTests
{
    [Fact]
    public void Run_byte_limit_stops_saving_images_but_keeps_manifest_lines()
    {
        // 時間無制限の保存でも、記録中の run が消されずにディスクを使い切らない
        using var d = new TempDir();
        var st = new RecordingStore(d["p"]) { RunLimitBytes = 2500 };
        for (int i = 1; i <= 5; i++)
            st.Add(7, new RecordingEntry(i, null, "tap", "button1", "Normal", true, 10, false, false, null, DateTime.UtcNow), new byte[1000], "jpg");
        Assert.Equal(2, Directory.GetFiles(st.RunDir(7), "*.jpg").Length);
        Assert.Equal(3, st.CappedCount(7));
        Assert.Equal(5, File.ReadAllLines(Path.Combine(st.RunDir(7), "manifest.jsonl")).Length);
        Assert.Equal(0, st.CappedCount(8));
    }

    private static RecordingEntry E(int step, string kind = "Normal") => new(step, step, "dpad", "dpad_up", kind, kind == "Normal", 500, false, false, null, new DateTime(2026, 10, 18, 5, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Add_writes_frames_and_manifest()
    {
        using var d = new TempDir();
        var st = new RecordingStore(d["p"]);
        st.Add(5, E(1), new byte[100], "jpg");
        st.Add(5, E(2, "NoChange"), null, "jpg");
        var info = Assert.Single(st.List());
        Assert.Equal(5, info.RunId);
        Assert.Equal(1, info.Frames);
        var lines = st.ManifestLines(5);
        Assert.Equal(2, lines.Count);
        Assert.Contains("\"file\":\"f000000-step00001.jpg\"", lines[0]);
        Assert.Contains("\"kind\":\"NoChange\"", lines[1]);
    }

    [Fact]
    public void Several_inputs_in_one_step_keep_their_own_frames()
    {
        // v0.5.1 ではメニューを開いた画面が、閉じた後の画面で上書きされていた
        using var d = new TempDir();
        var st = new RecordingStore(d["p"]);
        var a = st.Add(3, E(7), new byte[] { 1 }, "jpg");
        var b = st.Add(3, E(7), new byte[] { 2 }, "jpg");
        Assert.NotEqual(a, b);
        Assert.Equal(2, Assert.Single(st.List()).Frames);
        var st2 = new RecordingStore(d["p"]);   // 再起動後も続きの番号
        var c = st2.Add(3, E(8), new byte[] { 3 }, "jpg");
        Assert.StartsWith("f000002-", c);
    }

    [Fact]
    public void Limit_removes_oldest_recordings_but_keeps_the_current_one()
    {
        using var d = new TempDir();
        var st = new RecordingStore(d["p"]) { LimitBytes = 2500 };
        for (int i = 1; i <= 10; i++) st.Add(1, E(i), new byte[200], "jpg");   // 古い記録 約2000B
        Directory.SetLastWriteTimeUtc(st.RunDir(1), DateTime.UtcNow.AddHours(-2));
        foreach (var f in Directory.GetFiles(st.RunDir(1))) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-2));
        for (int i = 1; i <= 10; i++) st.Add(2, E(i), new byte[200], "jpg");   // 記録中
        Assert.DoesNotContain(st.List(), r => r.RunId == 1);
        Assert.Contains(st.List(), r => r.RunId == 2);
    }

    [Fact]
    public void Cleanup_removes_recordings_older_than_max_age()
    {
        using var d = new TempDir();
        var now = DateTime.UtcNow;
        var st = new RecordingStore(d["p"], () => now) { MaxAge = TimeSpan.FromDays(7) };
        st.Add(1, E(1), new byte[10], "jpg");
        st.Add(2, E(1), new byte[10], "jpg");
        foreach (var f in Directory.GetFiles(st.RunDir(1))) File.SetLastWriteTimeUtc(f, now.AddDays(-8));
        Assert.Equal(1, st.Cleanup());
        Assert.Equal(new long[] { 2 }, st.List().Select(r => r.RunId).ToArray());
    }

    [Fact]
    public void Export_thins_into_contiguous_blocks_within_budget()
    {
        using var d = new TempDir();
        var st = new RecordingStore(d["p"]);
        for (int i = 1; i <= 100; i++) st.Add(3, E(i), new byte[1000], "jpg");
        var r = st.Export(3, d["out/rec.zip"], budgetBytes: 30_000, blockSize: 10, readme: "テスト");
        Assert.True(r.Thinned);
        Assert.Equal(30, r.Frames);
        using var z = System.IO.Compression.ZipFile.OpenRead(r.Zip);
        var names = z.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("manifest.jsonl", names);
        Assert.Contains("README.txt", names);
        var steps = names.Where(n => n.StartsWith("frames/")).Select(n => int.Parse(n[(n.IndexOf("-step", StringComparison.Ordinal) + 5)..][..5])).OrderBy(x => x).ToList();
        Assert.Equal(30, steps.Count);
        Assert.Equal(Enumerable.Range(1, 10), steps.Take(10));            // 先頭の区間は連続
        var full = st.Export(3, d["out/all.zip"], budgetBytes: 10_000_000);
        Assert.False(full.Thinned);
        Assert.Equal(100, full.Frames);
    }
}

/// <summary>スキーマ v4（最上位原則に沿った整理）と、診断の記録の完全分離（U3）</summary>
public class SchemaV4AndDiagnosticsTests
{
    [Fact]
    public void Migration_v4_v5_from_v3_drops_semantic_tables_and_old_key_transitions_but_keeps_screen_modes()
    {
        using var d = new TempDir();
        var path = d["p/memory.sqlite"];
        Directory.CreateDirectory(d["p"]);
        using (var db = SqliteDb.Open(path, Schemas.MemoryMetaTable, Schemas.Memory.Take(3).ToList(), "0.6.0"))
        {
            Assert.Equal(3, db.SchemaVersion);
            db.Exec("INSERT INTO runs(run_id, started_at, end_reason, policy_version) VALUES(1,'t','abort','explore-0.1'),(2,'t','test_completed','separation-test')");
            db.Exec("UPDATE runs SET end_kind_est='unknown' WHERE run_id=1");
            db.Exec("INSERT INTO screen_modes(mode_id, prototype_blob, seen_count) VALUES(1, x'00', 5)");
            db.Exec("INSERT INTO mode_transitions(from_node, logical_key, to_node, n) VALUES('m1','old_name','m2',3)");
        }
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.7.0");
        Assert.Equal(16, s.Db.SchemaVersion);
        Assert.NotEmpty(s.Db.BackupsMade);
        var tables = s.Db.Tables();
        foreach (var t in new[] { "deaths", "hazard_model", "floor_maps" }) Assert.DoesNotContain(t, tables);
        Assert.Equal(1L, s.Db.Scalar("SELECT COUNT(*) FROM screen_modes"));
        Assert.Equal(0L, s.Db.Scalar("SELECT COUNT(*) FROM mode_transitions"));   // 旧キー名の遷移は破棄
        Assert.DoesNotContain("anchor_x", s.Db.Query("PRAGMA table_info(observations);", r => r.GetString(1)));   // v5: 操作対象を前提にした列は削除
        Assert.Equal(0, s.Db.Query("PRAGMA foreign_key_check;", r => r.GetValue(0)).Count);
    }

    [Theory]
    [InlineData("separation-test")]
    [InlineData("diag-toggle")]
    [InlineData("diagnostic")]
    public void Experience_store_refuses_diagnostic_runs(string policy)
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        Assert.True(ExperienceStore.IsDiagnosticPolicy(policy));
        Assert.ThrowsAny<Exception>(() => s.BeginRun("1", null, null, policy));
        Assert.Equal(0L, s.Db.Scalar("SELECT COUNT(*) FROM runs"));
        Assert.False(ExperienceStore.IsDiagnosticPolicy("explore-0.2"));
    }

    [Fact]
    public void Diagnostic_records_go_only_to_diagnostics_sqlite()
    {
        using var d = new TempDir();
        var dir = d["p"];
        using (var diag = DiagnosticStore.Open(dir, "t"))
        {
            var id = diag.BeginRun("separation", "sendinput", "wgc");
            for (int i = 0; i < 5; i++) diag.AddTurn(id, "button8", "normal", 120, true, true);
            diag.EndRun(id, "time_completed", "{}");
            Assert.Equal(5, diag.TurnCount(id));
            Assert.DoesNotContain("turns", diag.Db.Tables());
            Assert.DoesNotContain("mode_transitions", diag.Db.Tables());
        }
        Assert.True(File.Exists(Path.Combine(dir, DiagnosticStore.FileName)));
        Assert.False(File.Exists(Path.Combine(dir, "memory.sqlite")));   // 診断だけなら学習 DB は作られない
        using var s = ExperienceStore.Open(dir, "prof", "1", "t");
        Assert.Equal(0L, s.Db.Scalar("SELECT COUNT(*) FROM runs"));
        Assert.Equal(0L, s.Db.Scalar("SELECT COUNT(*) FROM turns"));
    }

    [Fact]
    public void Fresh_memory_has_no_knowledge_rows()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        foreach (var t in s.Db.Tables().Where(t => t is not ("schema_meta" or "migrations") && !t.StartsWith("sqlite_", StringComparison.Ordinal)))
            Assert.True(0L == (long)s.Db.Scalar($"SELECT COUNT(*) FROM \"{t}\"")!, $"{t} に初期の行がある（事前知識の注入）");
    }

    [Fact]
    public void Reversibility_and_patterns_round_trip_and_labels_are_display_only()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "t");
        s.SaveReversibility(new[] { ("button1", 3, 1, 2), ("dpad_up", 5, 0, 0) });
        s.SaveReversibility(new[] { ("button1", 4, 2, 2) });
        var rev = s.LoadReversibility().ToDictionary(r => r.Key);
        Assert.Equal((4, 2, 2), (rev["button1"].Returned, rev["button1"].NotReturned, rev["button1"].UndoHits));
        Assert.Equal(5, rev["dpad_up"].Returned);

        // 系列のパターン: 保存と読み戻し（番号・回数・種類）
        var L = new GameKouryakuKun.Core.Exploration.SequenceLearner();
        long seq = s.NextStreamSeq();
        Assert.Equal(1, seq);
        var run = s.BeginRun("1", null, null);
        for (int i = 0; i < 20; i++)
        {
            var st = new GameKouryakuKun.Core.Exploration.StreamStep(seq++, i % 2 == 0 ? "a" : "b", i % 2 == 0 ? "button1" : "button2", true, i % 2 == 0 ? "b" : "a", 0.1);
            var o = L.Observe(st);
            s.AppendStream(run, null, st, o.Surprisal, o.Boundary);
        }
        Assert.Equal(20, s.StreamCount());
        Assert.Equal(21, s.NextStreamSeq());
        s.SaveSequencePatterns(L.Patterns);
        var loaded = s.LoadSequencePatterns();
        Assert.Equal(L.Patterns.Count, loaded.Count);
        Assert.NotEmpty(loaded);
        foreach (var p in L.Patterns) Assert.Contains(loaded, q => q.Id == p.Id && q.Kind == p.Kind && q.Signature == p.Signature && q.Count == p.Count);
        // 履歴は実行単位をまたいで通し番号の順に読める（実行単位で区切らない）
        var run2 = s.BeginRun("1", null, null);
        s.AppendStream(run2, null, new GameKouryakuKun.Core.Exploration.StreamStep(seq, "a", "button1", true, "b", 0.1), 0, Array.Empty<GameKouryakuKun.Core.Exploration.BoundaryReason>());
        var hist = s.LoadStream(100);
        Assert.Equal(Enumerable.Range(1, 21).Select(x => (long)x), hist.Select(h => h.Seq));
        Assert.Equal(5, s.LoadStream(5).Count);
        Assert.Equal(21L, hist[^1].Seq);   // 上限があるときは最新の側を返す
        // 人向けの名前は説明層の表だけ。パターンの表には名前の列がない
        var p1 = loaded[0].Id;
        s.SetPatternLabelForDisplay(p1, "名前の例");
        Assert.Contains(s.PatternSummaryForDisplay(), x => x.Id == p1 && x.Label == "名前の例");
        Assert.DoesNotContain(s.Db.Query("PRAGMA table_info(sequence_patterns);", r => r.GetString(1)), c => c.Contains("label"));
    }
}

/// <summary>スキーマ v5（連続経験履歴）と、診断 zip の健全性レポート（2026-10-18 09:36）</summary>
public class SchemaV5AndHealthTests
{
    [Fact]
    public void Migration_v5_backfills_neutral_history_in_order_and_skips_diagnostics_and_old_keys()
    {
        using var d = new TempDir();
        var path = d["p/memory.sqlite"];
        Directory.CreateDirectory(d["p"]);
        using (var db = SqliteDb.Open(path, Schemas.MemoryMetaTable, Schemas.Memory.Take(4).ToList(), "0.7.0"))
        {
            Assert.Equal(4, db.SchemaVersion);
            db.Exec("INSERT INTO runs(run_id, started_at, end_reason, policy_version) VALUES(1,'t','ended','explore-0.2'),(2,'t','ended','explore-0.2'),(3,'t','test_completed','diag-x')");
            int a = 0;
            void T(int turn, int run, string before, string key, int changed, string after, double ratio)
            {
                db.Exec($"INSERT INTO turns(turn_id, run_id, turn_index, boundary_kind, created_at) VALUES({turn},{run},{turn},'normal','t')");
                db.Exec($"INSERT INTO observations(obs_id, turn_id, recog_conf_json) VALUES({turn},{turn},'{{\"node_before\":\"{before}\",\"node_after\":\"{after}\"}}')");
                db.Exec($"INSERT INTO actions(action_id, turn_id, logical_key) VALUES({++a},{turn},'{key}')");
                db.Exec($"INSERT INTO outcomes(action_id, screen_changed, diff_metrics_json) VALUES({a},{changed},'{{\"final_ratio\":{ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}')");
            }
            T(1, 1, "m1", "button1", 1, "m2", 0.2);
            T(2, 1, "m2", "dpad_up", 0, "m2", 0.0);
            T(3, 3, "m2", "button2", 1, "m3", 0.4);   // 診断の実行は移さない
            T(4, 2, "m2", "up", 1, "m1", 0.3);        // 旧キー名は移さない
            T(5, 2, "m2", "button3", 1, "m1", 0.3);   // 実行単位をまたいで続く
        }
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.8.0");
        Assert.Equal(16, s.Db.SchemaVersion);
        Assert.NotEmpty(s.Db.BackupsMade);
        var h = s.LoadStream(100);
        Assert.Equal(new long[] { 1, 2, 3 }, h.Select(x => x.Seq));
        Assert.Equal(new[] { "button1", "dpad_up", "button3" }, h.Select(x => x.Action));
        Assert.Equal(("m1", true, "m2", 0.2), (h[0].State, h[0].Changed, h[0].NextState, h[0].ChangeRatio));
        Assert.False(h[1].Changed);
        Assert.Equal(4, s.NextStreamSeq());
        foreach (var c in new[] { "anchor_x", "anchor_y", "anchor_ve_id", "anchor_conf" })
            Assert.DoesNotContain(c, s.Db.Query("PRAGMA table_info(observations);", r => r.GetString(1)));
        Assert.Equal("ok", s.Db.Scalar("PRAGMA integrity_check;"));
        Assert.Equal(0, s.Db.Query("PRAGMA foreign_key_check;", r => r.GetValue(0)).Count);
    }

    [Fact]
    public void Health_report_summarizes_system_state_from_db_and_logs()
    {
        using var d = new TempDir();
        using var mem = ExperienceStore.Open(d["p"], "prof", "1", "0.8.0");
        using var app = AppStore.Open(d["a"], "0.8.0");
        var run = mem.BeginRun("explore-0.3", null, null);
        mem.AppendStream(run, null, new GameKouryakuKun.Core.Exploration.StreamStep(1, "m1", "button1", true, "m2", 0.1), 1, Array.Empty<GameKouryakuKun.Core.Exploration.BoundaryReason>());
        var log = d["app-1.jsonl"];
        File.WriteAllLines(log, new[]
        {
            "{\"t\":\"2026-10-18T10:00:00+09:00\",\"type\":\"start\",\"version\":\"0.8.0\"}",
            "{\"t\":\"2026-10-18T10:00:01+09:00\",\"type\":\"profile_migrated\",\"from\":1,\"to\":2}",
            "{\"t\":\"2026-10-18T10:00:02+09:00\",\"type\":\"capture_probe\",\"backend\":\"wgc\",\"usable\":true,\"fps\":50,\"w\":10,\"h\":10}",
            "{\"t\":\"2026-10-18T10:00:03+09:00\",\"type\":\"turn\",\"kind\":\"Normal\",\"net_change\":true}",
            "{\"t\":\"2026-10-18T10:00:04+09:00\",\"type\":\"turn\",\"kind\":\"NoChange\",\"net_change\":false}",
            "{\"t\":\"2026-10-18T10:00:05+09:00\",\"type\":\"session_end\",\"reason\":\"time_completed\",\"stop_reason\":\"x\",\"steps\":20,\"duration_s\":5,\"user_events_pausing\":0,\"user_events_ignored\":3,\"diagnostic\":false,\"ua_pauses_while_bg\":0,\"turns_game_fg\":0,\"turns_other_fg\":20,\"late_changes\":3}",
            "not json",
        });
        var r = HealthReport.Build("0.8.0", mem.Db, app.Db, null, new[] { log }, 2, null);
        Assert.Contains(r.Checks, c => c.Area == "DB" && c.Name == "memory.sqlite" && c.Level == HealthReport.Level.Ok);
        Assert.Contains(r.Checks, c => c.Area == "Profile" && c.Name == "移行" && c.Level == HealthReport.Level.Ok);
        Assert.Contains(r.Checks, c => c.Area == "例外" && c.Level == HealthReport.Level.Ok);
        Assert.Contains(r.Checks, c => c.Area == "分離" && c.Name.StartsWith("ユーザー入力") && c.Level == HealthReport.Level.Ok);
        Assert.Contains(r.Checks, c => c.Area == "ターン判定" && c.Level == HealthReport.Level.Warn);   // 3/20 > 5%
        Assert.Contains(r.Checks, c => c.Area == "記録" && c.Name == "連続経験履歴" && c.Level == HealthReport.Level.Ok);
        Assert.Contains(r.Checks, c => c.Area == "入力" && c.Name.StartsWith("今回の起動の手") && c.Detail.Contains("Normal=1"));
        Assert.Equal(HealthReport.Level.Warn, r.Overall);
        Assert.Contains("総合 WARN", r.Text);
        using (var j = System.Text.Json.JsonDocument.Parse(r.Json)) Assert.Equal("warn", j.RootElement.GetProperty("overall").GetString());
        // 例外があれば NG
        File.AppendAllLines(log, new[] { "{\"t\":\"2026-10-18T10:00:06+09:00\",\"type\":\"session_error\",\"error\":\"x\"}" });
        Assert.Equal(HealthReport.Level.Fail, HealthReport.Build("0.8.0", mem.Db, app.Db, null, new[] { log }, 2, null).Overall);
    }

    [Fact]
    public void Compat_step_results_fit_the_schema_even_when_rejected()
    {
        // v0.8.0 まで受理されなかった回を "rejected"（CHECK の外）で書き、不合格のテストが記録ごと失われていた
        using var d = new TempDir();
        using var app = AppStore.Open(d["a"], "0.8.1");
        var steps = new[] { (false, false), (true, false), (true, true) }.Select((x, i) => new CompatStepRecord(i, "background", "[]", null, x.Item2, 80, CompatStepRecord.ResultOf(x.Item1, x.Item2))).ToList();
        Assert.Equal(new[] { "ignored", "not_reverted", "accepted" }, steps.Select(s => s.Result));
        app.RecordProbe(new ProbeRecord("session", "input", "window_message", "p", "proc", "cls", true, null, null, false, null, null, null, 80, 90, 1, 3, "Isolated", "none", "[]", "{}"), null, steps);
        Assert.Equal(3L, app.Db.Scalar("SELECT COUNT(*) FROM compat_test_steps"));
    }

    [Fact]
    public void Diagnostic_export_refuses_images_and_carries_text_reports()
    {
        using var d = new TempDir();
        using var mem = ExperienceStore.Open(d["p"], "prof", "1", "0.8.0");
        File.WriteAllBytes(d["shot.png"], new byte[] { 1, 2, 3 });
        Assert.Throws<ArgumentException>(() => DiagnosticExport.Write(d["out/x.zip"], new[] { ("memory.sqlite", mem.Db) }, new[] { ("profile/shot.png", d["shot.png"]) }, "s"));
        var res = DiagnosticExport.Write(d["out/y.zip"], new[] { ("memory.sqlite", mem.Db) }, Array.Empty<(string, string)>(), "s", new[] { ("health.txt", "ok") });
        Assert.Contains("health.txt", res.Entries);
        using var z = System.IO.Compression.ZipFile.OpenRead(res.ZipPath);
        Assert.DoesNotContain(z.Entries, e => DiagnosticExport.IsImage(e.FullName));
    }
}

/// <summary>スキーマ v6（2026-10-18 10:49 の汎用の機構）の保存と読み込み</summary>
public class SchemaV6Tests
{
    [Fact]
    public void V6_records_round_trip()
    {
        using var d = new TempDir();
        using var s = ExperienceStore.Open(d["p"], "prof", "1", "0.9.0");
        Assert.Equal(16, s.Db.SchemaVersion);
        // 連続履歴の新しい列
        s.AppendStream(s.BeginRun("1", "b", null, policyVersion: "explore-0.3"), null, new StreamStep(1, "m1", "button1", true, "m2", 0.1), 1.5, Array.Empty<BoundaryReason>(), 0.25, 3);
        Assert.Equal(0.25, Convert.ToDouble(s.Db.Scalar("SELECT excess FROM experience_stream WHERE seq=1")));
        Assert.Equal(3L, s.Db.Scalar("SELECT vis_state FROM experience_stream WHERE seq=1"));
        // 反応の対応表
        s.SaveReactionMaps(new[] { ("m1", "button1", -1, 10, 0, 0, 0.0, 0.0), ("m1", "button1", 5, 0, 7, 2, -8.0, 0.0) });
        s.SaveReactionMaps(new[] { ("m1", "button1", -1, 12, 0, 0, 0.0, 0.0) });   // 合計を上書き
        var rm = s.LoadReactionMaps();
        Assert.Equal(12, rm.Single(r => r.Cell == -1).N);
        Assert.Equal((7, 2), rm.Single(r => r.Cell == 5) is var c ? (c.Changed, c.Translated) : default);
        // 操作の並び
        var cand = new ActionSequenceCandidate { Id = 1, Steps = new[] { "button1", "button2" }, Start = "m1", End = "m2", Total = 5, AtStart = 4, StartSimilarity = 0.8, Reproducibility = 0.83, FoundAtSeq = 9 };
        s.SaveActionSequences(new[] { cand }); cand.Total = 6; s.SaveActionSequences(new[] { cand });
        var ls = Assert.Single(s.LoadActionSequences());
        Assert.Equal("ActionSequence #1", ls.Name); Assert.Equal(new[] { "button1", "button2" }, ls.Steps); Assert.Equal(6, ls.Total);
        // 見た目の状態・遷移の特徴・分割
        var vb = new VisualStateBook();
        var sig1 = Enumerable.Repeat(0.3f, VisualLayout.Length).ToArray(); var sig2 = (float[])sig1.Clone(); for (int i = 0; i < 12; i++) sig2[i] = 1f;
        var (v1, _) = vb.Assign(sig1, "m1"); var (v2, _) = vb.Assign(sig2, "m1");
        for (int i = 0; i < 6; i++) { vb.ObserveTransition(v1, "button1", false, v1); vb.ObserveTransition(v1, "button2", false, v1); vb.ObserveTransition(v1, "button1", false, v1); }
        for (int i = 0; i < 4; i++) { vb.ObserveTransition(v2, "button1", true, 9); vb.ObserveTransition(v2, "button2", true, 9); }
        Assert.Single(vb.RefreshSplits());
        s.SaveVisualStates(vb); s.SaveVisualStates(vb);
        var vb2 = new VisualStateBook(); s.LoadVisualStates(vb2);
        Assert.Equal(vb.Node("m1", v2), vb2.Node("m1", v2));
        Assert.Equal(2, vb2.States.Count);
        Assert.Equal(1L, s.Db.Scalar("SELECT COUNT(*) FROM state_splits"));
    }
}

