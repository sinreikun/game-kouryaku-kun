using System.Globalization;

namespace GameKouryakuKun.Storage;

public sealed record HardwareSnapshot(string OsBuild, string? CpuName, int CoresLogical, long? RamTotalMb, long? RamAvailMb, long? DiskFreeMb, string? IsaFlagsJson = null, string? GpusJson = null);

public sealed record ProbeRecord(
    string Trigger, string BackendKind, string BackendId, string? ProfileId, string? TargetProcess, string? TargetWindowClass,
    bool Available, string? UnavailableReason, bool? ForegroundOk, bool? BackgroundOk, bool? MinimizedOk,
    double? FpsP50, double? BlackFrameRatio, int? LatencyP50, int? LatencyP95, int? SuccessCount, int? TrialCount,
    string? IsolationLevel, string? Interference, string? WarningsJson, string? DetailsJson);

public sealed record CompatStepRecord(int StepIndex, string Condition, string SequenceJson, double? ChangeScore, bool Reverted, int? LatencyMs, string Result)
{
    /// <summary>
    /// 互換性テストの1回の結果を、compat_test_steps.result の値（accepted / ignored / not_reverted）にする。
    /// v0.8.0 まで受理されなかった回を "rejected"（CHECK 制約の外）で書いていたため、不合格のテストは記録ごと失われていた（2026-10-18 09:50 の実機診断で判明）
    /// </summary>
    public static string ResultOf(bool accepted, bool reverted) => !accepted ? "ignored" : reverted ? "accepted" : "not_reverted";
}

public sealed record ComponentInstallRecord(string ComponentId, string Version, string InstallDir, string FilesJson, string? LicenseSpdx, string? LicenseAcceptedAt, string Result, string? Detail);

/// <summary>
/// アプリDB（app.sqlite）。ハードウェア・バックエンドのプローブ結果と選択履歴・導入済み部品を記録する。
/// 壊れても再検出・再プローブで再構築でき、memory.sqlite（学習）には影響しない（設計書 §5.5）。
/// </summary>
public sealed class AppStore : IDisposable
{
    private readonly SqliteDb _db;
    private readonly Func<DateTime> _now;
    public SqliteDb Db => _db;
    /// <summary>壊れていたため退避した旧ファイル（作り直した場合）</summary>
    public string? RebuiltFrom { get; private init; }
    private AppStore(SqliteDb db, Func<DateTime> now) { _db = db; _now = now; }

