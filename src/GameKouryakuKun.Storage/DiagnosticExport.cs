using System.IO.Compression;
using System.Text;

namespace GameKouryakuKun.Storage;

/// <summary>
/// 不具合調査用の書き出し（設計書 §5.5）。使用中の DB も Online Backup API で一貫したコピーにする
/// （実機の報告 zip では memory.sqlite-wal が欠けて最新の行が見えなかったため。-wal/-shm を手で集める必要をなくす）。
/// ゲームのファイルやプロセスの情報は含めない（このアプリが自分で作ったファイルだけ）。
/// </summary>
public static class DiagnosticExport
{
    public sealed record Result(string ZipPath, IReadOnlyList<string> Entries, long Bytes);

    /// <param name="databases">(zip 内の名前, DB)。書き出し前に Checkpoint もする</param>
    /// <param name="files">(zip 内の名前, 元ファイル)。書き込み中のログも共有読み取りで含める。無いものは飛ばす</param>
    /// <param name="texts">(zip 内の名前, 内容)。健全性レポートなど</param>
    /// 画像（png・jpg など）は入れない（2026-10-18: 実機確認は診断 zip だけで行い、画像は開かない方針）
    public static Result Write(string zipPath, IEnumerable<(string Name, SqliteDb Db)> databases, IEnumerable<(string Name, string Path)> files, string summaryText,
        IEnumerable<(string Name, string Content)>? texts = null)
    {
        var images = files.Where(f => IsImage(f.Name) || IsImage(f.Path)).Select(f => f.Name).ToList();
        if (images.Count > 0) throw new ArgumentException("診断 zip に画像は入れません: " + string.Join(", ", images));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(zipPath))!);
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gkk-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var entries = new List<string>();
        try
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var sb = new StringBuilder(summaryText).AppendLine();
                foreach (var (name, db) in databases)
                {
                    try { db.Checkpoint(); } catch { }
                    var copy = System.IO.Path.Combine(tmp, System.IO.Path.GetFileName(name));
                    db.ExportCopy(copy);
                    z.CreateEntryFromFile(copy, name, CompressionLevel.Optimal); entries.Add(name);
                    sb.AppendLine($"{name}: schema v{db.SchemaVersion}, {new FileInfo(copy).Length} bytes（Backup API による一貫コピー）");
                    foreach (var b in db.ListBackups()) sb.AppendLine($"  バックアップ: {System.IO.Path.GetFileName(b)} {new FileInfo(b).Length} bytes {File.GetLastWriteTimeUtc(b):yyyy-MM-dd HH:mm:ss}Z");
                }
                foreach (var (name, path) in files)
                {
                    if (!File.Exists(path)) continue;
                    var e = z.CreateEntry(name, CompressionLevel.Optimal);
                    using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = e.Open();
                    src.CopyTo(dst);
                    entries.Add(name);
                }
                foreach (var (name, content) in texts ?? Array.Empty<(string, string)>())
                {
                    var te = z.CreateEntry(name, CompressionLevel.Optimal);
                    using (var w = new StreamWriter(te.Open(), new UTF8Encoding(true))) w.Write(content);
                    entries.Add(name);
                }
                var s = z.CreateEntry("summary.txt");
                using (var w = new StreamWriter(s.Open(), new UTF8Encoding(true))) w.Write(sb.ToString());
                entries.Add("summary.txt");
            }
            return new Result(zipPath, entries, new FileInfo(zipPath).Length);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    public static bool IsImage(string name) =>
        new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant());
}
