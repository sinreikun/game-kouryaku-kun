using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>App.xaml のダークテーマ配色が、文字と背景の全組み合わせでコントラスト比 4.5:1 以上か（実機で「白地に白文字」だった件の回帰テスト）</summary>
public class ThemeContrastTests
{
    // (文字色, 背景色, 用途) — App.xaml のテンプレートで実際に使う組み合わせ
    public static TheoryData<string, string, string> Pairs => new()
    {
        { "C.BtnFg", "C.BtnBg", "ボタン 通常" },
        { "C.FgStrong", "C.BtnHoverBg", "ボタン ホバー" },
        { "C.FgStrong", "C.BtnPressedBg", "ボタン 押下" },
        { "C.DisabledFg", "C.DisabledBg", "ボタン 無効" },
        { "C.FgStrong", "C.DangerBg", "緊急停止" },
        { "C.FgStrong", "C.DangerHoverBg", "緊急停止 ホバー" },
        { "C.DangerDisabledFg", "C.DangerDisabledBg", "緊急停止 無効" },
        { "C.BtnFg", "C.InputBg", "コンボ 本体" },
        { "C.DisabledFg", "C.DisabledBg", "コンボ 無効" },
        { "C.BtnFg", "C.Panel", "コンボ ドロップダウン項目" },
        { "C.FgStrong", "C.ItemHoverBg", "項目 ホバー" },
        { "C.FgStrong", "C.ItemSelectedBg", "項目 選択" },
        { "C.FgStrong", "C.ItemSelectedInactiveBg", "項目 選択（非アクティブ）" },
        { "C.Fg", "C.Well", "テキストボックス・ログ・一覧" },
        { "C.DisabledFg", "C.Bg", "テキストボックス 無効" },
        { "C.Fg", "C.Header", "ヘッダのチェックボックス" },
        { "C.DisabledFg", "C.Header", "ヘッダのチェックボックス 無効" },
        { "C.DisabledFg", "C.Bg", "チェックボックス 無効" },
        { "C.Fg", "C.Panel", "パネル本文・列見出し" },
        { "C.Dim", "C.Bg", "補足文字" },
        { "C.Dim", "C.Panel", "パネル内の補足文字" },
        { "C.Accent", "C.Panel", "パネル見出し" },
        { "C.BtnFg", "C.TooltipBg", "ツールチップ" },
        { "C.ModOnFg", "C.ModOnBg", "Human Control 同時押しのキー ON" },
        { "C.FgStrong", "C.ChordPadBg", "Human Control 同時押しになった方向" },
        { "C.PadOffFg", "C.PadOffBg", "Human Control 押せない方向" },
    };

    private static readonly Lazy<Dictionary<string, string>> Colors = new(() =>
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var xaml = XDocument.Load(Path.Combine(d!.FullName, "GameKouryakuKun.App", "App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return xaml.Descendants().Where(e => e.Name.LocalName == "Color" && e.Attribute(x + "Key") != null)
                   .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Value.Trim());
    });

    private static double Lum(string hex)
    {
        hex = hex.TrimStart('#'); if (hex.Length == 8) hex = hex[2..];
        double F(int i) { double c = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(0) + 0.7152 * F(2) + 0.0722 * F(4);
    }
    public static double Ratio(string a, string b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Text_and_background_have_at_least_4_5_contrast(string fg, string bg, string use)
    {
        var c = Colors.Value;
        Assert.True(c.ContainsKey(fg) && c.ContainsKey(bg), $"{use}: App.xaml に {fg} / {bg} がありません");
        double r = Ratio(c[fg], c[bg]);
        Assert.True(r >= 4.5, $"{use}: {fg} {c[fg]} on {bg} {c[bg]} = {r:0.00}:1（4.5 未満）");
    }

    [Fact]
    public void Disabled_buttons_look_different_from_enabled()
    {
        var c = Colors.Value;
        Assert.NotEqual(c["C.BtnBg"], c["C.DisabledBg"]);
        Assert.NotEqual(c["C.BtnFg"], c["C.DisabledFg"]);
        Assert.True(Ratio(c["C.BtnFg"], c["C.DisabledFg"]) >= 1.5, "無効の文字色が通常とほぼ同じ");
    }

    [Fact]
    public void Every_button_combo_checkbox_textbox_has_an_explicit_template()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameKouryakuKun.sln"))) d = d.Parent;
        var text = File.ReadAllText(Path.Combine(d!.FullName, "GameKouryakuKun.App", "App.xaml"));
        foreach (var t in new[] { "Button", "CheckBox", "TextBox", "ComboBox", "ComboBoxItem", "ListBoxItem", "ListViewItem", "GridViewColumnHeader", "ProgressBar", "ToolTip" })
            Assert.Contains($"<ControlTemplate TargetType=\"{t}\">", text);
    }
}
