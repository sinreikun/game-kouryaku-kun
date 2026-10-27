using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameKouryakuKun.Phase0;

/// <summary>キー指定 ("Escape", "Enter", "Z", "Up", "F1", "VK:0x1B" など) を VK とスキャンコードへ変換。</summary>
public sealed record KeySpec(string Name, ushort Vk, ushort Scan, bool Extended)
{
    public static KeySpec Parse(string s)
    {
        s = s.Trim();
        ushort vk;
        if (s.StartsWith("VK:", StringComparison.OrdinalIgnoreCase))
            vk = Convert.ToUInt16(s[3..].Trim(), s[3..].Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
        else if (s.Length == 1 && char.IsDigit(s[0])) vk = (ushort)s[0];
        else if (Enum.TryParse<Keys>(s, true, out var k)) vk = (ushort)((int)k & 0xFFFF);
        else throw new ArgumentException($"キー名を解釈できません: {s}（例: Escape, Enter, Space, Z, X, Up, F1, VK:0x1B）");
        uint sc = Native.MapVirtualKeyW(vk, Native.MAPVK_VK_TO_VSC_EX);
        bool ext = (sc & 0xFF00) == 0xE000 || (sc & 0xFF00) == 0xE100;
        // 矢印・Insert/Delete/Home/End/PageUp/Down は拡張キー
        if (vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E) ext = true;
        return new KeySpec(((Keys)vk).ToString(), vk, (ushort)(sc & 0xFF), ext);
    }
    public override string ToString() => $"{Name}(VK=0x{Vk:X2}, scan=0x{Scan:X2}{(Extended ? ", ext" : "")})";
}

public interface IProbeInputBackend
{
    string Id { get; }
    string Label { get; }
    (bool available, string detail) Availability();
    /// <summary>1回の押下（押す→holdMs待つ→離す）。戻り値は送信APIの結果説明。</summary>
    string Press(IntPtr target, string keyOrButton, int holdMs);
}

/// <summary>① 対象ウィンドウへ WM_KEYDOWN/WM_KEYUP を PostMessage（OS入力キューを通らない＝物理入力系統と独立）</summary>
public sealed class PostMessageBackend : IProbeInputBackend
{
    public string Id => "1_postmessage";
    public string Label => "①PostMessage(WM_KEYDOWN/UP)";
    public bool AlsoSendChar { get; set; }
    public (bool, string) Availability() => (true, "常に送信可能（受理するかはゲーム側の入力取得方式次第）");

    public static IntPtr MakeLParam(KeySpec k, bool up)
    {
        long l = 1;                       // repeat count
        l |= (long)k.Scan << 16;          // scan code
        if (k.Extended) l |= 1L << 24;    // extended
        if (up) { l |= 1L << 30; l |= 1L << 31; } // previous state + transition
        return new IntPtr(unchecked((int)l));
    }

    public string Press(IntPtr target, string key, int holdMs)
    {
        var k = KeySpec.Parse(key);
        bool d = Native.PostMessageW(target, Native.WM_KEYDOWN, (IntPtr)k.Vk, MakeLParam(k, false));
        int e1 = Marshal.GetLastWin32Error();
        if (AlsoSendChar && k.Vk is >= 0x20 and <= 0x5A)
            Native.PostMessageW(target, Native.WM_CHAR, (IntPtr)char.ToLowerInvariant((char)k.Vk), MakeLParam(k, false));
        Thread.Sleep(holdMs);
        bool u = Native.PostMessageW(target, Native.WM_KEYUP, (IntPtr)k.Vk, MakeLParam(k, true));
        return $"PostMessage down={d} up={u}{(d ? "" : " err=" + e1)} key={k} lParamDown=0x{MakeLParam(k, false).ToInt64() & 0xFFFFFFFF:X8}";
    }
}

/// <summary>② ViGEmBus 経由の仮想 Xbox360(XInput) パッド。ViGEmBus 未導入なら利用不可として扱う。</summary>
public sealed class VigemBackend : IProbeInputBackend, IDisposable
{
    public string Id => "2_vigem_xinput";
    public string Label => "②仮想パッド(ViGEmBus/XInput)";
    private object? _client;   // Nefarius.ViGEm.Client.ViGEmClient
    private object? _pad;      // IXbox360Controller
    private string _detail = "";

