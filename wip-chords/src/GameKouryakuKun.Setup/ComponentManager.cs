using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameKouryakuKun.Setup;

public enum ComponentHealth { Installed, Missing, Broken, Partial }

public sealed record FileCheck(FileEntry File, string FullPath, bool Exists, bool Ok, bool FromLockCache, string Detail);

public sealed record ComponentStatus(ComponentSpec Spec, ComponentHealth Health, IReadOnlyList<FileCheck> Files, DateTime? LicenseAcceptedAtUtc)
{
    public long MissingBytes => Files.Where(f => !f.Ok).Sum(f => f.File.SizeBytes);
    public string Summary => Health switch
    {
        ComponentHealth.Installed => "導入済み（検証OK）",
        ComponentHealth.Missing => "未導入",
        ComponentHealth.Partial => $"一部不足（{Files.Count(f => !f.Ok)}/{Files.Count} ファイル）",
        _ => $"破損（{string.Join(", ", Files.Where(f => !f.Ok).Select(f => f.File.Path))}）",
    };
}

public sealed record DownloadProgress(string ComponentId, string Stage, string? File, long Done, long Total, string? Message = null)
{
    public double Ratio => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

public sealed record InstallResult(ComponentSpec Spec, bool Ok, string Detail, IReadOnlyList<string> DownloadedUrls, long BytesDownloaded, bool Resumed);

public sealed class SetupException : Exception { public SetupException(string m, Exception? inner = null) : base(m, inner) { } }

/// <summary>
/// コンポーネントの検査・計画・ダウンロード・検証・配置（設計書 §4.1.1、付録A IComponentManager）。
///  - 検査: 存在・サイズ・SHA-256。manifest.lock のサイズ+更新日時が一致すれば再ハッシュしない（大容量の再検査を避ける）
///  - DL: cache/downloads/&lt;sha256&gt;.part へ。HTTP Range でレジューム、指数バックオフで再試行、URL を順に切替
///  - 検証: SHA-256 不一致なら .part を破棄して再取得（上限回数まで）
///  - 配置: ステージングに揃えてから rename で原子的に置き換える（途中で失敗しても既存の正常な版は壊さない）
///  - 正常なファイルは再取得しない（ステージングへはローカルコピー）
///  - ライセンス同意が無ければ導入しない
/// </summary>
public sealed class ComponentManager
{
    public string Root { get; }
    public ComponentManifest Manifest { get; }
    public string DownloadDir => Path.Combine(Root, "cache", "downloads");
    public string LockPath => Path.Combine(Root, "components", "manifest.lock");
    public int MaxAttemptsPerUrl { get; set; } = 3;
    public int MaxHashRetries { get; set; } = 2;
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromMilliseconds(500);
    public Func<long>? FreeSpaceProbe { get; set; }
    public event Action<ComponentSpec, string, bool>? Installed;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _now;
    private readonly object _lockGate = new();

    public ComponentManager(string root, ComponentManifest manifest, HttpClient http, Func<DateTime>? utcNow = null)
    {
        Root = Path.GetFullPath(root); Manifest = manifest; _http = http; _now = utcNow ?? (() => DateTime.UtcNow);
    }

    public string InstallPath(ComponentSpec c) => Path.Combine(Root, c.InstallDir.Replace('/', Path.DirectorySeparatorChar));
    public string FilePath(ComponentSpec c, FileEntry f) => Path.Combine(InstallPath(c), f.Path.Replace('/', Path.DirectorySeparatorChar));

