using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GameKouryakuKun.Storage;

/// <summary>v0.11 までの「画面を保存」（記録モード。v0.12.0 で廃止）の1手分の記録（manifest.jsonl の1行）。残った画像の一覧・自動削除にだけ使う</summary>
public sealed record RecordingEntry(int Step, long? TurnId, string Op, string Action, string Kind, bool Changed, double BoundaryMs, bool GameForeground, bool PanelSeen,
    string? File, DateTime At)
{
    /// <summary>画面ノード（記録モードの手探り探索が付けた、教師なしの画面の種類＋枠の配置）</summary>
    public string? Node { get; init; }
}

public sealed record RecordingInfo(long RunId, string Dir, int Frames, long Bytes, DateTime LastWriteUtc);

/// <summary>
/// 記録モードの評価用原寸画像（phase2-design.md §12.1）。data/profiles/&lt;id&gt;/recordings/run-&lt;runId&gt;/ に
/// 手ごとの画像（エンコード済みバイト列。JPEG 等）と manifest.jsonl を置く。
/// 過程データなので自動で削除する（決定 D8・§10.5）: 総量の上限（既定 2GB、古い記録から）と保存期間（既定 7 日）。
/// 評価用の正解データ（人が付ける）はここにも DB にも置かない（§13）。
/// </summary>
public sealed class RecordingStore
{
    public string Root { get; }
    public long LimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private long _approxTotal = -1;

    public RecordingStore(string profileDataDir, Func<DateTime>? utcNow = null)
    {
        Root = Path.Combine(profileDataDir, "recordings");
        _now = utcNow ?? (() => DateTime.UtcNow);
    }

    private readonly Dictionary<long, int> _seq = new();
    private readonly Dictionary<long, long> _runBytes = new();
    /// <summary>
    /// 1つの記録の画像の総量の上限。超えたら以降の画像は保存せず manifest に画像なしの行だけ残す（時間無制限の記録で
    /// 記録中の run が消されずにディスクを使い切らないため。既定は総量の上限と同じ）
    /// </summary>
    public long RunLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    /// <summary>上限のため保存しなかった画像の数（run ごと）</summary>
    public int CappedCount(long runId) { lock (_gate) return _capped.TryGetValue(runId, out var n) ? n : 0; }
    private readonly Dictionary<long, int> _capped = new();

    public string RunDir(long runId) => Path.Combine(Root, "run-" + runId.ToString(CultureInfo.InvariantCulture));

