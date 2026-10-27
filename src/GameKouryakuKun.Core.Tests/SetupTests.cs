using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GameKouryakuKun.Setup;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>Range 対応の偽サーバー。途中切断・内容改ざん・失敗の注入ができる。</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<(string Url, long? From)> Requests { get; } = new();
    /// <summary>この URL の n 回目までの応答は、指定バイトで切断する</summary>
    public Dictionary<string, (int Times, int CutAt)> CutOnce { get; } = new();
    public HashSet<string> Fail { get; } = new();
    public Dictionary<string, int> CorruptTimes { get; } = new();
    public bool IgnoreRange { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var url = req.RequestUri!.ToString();
        long? from = req.Headers.Range?.Ranges.FirstOrDefault()?.From;
        Requests.Add((url, from));
        if (Fail.Contains(url) || !Files.TryGetValue(url, out var data)) return Task.FromResult(new HttpResponseMessage(Fail.Contains(url) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound));
        if (CorruptTimes.TryGetValue(url, out int c) && c > 0) { CorruptTimes[url] = c - 1; data = (byte[])data.Clone(); data[^1] ^= 0xFF; }
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        long start = 0;
        if (from is long f && !IgnoreRange) { start = f; resp.StatusCode = HttpStatusCode.PartialContent; }
        byte[] body = data[(int)start..];
        Stream s = new MemoryStream(body);
        if (CutOnce.TryGetValue(url, out var cut) && cut.Times > 0)
        {
            CutOnce[url] = (cut.Times - 1, cut.CutAt);
            s = new CuttingStream(body, cut.CutAt);
        }
        resp.Content = new StreamContent(s);
        resp.Content.Headers.ContentLength = body.Length;
        if (resp.StatusCode == HttpStatusCode.PartialContent) resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
        return Task.FromResult(resp);
    }

    private sealed class CuttingStream : MemoryStream
    {
        private readonly int _cut;
        public CuttingStream(byte[] b, int cut) : base(b) => _cut = cut;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= _cut) throw new IOException("接続が切れました（テスト）");
            return base.Read(buffer, offset, (int)Math.Min(count, _cut - Position));
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= _cut) throw new IOException("接続が切れました（テスト）");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _cut - Position)], ct);
        }
    }
}