    // ================= 検査 =================
    public ComponentStatus Check(ComponentSpec c, bool forceHash = false)
    {
        var lockEntry = ReadLock().FirstOrDefault(e => e.Id == c.Id && e.Version == c.Version);
        var checks = new List<FileCheck>();
        foreach (var f in c.Files)
        {
            var full = FilePath(c, f);
            var fi = new FileInfo(full);
            if (!fi.Exists) { checks.Add(new FileCheck(f, full, false, false, false, "なし")); continue; }
            if (fi.Length != f.SizeBytes) { checks.Add(new FileCheck(f, full, true, false, false, $"サイズ不一致 {fi.Length}≠{f.SizeBytes}")); continue; }
            var cached = lockEntry?.Files.FirstOrDefault(x => x.Path == f.Path);
            if (!forceHash && cached != null && cached.Sha256 == f.Sha256 && cached.Size == fi.Length && cached.MtimeTicks == fi.LastWriteTimeUtc.Ticks)
            { checks.Add(new FileCheck(f, full, true, true, true, "manifest.lock と一致（サイズ・更新日時）")); continue; }
            var h = Sha256File(full);
            checks.Add(new FileCheck(f, full, true, h == f.Sha256, false, h == f.Sha256 ? "SHA-256 一致" : $"SHA-256 不一致 {h[..12]}…"));
        }
        ComponentHealth health = checks.All(x => x.Ok) ? ComponentHealth.Installed
            : checks.All(x => !x.Exists) ? ComponentHealth.Missing
            : checks.Any(x => x.Exists && !x.Ok) ? ComponentHealth.Broken : ComponentHealth.Partial;
        return new ComponentStatus(c, health, checks, lockEntry?.LicenseAcceptedAtUtc);
    }

    public IReadOnlyList<ComponentStatus> CheckAll(bool forceHash = false) => Manifest.Components.Select(c => Check(c, forceHash)).ToList();

    /// <summary>導入が必要な部品（不足・破損）の一覧と合計DL容量</summary>
    public (IReadOnlyList<ComponentStatus> Needed, long DownloadBytes) Plan(IEnumerable<string>? ids = null)
    {
        var want = ids?.ToHashSet();
        var needed = CheckAll().Where(s => s.Health != ComponentHealth.Installed && (want == null || want.Contains(s.Spec.Id))).ToList();
        long bytes = needed.Sum(s => s.Spec.Archive != null ? (ArchiveCached(s.Spec) ? 0 : s.Spec.Archive.SizeBytes) : s.MissingBytes);
        return (needed, bytes);
    }

    private bool ArchiveCached(ComponentSpec c) =>
        c.Archive != null && File.Exists(CachedPath(c.Archive.Sha256)) && new FileInfo(CachedPath(c.Archive.Sha256)).Length == c.Archive.SizeBytes;

    private string PartPath(string sha) => Path.Combine(DownloadDir, sha + ".part");
    private string CachedPath(string sha) => Path.Combine(DownloadDir, sha + ".verified");

