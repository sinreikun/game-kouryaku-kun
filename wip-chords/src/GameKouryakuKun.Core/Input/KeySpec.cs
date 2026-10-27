using System.Globalization;

namespace GameKouryakuKun.Core.Input;

/// <summary>
/// 物理キーの指定（仮想キーコード VK・スキャンコード・拡張キーか）。OS API に頼らない静的表で解決するので Linux でもテストできる。
/// テンキーと上段数字を厳密に区別する（Phase 0 で「0」「2」がテンキーのつもりだった事故への対策）。
/// 受け付ける表記: Num0〜Num9 / NumPad0 / テンキー0 / KP0（テンキー）, D0〜D9 / Top0 / 上段0（上段数字）, A〜Z,
/// Up/Down/Left/Right, Escape, Enter, Space, Tab, Back, Insert, Delete, Home, End, PageUp, PageDown,
/// Shift/LShift/RShift, Ctrl/LCtrl/RCtrl, Alt/LAlt/RAlt, LWin/RWin/Win, Apps, F1〜F24, NumMultiply 等, VK:0x60。
/// 数字1文字だけの指定はあいまいなので AmbiguousKeyException。
/// </summary>
public sealed record KeySpec(string Name, ushort Vk, ushort Scan, bool Extended)
{
    public sealed class AmbiguousKeyException : FormatException
    {
        public char Digit { get; }
        public AmbiguousKeyException(char d) : base($"「{d}」はテンキーか上段数字か区別できません（テンキーなら Num{d}、上段数字なら D{d} と書いてください）") { Digit = d; }
    }

    // テンキー 0〜9 のスキャンコード（NumLock ON 時の数字。拡張キーではない）
    internal static readonly ushort[] NumpadScan = { 0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 };
    private static readonly ushort[] DigitScan = { 0x0B, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A };
    // A〜Z のスキャンコード（Set 1）
    private static readonly ushort[] LetterScan =
    {
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
    };
    private static readonly ushort[] FScan =
    {
        0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58,
        0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x76,
    };

    private static readonly Dictionary<string, KeySpec> Named = BuildNamed();
    private static Dictionary<string, KeySpec> BuildNamed()
    {
        var d = new Dictionary<string, KeySpec>(StringComparer.OrdinalIgnoreCase);
        void Add(string canonical, ushort vk, ushort sc, bool ext, params string[] aliases)
        {
            var k = new KeySpec(canonical, vk, sc, ext);
            d[canonical] = k; foreach (var a in aliases) d[a] = k;
        }
        Add("Escape", 0x1B, 0x01, false, "Esc");
        Add("Enter", 0x0D, 0x1C, false, "Return");
        Add("NumEnter", 0x0D, 0x1C, true);
        Add("Space", 0x20, 0x39, false);
        Add("Tab", 0x09, 0x0F, false);
        Add("Back", 0x08, 0x0E, false, "Backspace");
        Add("Up", 0x26, 0x48, true); Add("Down", 0x28, 0x50, true); Add("Left", 0x25, 0x4B, true); Add("Right", 0x27, 0x4D, true);
        Add("Insert", 0x2D, 0x52, true); Add("Delete", 0x2E, 0x53, true); Add("Home", 0x24, 0x47, true); Add("End", 0x23, 0x4F, true);
        Add("PageUp", 0x21, 0x49, true); Add("PageDown", 0x22, 0x51, true);
        Add("Shift", 0x10, 0x2A, false); Add("LShift", 0xA0, 0x2A, false); Add("RShift", 0xA1, 0x36, false);
        Add("Ctrl", 0x11, 0x1D, false, "Control"); Add("LCtrl", 0xA2, 0x1D, false); Add("RCtrl", 0xA3, 0x1D, true);
        Add("Alt", 0x12, 0x38, false, "Menu"); Add("LAlt", 0xA4, 0x38, false); Add("RAlt", 0xA5, 0x38, true);
        Add("LWin", 0x5B, 0x5B, true, "Win", "Windows"); Add("RWin", 0x5C, 0x5C, true); Add("Apps", 0x5D, 0x5D, true);
        Add("CapsLock", 0x14, 0x3A, false); Add("NumLock", 0x90, 0x45, false); Add("ScrollLock", 0x91, 0x46, false);
        Add("Pause", 0x13, 0x45, false); Add("PrintScreen", 0x2C, 0x37, true);
        Add("NumMultiply", 0x6A, 0x37, false); Add("NumAdd", 0x6B, 0x4E, false); Add("NumSubtract", 0x6D, 0x4A, false);
        Add("NumDecimal", 0x6E, 0x53, false); Add("NumDivide", 0x6F, 0x35, true);
        return d;
    }

