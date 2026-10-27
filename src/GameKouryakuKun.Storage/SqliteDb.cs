using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameKouryakuKun.Storage;

/// <summary>
/// 1つのマイグレーション（前方のみ・単一トランザクションで適用）。
/// RebuildsTables=true は表の作り直し（CHECK 制約の変更など）を含むもの: SQLite の手順どおり、適用中だけ foreign_keys を切り、
/// コミット前に PRAGMA foreign_key_check で参照の整合を確かめる（不整合ならロールバック）。
/// </summary>
public sealed record Migration(int Version, string Description, string Sql, bool RebuildsTables = false);

/// <summary>
/// SQLite ファイル1つの共通処理: 接続設定（WAL・foreign_keys）、差分マイグレーション、自動バックアップ（Online Backup API）。
/// 設計書 §5.1 / §5.5。バージョンは PRAGMA user_version とメタ表（schema_meta / app_schema_meta）の両方に記録する。
/// </summary>
public sealed class SqliteDb : IDisposable
{
    public string Path { get; }
    public SqliteConnection Connection { get; }
    public int SchemaVersion { get; private set; }
    public IReadOnlyList<string> BackupsMade => _backups;
    private readonly List<string> _backups = new();
    private readonly object _gate = new();

    private SqliteDb(string path, SqliteConnection c) { Path = path; Connection = c; }

    /// <summary>
    /// 開いて最新スキーマまでマイグレーションする。
    /// 既存DBにマイグレーションを当てる前、および前回のバックアップから backupInterval 以上経っていれば、自動バックアップを取る。
    /// </summary>
    public static SqliteDb Open(string path, string metaTable, IReadOnlyList<Migration> migrations, string appVersion,
        IReadOnlyDictionary<string, string>? initialMeta = null, TimeSpan? backupInterval = null, int keepBackups = 5, Func<DateTime>? utcNow = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        bool existed = File.Exists(path) && new FileInfo(path).Length > 0;
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        var db = new SqliteDb(path, c);
        db.Exec("PRAGMA journal_mode=WAL;");
        db.Exec("PRAGMA foreign_keys=ON;");
        db.Exec("PRAGMA synchronous=NORMAL;");
        db.Exec("PRAGMA busy_timeout=3000;");
        int current = Convert.ToInt32(db.Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        int target = migrations.Count == 0 ? 0 : migrations.Max(m => m.Version);
        if (current > target) { c.Dispose(); throw new InvalidOperationException($"{path} のスキーマ v{current} はこのアプリ（対応 v{target}）より新しいため開けません"); }

        if (existed && current < target) db.Backup($"v{current}", now(), keepBackups);
        else if (existed && backupInterval is TimeSpan iv)
        {
            var age = db.LastBackupAgeOrNull(now());
            if (age == null || age.Value >= iv) db.Backup($"v{current}", now(), keepBackups); // 定期バックアップ（既定: 1日1回）
        }

        foreach (var m in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            if (m.RebuildsTables) db.Exec("PRAGMA foreign_keys=OFF;");   // トランザクションの外でしか切り替えられない
            try
            {
            using var tx = c.BeginTransaction();
            db.Exec(m.Sql, tx);
            if (m.RebuildsTables)
            {
                var bad = new List<string>();
                using (var chk = db.Cmd("PRAGMA foreign_key_check;", tx))
                using (var rd = chk.ExecuteReader())
                    while (rd.Read()) bad.Add($"{rd.GetValue(0)} rowid={rd.GetValue(1)} → {rd.GetValue(2)}");
                if (bad.Count > 0) { tx.Rollback(); throw new InvalidOperationException($"マイグレーション v{m.Version} で参照の不整合: " + string.Join("; ", bad.Take(5))); }
            }
            db.Exec($"CREATE TABLE IF NOT EXISTS {metaTable}(key TEXT PRIMARY KEY, value TEXT);", tx);
            db.Exec("CREATE TABLE IF NOT EXISTS migrations(version INTEGER PRIMARY KEY, applied_at TEXT, app_version TEXT, description TEXT);", tx);
            db.Exec("INSERT OR REPLACE INTO migrations(version, applied_at, app_version, description) VALUES($v,$t,$a,$d);", tx,
                ("$v", m.Version), ("$t", Iso(now())), ("$a", appVersion), ("$d", m.Description));
            db.Exec($"INSERT OR REPLACE INTO {metaTable}(key,value) VALUES('schema_version',$v);", tx, ("$v", m.Version.ToString(CultureInfo.InvariantCulture)));
            if (current == 0 && initialMeta != null)
                foreach (var kv in initialMeta) db.Exec($"INSERT OR IGNORE INTO {metaTable}(key,value) VALUES($k,$v);", tx, ("$k", kv.Key), ("$v", kv.Value));
            db.Exec($"PRAGMA user_version={m.Version};", tx);
            tx.Commit();
            current = m.Version;
            }
            finally { if (m.RebuildsTables) db.Exec("PRAGMA foreign_keys=ON;"); }
        }
        db.SchemaVersion = current;
        return db;
    }

    public static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>バックアップファイル名の接頭辞（例 memory.sqlite.bak-）</summary>
    public string BackupPrefix => System.IO.Path.GetFileName(Path) + ".bak-";

    public IEnumerable<string> ListBackups()
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        return Directory.EnumerateFiles(dir, BackupPrefix + "*").OrderBy(f => f, StringComparer.Ordinal);
    }

