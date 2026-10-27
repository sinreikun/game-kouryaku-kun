using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Input;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class KeySpecTests
{
    [Theory]
    [InlineData("Num0", 0x60, 0x52)] [InlineData("Num1", 0x61, 0x4F)] [InlineData("Num2", 0x62, 0x50)] [InlineData("Num3", 0x63, 0x51)]
    [InlineData("Num4", 0x64, 0x4B)] [InlineData("Num5", 0x65, 0x4C)] [InlineData("Num6", 0x66, 0x4D)] [InlineData("Num7", 0x67, 0x47)]
    [InlineData("Num8", 0x68, 0x48)] [InlineData("Num9", 0x69, 0x49)] [InlineData("テンキー0", 0x60, 0x52)] [InlineData("NumPad5", 0x65, 0x4C)]
    public void Numpad_has_correct_vk_and_scan_and_is_not_extended(string name, int vk, int scan)
    {
        var k = KeySpec.Parse(name);
        Assert.Equal(vk, k.Vk); Assert.Equal(scan, k.Scan); Assert.False(k.Extended); Assert.True(k.IsNumpadDigit);
    }

    [Theory]
    [InlineData("W", 0x57, 0x11)] [InlineData("A", 0x41, 0x1E)] [InlineData("S", 0x53, 0x1F)] [InlineData("D", 0x44, 0x20)]
    [InlineData("d0", 0x30, 0x0B)] [InlineData("上段2", 0x32, 0x03)] [InlineData("F12", 0x7B, 0x58)] [InlineData("Escape", 0x1B, 0x01)]
    public void Letters_digits_function_keys(string name, int vk, int scan)
    {
        var k = KeySpec.Parse(name);
        Assert.Equal(vk, k.Vk); Assert.Equal(scan, k.Scan); Assert.False(k.Extended);
    }

    [Theory] [InlineData("Up")] [InlineData("Down")] [InlineData("Left")] [InlineData("Right")] [InlineData("Delete")] [InlineData("RCtrl")]
    public void Navigation_keys_are_extended(string name) => Assert.True(KeySpec.Parse(name).Extended);

    [Theory] [InlineData("0")] [InlineData("2")] [InlineData("２")]
    public void Single_digit_is_ambiguous(string s) => Assert.Throws<KeySpec.AmbiguousKeyException>(() => KeySpec.Parse(s));

    [Fact] public void Unknown_key_name_is_error() => Assert.Throws<FormatException>(() => KeySpec.Parse("Fooo"));

    [Fact]
    public void Combo_matches_modifiers_regardless_of_side()
    {
        var c = KeyCombo.Parse("Alt+F4");
        Assert.True(c.IsContainedIn(new[] { KeySpec.Parse("LAlt"), KeySpec.Parse("F4") }));
        Assert.False(c.IsContainedIn(new[] { KeySpec.Parse("F4") }));
    }
}

public class InputPlannerTests
{
    private static readonly KeySpec W = KeySpec.Parse("W"), D = KeySpec.Parse("D"), N3 = KeySpec.Parse("Num3");

    [Fact]
    public void Tap_is_one_down_and_one_up_without_repeat_even_if_long()
    {
        var p = InputPlanner.Plan(new[] { W }, 80, PressMode.Tap);
        Assert.Equal(2, p.Count);
        Assert.Equal((0.0, KeyEventType.Down), (p[0].AtMs, p[0].Type));
        Assert.Equal((80.0, KeyEventType.Up), (p[1].AtMs, p[1].Type));
        var longTap = InputPlanner.Plan(new[] { W }, 600, PressMode.Tap);
        Assert.DoesNotContain(longTap, e => e.Type == KeyEventType.Repeat);
    }

    [Fact]
    public void Chord_presses_in_order_and_releases_in_reverse()
    {
        var p = InputPlanner.Plan(new[] { N3, W, D }, 80, PressMode.Tap, new InputTiming { ChordGapMs = 15 });
        Assert.Equal(new[] { "Num3", "W", "D", "D", "W", "Num3" }, p.Select(e => e.Key.Name));
        Assert.Equal(new[] { 0.0, 15, 30, 110, 125, 140 }, p.Select(e => e.AtMs));
        Assert.Equal(3, p.Count(e => e.Type == KeyEventType.Down));
    }

    [Fact]
    public void Hold_mode_repeats_last_key_after_delay()
    {
        var p = InputPlanner.Plan(new[] { D }, 1200, PressMode.Hold, new InputTiming { RepeatDelayMs = 250, RepeatIntervalMs = 33 });
        var reps = p.Where(e => e.Type == KeyEventType.Repeat).ToList();
        Assert.Equal(29, reps.Count); // 250,283,...,<1200
        Assert.All(reps, r => Assert.Equal("D", r.Key.Name));
        Assert.Equal(KeyEventType.Up, p[^1].Type);
    }

    [Fact]
    public void Duplicate_keys_rejected() => Assert.Throws<ArgumentException>(() => InputPlanner.Plan(new[] { W, W }, 80, PressMode.Tap));

