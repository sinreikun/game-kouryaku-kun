using GameKouryakuKun.Core.Profile;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ProfileTests
{
    private static string TornekoPath => Path.Combine(AppContext.BaseDirectory, "profiles", "torneko-remaster", "profile.json");

    private const string Minimal = """
    { "schema_version": 2, "profile_id": "x", "profile_version": "0.0.1", "display_name": "x",
      "window": { "process_name": "Game" },
      "input": { "keys": { "dpad_up": "W", "button1": { "key": "Num0" } } } }
    """;

    [Fact]
    public void Torneko_profile_loads_with_user_key_settings()
    {
        var p = GameProfileLoader.Load(TornekoPath);
        Assert.Equal("torneko-remaster", p.ProfileId);
        Assert.Equal("TornekosMysteryDungeon-Win64-Shipping", p.Window.ProcessName);
        Assert.Equal("UnrealWindow", p.Window.ClassName);
        Assert.Equal(80, p.Input.DefaultHoldMs);
        var expect = new Dictionary<string, (int vk, int scan)>
        {
            ["dpad_up"] = (0x57, 0x11), ["dpad_left"] = (0x41, 0x1E), ["dpad_down"] = (0x53, 0x1F), ["dpad_right"] = (0x44, 0x20),
            ["button1"] = (0x61, 0x4F), ["button2"] = (0x62, 0x50), ["button3"] = (0x63, 0x51), ["button4"] = (0x64, 0x4B),
            ["button5"] = (0x65, 0x4C), ["button6"] = (0x66, 0x4D), ["button7"] = (0x67, 0x47), ["button8"] = (0x60, 0x52), ["button9"] = (0x09, 0x0F),
        };
        Assert.Equal(expect.Count, p.Input.Keys.Count);
        foreach (var (k, v) in expect)
        {
            Assert.Equal(v.vk, p.Input.Keys[k].Key.Vk);
            Assert.Equal(v.scan, p.Input.Keys[k].Key.Scan);
            Assert.False(p.Input.Keys[k].Key.Extended);
        }
        Assert.Contains(p.Input.ForbiddenKeys, k => k.Name == "Escape");
        Assert.True(p.Window.Matches("TornekosMysteryDungeon-Win64-Shipping.exe", "UnrealWindow", "何でも"));
        Assert.False(p.Window.Matches("notepad", "UnrealWindow", ""));
        Assert.DoesNotContain("diagnostics", File.ReadAllText(Path.Combine(TestPaths.SrcRoot(), "profiles/torneko-remaster/profile.json")));
    }

    [Fact]
    public void Bundled_profile_has_only_neutral_names_and_no_meaning()
    {
        var text = File.ReadAllText(TornekoPath);
        Assert.DoesNotContain("\"label\"", text);
        var p = GameProfileLoader.Load(TornekoPath);
        Assert.All(p.Input.Keys.Keys, k => Assert.Matches("^(dpad_(up|down|left|right)|button([1-9]|[12][0-9]|3[0-2]))$", k));
    }

    [Theory]
    [InlineData("menu")]
    [InlineData("confirm")]
    [InlineData("up")]
    [InlineData("button0")]
    [InlineData("button33")]
    [InlineData("dpad_upleft")]
    public void Semantic_or_out_of_range_logical_names_are_rejected(string name)
    {
        var e = Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(Minimal.Replace("\"dpad_up\":", $"\"{name}\":")));
        Assert.Contains(e.Errors, x => x.Contains("中立の名前"));
    }

    [Fact]
    public void Labels_cannot_be_written_any_more()
    {
        var e = Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(Minimal.Replace("{ \"key\": \"Num0\" }", "{ \"key\": \"Num0\", \"label\": \"x\" }")));
        Assert.Contains(e.Errors, x => x.Contains("未知のキー"));
    }

    [Fact]
    public void Schema_v1_is_rejected_with_migration_hint()
    {
        var e = Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(Minimal.Replace("\"schema_version\": 2", "\"schema_version\": 1")));
        Assert.Contains(e.Errors, x => x.Contains("v2"));
    }

    [Fact]
    public void Diagnostics_known_operation_section_is_rejected()
    {
        // v0.15.0: 「このキーで変わって、このキーで戻る」という特定の状態の性質を Profile に書かせない
        var bad = Minimal.TrimEnd().TrimEnd('}') + ", \"diagnostics\": { \"toggle\": [\"button1\", \"dpad_up\"] } }";
        Assert.ThrowsAny<Exception>(() => GameProfileLoader.Parse(bad));
    }

    [Fact]
    public void Minimal_profile_loads() => Assert.Equal(2, GameProfileLoader.Parse(Minimal).Input.Keys.Count);

    [Theory]
    [InlineData("\"targets\": [{\"name\":\"a\",\"value\":5}],")]
    [InlineData("\"items\": {},")]
    [InlineData("\"strategy\": \"run away\",")]
    [InlineData("\"description\": \"x を見つけたら y する\",")]
    public void Unknown_top_level_keys_such_as_strategy_knowledge_are_rejected(string extra)
    {
        var json = Minimal.Replace("{ \"schema_version\"", "{ " + extra + " \"schema_version\"");
        var e = Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(json));
        Assert.Contains(e.Errors, x => x.Contains("未知のキー"));
    }

    [Fact]
    public void Unknown_nested_keys_are_rejected()
    {
        var json = Minimal.Replace("{ \"key\": \"Num0\" }", "{ \"key\": \"Num0\", \"effect\": \"x\" }");
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(json));
        var json2 = Minimal.Replace("\"process_name\": \"Game\"", "\"process_name\": \"Game\", \"memory_offset\": 1234");
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(json2));
    }

    [Fact]
    public void Ambiguous_digit_key_is_rejected()
    {
        var e = Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(Minimal.Replace("\"Num0\"", "\"0\"")));
        Assert.Contains(e.Errors, x => x.Contains("テンキー"));
    }

    [Fact]
    public void Forbidden_key_cannot_be_bound()
    {
        var json = Minimal.Replace("\"dpad_up\": \"W\"", "\"dpad_up\": \"Escape\"").Replace("\"keys\":", "\"forbidden_keys\": [\"Escape\"], \"keys\":");
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(json));
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(Minimal.Replace("\"dpad_up\": \"W\"", "\"dpad_up\": \"LWin\"")));
    }

    [Fact]
    public void Same_physical_key_twice_is_rejected() =>
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(Minimal.Replace("\"Num0\"", "\"W\"")));

    [Fact]
    public void Missing_required_and_bad_values()
    {
        var e = Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse("{ \"schema_version\": 1, \"input\": { \"default_hold_ms\": 5, \"keys\": {} } }"));
        Assert.Contains(e.Errors, x => x.Contains("profile_id"));
        Assert.Contains(e.Errors, x => x.Contains("window"));
        Assert.Contains(e.Errors, x => x.Contains("schema_version"));
        Assert.Contains(e.Errors, x => x.Contains("default_hold_ms"));
        Assert.Contains(e.Errors, x => x.Contains("keys が空"));
    }

    [Fact]
    public void Bad_logical_id_is_rejected() =>
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse(Minimal.Replace("\"dpad_up\":", "\"Up Key\":")));

    [Fact]
    public void Invalid_json_is_reported() =>
        Assert.Throws<ProfileValidationException>(() => GameProfileLoader.Parse("{ not json"));
}