public class SetupTests
{
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static byte[] Rand(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    private static (ComponentManifest M, FakeServer S, byte[] A, byte[] B) DirectManifest()
    {
        var a = Rand(300_000, 1); var b = Rand(1234, 2);
        var s = new FakeServer();
        s.Files["https://example.test/a.bin"] = a; s.Files["https://mirror.test/a.bin"] = a; s.Files["https://example.test/b.txt"] = b;
        var json = $$"""
        { "manifest_version": 1, "components": [ { "id": "m", "version": "1.0", "display_name": "モデル",
          "license": { "spdx": "Apache-2.0", "url": "https://example.test/LICENSE" },
          "files": [
            { "path": "a.bin", "size_bytes": {{a.Length}}, "sha256": "{{Sha(a)}}", "urls": ["https://example.test/a.bin", "https://mirror.test/a.bin"] },
            { "path": "sub/b.txt", "size_bytes": {{b.Length}}, "sha256": "{{Sha(b)}}", "urls": ["https://example.test/b.txt"] } ] } ] }
        """;
        return (ManifestLoader.Parse(json), s, a, b);
    }

    private static ComponentManager Mgr(string root, ComponentManifest m, FakeServer s) =>
        new(root, m, new HttpClient(s)) { BackoffBase = TimeSpan.FromMilliseconds(1), FreeSpaceProbe = () => long.MaxValue };

    [Fact]
    public async Task Install_downloads_verifies_places_and_locks()
    {
        using var d = new TempDir();
        var (m, s, a, b) = DirectManifest();
        var mgr = Mgr(d.Path, m, s);
        var c = m.Components[0];
        Assert.Equal(ComponentHealth.Missing, mgr.Check(c).Health);
        Assert.Equal(a.Length + b.Length, mgr.Plan().DownloadBytes);
        await Assert.ThrowsAsync<SetupException>(() => mgr.InstallAsync(c, licenseAccepted: false));
        var r = await mgr.InstallAsync(c, licenseAccepted: true);
        Assert.True(r.Ok);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(d.Path, "components/m/1.0/a.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(d.Path, "components/m/1.0/sub/b.txt")));
        var st = mgr.Check(c);
        Assert.Equal(ComponentHealth.Installed, st.Health);
        Assert.All(st.Files, f => Assert.True(f.FromLockCache)); // サイズ+更新日時で一次判定（再ハッシュしない）
        Assert.NotNull(st.LicenseAcceptedAtUtc);
        Assert.Empty(Directory.GetDirectories(Path.Combine(d.Path, "components/m"), ".staging-*"));
        // 正常なら再取得しない
        int before = s.Requests.Count;
        var r2 = await mgr.InstallAsync(c, true);
        Assert.Equal(before, s.Requests.Count);
        Assert.Equal(0, r2.BytesDownloaded);
        Assert.Empty(mgr.Plan().Needed);
    }

    [Fact]
    public async Task Identical_file_from_another_installed_component_is_reused_without_download()
    {
        using var d = new TempDir();
        var a = Rand(5000, 7); var b = Rand(800, 8);
        var s = new FakeServer();
        s.Files["https://example.test/old/dict.txt"] = a; s.Files["https://example.test/new/allowed.txt"] = a; s.Files["https://example.test/new/rec.bin"] = b;
        var json = $$"""
        { "manifest_version": 1, "components": [
          { "id": "old", "version": "4", "license": { "spdx": "MIT" },
            "files": [ { "path": "dict.txt", "size_bytes": {{a.Length}}, "sha256": "{{Sha(a)}}", "urls": ["https://example.test/old/dict.txt"] } ] },
          { "id": "new", "version": "5", "license": { "spdx": "MIT" },
            "files": [ { "path": "rec.bin", "size_bytes": {{b.Length}}, "sha256": "{{Sha(b)}}", "urls": ["https://example.test/new/rec.bin"] },
                       { "path": "allowed.txt", "size_bytes": {{a.Length}}, "sha256": "{{Sha(a)}}", "urls": ["https://example.test/new/allowed.txt"] } ] } ] }
        """;
        var m = ManifestLoader.Parse(json);
        var mgr = Mgr(d.Path, m, s);
        await mgr.InstallAsync(m.Find("old")!, true);
        var r = await mgr.InstallAsync(m.Find("new")!, true);
        Assert.True(r.Ok);
        Assert.DoesNotContain(s.Requests, q => q.Url.EndsWith("/new/allowed.txt"));
        Assert.Equal(b.Length, r.BytesDownloaded);
        Assert.Contains("再利用", r.Detail);
        Assert.Equal(ComponentHealth.Installed, mgr.Check(m.Find("new")!).Health);
    }

    [Fact]
    public async Task Interrupted_download_resumes_with_range_request()
    {
        using var d = new TempDir();
        var (m, s, a, _) = DirectManifest();
        s.CutOnce["https://example.test/a.bin"] = (1, 100_000);
        var mgr = Mgr(d.Path, m, s);
        var r = await mgr.InstallAsync(m.Components[0], true);
        Assert.True(r.Ok);
        Assert.True(r.Resumed);
        Assert.Contains(s.Requests, q => q.Url.EndsWith("a.bin") && q.From == 100_000);
        Assert.True(r.BytesDownloaded < a.Length + 2000); // 切れた分を取り直していない
    }

    [Fact]
    public async Task Server_ignoring_range_restarts_from_zero_and_still_succeeds()
    {
        using var d = new TempDir();
        var (m, s, _, _) = DirectManifest();
        s.CutOnce["https://example.test/a.bin"] = (1, 50_000);
        s.IgnoreRange = true;
        var r = await Mgr(d.Path, m, s).InstallAsync(m.Components[0], true);
        Assert.True(r.Ok);
        Assert.False(r.Resumed);
    }

    [Fact]
    public async Task Hash_mismatch_discards_part_and_retries_then_fails_cleanly()
    {
        using var d = new TempDir();
        var (m, s, _, _) = DirectManifest();
        s.CorruptTimes["https://example.test/a.bin"] = 1;
        var mgr = Mgr(d.Path, m, s);
        Assert.True((await mgr.InstallAsync(m.Components[0], true)).Ok); // 1回目は改ざん → 破棄して取り直し

        using var d2 = new TempDir();
        var (m2, s2, _, _) = DirectManifest();
        s2.CorruptTimes["https://example.test/a.bin"] = 99; s2.CorruptTimes["https://mirror.test/a.bin"] = 99;
        var mgr2 = Mgr(d2.Path, m2, s2);
        var ex = await Assert.ThrowsAsync<SetupException>(() => mgr2.InstallAsync(m2.Components[0], true));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(d2.Path, "components/m/1.0")));
        Assert.Empty(Directory.GetFiles(Path.Combine(d2.Path, "cache/downloads"), "*.part"));
    }