    // ================= 導入 =================
    public async Task<InstallResult> InstallAsync(ComponentSpec c, bool licenseAccepted, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (!licenseAccepted) throw new SetupException($"{c.DisplayName}: ライセンス（{c.License.Spdx}）への同意がないため導入しません");
        var status = Check(c);
        if (status.Health == ComponentHealth.Installed)
        {
            WriteLock(c, status.LicenseAcceptedAtUtc ?? _now());
            return new InstallResult(c, true, "導入済み（再取得なし）", Array.Empty<string>(), 0, false);
        }
        Directory.CreateDirectory(DownloadDir);
        long need = c.Archive != null ? (ArchiveCached(c) ? 0 : c.Archive.SizeBytes) + c.InstalledBytes : status.MissingBytes;
        long free = FreeSpaceProbe?.Invoke() ?? FreeBytes(Root);
        if (free >= 0 && free < need + 16 * 1024 * 1024) throw new SetupException($"空き容量が足りません（必要 {Mb(need)}、空き {Mb(free)}）");

        var final = InstallPath(c);
        var parent = Path.GetDirectoryName(final)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".staging-{c.Version}-{Guid.NewGuid():N}"[..Math.Min(60, 26 + c.Version.Length)]);
        var urls = new List<string>(); long downloaded = 0; bool resumed = false; int reused = 0;
        try
        {
            Directory.CreateDirectory(staging);
            if (c.Archive != null)
            {
                var (path, n, r, u) = await FetchVerifiedAsync(c, null, c.Archive.Sha256, c.Archive.SizeBytes, c.Source.Urls, progress, ct).ConfigureAwait(false);
                downloaded += n; resumed |= r; if (u != null) urls.Add(u);
                progress?.Report(new DownloadProgress(c.Id, "展開", null, 0, c.Files.Count));
                using (var zip = ZipFile.OpenRead(path))
                {
                    int k = 0;
                    foreach (var f in c.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = zip.GetEntry(f.ArchivePath!) ?? throw new SetupException($"{c.Id}: アーカイブに {f.ArchivePath} がありません");
                        var dest = StagingFile(staging, f);
                        entry.ExtractToFile(dest, true);
                        VerifyFile(dest, f, c.Id);
                        progress?.Report(new DownloadProgress(c.Id, "展開", f.Path, ++k, c.Files.Count));
                    }
                }
            }
            else
            {
                foreach (var fc in status.Files)
                {
                    var dest = StagingFile(staging, fc.File);
                    if (fc.Ok) { File.Copy(fc.FullPath, dest, true); continue; } // 正常なファイルは再取得しない
                    if (FindLocalCopy(c, fc.File) is string local) // 別の部品に同じ内容（同じ SHA-256）の導入済みファイルがあれば複製
                    {
                        File.Copy(local, dest, true); VerifyFile(dest, fc.File, c.Id); reused++;
                        continue;
                    }
                    var (path, n, r, u) = await FetchVerifiedAsync(c, fc.File.Path, fc.File.Sha256, fc.File.SizeBytes, fc.File.Urls.Concat(c.Source.Urls).ToList(), progress, ct).ConfigureAwait(false);
                    downloaded += n; resumed |= r; if (u != null) urls.Add(u);
                    File.Copy(path, dest, true);
                    VerifyFile(dest, fc.File, c.Id);
                }
            }
            progress?.Report(new DownloadProgress(c.Id, "配置", null, 0, 1));
            AtomicSwap(staging, final);
            foreach (var f in c.Files) // ステージングから移動したファイルの最終確認
                VerifyFile(FilePath(c, f), f, c.Id);
            var acceptedAt = _now();
            WriteLock(c, acceptedAt);
            if (c.Archive != null) TryDelete(CachedPath(c.Archive.Sha256)); // 展開済みアーカイブは不要
            foreach (var f in c.Files.Where(f => c.Archive == null)) TryDelete(CachedPath(f.Sha256));
            progress?.Report(new DownloadProgress(c.Id, "完了", null, 1, 1));
            Installed?.Invoke(c, final, true);
            return new InstallResult(c, true, $"導入しました（DL {Mb(downloaded)}{(resumed ? "・途中から再開" : "")}{(reused > 0 ? $"・導入済みの同一ファイル {reused} 個を再利用" : "")}）", urls, downloaded, resumed);
        }
        catch
        {
            TryDeleteDir(staging);
            Installed?.Invoke(c, final, false);
            throw;
        }
    }

