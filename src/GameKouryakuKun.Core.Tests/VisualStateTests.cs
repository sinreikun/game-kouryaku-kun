using GameKouryakuKun.Core.Exploration;
using GameKouryakuKun.Core.Imaging;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>見た目の似かよりと遷移の特徴の似かよりを別々に持つ（v0.9.0。2026-10-18 10:49 のユーザー条件）</summary>
public class VisualStateTests
{
    private static float[] Sig(float baseV, int patch, float patchV)
    {
        var s = Enumerable.Repeat(baseV, VisualLayout.Length).ToArray();
        for (int c = patch; c < patch + 3; c++) for (int j = 0; j < 3; j++) s[c * 3 + j] = patchV;   // 48 区画のうち 3 区画だけ違う
        return s;
    }

    private static void Feed(VisualStateBook b, int v, string action, int outcome, int n, int other)
    {
        for (int i = 0; i < n; i++) b.ObserveTransition(v, action, outcome != 0, outcome == 2 ? other : v);
    }

    [Fact]
    public void Layout_signature_sees_a_moved_block_that_the_color_distribution_does_not()
    {
        const int W = 160, H = 120;
        byte[] Img(int bx) { var p = new byte[W * H * 4]; for (int y = 40; y < 70; y++) for (int x = bx; x < bx + 30; x++) { int k = (y * W + x) * 4; p[k] = p[k + 1] = p[k + 2] = 255; } return p; }
        var a = new Frame(1, W, H, Img(10), 0); var b = new Frame(2, W, H, Img(110), 0);
        Assert.True(ScreenSignature.Of(a).Distance(ScreenSignature.Of(b).Hist) < 0.02);
        Assert.True(VisualLayout.Distance(VisualLayout.Of(a), VisualLayout.Of(b)) > new VisualStateBook().InitialNewStateDistance);
    }

    [Fact]
    public void Looks_similar_but_transitions_differ_is_split_only_after_evidence()
    {
        var b = new VisualStateBook();
        var (v1, _) = b.Assign(Sig(0.3f, 5, 0.3f), "m1");
        var (v2, n2) = b.Assign(Sig(0.3f, 5, 1.0f), "m1");   // 区画が1つ違うだけ（見た目は似ている）、同じ粗いノード
        Assert.True(n2); Assert.NotEqual(v1, v2);
        // 証拠が少ないうち（入力ごとに 2 回）は分けない
        Feed(b, v1, "button1", 0, 2, 0); Feed(b, v1, "button2", 0, 2, 0);
        Feed(b, v2, "button1", 2, 2, 99); Feed(b, v2, "button2", 2, 2, 98);
        Assert.Empty(b.RefreshSplits());
        Assert.Equal("m1", b.Node("m1", v2));
        // 証拠がそろったら（v1 は何も起きない・v2 は別の見た目へ移る）、v2 を別のノードにする
        Feed(b, v1, "button1", 0, 4, 0); Feed(b, v1, "button2", 0, 4, 0);
        Feed(b, v2, "button1", 2, 2, 99); Feed(b, v2, "button2", 2, 2, 98);
        var made = Assert.Single(b.RefreshSplits());
        Assert.Equal(v2, made.Id);
        Assert.Equal($"m1#v{v2}", b.Node("m1", v2));
        Assert.Equal("m1", b.Node("m1", v1));
        Assert.Empty(b.RefreshSplits());   // 一度分けたものは繰り返さない
    }

    [Fact]
    public void Looks_different_but_transitions_similar_is_grouped_but_not_merged()
    {
        var b = new VisualStateBook();
        var (va, _) = b.Assign(Sig(0.1f, 0, 0.1f), "m1");
        var (vb, _) = b.Assign(Sig(0.9f, 0, 0.9f), "m2");   // 見た目がまったく違う
        foreach (var v in new[] { va, vb }) { Feed(b, v, "button1", 2, 5, 50); Feed(b, v, "dpad_up", 1, 5, 0); }
        var g = b.TransitionGroups();
        Assert.Equal(g[va], g[vb]);                        // 遷移の特徴は同じ群
        Assert.Equal("m1", b.Node("m1", va)); Assert.Equal("m2", b.Node("m2", vb));   // ノード（探索の単位）は別のまま
        Assert.Empty(b.RefreshSplits());
    }

    [Fact]
    public void Saved_state_round_trips()
    {
        var b = new VisualStateBook();
        var (v1, _) = b.Assign(Sig(0.3f, 5, 0.3f), "m1"); var (v2, _) = b.Assign(Sig(0.3f, 5, 1.0f), "m1");
        Feed(b, v1, "button1", 0, 6, 0); Feed(b, v1, "button2", 0, 6, 0); Feed(b, v1, "button1", 0, 6, 0);
        Feed(b, v2, "button1", 2, 6, 9); Feed(b, v2, "button2", 2, 6, 9);
        b.RefreshSplits();
        var c = new VisualStateBook();
        c.Load(b.States.Select(s => (s.Id, s.Prototype, s.Seen, s.DominantCoarse)),
               b.Fingerprints.SelectMany(kv => kv.Value.ByAction.Select(a => (kv.Key, a.Key, a.Value[0], a.Value[1], a.Value[2]))),
               b.Splits.Select(kv => (kv.Value.Coarse, kv.Key, kv.Value.Node, kv.Value.Distance, kv.Value.Leader)));
        Assert.Equal(b.Node("m1", v2), c.Node("m1", v2));
        Assert.Equal(b.TransitionGroups().Count, c.TransitionGroups().Count);
        Assert.Equal(v2, c.Assign(Sig(0.3f, 5, 1.0f), "m1").Id);
    }
}
