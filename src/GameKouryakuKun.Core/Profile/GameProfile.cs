using System.Text.Json;
using System.Text.RegularExpressions;
using GameKouryakuKun.Core.Input;

namespace GameKouryakuKun.Core.Profile;

/// <summary>
/// Game Profile（設計書 §9）。「身体と接続条件」だけを持つ（最上位原則 7）。スキーマ v2 で書けるのは
///  - window     : 対象ウィンドウの特定条件（プロセス名・クラス名・タイトル）
///  - input      : 中立の論理キー名（dpad_up/down/left/right・button1〜32）→ 物理キーの割当、既定押下時間、禁止キー
///  - （v0.15.0 で diagnostics.toggle を廃止: 特定の状態でキーが効くことを前提にした既知操作は、診断にも使わない）
/// のみ。キーの意味（決定・取消など）・表示名・攻略知識を書く欄はなく、未知のキーや意味を含む論理名はエラーになる。
/// </summary>
public sealed class GameProfile
{
    public const int SupportedSchemaVersion = 2;   // 根拠: スキーマの版（v2 で論理キー名を中立化・表示名を廃止）
    public int SchemaVersion { get; init; }
    public string ProfileId { get; init; } = "";
    public string ProfileVersion { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public WindowMatch Window { get; init; } = new();
    public InputSection Input { get; init; } = new();
    public string SourcePath { get; init; } = "";

    public LogicalKeyMap KeyMap() => new(Input.Keys.ToDictionary(kv => kv.Key, kv => kv.Value.Key));
    public ForbiddenKeyGuard Guard() => new(Input.ForbiddenKeys, Input.ForbiddenCombos);
}

public sealed class WindowMatch
{
    /// <summary>実行ファイル名（.exe なし、大文字小文字無視）</summary>
    public string? ProcessName { get; init; }
    public string? ClassName { get; init; }
    public string? TitleRegex { get; init; }

    public bool Matches(string processName, string className, string title)
    {
        if (ProcessName != null && !string.Equals(StripExe(processName), StripExe(ProcessName), StringComparison.OrdinalIgnoreCase)) return false;
        if (ClassName != null && !string.Equals(className, ClassName, StringComparison.Ordinal)) return false;
        if (TitleRegex != null && !Regex.IsMatch(title, TitleRegex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))) return false;
        return true;
    }
    private static string StripExe(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    public override string ToString() => $"process={ProcessName ?? "*"} class={ClassName ?? "*"} title~{TitleRegex ?? "*"}";
}

/// <summary>論理キーの割当（v2: 表示名・意味は持たない）</summary>
public sealed record KeyBinding(KeySpec Key);


public sealed class InputSection
{
    public int DefaultHoldMs { get; init; } = 80;   // 根拠: 汎用（人がキーを軽く1回押す長さ 50〜100ms の中ほど。身体の設定）
    public int MinIntervalMs { get; init; } = 100;  // 根拠: 汎用（人の連打の間隔の下限程度。身体の設定）
    public IReadOnlyDictionary<string, KeyBinding> Keys { get; init; } = new Dictionary<string, KeyBinding>();
    public IReadOnlyList<KeySpec> ForbiddenKeys { get; init; } = Array.Empty<KeySpec>();
    public IReadOnlyList<KeyCombo> ForbiddenCombos { get; init; } = Array.Empty<KeyCombo>();
    /// <summary>身体の条件としての同時押し（v0.26.0）。各要素は論理キー名の並び（2〜3キー、名前順）。これ以外の同時押しは送らない</summary>
    public IReadOnlyList<IReadOnlyList<string>> Chords { get; init; } = Array.Empty<IReadOnlyList<string>>();
}

public sealed class ProfileValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public ProfileValidationException(string source, IReadOnlyList<string> errors)
        : base($"Profile「{source}」の検証に失敗しました:\n - " + string.Join("\n - ", errors)) { Errors = errors; }
}

/// <summary>JSON を厳格に検証して読み込む（リフレクション不使用＝トリミング安全）。</summary>
public static class GameProfileLoader
{
    /// <summary>中立の論理キー名だけを許す（意味を含む名前＝攻略知識の混入を構造的に防ぐ。U2）</summary>
    private static readonly Regex LogicalId = new("^(dpad_(up|down|left|right)|button([1-9]|[12][0-9]|3[0-2]))$", RegexOptions.CultureInvariant);
    private static readonly Regex ProfileIdRx = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TopKeys = new() { "schema_version", "profile_id", "profile_version", "display_name", "window", "input" };
    private static readonly HashSet<string> WindowKeys = new() { "process_name", "class_name", "title_regex" };
    public const int MaxChordKeys = 3;   // 根拠: 汎用（片手・両手で同時に押せる程度。PostMessage の計画は N キーに対応）
    private static readonly HashSet<string> InputKeys = new() { "default_hold_ms", "min_interval_ms", "keys", "forbidden_keys", "forbidden_combos", "chords" };
    private static readonly HashSet<string> BindingKeys = new() { "key" };