    [Fact]
    public void LParam_bits()
    {
        var n1 = KeySpec.Parse("Num1");
        Assert.Equal(0x004F0001L, InputPlanner.MakeKeyLParam(n1, KeyEventType.Down));
        Assert.Equal(0x404F0001L, InputPlanner.MakeKeyLParam(n1, KeyEventType.Repeat));
        Assert.Equal(0xC04F0001L, InputPlanner.MakeKeyLParam(n1, KeyEventType.Up));
        Assert.Equal(0x01480001L, InputPlanner.MakeKeyLParam(KeySpec.Parse("Up"), KeyEventType.Down));
    }
}

public class ForbiddenKeyGuardTests
{
    [Fact]
    public void Always_forbidden_combos_and_keys()
    {
        var g = new ForbiddenKeyGuard();
        Assert.NotNull(g.Check(new[] { KeySpec.Parse("LAlt"), KeySpec.Parse("F4") }));
        Assert.NotNull(g.Check(new[] { KeySpec.Parse("LWin") }));
        Assert.NotNull(g.Check(new[] { KeySpec.Parse("Shift"), KeySpec.Parse("Tab") }));
        Assert.Null(g.Check(new[] { KeySpec.Parse("W"), KeySpec.Parse("D") }));
    }

    [Fact]
    public void Profile_forbidden_keys()
    {
        var g = new ForbiddenKeyGuard(new[] { KeySpec.Parse("Escape"), KeySpec.Parse("Alt") });
        Assert.NotNull(g.Check(new[] { KeySpec.Parse("Escape") }));
        Assert.NotNull(g.Check(new[] { KeySpec.Parse("RAlt") }));
        Assert.Null(g.Check(new[] { KeySpec.Parse("Enter") }));
    }
}

/// <summary>PlannedInputBackend の動作（送出順・押下時間・禁止キー拒否・強制解放）を偽の送出先で確認</summary>
public class PlannedInputBackendTests
{
    private sealed class FakeSleeper : ISleeper
    {
        private readonly ManualClock _c; public FakeSleeper(ManualClock c) => _c = c;
        public void SleepUntil(double t) { if (t > _c.NowMs) _c.NowMs = t; }
    }
    private sealed class FakeBackend : PlannedInputBackend
    {
        public readonly List<(double At, PlannedKeyEvent E)> Sent = new();
        public Func<PlannedKeyEvent, bool> Result = _ => true;
        public string? Preflight;
        public FakeBackend(ManualClock c) : base(c, new FakeSleeper(c)) { }
        public override string Id => "fake";
        public override string DisplayName => "fake";
        public override IsolationInfo Isolation => new(IsolationLevel.Isolated, false, false, false, null);
        public override InputProbeResult Probe(nint target) => new(true, "");
        protected override string? PreflightCheck() => Preflight;
        protected override bool Emit(PlannedKeyEvent e) { Sent.Add((Clock.NowMs, e)); return Result(e); }
    }

    private static (FakeBackend, ManualClock) Make()
    {
        var c = new ManualClock { NowMs = 1000 };
        var b = new FakeBackend(c);
        var map = new LogicalKeyMap(new Dictionary<string, KeySpec> { ["dpad_up"] = KeySpec.Parse("W"), ["dpad_right"] = KeySpec.Parse("D"), ["button8"] = KeySpec.Parse("Num0"), ["bad"] = KeySpec.Parse("LWin") });
        b.Bind(1, map, new ForbiddenKeyGuard(), 80);
        return (b, c);
    }

    [Fact]
    public void Default_tap_is_80ms()
    {
        var (b, _) = Make();
        var r = b.Send(InputAction.Tap("button8"));
        Assert.True(r.Ok);
        Assert.Equal(2, b.Sent.Count);
        Assert.Equal(80, b.Sent[1].At - b.Sent[0].At);
        Assert.Equal(1000, r.DownAtMs);
        Assert.Empty(b.KeysDown);
    }

    [Fact]
    public void Chord_sends_both_keys()
    {
        var (b, _) = Make();
        var r = b.Send(InputAction.Tap("dpad_up", "dpad_right"));
        Assert.True(r.Ok);
        Assert.Equal(new[] { "W", "D", "D", "W" }, b.Sent.Select(s => s.E.Key.Name));
    }

    [Fact]
    public void Forbidden_and_unknown_keys_are_rejected_without_sending()
    {
        var (b, _) = Make();
        Assert.False(b.Send(InputAction.Tap("bad")).Ok);
        Assert.False(b.Send(InputAction.Tap("nope")).Ok);
        Assert.Empty(b.Sent);
    }

    [Fact]
    public void Preflight_failure_rejects()
    {
        var (b, _) = Make();
        b.Preflight = "前面がゲームではない";
        var r = b.Send(InputAction.Tap("button8"));
        Assert.False(r.Ok); Assert.Contains("前面", r.Detail); Assert.Empty(b.Sent);
    }

    [Fact]
    public void Keys_are_released_even_if_up_fails()
    {
        var (b, _) = Make();
        b.Result = e => e.Type != KeyEventType.Up || b.Sent.Count(s => s.E.Type == KeyEventType.Up) > 1;
        var r = b.Send(InputAction.Tap("button8"));
        Assert.False(r.Ok);
        Assert.Empty(b.KeysDown);
    }
}