    private TimeSpan? LastBackupAgeOrNull(DateTime utcNow)
    {
        var last = ListBackups().Select(f => File.GetLastWriteTimeUtc(f)).DefaultIfEmpty(DateTime.MinValue).Max();
        return last == DateTime.MinValue ? null : utcNow - last;
    }

    /// <summary>SQLite Online Backup API でバックアップ（{file}.bak-{tag}-{yyyyMMddTHHmmssZ}）。古いものは keep 個を残して削除。</summary>
    public string Backup(string tag, DateTime utcNow, int keep = 5)
    {
        lock (_gate)
        {
            var dest = $"{System.IO.Path.GetFullPath(Path)}.bak-{tag}-{utcNow:yyyyMMdd'T'HHmmss'Z'}";
            int n = 1; var baseDest = dest;
            while (File.Exists(dest)) dest = $"{baseDest}-{n++}";
            using (var d = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dest, Pooling = false }.ToString()))
            {
                d.Open();
                Connection.BackupDatabase(d);
            }
            File.SetLastWriteTimeUtc(dest, utcNow);
            _backups.Add(dest);
            foreach (var old in ListBackups().OrderByDescending(File.GetLastWriteTimeUtc).Skip(Math.Max(1, keep)).ToList())
                try { File.Delete(old); } catch { }
            return dest;
        }
    }

    /// <summary>WAL の内容を本体ファイルへ書き戻して WAL を空にする（冒険の終了時・アプリ終了時）。返り値: (busy, log, checkpointed)</summary>
    public (long Busy, long Log, long Checkpointed) Checkpoint()
    {
        lock (_gate)
        {
            using var c = Cmd("PRAGMA wal_checkpoint(TRUNCATE);");
            using var r = c.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)) : (0, 0, 0);
        }
    }

    /// <summary>一貫したコピーを dest に書き出す（Online Backup API。使用中でも WAL の内容まで含む。バックアップ世代管理の対象外）</summary>
    public void ExportCopy(string dest)
    {
        lock (_gate)
        {
            if (File.Exists(dest)) File.Delete(dest);
            using var d = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dest, Pooling = false }.ToString());
            d.Open();
            Connection.BackupDatabase(d);
        }
    }

    // ---- 小さなヘルパ ----
    public SqliteCommand Cmd(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] ps)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql; cmd.Transaction = tx;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }
    public int Exec(string sql, SqliteTransaction? tx = null, params (string, object?)[] ps) { lock (_gate) { using var c = Cmd(sql, tx, ps); return c.ExecuteNonQuery(); } }
    public object? Scalar(string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        lock (_gate) { using var c = Cmd(sql, tx, ps); var r = c.ExecuteScalar(); return r is DBNull ? null : r; }
    }
    public long Insert(string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        lock (_gate)
        {
            using var c = Cmd(sql + "; SELECT last_insert_rowid();", tx, ps);
            return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        lock (_gate)
        {
            using var c = Cmd(sql, null, ps);
            using var r = c.ExecuteReader();
            var l = new List<T>();
            while (r.Read()) l.Add(map(r));
            return l;
        }
    }
    public T InTransaction<T>(Func<SqliteTransaction, T> f)
    {
        lock (_gate)
        {
            using var tx = Connection.BeginTransaction();
            var r = f(tx);
            tx.Commit();
            return r;
        }
    }
    public IReadOnlyList<string> Tables() => Query("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name", r => r.GetString(0));
    public string? Meta(string metaTable, string key) => Scalar($"SELECT value FROM {metaTable} WHERE key=$k", null, ("$k", key)) as string;

    public void Dispose()
    {
        try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { }
        Connection.Dispose();
    }
}
