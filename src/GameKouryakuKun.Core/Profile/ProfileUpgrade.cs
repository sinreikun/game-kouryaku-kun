using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameKouryakuKun.Core.Profile;

/// <summary>
/// 同梱 Profile の版上げ（v0.26.0: torneko-remaster 0.2.0 → 0.3.0）を、ユーザーのデータの Profile に手作業なしで反映する。
/// 対象は「同じ profile_id で input.chords がない」ファイルだけ。元のファイルは必ず退避してから書き換える。
///  - 割当（keys・forbidden_keys）が旧同梱版のままのとき: 同梱の新版でそのまま置き換える（button9=Tab の追加・Tab の禁止解除を含む）。
///  - 割当をユーザーが変えているとき: 割当には触れず、同梱の chords のうち今の keys にある論理キーだけで書ける同時押しを足す。
/// どちらの場合も、来歴として退避先と方式を返す（ログ profile_upgraded と起動時の通知に残す）。
/// </summary>
public static class ProfileUpgrade
{
    public sealed record Result(bool Upgraded, string Mode, string? BackupPath, string? FromVersion, string? ToVersion, int Chords);

    private static readonly JsonDocumentOptions DocOpt = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static Result Migrate(string path, string shippedText)
    {
        JsonObject? user, shipped;
        try { user = JsonNode.Parse(File.ReadAllText(path), documentOptions: DocOpt) as JsonObject; shipped = JsonNode.Parse(shippedText, documentOptions: DocOpt) as JsonObject; }
        catch (JsonException) { return new Result(false, "unreadable", null, null, null, 0); }   // 読めない JSON は検証に任せる
        if (user == null || shipped == null) return new Result(false, "unreadable", null, null, null, 0);
        if (user["input"] is not JsonObject uin || shipped["input"] is not JsonObject sin) return new Result(false, "no_input", null, null, null, 0);
        if (uin.ContainsKey("chords") || sin["chords"] is not JsonArray schords) return new Result(false, "none", null, null, null, 0);
        if ((string?)user["profile_id"] != (string?)shipped["profile_id"]) return new Result(false, "other_profile", null, null, null, 0);

        string? from = (string?)user["profile_version"], to = (string?)shipped["profile_version"];
        var bak = path + $".upgrade-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        for (int i = 1; File.Exists(bak); i++) bak = path + $".upgrade-{DateTime.Now:yyyyMMdd-HHmmss}-{i}.bak";

        if (IsOldDefault(uin))
        {
            File.Copy(path, bak);
            WriteAtomic(path, shippedText);
            return new Result(true, "replaced_with_shipped", bak, from, to, schords.Count);
        }
        var keys = (uin["keys"] as JsonObject)?.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var add = new JsonArray();
        foreach (var c in schords) if (c?.GetValue<string>() is string s && s.Split('+').All(keys.Contains)) add.Add(s);
        File.Copy(path, bak);
        uin["chords"] = add;
        WriteAtomic(path, user.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return new Result(true, "chords_added_keys_kept", bak, from, from, add.Count);
    }

    /// <summary>旧同梱版（0.2.0）の割当: W/A/S/D、button1〜7=Num1〜7、button8=Num0、禁止 Escape/F12/Alt/Tab</summary>
    private static bool IsOldDefault(JsonObject inp)
    {
        var old = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dpad_up"] = "W", ["dpad_left"] = "A", ["dpad_down"] = "S", ["dpad_right"] = "D",
            ["button1"] = "Num1", ["button2"] = "Num2", ["button3"] = "Num3", ["button4"] = "Num4",
            ["button5"] = "Num5", ["button6"] = "Num6", ["button7"] = "Num7", ["button8"] = "Num0",
        };
        if (inp["keys"] is not JsonObject k || k.Count != old.Count) return false;
        foreach (var kv in k)
        {
            string? v = kv.Value is JsonValue jv ? jv.GetValue<string>() : (string?)kv.Value?["key"];
            if (!old.TryGetValue(kv.Key, out var want) || !string.Equals(v, want, StringComparison.OrdinalIgnoreCase)) return false;
        }
        var fk = (inp["forbidden_keys"] as JsonArray)?.Select(x => (string?)x).OrderBy(x => x, StringComparer.Ordinal).ToArray() ?? Array.Empty<string?>();
        return fk.SequenceEqual(new[] { "Alt", "Escape", "F12", "Tab" });
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
