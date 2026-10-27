using System.Runtime.InteropServices;

namespace GameKouryakuKun.Phase0;

public interface IProbeInputBackend
{
    string Id { get; }
    string Label { get; }
    (bool available, string detail) Availability();
    /// <summary>1回の押下（押す→holdMs待つ→離す）。戻り値は送信APIの結果説明。</summary>
    string Press(IntPtr target, string keyOrButton, int holdMs);
}

/// <summary>
/// ① 対象ウィンドウへ WM_KEYDOWN/WM_KEYUP(+任意で WM_CHAR) を PostMessage。OSの入力キューを通らない＝物理入力系統と独立。
/// 押しっぱなし（オートリピート相当の WM_KEYDOWN 連送）と同時押しも送れる。
/// </summary>
public sealed class PostMessageBackend : IProbeInputBackend
{
    public string Id => AlsoSendChar ? "1b_postmessage_char" : "1a_postmessage";
    public string Label => AlsoSendChar ? "①b PostMessage(KEYDOWN/UP + WM_CHAR)" : "①a PostMessage(KEYDOWN/UPのみ)";
    public bool AlsoSendChar { get; set; }
    public (bool, string) Availability() => (true, "常に送信可能（受理するかはゲーム側の入力取得方式次第）");

    /// <param name="repeat">true = 押しっぱなし中の2回目以降（lParam bit30=1）</param>
    public static IntPtr MakeLParam(KeySpec k, bool up, bool repeat = false)
    {
        long l = 1;                       // repeat count
        l |= (long)k.Scan << 16;          // scan code
        if (k.Extended) l |= 1L << 24;    // extended
        if (repeat || up) l |= 1L << 30;  // previous key state
        if (up) l |= 1L << 31;            // transition state
        return new IntPtr(unchecked((int)l));
    }

    public bool Down(IntPtr target, KeySpec k, bool repeat = false)
    {
        bool ok = Native.PostMessageW(target, Native.WM_KEYDOWN, (IntPtr)k.Vk, MakeLParam(k, false, repeat));
        if (AlsoSendChar && k.Char is char c) Native.PostMessageW(target, Native.WM_CHAR, (IntPtr)c, MakeLParam(k, false, repeat));
        return ok;
    }
    public bool Up(IntPtr target, KeySpec k) => Native.PostMessageW(target, Native.WM_KEYUP, (IntPtr)k.Vk, MakeLParam(k, true));

    public string Press(IntPtr target, string key, int holdMs)
    {
        var k = KeySpec.Parse(key);
        bool d = Down(target, k); int e1 = Marshal.GetLastWin32Error();
        Thread.Sleep(holdMs);
        bool u = Up(target, k);
        return $"PostMessage down={d} up={u}{(d ? "" : " err=" + e1)} key={k} lParamDown=0x{MakeLParam(k, false).ToInt64() & 0xFFFFFFFF:X8}{(AlsoSendChar ? (k.Char != null ? " +WM_CHAR" : " (WM_CHAR対象外のキー)") : "")}";
    }

    /// <summary>
    /// 同時押し・押しっぱなし: keys を順に押し、holdMs の間 repeatMs ごとに全キーの WM_KEYDOWN(repeat) を連送し、逆順に離す。
    /// </summary>
    public string Chord(IntPtr target, IReadOnlyList<KeySpec> keys, int holdMs, int repeatMs = 33, int gapMs = 30)
    {
        int posted = 0;
        foreach (var k in keys) { Down(target, k); posted++; Thread.Sleep(gapMs); }
        var until = DateTime.Now.AddMilliseconds(holdMs);
        int reps = 0;
        // 実機のオートリピートは最後に押したキーだけ繰り返すため、それに合わせる
        var last = keys[^1];
        Thread.Sleep(Math.Min(250, holdMs));
        while (DateTime.Now < until) { Down(target, last, repeat: true); reps++; Thread.Sleep(repeatMs); }
        for (int i = keys.Count - 1; i >= 0; i--) { Up(target, keys[i]); posted++; Thread.Sleep(gapMs); }
        return $"PostMessage chord [{string.Join("+", keys.Select(k => k.Name))}] hold={holdMs}ms repeat({last.Name})={reps}回 down/up={posted}{(AlsoSendChar ? " +WM_CHAR" : "")}";
    }
}

