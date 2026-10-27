using System.Globalization;

namespace GameKouryakuKun.Phase0;

/// <summary>
/// キー指定。テンキーと上段数字を厳密に区別する。
/// 受け付ける表記: Num0 / NumPad0 / テンキー0 / KP0（テンキー）, D0 / Top0 / 上段0（上段数字）, A〜Z, Up/Down/Left/Right,
/// Escape, Enter, Space, Tab, Back, F1〜F24, VK:0x60 など。数字1文字だけの指定は「あいまい」として確認を求める。
/// </summary>
public sealed record KeySpec(string Name, ushort Vk, ushort Scan, bool Extended, char? Char)
{
    public sealed class AmbiguousKeyException : Exception
    {
        public char Digit { get; }
        public AmbiguousKeyException(char d) : base($"「{d}」はテンキーか上段数字か区別できません（Num{d} または D{d} と指定してください）") { Digit = d; }
    }

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Space"] = 0x20, ["Tab"] = 0x09,
        ["Back"] = 0x08, ["Backspace"] = 0x08, ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Shift"] = 0x10, ["LShift"] = 0xA0, ["RShift"] = 0xA1, ["Ctrl"] = 0x11, ["LCtrl"] = 0xA2, ["RCtrl"] = 0xA3,
        ["NumMultiply"] = 0x6A, ["NumAdd"] = 0x6B, ["NumSubtract"] = 0x6D, ["NumDecimal"] = 0x6E, ["NumDivide"] = 0x6F,
        ["F24"] = 0x87,
    };

    // テンキー(NumLock ON時)のスキャンコード。いずれも拡張キーではない。
    private static readonly ushort[] NumpadScan = { 0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 };

    public static string VkName(ushort vk) => vk switch
    {
        >= 0x60 and <= 0x69 => $"VK_NUMPAD{vk - 0x60}",
        >= 0x30 and <= 0x39 => $"'{(char)vk}'(上段)",
        >= 0x41 and <= 0x5A => $"'{(char)vk}'",
        >= 0x70 and <= 0x87 => $"VK_F{vk - 0x6F}",
        _ => Named.FirstOrDefault(kv => kv.Value == vk).Key is { } n ? n : $"0x{vk:X2}",
    };

    public static KeySpec Parse(string input)
    {
        string s = input.Trim();
        if (s.Length == 0) throw new ArgumentException("キーが空です");
        // 全角数字→半角
        s = new string(s.Select(c => c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c).ToArray());
        if (s.Length == 1 && char.IsDigit(s[0])) throw new AmbiguousKeyException(s[0]);

        foreach (var pre in new[] { "NumPad", "Numpad", "Num", "テンキー", "KP", "N" })
            if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && s.Length == pre.Length + 1 && char.IsDigit(s[^1]))
                return Numpad(s[^1] - '0');
        foreach (var pre in new[] { "D", "Top", "上段", "Digit" })
            if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && s.Length == pre.Length + 1 && char.IsDigit(s[^1]))
                return Make((ushort)s[^1], s[^1]);

        if (s.StartsWith("VK:", StringComparison.OrdinalIgnoreCase))
        {
            var t = s[3..].Trim();
            ushort v = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ushort.Parse(t[2..], NumberStyles.HexNumber) : ushort.Parse(t);
            if (v is >= 0x60 and <= 0x69) return Numpad(v - 0x60);
            return Make(v, v is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? char.ToLowerInvariant((char)v) : null);
        }
        if (s.Length == 1 && char.IsLetter(s[0]) && s[0] < 128) { char u = char.ToUpperInvariant(s[0]); return Make(u, char.ToLowerInvariant(u)); }
        if (s.Length >= 2 && (s[0] == 'F' || s[0] == 'f') && int.TryParse(s[1..], out int fn) && fn is >= 1 and <= 24) return Make((ushort)(0x6F + fn), null);
        if (Named.TryGetValue(s, out var vk)) return Make(vk, vk == 0x20 ? ' ' : vk == 0x0D ? '\r' : null);
        throw new ArgumentException($"キー名を解釈できません: {input}（例: Num0, Num2, W, A, S, D, Escape, Enter, Up, F1, D0, VK:0x60）");
    }

    private static KeySpec Numpad(int d) => new($"Num{d}", (ushort)(0x60 + d), NumpadScan[d], false, (char)('0' + d));

    private static KeySpec Make(ushort vk, char? ch)
    {
        uint sc = Native.MapVirtualKeyW(vk, Native.MAPVK_VK_TO_VSC_EX);
        bool ext = (sc & 0xFF00) is 0xE000 or 0xE100 || vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0xA3;
        string name = vk is >= 0x30 and <= 0x39 ? $"D{(char)vk}" : vk is >= 0x41 and <= 0x5A ? ((char)vk).ToString() : VkName(vk);
        return new KeySpec(name, vk, (ushort)(sc & 0xFF), ext, ch);
    }

    public override string ToString() => $"{Name}(VK=0x{Vk:X2} {VkName(Vk)}, scan=0x{Scan:X2}{(Extended ? ", 拡張" : "")})";
}

/// <summary>ユーザーのゲーム内キー設定（ユーザーが提示したスクリーンショットに基づく入力割り当て。攻略知識ではない）</summary>
public static class TornekoPreset
{
    public const string Up = "W", Left = "A", Down = "S", Right = "D";
    public const string Confirm = "Num1";   // 決定/攻撃/話す
    public const string Cancel = "Num2";    // キャンセル/ダッシュ
    public const string Turn = "Num5";      // 方向転換/整理
    public const string Menu = "Num0";      // メニュー
    public const string Arrow = "Num4";     // 矢
    public const string Diagonal = "Num3";  // 斜め移動補助
    public const string Map = "Num7";       // マップ
    public const string Step = "Num6";      // 足踏み

    public static IEnumerable<(string role, string key)> All() => new[]
    {
        ("上", Up), ("左", Left), ("下", Down), ("右", Right), ("決定/攻撃/話す", Confirm), ("キャンセル/ダッシュ", Cancel),
        ("方向転換/整理", Turn), ("メニュー", Menu), ("矢", Arrow), ("斜め移動補助", Diagonal), ("マップ", Map), ("足踏み", Step),
    };
}
