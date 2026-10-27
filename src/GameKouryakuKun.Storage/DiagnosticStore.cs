using System.Globalization;

namespace GameKouryakuKun.Storage;

/// <summary>
/// システム診断の記録（diagnostics.sqlite）。v0.14.1 までの分離試験の記録（参考として読むだけ）。v0.15.0 以降は新しく書かない（既知操作を使う診断は廃止）。
/// U3: 攻略 AI の経験・知識（学習 DB）とは別ファイルで、学習・探索の処理はこのクラスを参照しない（静的検査で保証）。
/// </summary>
public sealed class DiagnosticStore : IDisposable
{
    public const string FileName = "diagnostics.sqlite";
    private readonly SqliteDb _db;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<long, int> _next = new();
    public SqliteDb Db => _db;
    public string Path { get; }

    private DiagnosticStore(SqliteDb db, string path, Func<DateTime> now) { _db = db; Path = path; _now = now; }

    public static DiagnosticStore Open(string profileDataDir, string appVersion, Func<DateTime>? utcNow = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        var path = System.IO.Path.Combine(profileDataDir, FileName);
        var db = SqliteDb.Open(path, Schemas.DiagMetaTable, Schemas.Diagnostics, appVersion, new Dictionary<string, string> { ["created_by_app"] = appVersion }, TimeSpan.FromDays(7), utcNow: now);
        return new DiagnosticStore(db, path, now);
    }

    private string Now() => SqliteDb.Iso(_now());

    public long BeginRun(string kind, string? inputBackendId, string? captureBackendId, string? notesJson = null)
    {
        var id = _db.Insert("INSERT INTO diag_runs(kind, started_at, input_backend_id, capture_backend_id, notes_json) VALUES($k,$t,$i,$c,$n)", null,
            ("$k", kind), ("$t", Now()), ("$i", inputBackendId), ("$c", captureBackendId), ("$n", notesJson));
        _next[id] = 0;
        return id;
    }

    public void AddTurn(long runId, string action, string boundaryKind, double latencyMs, bool changed, bool gameForeground)
    {
        int i = _next.TryGetValue(runId, out var n) ? n : 0; _next[runId] = i + 1;
        _db.Exec("INSERT INTO diag_turns(diag_run_id, turn_index, action, boundary_kind, latency_ms, screen_changed, game_foreground, created_at) VALUES($r,$i,$a,$k,$l,$c,$g,$t)", null,
            ("$r", runId), ("$i", i), ("$a", action), ("$k", boundaryKind), ("$l", (int)Math.Round(latencyMs)), ("$c", changed ? 1 : 0), ("$g", gameForeground ? 1 : 0), ("$t", Now()));
    }

    public void EndRun(long runId, string endReason, string? resultJson)
    {
        _db.Exec("UPDATE diag_runs SET ended_at=$t, end_reason=$r, result_json=$j WHERE diag_run_id=$id", null, ("$t", Now()), ("$r", endReason), ("$j", resultJson), ("$id", runId));
        try { _db.Checkpoint(); } catch { }
    }

    public int TurnCount(long? runId = null) =>
        Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM diag_turns WHERE ($r IS NULL OR diag_run_id=$r)", null, ("$r", runId)), CultureInfo.InvariantCulture);

    public void Dispose() => _db.Dispose();
}
