using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameKouryakuKun.Core.Profile;

/// <summary>
/// 以前の版で廃止した Profile の欄を、読み込みの前に安全に取り除く（v0.15.1）。
/// 対象は下の一覧の欄だけ。それ以外の未知の欄は従来どおり検証で拒否する。
/// 元のファイルはバックアップしてから書き換える（取り除く欄がないときは何もしない）。
/// </summary>
public static class ProfileRetiredKeys
{
    /// <summary>廃止した最上位の欄（版: 理由）。diagnostics … v0.15.0 で廃止（既知の操作を使う診断＝暗黙のゲーム知識のため）</summary>
    public static readonly IReadOnlyList<string> TopLevel = new[] { "diagnostics" };

    public sealed record Result(bool Migrated, string? BackupPath, IReadOnlyList<string> Removed);

    public static Result Migrate(string path)
    {
        string text = File.ReadAllText(path);
        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return new Result(false, null, Array.Empty<string>()); }   // 読めない JSON は検証に任せる
        if (node is not JsonObject obj) return new Result(false, null, Array.Empty<string>());
        var removed = TopLevel.Where(obj.ContainsKey).ToList();
        if (removed.Count == 0) return new Result(false, null, removed);
        var bak = path + $".retired-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        for (int i = 1; File.Exists(bak); i++) bak = path + $".retired-{DateTime.Now:yyyyMMdd-HHmmss}-{i}.bak";
        File.Copy(path, bak);
        foreach (var k in removed) obj.Remove(k);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(tmp, path, overwrite: true);
        return new Result(true, bak, removed);
    }
}
