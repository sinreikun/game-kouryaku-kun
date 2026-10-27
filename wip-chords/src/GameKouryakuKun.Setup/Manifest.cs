using System.Text.Json;

namespace GameKouryakuKun.Setup;

public sealed record LicenseInfo(string Spdx, string? Url, string? Notes);
public sealed record SourceInfo(string? Homepage, IReadOnlyList<string> Urls);
public sealed record ArchiveInfo(string Format, long SizeBytes, string Sha256, string? StripPrefix);
/// <summary>配置するファイル。archive を持つ部品では ArchivePath（zip内パス）から取り出し、持たない部品では Urls から直接取得する。</summary>
public sealed record FileEntry(string Path, long SizeBytes, string Sha256, IReadOnlyList<string> Urls, string? ArchivePath);

public sealed record ComponentSpec(
    string Id, string Kind, bool Required, string? Feature, string Version, string DisplayName, string? Description,
    LicenseInfo License, SourceInfo Source, ArchiveInfo? Archive, IReadOnlyList<FileEntry> Files, string InstallDir)
{
    public long DownloadBytes => Archive?.SizeBytes ?? Files.Sum(f => f.SizeBytes);
    public long InstalledBytes => Files.Sum(f => f.SizeBytes);
}

public sealed record ComponentManifest(int ManifestVersion, string AppMinVersion, IReadOnlyList<ComponentSpec> Components)
{
    public ComponentSpec? Find(string id) => Components.FirstOrDefault(c => c.Id == id);
}

public sealed class ManifestException : Exception { public ManifestException(string m) : base(m) { } }

/// <summary>
/// コンポーネントマニフェストの読込（設計書 §4.3 の形式。v1 の拡張: files[].urls / files[].archive_path / archive.strip_prefix）。
/// 不正な値（ハッシュ形式・サイズ・パスの逸脱・未知の archive 形式）は読込時に拒否する。
/// 署名検証（ed25519）は未実装: 現状は exe に埋め込んだマニフェストだけを信頼する。
/// </summary>
public static class ManifestLoader
{
    public static ComponentManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        int ver = root.GetProperty("manifest_version").GetInt32();
        if (ver != 1) throw new ManifestException($"manifest_version {ver} は未対応");
        var list = new List<ComponentSpec>();
        foreach (var c in root.GetProperty("components").EnumerateArray())
        {
            string id = Str(c, "id") ?? throw new ManifestException("id がありません");
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9._-]{0,63}$")) throw new ManifestException($"不正な id: {id}");
            string version = Str(c, "version") ?? throw new ManifestException($"{id}: version がありません");
            if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^[A-Za-z0-9._-]{1,40}$")) throw new ManifestException($"{id}: 不正な version");
            var lic = c.TryGetProperty("license", out var l)
                ? new LicenseInfo(Str(l, "spdx") ?? throw new ManifestException($"{id}: license.spdx がありません"), Str(l, "url"), Str(l, "notes"))
                : throw new ManifestException($"{id}: license がありません");
            var src = c.TryGetProperty("source", out var s) ? new SourceInfo(Str(s, "homepage"), Urls(s)) : new SourceInfo(null, Array.Empty<string>());
            ArchiveInfo? arc = null;
            if (c.TryGetProperty("archive", out var a))
            {
                var fmt = Str(a, "format") ?? "zip";
                if (fmt != "zip") throw new ManifestException($"{id}: archive.format {fmt} は未対応（zip のみ）");
                arc = new ArchiveInfo(fmt, a.GetProperty("size_bytes").GetInt64(), Hash(Str(a, "sha256"), id), Str(a, "strip_prefix"));
                if (arc.SizeBytes <= 0) throw new ManifestException($"{id}: archive.size_bytes が不正");
                if (src.Urls.Count == 0) throw new ManifestException($"{id}: archive の取得元 URL がありません");
            }
            var files = new List<FileEntry>();
            foreach (var f in c.GetProperty("files").EnumerateArray())
            {
                var path = Str(f, "path") ?? throw new ManifestException($"{id}: files[].path がありません");
                CheckRelative(path, id);
                var fe = new FileEntry(path, f.GetProperty("size_bytes").GetInt64(), Hash(Str(f, "sha256"), id), Urls(f), Str(f, "archive_path"));
                if (fe.SizeBytes <= 0) throw new ManifestException($"{id}: {path} の size_bytes が不正");
                if (arc == null && fe.Urls.Count == 0) throw new ManifestException($"{id}: {path} の取得元 URL がありません");
                if (arc != null && fe.ArchivePath == null) throw new ManifestException($"{id}: {path} の archive_path がありません");
                foreach (var u in fe.Urls.Concat(src.Urls)) if (!u.StartsWith("https://", StringComparison.Ordinal)) throw new ManifestException($"{id}: https 以外の URL は使えません: {u}");
                files.Add(fe);
            }
            if (files.Count == 0) throw new ManifestException($"{id}: files が空です");
            string installDir = Str(c, "install_dir") ?? $"components/{id}/{version}";
            CheckRelative(installDir, id);
            list.Add(new ComponentSpec(id, Str(c, "kind") ?? "data", c.TryGetProperty("required", out var rq) && rq.GetBoolean(), Str(c, "feature"), version,
                Str(c, "display_name") ?? id, Str(c, "description"), lic, src, arc, files, installDir));
        }
        if (list.Select(x => x.Id).Distinct().Count() != list.Count) throw new ManifestException("id が重複しています");
        return new ComponentManifest(ver, Str(root, "app_min_version") ?? "0.0.0", list);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static IReadOnlyList<string> Urls(JsonElement e) =>
        e.TryGetProperty("urls", out var u) && u.ValueKind == JsonValueKind.Array ? u.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : Array.Empty<string>();
    private static string Hash(string? h, string id)
    {
        if (h == null || !System.Text.RegularExpressions.Regex.IsMatch(h, "^[0-9a-fA-F]{64}$")) throw new ManifestException($"{id}: sha256 が不正（64桁の16進）: {h}");
        return h.ToLowerInvariant();
    }
    internal static void CheckRelative(string p, string id)
    {
        if (p.Length == 0 || System.IO.Path.IsPathRooted(p) || p.Contains(':') || p.Replace('\\', '/').Split('/').Any(seg => seg == ".." || seg == ""))
            throw new ManifestException($"{id}: 不正なパス（絶対パス・..・空要素は不可）: {p}");
    }
}