    /// <summary>1手分の画像を書き、manifest に1行足す。上限を超えたら古い記録を消す（記録中の run は最後まで残す）</summary>
    public string? Add(long runId, RecordingEntry entry, byte[]? image, string ext)
    {
        lock (_gate)
        {
            var dir = RunDir(runId);
            Directory.CreateDirectory(dir);
            string? name = null;
            if (image != null)
            {
                if (!_runBytes.TryGetValue(runId, out long used)) used = Directory.GetFiles(dir).Where(f => !f.EndsWith(".jsonl", StringComparison.Ordinal)).Sum(f => new FileInfo(f).Length);
                if (used + image.Length > RunLimitBytes)
                {
                    _capped[runId] = (_capped.TryGetValue(runId, out var c) ? c : 0) + 1;
                    _runBytes[runId] = used;
                    image = null;
                }
                else _runBytes[runId] = used + image.Length;
            }
            if (image != null)
            {
                // 1手に複数の入力（往復の操作など）があっても上書きしないよう、通し番号を先頭に付ける
                if (!_seq.TryGetValue(runId, out int seq))
                {
                    var mf = Path.Combine(dir, "manifest.jsonl");
                    seq = File.Exists(mf) ? File.ReadLines(mf).Count() : 0;
                }
                _seq[runId] = seq + 1;
                name = $"f{seq:000000}-step{entry.Step:00000}.{ext}";
                var tmp = Path.Combine(dir, name + ".tmp");
                File.WriteAllBytes(tmp, image);
                File.Move(tmp, Path.Combine(dir, name), true);
                if (_approxTotal < 0) _approxTotal = TotalBytes();
                else _approxTotal += image.Length;
            }
            var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["step"] = entry.Step, ["turn_id"] = entry.TurnId, ["op"] = entry.Op, ["action"] = entry.Action, ["kind"] = entry.Kind, ["changed"] = entry.Changed,
                ["boundary_ms"] = Math.Round(entry.BoundaryMs, 1), ["game_fg"] = entry.GameForeground, ["panel"] = entry.PanelSeen, ["file"] = name, ["node"] = entry.Node,
                ["at"] = entry.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            });
            File.AppendAllText(Path.Combine(dir, "manifest.jsonl"), line + "\n", new UTF8Encoding(false));
            if (_approxTotal > LimitBytes) { EnforceLimit(keepRunId: runId); _approxTotal = TotalBytes(); }
            return name;
        }
    }

    public IReadOnlyList<RecordingInfo> List()
    {
        if (!Directory.Exists(Root)) return Array.Empty<RecordingInfo>();
        var list = new List<RecordingInfo>();
        foreach (var d in Directory.GetDirectories(Root, "run-*"))
        {
            if (!long.TryParse(Path.GetFileName(d)[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            var files = Directory.GetFiles(d);
            list.Add(new RecordingInfo(id, d, files.Count(f => !f.EndsWith(".jsonl", StringComparison.Ordinal) && !f.EndsWith(".tmp", StringComparison.Ordinal)),
                files.Sum(f => new FileInfo(f).Length), files.Length == 0 ? Directory.GetLastWriteTimeUtc(d) : files.Max(File.GetLastWriteTimeUtc)));
        }
        return list.OrderBy(x => x.RunId).ToList();
    }

    public long TotalBytes() => List().Sum(x => x.Bytes);

    /// <summary>総量の上限を超えていたら古い記録から消す（keepRunId は消さない）。消した記録の数</summary>
    public int EnforceLimit(long? keepRunId = null)
    {
        int n = 0;
        var list = List().ToList();
        long total = list.Sum(x => x.Bytes);
        foreach (var r in list.OrderBy(x => x.LastWriteUtc))
        {
            if (total <= LimitBytes) break;
            if (r.RunId == keepRunId) continue;
            if (Delete(r.RunId)) { total -= r.Bytes; n++; }
        }
        return n;
    }

    /// <summary>保存期間を過ぎた記録と、上限を超えた分を消す（起動時に呼ぶ）</summary>
    public int Cleanup()
    {
        int n = 0;
        foreach (var r in List())
            if (_now() - r.LastWriteUtc > MaxAge && Delete(r.RunId)) n++;
        return n + EnforceLimit();
    }

    public bool Delete(long runId)
    {
        var d = RunDir(runId);
        if (!Directory.Exists(d)) return false;
        try { Directory.Delete(d, true); return true; } catch { return false; }
    }

    public IReadOnlyList<string> ManifestLines(long runId)
    {
        var p = Path.Combine(RunDir(runId), "manifest.jsonl");
        return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Length > 0).ToList() : new List<string>();
    }

    /// <summary>
    /// 記録を zip に書き出す（画像は圧縮済みなので無圧縮で格納）。budgetBytes を超える場合は、手の前後の組が残るよう
    /// 連続した区間（blockSize 手ずつ）を等間隔に選ぶ。manifest は全行を入れる（画像が含まれない行は file を空にしない: 元の名前のまま）。
    /// </summary>
    public (string Zip, int Frames, long Bytes, bool Thinned) Export(long runId, string zipPath, long budgetBytes = 95L * 1024 * 1024, int blockSize = 10, string? readme = null)
    {
        var dir = RunDir(runId);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("記録が見つかりません: run " + runId);
        var images = Directory.GetFiles(dir).Where(f => !f.EndsWith(".jsonl", StringComparison.Ordinal) && !f.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        long total = images.Sum(f => new FileInfo(f).Length);
        var chosen = images;
        bool thinned = false;
        if (total > budgetBytes && images.Count > 0)
        {
            thinned = true;
            double avg = (double)total / images.Count;
            int keep = Math.Max(blockSize, (int)(budgetBytes / avg));
            int blocks = Math.Max(1, keep / blockSize), nBlocks = (images.Count + blockSize - 1) / blockSize;
            var pick = new HashSet<int>();
            for (int i = 0; i < blocks; i++) pick.Add((int)Math.Floor((double)i * nBlocks / blocks));
            chosen = images.Where((f, i) => pick.Contains(i / blockSize)).ToList();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        var tmp = zipPath + ".tmp";
        long bytes = 0;
        using (var fs = File.Create(tmp))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var man = Path.Combine(dir, "manifest.jsonl");
            if (File.Exists(man)) z.CreateEntryFromFile(man, "manifest.jsonl", CompressionLevel.Optimal);
            if (readme != null)
            {
                var e = z.CreateEntry("README.txt", CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(true));
                w.Write(readme);
            }
            foreach (var f in chosen) { z.CreateEntryFromFile(f, "frames/" + Path.GetFileName(f), CompressionLevel.NoCompression); bytes += new FileInfo(f).Length; }
        }
        File.Move(tmp, zipPath, true);
        return (zipPath, chosen.Count, new FileInfo(zipPath).Length, thinned);
    }
}
