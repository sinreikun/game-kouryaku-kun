using System.Security.Cryptography;

namespace GameKouryakuKun.Core.Supervisor;

/// <summary>
/// 設定に明示したファイルの変化の見張り（v0.21.0）。変化の検出・差分（大きさ・ハッシュ・更新時刻）の記録・警告だけをする。
/// 推測で対象を増やさない・自動では戻さない。設定・セーブ・学習 DB をひとまとめに扱わない（学習 DB は対象にしない）。
/// 将来: 基準のスナップショット（ハッシュ）を取り、区別が確かなものだけ任意で戻せるようにする（今はしない）。
/// </summary>
public sealed class ProtectedFiles
{
    public sealed record Snap(bool Exists, long Size, string Hash, DateTime ModifiedUtc);
    private readonly Dictionary<string, Snap> _base = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, Snap> Baseline => _base;

    public static Snap Take(string path)
    {
        try
        {
            var fi = new FileInfo(path); if (!fi.Exists) return new Snap(false, 0, "", DateTime.MinValue);
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new Snap(true, fi.Length, Convert.ToHexString(SHA256.HashData(fs)), fi.LastWriteTimeUtc);
        }
        catch (Exception e) { return new Snap(false, -1, "error:" + e.GetType().Name, DateTime.MinValue); }
    }

    private readonly Dictionary<string, string> _warned = new(StringComparer.OrdinalIgnoreCase);
    public const string BaselineFileName = "supervisor-file-baseline.json";
    /// <summary>基準をどこから得たか（created＝今回はじめて取った・loaded＝前回までに保存した基準）</summary>
    public string BaselineOrigin { get; private set; } = "none";

    public void SetBaseline(IEnumerable<string> paths) { _base.Clear(); _warned.Clear(); foreach (var p in paths) _base[p] = Take(p); BaselineOrigin = "created"; }

    /// <summary>
    /// v0.23.1: 基準は最初に見張り始めたときに 1 回だけ取って保存し（data\supervisor-file-baseline.json）、起動のたび・変化のたびに取り直さない。
    /// アプリを閉じている間・再起動の間の変化も基準との差として見つかる。新しく足したファイルだけ、そのときの状態を基準に加える。基準を今にするのは人がファイルを消したときだけ。
    /// </summary>
    public void LoadOrCreateBaseline(IEnumerable<string> paths, string baselinePath)
    {
        _base.Clear(); _warned.Clear();
        var saved = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(baselinePath))
                foreach (var e in System.Text.Json.JsonDocument.Parse(File.ReadAllText(baselinePath)).RootElement.EnumerateObject())
                    saved[e.Name] = new Snap(e.Value.GetProperty("exists").GetBoolean(), e.Value.GetProperty("size").GetInt64(), e.Value.GetProperty("hash").GetString() ?? "", DateTime.FromBinary(e.Value.GetProperty("mtime").GetInt64()));
        }
        catch { saved.Clear(); }
        bool added = false;
        foreach (var p in paths) { if (saved.TryGetValue(p, out var sn)) _base[p] = sn; else { _base[p] = Take(p); added = true; } }
        BaselineOrigin = saved.Count > 0 ? (added ? "loaded+added" : "loaded") : "created";
        if (added || saved.Count != _base.Count)
        {
            var o = new System.Text.Json.Nodes.JsonObject();
            foreach (var (k, v) in _base) o[k] = new System.Text.Json.Nodes.JsonObject { ["exists"] = v.Exists, ["size"] = v.Size, ["hash"] = v.Hash, ["mtime"] = v.ModifiedUtc.ToBinary() };
            try { Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!); File.WriteAllText(baselinePath, o.ToJsonString()); } catch { }
        }
    }

    /// <summary>基準からの変化（変わったものだけ）。基準は新しい値に進めない（何回変わったかではなく、基準からの差を見る）</summary>
    public IReadOnlyList<(string Path, Snap Before, Snap After)> Changes() =>
        _base.Select(kv => (kv.Key, kv.Value, Take(kv.Key))).Where(x => x.Value.Hash != x.Item3.Hash || x.Value.Exists != x.Item3.Exists).ToList();

    /// <summary>まだ知らせていない変化だけ（同じ内容の変化を何度も警告しない。基準は動かさない）</summary>
    public IReadOnlyList<(string Path, Snap Before, Snap After)> NewChanges()
    {
        var r = new List<(string, Snap, Snap)>();
        foreach (var c in Changes())
        {
            if (_warned.TryGetValue(c.Path, out var h) && h == c.After.Hash) continue;
            _warned[c.Path] = c.After.Hash; r.Add(c);
        }
        return r;
    }
}