    public static GameProfile Load(string path) => Parse(File.ReadAllText(path), path);

    public static GameProfile Parse(string json, string sourceName = "(memory)")
    {
        var errors = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new ProfileValidationException(sourceName, new[] { "JSON として読めません: " + e.Message }); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ProfileValidationException(sourceName, new[] { "最上位はオブジェクトである必要があります" });
            Unknown(root, TopKeys, "", errors);

            int schema = Int(root, "schema_version", "", errors, required: true) ?? 0;
            if (schema != GameProfile.SupportedSchemaVersion && schema != 0)
                errors.Add($"schema_version={schema} は未対応です（対応: {GameProfile.SupportedSchemaVersion}。v2 では論理キー名を dpad_up/down/left/right・button1〜32 に、表示名 label を削除してください）");
            string id = Str(root, "profile_id", "", errors, required: true) ?? "";
            if (id.Length > 0 && !ProfileIdRx.IsMatch(id)) errors.Add($"profile_id「{id}」は英小文字・数字・._- のみで書いてください");
            string ver = Str(root, "profile_version", "", errors, required: true) ?? "";
            string disp = Str(root, "display_name", "", errors, required: true) ?? "";
            if (disp.Length > 120) errors.Add("display_name が長すぎます（120文字まで）");

            var window = new WindowMatch();
            if (Obj(root, "window", "", errors, required: true) is JsonElement w)
            {
                Unknown(w, WindowKeys, "window.", errors);
                var pn = Str(w, "process_name", "window.", errors);
                var cn = Str(w, "class_name", "window.", errors);
                var tr = Str(w, "title_regex", "window.", errors);
                if (pn == null && tr == null) errors.Add("window には process_name か title_regex の少なくとも一方が必要です");
                if (tr != null) { try { _ = new Regex(tr); } catch (ArgumentException e) { errors.Add($"window.title_regex が正規表現として不正: {e.Message}"); } }
                window = new WindowMatch { ProcessName = pn, ClassName = cn, TitleRegex = tr };
            }

            var input = new InputSection();
            if (Obj(root, "input", "", errors, required: true) is JsonElement inp)
            {
                Unknown(inp, InputKeys, "input.", errors);
                int hold = Int(inp, "default_hold_ms", "input.", errors) ?? 80;
                if (hold is < 20 or > 1000) errors.Add($"input.default_hold_ms={hold} は 20〜1000 の範囲で指定してください");
                int interval = Int(inp, "min_interval_ms", "input.", errors) ?? 100;
                if (interval is < 0 or > 5000) errors.Add($"input.min_interval_ms={interval} は 0〜5000 の範囲で指定してください");

                var forbiddenKeys = new List<KeySpec>();
                foreach (var (s, path) in StrArray(inp, "forbidden_keys", "input.", errors))
                {
                    try { forbiddenKeys.Add(KeySpec.Parse(s)); } catch (FormatException e) { errors.Add($"{path}: {e.Message}"); }
                }
                var forbiddenCombos = new List<KeyCombo>();
                foreach (var (s, path) in StrArray(inp, "forbidden_combos", "input.", errors))
                {
                    try { var c = KeyCombo.Parse(s); if (c.Keys.Count < 2) errors.Add($"{path}: 組合せは2キー以上（単独キーは forbidden_keys へ）"); else forbiddenCombos.Add(c); }
                    catch (FormatException e) { errors.Add($"{path}: {e.Message}"); }
                }

                var keys = new Dictionary<string, KeyBinding>(StringComparer.Ordinal);
                if (Obj(inp, "keys", "input.", errors, required: true) is JsonElement ks)
                {
                    foreach (var p in ks.EnumerateObject())
                    {
                        string path = $"input.keys.{p.Name}";
                        if (!LogicalId.IsMatch(p.Name)) { errors.Add($"{path}: 論理キー名は中立の名前 dpad_up / dpad_down / dpad_left / dpad_right / button1〜button32 だけです（キーの意味を名前に書けません）"); continue; }
                        string? keyText = null;
                        if (p.Value.ValueKind == JsonValueKind.String) keyText = p.Value.GetString();
                        else if (p.Value.ValueKind == JsonValueKind.Object)
                        {
                            Unknown(p.Value, BindingKeys, path + ".", errors);
                            keyText = Str(p.Value, "key", path + ".", errors, required: true);
                        }
                        else { errors.Add($"{path}: 文字列（キー名）または {{\"key\":...}} で指定してください"); continue; }
                        if (keyText == null) continue;
                        try { keys[p.Name] = new KeyBinding(KeySpec.Parse(keyText)); }
                        catch (FormatException e) { errors.Add($"{path}: {e.Message}"); }
                    }
                    if (keys.Count == 0) errors.Add("input.keys が空です");
                }

                foreach (var dup in keys.GroupBy(kv => (kv.Value.Key.Vk, kv.Value.Key.Extended)).Where(g => g.Count() > 1))
                    errors.Add($"同じ物理キー {dup.First().Value.Key.Name} が複数の論理キーに割り当てられています: {string.Join(", ", dup.Select(kv => kv.Key))}");
                var guard = new ForbiddenKeyGuard(forbiddenKeys, forbiddenCombos);
                foreach (var kv in keys)
                    if (guard.Check(new[] { kv.Value.Key }) is string why) errors.Add($"input.keys.{kv.Key}: {why}（禁止キーは割り当てられません）");

                var chords = new List<IReadOnlyList<string>>();
                var seenChords = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (s, path) in StrArray(inp, "chords", "input.", errors))
                {
                    var parts = s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is < 2 or > MaxChordKeys) { errors.Add($"{path}: 同時押しは 2〜{MaxChordKeys} キーで書いてください（「{s}」）"); continue; }
                    if (parts.Distinct(StringComparer.Ordinal).Count() != parts.Length) { errors.Add($"{path}: 同じ論理キーが重複しています（「{s}」）"); continue; }
                    var unknown = parts.Where(x => !keys.ContainsKey(x)).ToList();
                    if (unknown.Count > 0) { errors.Add($"{path}: 論理キー {string.Join(", ", unknown)} は input.keys にありません（同時押しは論理キー名で書きます）"); continue; }
                    if (guard.Check(parts.Select(x => keys[x].Key).ToList()) is string cwhy) { errors.Add($"{path}: {cwhy}（禁止の組合せは書けません）"); continue; }
                    var sorted = parts.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    if (!seenChords.Add(string.Join("+", sorted))) { errors.Add($"{path}: 同じ同時押しが重複しています（「{s}」）"); continue; }
                    chords.Add(sorted);
                }