/// <summary>② ViGEmBus 経由の仮想 Xbox360(XInput) パッド。ViGEmBus 未導入なら利用不可として扱う。</summary>
public sealed class VigemBackend : IProbeInputBackend, IDisposable
{
    public string Id => "2_vigem_xinput";
    public string Label => "②仮想パッド(ViGEmBus/XInput)";
    private Nefarius.ViGEm.Client.ViGEmClient? _client;
    private Nefarius.ViGEm.Client.Targets.IXbox360Controller? _pad;
    private string _detail = "";

    public (bool, string) Availability()
    {
        if (_pad != null) return (true, _detail);
        try
        {
            _client = new Nefarius.ViGEm.Client.ViGEmClient();
            var pad = _client.CreateXbox360Controller();
            pad.AutoSubmitReport = false;
            pad.Connect();
            _pad = pad;
            Thread.Sleep(1500);
            _detail = "ViGEmBus 検出・仮想Xbox360パッド接続済み";
            return (true, _detail);
        }
        catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException)
        { return (false, "ViGEmBus ドライバが見つかりません（未導入）→ 利用不可としてスキップ"); }
        catch (Exception e) { return (false, $"ViGEm 初期化失敗: {e.GetType().Name}: {e.Message}"); }
    }

    public static Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button ParseButton(string name)
    {
        var map = new Dictionary<string, Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.A, ["B"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.B,
            ["X"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.X, ["Y"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.Y,
            ["Start"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.Start, ["Back"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.Back,
            ["Up"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.Up, ["Down"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.Down,
            ["Left"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.Left, ["Right"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.Right,
            ["LeftShoulder"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.LeftShoulder, ["RightShoulder"] = Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360Button.RightShoulder,
        };
        return map.TryGetValue(name.Trim(), out var b) ? b
            : throw new ArgumentException($"パッドボタン名を解釈できません: {name}（例: A, B, X, Y, Start, Back, Up, Down, Left, Right, LeftShoulder, RightShoulder）");
    }

    public string Press(IntPtr target, string button, int holdMs)
    {
        if (_pad == null) return "未接続";
        var b = ParseButton(button);
        _pad.SetButtonState(b, true); _pad.SubmitReport();
        Thread.Sleep(Math.Max(holdMs, 60));
        _pad.SetButtonState(b, false); _pad.SubmitReport();
        return $"XInput button={button} hold={holdMs}ms（パッドはシステム全体のデバイス）";
    }

    public void Dispose()
    {
        try { _pad?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
        _pad = null; _client = null;
    }
}

/// <summary>
/// ③ SendInput（OS全体の入力キューへ注入。前面ウィンドウに届く）。
/// v2: 仮想キーコード(VK)で送信（スキャンコードモードはNumLock状態でテンキーが別キーになるため不使用）。dwExtraInfo に署名。
/// </summary>
public sealed class SendInputBackend : IProbeInputBackend
{
    public string Id => "3_sendinput";
    public string Label => "③SendInput(OS全体, VK送信)";
    public IntPtr RequiredForeground { get; set; }
    public (bool, string) Availability() => (true, "利用可能（前面ウィンドウにのみ届く・ユーザー操作と干渉する最終手段）");

    public string Press(IntPtr target, string key, int holdMs)
    {
        var k = KeySpec.Parse(key);
        bool numLock = Native.NumLockOn;
        if (RequiredForeground != IntPtr.Zero && Native.GetForegroundWindow() != RequiredForeground)
            return "中止: 前面ウィンドウが想定と異なるため送信しませんでした（誤入力防止）";
        uint flags = k.Extended ? Native.KEYEVENTF_EXTENDEDKEY : 0;
        var down = new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new() { ki = new() { wVk = k.Vk, wScan = k.Scan, dwFlags = flags, dwExtraInfo = Native.InjectSignature } } };
        uint r1 = Native.SendInput(1, new[] { down }, Marshal.SizeOf<Native.INPUT>());
        Thread.Sleep(holdMs);
        var up = down; up.U.ki.dwFlags |= Native.KEYEVENTF_KEYUP;
        uint r2 = Native.SendInput(1, new[] { up }, Marshal.SizeOf<Native.INPUT>());
        return $"SendInput down={r1} up={r2} key={k} (VKモード) NumLock={(numLock ? "ON" : "OFF")}(参考値)";
    }

    public static uint InjectHarmless()
    {
        var k = new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new() { ki = new() { wVk = 0x87 /*F24*/, dwExtraInfo = Native.InjectSignature } } };
        var u = k; u.U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
        return Native.SendInput(2, new[] { k, u }, Marshal.SizeOf<Native.INPUT>());
    }
}