    public (bool, string) Availability()
    {
        if (_pad != null) return (true, _detail);
        try
        {
            var client = new Nefarius.ViGEm.Client.ViGEmClient();
            _client = client;
            var pad = client.CreateXbox360Controller();
            pad.AutoSubmitReport = false;
            pad.Connect();
            _pad = pad;
            Thread.Sleep(1500); // OS/ゲームが新しいパッドを認識するまで待つ
            _detail = "ViGEmBus 検出・仮想Xbox360パッド接続済み";
            return (true, _detail);
        }
        catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException)
        { return (false, "ViGEmBus ドライバが見つかりません（未導入）→ 利用不可としてスキップ"); }
        catch (Exception e) { return (false, $"ViGEm 初期化失敗: {e.GetType().Name}: {e.Message}"); }
    }

    public static Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button ParseButton(string name)
    {
        var t = typeof(Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button);
        var p = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                 .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.PropertyType == t);
        if (p != null) return (Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button)p.GetValue(null)!;
        var f = t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                 .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.FieldType == t);
        if (f != null) return (Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button)f.GetValue(null)!;
        throw new ArgumentException($"パッドボタン名を解釈できません: {name}（例: A, B, X, Y, Start, Back, Up, Down, Left, Right, LeftShoulder, RightShoulder）");
    }

    public string Press(IntPtr target, string button, int holdMs)
    {
        if (_pad is not Nefarius.ViGEm.Client.Targets.IXbox360Controller pad) return "未接続";
        var b = ParseButton(button);
        pad.SetButtonState(b, true); pad.SubmitReport();
        Thread.Sleep(Math.Max(holdMs, 60));
        pad.SetButtonState(b, false); pad.SubmitReport();
        return $"XInput button={button} hold={holdMs}ms（対象ウィンドウ指定なし：パッドはシステム全体のデバイス）";
    }

    public void Dispose()
    {
        try { (_pad as Nefarius.ViGEm.Client.Targets.IXbox360Controller)?.Disconnect(); } catch { }
        try { (_client as IDisposable)?.Dispose(); } catch { }
        _pad = null; _client = null;
    }
}

/// <summary>③ SendInput（OS全体の入力キューへ注入。前面ウィンドウに届く）。dwExtraInfo に署名を付け自分の注入を識別可能にする。</summary>
public sealed class SendInputBackend : IProbeInputBackend
{
    public string Id => "3_sendinput";
    public string Label => "③SendInput(OS全体)";
    /// <summary>送信直前に前面ウィンドウがこれと一致しなければ送信を中止する（誤入力防止）。</summary>
    public IntPtr RequiredForeground { get; set; }
    public (bool, string) Availability() => (true, "利用可能（前面ウィンドウにのみ届く・ユーザー操作と干渉する最終手段）");

    public string Press(IntPtr target, string key, int holdMs)
    {
        var k = KeySpec.Parse(key);
        if (RequiredForeground != IntPtr.Zero && Native.GetForegroundWindow() != RequiredForeground)
            return "中止: 前面ウィンドウが想定と異なるため送信しませんでした（誤入力防止）";
        uint flags = Native.KEYEVENTF_SCANCODE | (k.Extended ? Native.KEYEVENTF_EXTENDEDKEY : 0);
        var down = new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new() { ki = new() { wVk = 0, wScan = k.Scan, dwFlags = flags, dwExtraInfo = Native.InjectSignature } } };
        uint r1 = Native.SendInput(1, new[] { down }, Marshal.SizeOf<Native.INPUT>());
        Thread.Sleep(holdMs);
        // keyup は前面チェックせず必ず送る（押しっぱなしを防ぐ）
        var up = down; up.U.ki.dwFlags |= Native.KEYEVENTF_KEYUP;
        uint r2 = Native.SendInput(1, new[] { up }, Marshal.SizeOf<Native.INPUT>());
        return $"SendInput down={r1} up={r2} key={k} (scancode mode)";
    }

    /// <summary>T4デモ用: 署名付きで無害キー(F24)を注入</summary>
    public static uint InjectHarmless()
    {
        var k = new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new() { ki = new() { wVk = 0x87 /*F24*/, dwExtraInfo = Native.InjectSignature } } };
        var u = k; u.U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
        return Native.SendInput(2, new[] { k, u }, Marshal.SizeOf<Native.INPUT>());
    }
}
