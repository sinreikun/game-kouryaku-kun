using GameKouryakuKun.Core.Ui;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class BlockV0302PanelOrderTests
{
    private static readonly string[] D = { "1手サマリー", "学習の成長", "目的", "結果" };

    [Fact]
    public void Order_persists_through_settings_and_reset_returns_default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gkk-po-" + Guid.NewGuid().ToString("N"));
        var s = UiSettings.Load(dir);
        var o = PanelOrder.Apply(D, s.GetValue(PanelOrder.Key));
        Assert.Equal(D, o);
        o = PanelOrder.Move(o, "結果", -1); o = PanelOrder.Move(o, "結果", -1);
        s.SetValue(PanelOrder.Key, PanelOrder.Serialize(o));
        var back = PanelOrder.Apply(D, UiSettings.Load(dir).GetValue(PanelOrder.Key));
        Assert.Equal(new[] { "1手サマリー", "結果", "学習の成長", "目的" }, back);
        s.SetValue(PanelOrder.Key, "");   // 既定に戻す
        Assert.Equal(D, PanelOrder.Apply(D, UiSettings.Load(dir).GetValue(PanelOrder.Key)));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Unknown_titles_are_dropped_new_ones_appended_and_edges_do_not_move()
    {
        Assert.Equal(new[] { "目的", "1手サマリー", "学習の成長", "結果" }, PanelOrder.Apply(D, "目的\n消えた欄\n目的"));
        Assert.Equal(D, PanelOrder.Move(D, "1手サマリー", -1));
        Assert.Equal(D, PanelOrder.Move(D, "結果", +1));
        Assert.Equal(D, PanelOrder.Move(D, "無い欄", 1));
    }
}