    public static string VkName(ushort vk) => vk switch
    {
        >= 0x60 and <= 0x69 => $"VK_NUMPAD{vk - 0x60}",
        >= 0x30 and <= 0x39 => $"'{(char)vk}'(上段)",
        >= 0x41 and <= 0x5A => $"'{(char)vk}'",
        >= 0x70 and <= 0x87 => $"VK_F{vk - 0x6F}",
        _ => Named.Values.FirstOrDefault(k => k.Vk == vk)?.Name ?? $"0x{vk:X2}",
    };

    public bool IsNumpadDigit => Vk is >= 0x60 and <= 0x69;
    /// <summary>修飾キー（Shift/Ctrl/Alt/Win）か</summary>
    public bool IsModifier => Vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
    /// <summary>修飾キーの左右を区別しない族名（Alt/Shift/Ctrl/Win）。それ以外は Name。</summary>
    public string Family => Vk switch
    {
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        0x5B or 0x5C => "Win",
        _ => Name,
    };

    public static bool TryParse(string input, out KeySpec? key, out string? error)
    {
        try { key = Parse(input); error = null; return true; }
        catch (FormatException e) { key = null; error = e.Message; return false; }
    }

    public static KeySpec Parse(string input)
    {
        string s = (input ?? "").Trim();
        if (s.Length == 0) throw new FormatException("キーが空です");
        s = new string(s.Select(c => c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c is >= 'Ａ' and <= 'Ｚ' ? (char)('A' + (c - 'Ａ')) : c).ToArray());
        if (s.Length == 1 && char.IsAsciiDigit(s[0])) throw new AmbiguousKeyException(s[0]);

        foreach (var pre in new[] { "NumPad", "Num", "テンキー", "KP" })
            if (s.Length == pre.Length + 1 && s.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(s[^1]))
                return Numpad(s[^1] - '0');
        foreach (var pre in new[] { "Digit", "Top", "上段", "D" })
            if (s.Length == pre.Length + 1 && s.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(s[^1]))
                return TopDigit(s[^1] - '0');

        if (s.StartsWith("VK:", StringComparison.OrdinalIgnoreCase))
        {
            var t = s[3..].Trim();
            bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ushort.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                : ushort.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            if (!ok) throw new FormatException($"VK の数値を解釈できません: {input}");
            return FromVk(v) ?? throw new FormatException($"この VK はスキャンコード表にありません: 0x{v:X2}");
        }
        if (s.Length == 1 && char.IsAsciiLetter(s[0])) return Letter(char.ToUpperInvariant(s[0]));
        if (s.Length >= 2 && (s[0] == 'F' || s[0] == 'f') && int.TryParse(s[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int fn) && fn is >= 1 and <= 24)
            return new KeySpec($"F{fn}", (ushort)(0x6F + fn), FScan[fn - 1], false);
        if (Named.TryGetValue(s, out var named)) return named;
        throw new FormatException($"キー名を解釈できません: {input}（例: Num0, Num2, W, A, S, D, Escape, Enter, Up, F1, D0, VK:0x60）");
    }

    public static KeySpec? FromVk(ushort v)
    {
        if (v is >= 0x60 and <= 0x69) return Numpad(v - 0x60);
        if (v is >= 0x30 and <= 0x39) return TopDigit(v - 0x30);
        if (v is >= 0x41 and <= 0x5A) return Letter((char)v);
        if (v is >= 0x70 and <= 0x87) return new KeySpec($"F{v - 0x6F}", v, FScan[v - 0x70], false);
        return Named.Values.FirstOrDefault(k => k.Vk == v && !(k.Name == "NumEnter"));
    }

    private static KeySpec Numpad(int d) => new($"Num{d}", (ushort)(0x60 + d), NumpadScan[d], false);
    private static KeySpec TopDigit(int d) => new($"D{d}", (ushort)('0' + d), DigitScan[d], false);
    private static KeySpec Letter(char u) => new(u.ToString(), u, LetterScan[u - 'A'], false);

    public override string ToString() => $"{Name}(VK=0x{Vk:X2} {VkName(Vk)}, scan=0x{Scan:X2}{(Extended ? ", 拡張" : "")})";
}

/// <summary>同時押しの組合せ（"Alt+F4" など）。</summary>
public sealed record KeyCombo(IReadOnlyList<KeySpec> Keys)
{
    public static KeyCombo Parse(string text)
    {
        var parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new FormatException("組合せが空です");
        return new KeyCombo(parts.Select(KeySpec.Parse).ToList());
    }
    /// <summary>keys がこの組合せを全部含むか（修飾キーは左右を区別しない）</summary>
    public bool IsContainedIn(IEnumerable<KeySpec> keys)
    {
        var fam = keys.Select(k => k.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Keys.All(k => fam.Contains(k.Family));
    }
    public override string ToString() => string.Join("+", Keys.Select(k => k.Name));
}
