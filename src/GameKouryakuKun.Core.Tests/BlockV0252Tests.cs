using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Goals;
using GameKouryakuKun.Core.Ocr;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.25.2: 文字の観測を意味のない特徴にする・地域は自分の操作で確かに動ける関係で作る</summary>
public class BlockV0252Tests
{
    private static OcrResult R(params (string T, int X, int Y)[] ls) => new("t", ls.Select(l => new OcrLine(l.T, 0.9, new OcrBox(l.X, l.Y, 40, 10))).ToList(), 5, "");

    [Fact]
    public void Text_features_ignore_number_values_but_not_text_or_layout()
    {
        var a = TextFeatures.Of(R(("AB 15/15", 10, 10)), 320, 240);
        var b = TextFeatures.Of(R(("AB 7/15", 10, 10)), 320, 240);
        var c = TextFeatures.Of(R(("CD 7/15", 10, 10)), 320, 240);
        var d = TextFeatures.Of(R(("AB 7/15", 250, 200)), 320, 240);
        Assert.Equal(a.TextHash, b.TextHash);          // 数字の値だけの違いは同じ並び
        Assert.NotEqual(b.TextHash, c.TextHash);
        Assert.NotEqual(b.LayoutHash, d.LayoutHash);
        Assert.Equal(2, a.Numbers);
        Assert.False(TextFeatures.Of(R(), 320, 240).HasText);
    }

    [Fact]
    public void State_text_book_samples_a_fixed_number_and_round_trips()
    {
        var bk = new StateTextBook();
        for (int i = 0; i < 5; i++) { Assert.True(bk.Wants("s", false)); bk.Add("s", R(("AB", 1, 1)), 100, 100); }
        Assert.False(bk.Wants("s", false));
        Assert.True(bk.Wants("s", true));              // 区切りの候補は追加で読む
        bk.Add("e", R(), 100, 100);
        var b2 = new StateTextBook(); b2.ImportState(bk.ExportState());
        Assert.Equal(bk.Describe("s"), b2.Describe("s"));
        Assert.Contains("文字あり 5/5", b2.Describe("s"));
        Assert.Contains("文字あり 0/1", b2.Describe("e"));
    }

    [Fact]
    public void Ocr_code_does_not_interpret_words()
    {
        // 辞書・単語の照合を入れない（静的検査）: 特徴のコードにゲームの言葉の文字列リテラルがない
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "GameKouryakuKun.Core", "Ocr", "StateText.cs"));
        foreach (var w in new[] { "Contains(\"", "StartsWith(\"", "Regex" }) Assert.DoesNotContain(w, src);
    }

    [Fact]
    public void Plan_edges_give_finer_regions_than_all_transitions()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "Up", "Down" }).ToList();
        var ex = new Explorer(acts, 1); var m = new TransitionModel();
        var up = acts.First(a => a.Name == "Up"); var dn = acts.First(a => a.Name == "Down");
        long q = 0;
        void O(string f, ExploreAction a, string t) { ex.Observe(f, a, t, f != t); m.Observe(q++, f, a.Name, t, f != t); }
        for (int i = 0; i < 6; i++) { O("a", up, "b"); O("b", dn, "a"); }
        O("b", up, "c"); for (int i = 0; i < 5; i++) O("b", up, "b");   // b→c は 1 回だけ（確かでない）
        for (int i = 0; i < 6; i++) O("c", dn, "a");
        var coarse = new FrontierRegions(); coarse.Recompute(ex.Transitions().Select(t => (t.From, t.To)));
        var fine = new FrontierRegions(); fine.Recompute(new SelfGoals().PlanEdges(ex, m));
        Assert.Equal(coarse.RegionOf("a"), coarse.RegionOf("c"));
        Assert.NotEqual(fine.RegionOf("a"), fine.RegionOf("c"));
        Assert.Equal(fine.RegionOf("a"), fine.RegionOf("b"));
    }
}
