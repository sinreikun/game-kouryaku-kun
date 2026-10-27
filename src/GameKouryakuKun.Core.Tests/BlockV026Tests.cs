using GameKouryakuKun.Core.Control;
using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Input;
using GameKouryakuKun.Core.Profile;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.26.0: 操作の集合は Profile の単独キー＋chords だけ（37 件）。ランダムな同時押しはない</summary>
public class BlockV026Tests
{
    private static string ShippedText() => File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), "profiles/torneko-remaster/profile.json"));
    private static GameProfile Shipped() => GameProfileLoader.Parse(ShippedText(), "shipped");

    [Fact]
    public void Shipped_profile_gives_exactly_37_actions_with_the_expected_keys()
    {
        var p = Shipped();
        var acts = Explorer.FromProfile(p.KeyMap().All.Keys, p.Input.Chords).Where(a => !a.IsWait).ToList();
        Assert.Equal(37, acts.Count);
        Assert.Equal(13, acts.Count(a => a.Group != ActionGroup.Chord));
        Assert.Equal(24, acts.Count(a => a.Group == ActionGroup.Chord));
        var phys = p.KeyMap().All.Values.Select(k => k.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "A", "D", "Num0", "Num1", "Num2", "Num3", "Num4", "Num5", "Num6", "Num7", "S", "Tab", "W" }.OrderBy(x => x), phys);
        Assert.Equal(4, acts.Count(a => a.Keys.Count() == 2 && a.Keys.All(k => k.StartsWith("dpad_"))));
        Assert.Equal(8, acts.Count(a => a.Group == ActionGroup.Chord && a.Keys.Contains("button5")));
        Assert.Equal(8, acts.Count(a => a.Group == ActionGroup.Chord && a.Keys.Contains("button2")));
        Assert.Equal(4, acts.Count(a => a.Keys.Contains("button3") && a.Keys.Count() == 3));
        Assert.Contains(acts, a => a.Name == "button5+dpad_left+dpad_up");
        Assert.DoesNotContain(p.Input.ForbiddenKeys, k => k.Name == "Tab");
        Assert.Equal("Tab", p.KeyMap().Resolve("button9").Name);
        Assert.Equal(0x09, p.KeyMap().Resolve("button9").Vk);
        for (int i = 0; i <= 7; i++) Assert.Contains(p.KeyMap().All.Values, k => k.Vk == 0x60 + i);   // VK_NUMPAD0〜7
    }

    [Fact]
    public void Startup_action_table_plans_three_key_chords_as_simultaneous_press()
    {
        var rows = ActionTable.Build(Shipped());
        Assert.Equal(37, rows.Count);
        Assert.All(rows, r => Assert.True(r.Ok, r.Action + " " + r.Problem));
        var r3 = rows.Single(r => r.Action == "button3+dpad_down+dpad_right");
        Assert.Equal(6, r3.Events);
        var p = Shipped();
        var plan = InputPlanner.Plan(new[] { "button5", "dpad_up", "dpad_left" }.Select(p.KeyMap().Resolve).ToList(), 80, PressMode.Tap);
        Assert.Equal(new[] { KeyEventType.Down, KeyEventType.Down, KeyEventType.Down, KeyEventType.Up, KeyEventType.Up, KeyEventType.Up }, plan.Select(e => e.Type));
        Assert.True(plan[2].AtMs < plan[3].AtMs);   // 3 キーが同時に押されている時間がある
    }

    [Fact]
    public void Chord_validation_rejects_unknown_forbidden_or_too_long_chords()
    {
        string P(string chords) => ShippedText().Replace("\"chords\": [", "\"chords\": [" + chords + ",");
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(P("\"button5+button10\""), "x"));
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(P("\"button5+dpad_up+dpad_left+button2\""), "x"));
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(P("\"dpad_left+dpad_up\""), "x"));   // 重複
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(P("\"dpad_up\""), "x"));
    }

    [Fact]
    public void Old_shipped_user_profile_is_upgraded_with_backup_and_custom_profile_keeps_keys()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "profile.json");
        var oldText = ShippedText().Replace("\"profile_version\": \"0.3.0\"", "\"profile_version\": \"0.2.0\"")
            .Replace(",\n      \"button9\": \"Tab\"", "").Replace("\"Escape\", \"F12\", \"Alt\"]", "\"Escape\", \"F12\", \"Alt\", \"Tab\"]");
        int a = oldText.IndexOf("    \"chords\""), b = oldText.IndexOf("    // OS・ウィンドウ");
        oldText = oldText[..a] + oldText[b..];
        File.WriteAllText(path, oldText);
        Assert.Empty(GameProfileLoader.Parse(oldText, "old").Input.Chords);
        var r = ProfileUpgrade.Migrate(path, ShippedText());
        Assert.True(r.Upgraded); Assert.Equal("replaced_with_shipped", r.Mode); Assert.True(File.Exists(r.BackupPath));
        Assert.Equal(24, GameProfileLoader.Load(path).Input.Chords.Count);
        Assert.False(ProfileUpgrade.Migrate(path, ShippedText()).Upgraded);   // 2 回目は何もしない

        File.WriteAllText(path, oldText.Replace("\"button7\": \"Num7\"", "\"button7\": \"Num9\""));
        var r2 = ProfileUpgrade.Migrate(path, ShippedText());
        Assert.Equal("chords_added_keys_kept", r2.Mode);
        var p2 = GameProfileLoader.Load(path);
        Assert.Equal("Num9", p2.KeyMap().Resolve("button7").Name);
        Assert.Equal(24, p2.Input.Chords.Count);
    }

    [Fact]
    public void Old_chords_outside_the_set_in_saved_records_are_ignored_but_matching_names_keep_learning()
    {
        var p = Shipped();
        var ex = new Explorer(Explorer.FromProfile(p.KeyMap().All.Keys, p.Input.Chords), 1);
        ex.Load(new[] { ("m1", "button1+button4", "m2", 5), ("m1", "dpad_left+dpad_up", "m3", 4), ("m1", "button2+dpad_up", "m4", 2) });
        Assert.DoesNotContain(ex.Actions, a => a.Name == "button1+button4");
        Assert.Equal(37 + 1, ex.Actions.Count);   // 37 操作＋待つ
        var cands = ex.Choose("m1").Candidates;
        Assert.DoesNotContain(cands, c => c.Action == "button1+button4");
        Assert.Null(ex.AddSequence(new ActionSequenceCandidate { Id = 1, Start = "m1", Steps = new[] { "button1+button4", "button1" } }));
        for (int i = 0; i < 300; i++) Assert.NotEqual("chord_probe", ex.Choose("m1").Rule);
    }

    [Fact]
    public void Human_pad_layout_uses_only_allowed_actions()
    {
        var p = Shipped();
        var allowed = Explorer.FromProfile(p.KeyMap().All.Keys, p.Input.Chords).Select(a => a.Name).ToHashSet();
        var phys = p.KeyMap().All.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        Assert.Equal(new[] { "button2", "button3", "button5" }, HumanPad.Modifiers(allowed));
        Assert.All(HumanPad.DirectionCells(phys, allowed, null), c => Assert.NotEqual("", c.Action));
        Assert.All(HumanPad.DirectionCells(phys, allowed, "button5"), c => Assert.NotEqual("", c.Action));
        var n3 = HumanPad.DirectionCells(phys, allowed, "button3");
        Assert.Equal(4, n3.Count(c => c.Action != ""));   // Num3 は斜めだけ
        Assert.Equal("Num3+W+A", n3.Single(c => c.Row == 0 && c.Col == 0).Label);
        var (num, others) = HumanPad.ButtonCells(phys, allowed);
        Assert.Equal(8, num.Count);
        Assert.Equal((0, 0), num.Where(c => c.Label == "Num7").Select(c => (c.Row, c.Col)).Single());
        Assert.Equal((3, 2), num.Where(c => c.Label == "Num0").Select(c => (c.Row, c.ColSpan)).Single());
        Assert.Equal("Tab", others.Single().Label);
    }
}