    public static AppStore Open(string dataDir, string appVersion, Func<DateTime>? utcNow = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        var path = Path.Combine(dataDir, "app.sqlite");
        SqliteDb db; string? rebuilt = null;
        try { db = SqliteDb.Open(path, Schemas.AppMetaTable, Schemas.App, appVersion, new Dictionary<string, string> { ["created_by_app"] = appVersion }, TimeSpan.FromDays(7), utcNow: now); }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // 壊れた app.sqlite は退避して作り直す（再検出・再プローブで復元できるため）
            var broken = $"{path}.broken-{now():yyyyMMdd'T'HHmmss'Z'}";
            try { File.Move(path, broken); rebuilt = broken; } catch { }
            foreach (var side in new[] { "-wal", "-shm" }) try { File.Delete(path + side); } catch { }
            db = SqliteDb.Open(path, Schemas.AppMetaTable, Schemas.App, appVersion, new Dictionary<string, string> { ["created_by_app"] = appVersion, ["rebuilt_from"] = broken }, utcNow: now);
        }
        return new AppStore(db, now) { RebuiltFrom = rebuilt };
    }

    private string Now() => SqliteDb.Iso(_now());

    public long RecordHardware(HardwareSnapshot h) =>
        _db.Insert("INSERT INTO hardware_snapshots(taken_at, os_build, cpu_name, cores_logical, isa_flags_json, ram_total_mb, ram_avail_mb, gpus_json, disk_free_mb) " +
                   "VALUES($t,$o,$c,$n,$i,$rt,$ra,$g,$d)", null,
            ("$t", Now()), ("$o", h.OsBuild), ("$c", h.CpuName), ("$n", h.CoresLogical), ("$i", h.IsaFlagsJson), ("$rt", h.RamTotalMb), ("$ra", h.RamAvailMb), ("$g", h.GpusJson), ("$d", h.DiskFreeMb));

    public long RecordProbe(ProbeRecord p, long? snapshotId = null, IReadOnlyList<CompatStepRecord>? steps = null) => _db.InTransaction(tx =>
    {
        long id = _db.Insert("INSERT INTO backend_probe_results(taken_at, trigger, backend_kind, backend_id, profile_id, target_process, target_window_class, game_window_mode, os_build, snapshot_id, " +
                             "available, unavailable_reason, foreground_ok, background_ok, minimized_ok, fps_p50, black_frame_ratio, latency_ms_p50, latency_ms_p95, success_count, trial_count, " +
                             "isolation_level, user_input_interference, requires_admin, warnings_json, details_json) VALUES($t,$tr,$k,$b,$pr,$tp,$tc,'windowed',$os,$s,$a,$ur,$fo,$bo,$mo,$fps,$bl,$l50,$l95,$sc,$tc2,$iso,$int,0,$w,$d)", tx,
            ("$t", Now()), ("$tr", p.Trigger), ("$k", p.BackendKind), ("$b", p.BackendId), ("$pr", p.ProfileId), ("$tp", p.TargetProcess), ("$tc", p.TargetWindowClass),
            ("$os", Environment.OSVersion.VersionString), ("$s", snapshotId), ("$a", p.Available ? 1 : 0), ("$ur", p.UnavailableReason),
            ("$fo", B(p.ForegroundOk)), ("$bo", B(p.BackgroundOk)), ("$mo", B(p.MinimizedOk)), ("$fps", p.FpsP50), ("$bl", p.BlackFrameRatio),
            ("$l50", p.LatencyP50), ("$l95", p.LatencyP95), ("$sc", p.SuccessCount), ("$tc2", p.TrialCount), ("$iso", p.IsolationLevel), ("$int", p.Interference),
            ("$w", p.WarningsJson), ("$d", p.DetailsJson));
        if (steps != null)
            foreach (var s in steps)
                _db.Exec("INSERT INTO compat_test_steps(probe_id, step_index, condition, sequence_json, change_score, reverted, latency_ms, result) VALUES($p,$i,$c,$q,$cs,$r,$l,$res)", tx,
                    ("$p", id), ("$i", s.StepIndex), ("$c", s.Condition), ("$q", s.SequenceJson), ("$cs", s.ChangeScore), ("$r", s.Reverted ? 1 : 0), ("$l", s.LatencyMs), ("$res", s.Result));
        return id;
    });

    private static object? B(bool? b) => b is bool v ? (v ? 1 : 0) : null;

    public long RecordSelection(string? captureId, string? inputId, string placement, string mode, bool consentOccupy, string reason) =>
        _db.Insert("INSERT INTO backend_selection_history(selected_at, capture_backend_id, input_backend_id, placement, mode, user_consent_occupy, reason) VALUES($t,$c,$i,$p,$m,$o,$r)", null,
            ("$t", Now()), ("$c", captureId), ("$i", inputId), ("$p", placement), ("$m", mode), ("$o", consentOccupy ? 1 : 0), ("$r", reason));

    public long RecordComponentInstall(ComponentInstallRecord r) =>
        _db.Insert("INSERT INTO component_installs(component_id, version, install_dir, files_json, license_spdx, license_accepted_at, result, detail, created_at) VALUES($c,$v,$d,$f,$l,$la,$r,$de,$t)", null,
            ("$c", r.ComponentId), ("$v", r.Version), ("$d", r.InstallDir), ("$f", r.FilesJson), ("$l", r.LicenseSpdx), ("$la", r.LicenseAcceptedAt), ("$r", r.Result), ("$de", r.Detail), ("$t", Now()));

    public sealed record ProbeRow(long ProbeId, string TakenAt, string Kind, string BackendId, bool Available, bool? BackgroundOk, double? Fps, int? LatencyP50, int? LatencyP95, int? Success, int? Trials, string? Isolation);

    public IReadOnlyList<ProbeRow> LatestProbes(int limit = 20) =>
        _db.Query("SELECT probe_id, taken_at, backend_kind, backend_id, available, background_ok, fps_p50, latency_ms_p50, latency_ms_p95, success_count, trial_count, isolation_level " +
                  "FROM backend_probe_results ORDER BY probe_id DESC LIMIT $n",
            r => new ProbeRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0, r.IsDBNull(5) ? null : r.GetInt32(5) != 0,
                r.IsDBNull(6) ? null : r.GetDouble(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11)), ("$n", limit));

    public int Count(string table) => Convert.ToInt32(_db.Scalar($"SELECT COUNT(*) FROM {table}"), CultureInfo.InvariantCulture);

    public void Dispose() => _db.Dispose();
}