    [Fact]
    public async Task Failing_primary_url_falls_back_to_mirror()
    {
        using var d = new TempDir();
        var (m, s, _, _) = DirectManifest();
        s.Fail.Add("https://example.test/a.bin");
        var r = await Mgr(d.Path, m, s).InstallAsync(m.Components[0], true);
        Assert.Contains("https://mirror.test/a.bin", r.DownloadedUrls);
    }

    [Fact]
    public async Task Broken_file_is_detected_and_only_that_file_is_fetched_again()
    {
        using var d = new TempDir();
        var (m, s, a, _) = DirectManifest();
        var mgr = Mgr(d.Path, m, s);
        await mgr.InstallAsync(m.Components[0], true);
        var p = Path.Combine(d.Path, "components/m/1.0/sub/b.txt");
        var bytes = File.ReadAllBytes(p); bytes[0] ^= 1; File.WriteAllBytes(p, bytes); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(1)); // 同一サイズ・別内容（更新日時は変わる）
        var st = mgr.Check(m.Components[0]);
        Assert.Equal(ComponentHealth.Broken, st.Health);
        s.Requests.Clear();
        var r = await mgr.InstallAsync(m.Components[0], true);
        Assert.True(r.Ok);
        Assert.All(s.Requests, q => Assert.EndsWith("b.txt", q.Url));
        Assert.Equal(ComponentHealth.Installed, mgr.Check(m.Components[0], forceHash: true).Health);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(d.Path, "components/m/1.0/a.bin")));
    }

    [Fact]
    public async Task Failed_reinstall_keeps_existing_good_version_intact()
    {
        using var d = new TempDir();
        var (m, s, a, _) = DirectManifest();
        var mgr = Mgr(d.Path, m, s);
        await mgr.InstallAsync(m.Components[0], true);
        File.Delete(Path.Combine(d.Path, "components/m/1.0/sub/b.txt"));
        s.Fail.Add("https://example.test/b.txt");
        await Assert.ThrowsAsync<SetupException>(() => mgr.InstallAsync(m.Components[0], true));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(d.Path, "components/m/1.0/a.bin")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(d.Path, "components/m"), ".staging-*"));
    }

    [Fact]
    public async Task Archive_component_extracts_listed_files_and_verifies_each()
    {
        using var d = new TempDir();
        var dll = Rand(5000, 3); var lic = "MIT License"u8.ToArray();
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using (var e = z.CreateEntry("pkg-1/lib/x.dll").Open()) e.Write(dll);
                using (var e = z.CreateEntry("pkg-1/LICENSE").Open()) e.Write(lic);
                using (var e = z.CreateEntry("pkg-1/unused.pdb").Open()) e.Write(Rand(100, 4));
            }
            zip = ms.ToArray();
        }
        var s = new FakeServer(); s.Files["https://example.test/pkg.zip"] = zip;
        var m = ManifestLoader.Parse($$"""
        { "manifest_version": 1, "components": [ { "id": "rt", "version": "1", "license": { "spdx": "MIT" },
          "source": { "urls": ["https://example.test/pkg.zip"] },
          "archive": { "format": "zip", "size_bytes": {{zip.Length}}, "sha256": "{{Sha(zip)}}" },
          "files": [ { "path": "x.dll", "archive_path": "pkg-1/lib/x.dll", "size_bytes": {{dll.Length}}, "sha256": "{{Sha(dll)}}" },
                     { "path": "LICENSE", "archive_path": "pkg-1/LICENSE", "size_bytes": {{lic.Length}}, "sha256": "{{Sha(lic)}}" } ] } ] }
        """);
        var mgr = Mgr(d.Path, m, s);
        var r = await mgr.InstallAsync(m.Components[0], true);
        Assert.True(r.Ok);
        Assert.Equal(dll, File.ReadAllBytes(Path.Combine(d.Path, "components/rt/1/x.dll")));
        Assert.False(File.Exists(Path.Combine(d.Path, "components/rt/1/unused.pdb")));
        Assert.Empty(Directory.GetFiles(Path.Combine(d.Path, "cache/downloads"))); // 展開後はアーカイブを消す
    }

    [Theory]
    [InlineData("\"path\": \"../evil.dll\"")]
    [InlineData("\"path\": \"C:/evil.dll\"")]
    [InlineData("\"sha256\": \"1234\"")]
    [InlineData("\"urls\": [\"http://insecure.test/a\"]")]
    public void Manifest_rejects_unsafe_entries(string replacement)
    {
        var good = """{ "manifest_version": 1, "components": [ { "id": "m", "version": "1", "license": { "spdx": "MIT" }, "files": [ { "path": "a.bin", "size_bytes": 1, "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "urls": ["https://x.test/a"] } ] } ] }""";
        var key = replacement.Split(':')[0];
        var bad = System.Text.RegularExpressions.Regex.Replace(good, key + @": (\[[^\]]*\]|""[^""]*"")", replacement);
        Assert.NotEqual(good, bad);
        ManifestLoader.Parse(good);
        Assert.Throws<ManifestException>(() => ManifestLoader.Parse(bad));
    }

    [Fact]
    public void Bundled_manifest_is_valid_and_registers_real_ocr_model_and_runtime()
    {
        var m = ManifestLoader.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "components", "manifest.json")));
        var ort = m.Find("onnxruntime")!; var ocr = m.Find("ocr-paddle-ja")!;
        Assert.Equal("MIT", ort.License.Spdx); Assert.Equal("Apache-2.0", ocr.License.Spdx);
        Assert.NotNull(ort.Archive);
        Assert.Contains(ort.Files, f => f.Path == "onnxruntime.dll");
        Assert.Equal(new[] { "det.onnx", "rec_japan.onnx", "japan_dict.txt" }, ocr.Files.Select(f => f.Path));
        Assert.All(ocr.Files, f => Assert.StartsWith("https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.4.0/", f.Urls[0]));
        var v5 = m.Find("ocr-paddle-v5")!;
        Assert.Equal("Apache-2.0", v5.License.Spdx);
        Assert.Equal(new[] { "det.onnx", "rec.onnx", "dict.txt", "allowed_chars.txt" }, v5.Files.Select(f => f.Path));
        Assert.All(v5.Files, f => Assert.StartsWith("https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.4.0/", f.Urls[0]));
        // 予備の v4 と別ディレクトリ（片方の導入・削除がもう片方に影響しない）
        Assert.NotEqual(v5.InstallDir, ocr.InstallDir);
    }
}