                input = new InputSection { DefaultHoldMs = hold, MinIntervalMs = interval, Keys = keys, ForbiddenKeys = forbiddenKeys, ForbiddenCombos = forbiddenCombos, Chords = chords };
            }

            if (errors.Count > 0) throw new ProfileValidationException(sourceName, errors);
            return new GameProfile { SchemaVersion = schema, ProfileId = id, ProfileVersion = ver, DisplayName = disp, Window = window, Input = input, SourcePath = sourceName };
        }
    }

    private static void Unknown(JsonElement o, HashSet<string> allowed, string prefix, List<string> errors)
    {
        foreach (var p in o.EnumerateObject())
            if (!allowed.Contains(p.Name))
                errors.Add($"未知のキー「{prefix}{p.Name}」（このスキーマで書けるのは {string.Join(", ", allowed)} だけです。攻略知識・説明文は書けません）");
    }

    private static JsonElement? Get(JsonElement o, string name, string prefix, List<string> errors, bool required)
    {
        if (o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null) return v;
        if (required) errors.Add($"必須キー「{prefix}{name}」がありません");
        return null;
    }
    private static string? Str(JsonElement o, string name, string prefix, List<string> errors, bool required = false)
    {
        if (Get(o, name, prefix, errors, required) is not JsonElement v) return null;
        if (v.ValueKind != JsonValueKind.String) { errors.Add($"「{prefix}{name}」は文字列で指定してください"); return null; }
        var s = v.GetString()!.Trim();
        if (s.Length == 0) { if (required) errors.Add($"「{prefix}{name}」が空です"); return null; }
        return s;
    }
    private static int? Int(JsonElement o, string name, string prefix, List<string> errors, bool required = false)
    {
        if (Get(o, name, prefix, errors, required) is not JsonElement v) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) { errors.Add($"「{prefix}{name}」は整数で指定してください"); return null; }
        return i;
    }
    private static JsonElement? Obj(JsonElement o, string name, string prefix, List<string> errors, bool required = false)
    {
        if (Get(o, name, prefix, errors, required) is not JsonElement v) return null;
        if (v.ValueKind != JsonValueKind.Object) { errors.Add($"「{prefix}{name}」はオブジェクトで指定してください"); return null; }
        return v;
    }
    private static IEnumerable<(string, string)> StrArray(JsonElement o, string name, string prefix, List<string> errors)
    {
        if (Get(o, name, prefix, errors, false) is not JsonElement v) yield break;
        if (v.ValueKind != JsonValueKind.Array) { errors.Add($"「{prefix}{name}」は文字列の配列で指定してください"); yield break; }
        int i = 0;
        foreach (var e in v.EnumerateArray())
        {
            string path = $"{prefix}{name}[{i++}]";
            if (e.ValueKind != JsonValueKind.String) { errors.Add($"{path}: 文字列で指定してください"); continue; }
            yield return (e.GetString()!, path);
        }
    }
}