    private static string StagingFile(string staging, FileEntry f)
    {
        var dest = Path.Combine(staging, f.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        return dest;
    }

    /// <summary>他の部品の導入済みファイルで、サイズと SHA-256 が一致するもの（実測で確認）</summary>
    private string? FindLocalCopy(ComponentSpec c, FileEntry f)
    {
        foreach (var o in Manifest.Components.Where(o => o.Id != c.Id))
            foreach (var of in o.Files.Where(x => string.Equals(x.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase) && x.SizeBytes == f.SizeBytes))
            {
                var p = FilePath(o, of);
                try { if (File.Exists(p) && new FileInfo(p).Length == f.SizeBytes && string.Equals(Sha256File(p), f.Sha256, StringComparison.OrdinalIgnoreCase)) return p; }
                catch { }
            }
        return null;
    }

    private static void VerifyFile(string path, FileEntry f, string id)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length != f.SizeBytes) throw new SetupException($"{id}: {f.Path} のサイズが一致しません（{(fi.Exists ? fi.Length : -1)}≠{f.SizeBytes}）");
        var h = Sha256File(path);
        if (h != f.Sha256) throw new SetupException($"{id}: {f.Path} の SHA-256 が一致しません（{h}）");
    }

    /// <summary>ステージングを本番の位置へ rename で置き換える。既存の版はいったん退避し、成功後に削除。</summary>
    internal static void AtomicSwap(string staging, string final)
    {
        string? old = null;
        if (Directory.Exists(final))
        {
            old = final + $".old-{Guid.NewGuid():N}"[..13];
            Directory.Move(final, old);
        }
        try { Directory.Move(staging, final); }
        catch
        {
            if (old != null && !Directory.Exists(final)) Directory.Move(old, final); // 元に戻す
            throw;
        }
        if (old != null) TryDeleteDir(old);
    }

    /// <summary>
    /// 1ファイルを取得し SHA-256 を検証して cache/downloads/&lt;sha&gt;.verified に置く（既にあれば再取得しない）。
    /// 戻り値: (パス, 今回DLしたバイト数, レジュームしたか, 成功したURL)
    /// </summary>
    private async Task<(string Path, long Bytes, bool Resumed, string? Url)> FetchVerifiedAsync(ComponentSpec c, string? fileName, string sha, long size, IReadOnlyList<string> urls,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var verified = CachedPath(sha);
        if (File.Exists(verified) && new FileInfo(verified).Length == size && Sha256File(verified) == sha) return (verified, 0, false, null);
        if (urls.Count == 0) throw new SetupException($"{c.Id}: 取得元 URL がありません");
        var part = PartPath(sha);
        long total = 0; bool resumedAny = false; Exception? last = null;
        for (int hashTry = 0; hashTry <= MaxHashRetries; hashTry++)
        {
            foreach (var url in urls)
            {
                for (int attempt = 0; attempt < MaxAttemptsPerUrl; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var (n, resumed) = await DownloadToPartAsync(c.Id, fileName, url, part, size, progress, ct).ConfigureAwait(false);
                        total += n; resumedAny |= resumed;
                        progress?.Report(new DownloadProgress(c.Id, "検証", fileName, size, size));
                        var h = Sha256File(part);
                        if (h == sha && new FileInfo(part).Length == size)
                        {
                            File.Move(part, verified, true);
                            return (verified, total, resumedAny, url);
                        }
                        TryDelete(part); // 不一致 → 破棄して取り直し
                        last = new SetupException($"{c.Id}: SHA-256 が一致しません（{url} → {h[..12]}…）");
                        goto nextHashTry;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or SetupException)
                    {
                        last = e;
                        progress?.Report(new DownloadProgress(c.Id, "再試行", fileName, File.Exists(part) ? new FileInfo(part).Length : 0, size, $"{e.Message}（{attempt + 1}/{MaxAttemptsPerUrl}）"));
                        await Task.Delay(TimeSpan.FromMilliseconds(BackoffBase.TotalMilliseconds * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
                    }
                }
            }
            break; // 全URL・全試行が通信失敗
        nextHashTry:;
        }
        throw new SetupException($"{c.Id}: 取得に失敗しました: {last?.Message}", last);
    }

    /// <summary>.part へ追記ダウンロード（HTTP Range でレジューム）。戻り値: (今回のバイト数, 再開したか)</summary>
    private async Task<(long Bytes, bool Resumed)> DownloadToPartAsync(string id, string? fileName, string url, string part, long size, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > size) { TryDelete(part); have = 0; }
        if (have == size) return (0, true);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        bool append;
        if (resp.StatusCode == HttpStatusCode.PartialContent && have > 0)
        {
            var cr = resp.Content.Headers.ContentRange;
            if (cr?.From != have) { append = false; have = 0; } // サーバーの返した範囲が違う → 最初から
            else append = true;
        }
        else if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { TryDelete(part); throw new SetupException("範囲指定が拒否されました（最初から取り直します）"); }
        else { resp.EnsureSuccessStatusCode(); append = false; have = 0; }
        if (resp.Content.Headers.ContentLength is long len && have + len != size)
            throw new SetupException($"サーバーのサイズ {have + len} がマニフェスト {size} と一致しません");
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var buf = new byte[1 << 16]; long got = 0; int n; long lastReport = 0;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            got += n;
            if (have + got > size) throw new SetupException("マニフェストのサイズを超えるデータが届きました");
            if (got - lastReport > 256 * 1024) { lastReport = got; progress?.Report(new DownloadProgress(id, "ダウンロード", fileName, have + got, size, url)); }
        }
        progress?.Report(new DownloadProgress(id, "ダウンロード", fileName, have + got, size, url));
        if (have + got < size) throw new IOException($"接続が途中で切れました（{have + got}/{size}）");
        return (got, append);
    }

    // ================= manifest.lock =================
    public sealed record LockFile(string Path, long Size, long MtimeTicks, string Sha256);
    public sealed record LockEntry(string Id, string Version, DateTime InstalledAtUtc, DateTime? LicenseAcceptedAtUtc, IReadOnlyList<LockFile> Files);

    public IReadOnlyList<LockEntry> ReadLock()
    {
        lock (_lockGate)
        {
            if (!File.Exists(LockPath)) return Array.Empty<LockEntry>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(LockPath));
                return doc.RootElement.GetProperty("components").EnumerateArray().Select(e => new LockEntry(
                    e.GetProperty("id").GetString()!, e.GetProperty("version").GetString()!,
                    DateTime.Parse(e.GetProperty("installed_at").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
                    e.TryGetProperty("license_accepted_at", out var la) && la.ValueKind == JsonValueKind.String ? DateTime.Parse(la.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
                    e.GetProperty("files").EnumerateArray().Select(f => new LockFile(f.GetProperty("path").GetString()!, f.GetProperty("size").GetInt64(),
                        f.GetProperty("mtime_ticks").GetInt64(), f.GetProperty("sha256").GetString()!)).ToList())).ToList();
            }
            catch { return Array.Empty<LockEntry>(); } // 壊れた lock は無視（フルハッシュで再検査される）
        }
    }

    private void WriteLock(ComponentSpec c, DateTime licenseAcceptedAt)
    {
        lock (_lockGate)
        {
            var entries = ReadLock().Where(e => e.Id != c.Id).ToList();
            entries.Add(new LockEntry(c.Id, c.Version, _now(), licenseAcceptedAt,
                c.Files.Select(f => { var fi = new FileInfo(FilePath(c, f)); return new LockFile(f.Path, fi.Length, fi.LastWriteTimeUtc.Ticks, f.Sha256); }).ToList()));
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject(); w.WriteNumber("lock_version", 1); w.WriteStartArray("components");
                foreach (var e in entries.OrderBy(e => e.Id, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("id", e.Id); w.WriteString("version", e.Version); w.WriteString("installed_at", e.InstalledAtUtc.ToString("O"));
                    if (e.LicenseAcceptedAtUtc is DateTime la) w.WriteString("license_accepted_at", la.ToString("O"));
                    w.WriteStartArray("files");
                    foreach (var f in e.Files) { w.WriteStartObject(); w.WriteString("path", f.Path); w.WriteNumber("size", f.Size); w.WriteNumber("mtime_ticks", f.MtimeTicks); w.WriteString("sha256", f.Sha256); w.WriteEndObject(); }
                    w.WriteEndArray(); w.WriteEndObject();
                }
                w.WriteEndArray(); w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
            var tmp = LockPath + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, LockPath, true);
        }
    }

    // ================= ユーティリティ =================
    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
    private static long FreeBytes(string path) { try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; } catch { return -1; } }
    public static string Mb(long b) => b < 1024 * 1024 ? $"{b / 1024.0:0.#}KB" : $"{b / 1024.0 / 1024.0:0.#}MB";
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    private static void TryDeleteDir(string p) { try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch { } }

    /// <summary>既定の通信設定で作る（通信を行うのはこのプロジェクトだけ。アプリ側は HttpClient を直接扱わない）</summary>
    public static ComponentManager CreateDefault(string root, ComponentManifest manifest, string appVersion) => new(root, manifest, CreateHttpClient(appVersion));

    /// <summary>ダウンロードに使う HttpClient（UA を明示・自動リダイレクト）</summary>
    public static HttpClient CreateHttpClient(string appVersion)
    {
        var h = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"GameKouryakuKun/{appVersion}");
        return h;
    }
}
