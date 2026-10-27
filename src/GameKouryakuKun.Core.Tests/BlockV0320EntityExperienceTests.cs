using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.32.0: 追跡の評価（大きさ別）と Entity ごとの経験（P-6）</summary>
public class BlockV0320EntityExperienceTests
{
    private const int W = 320, H = 240;
    private static long _id;
    private static Frame F(params (int X, int Y, int S, bool Alt)[] sq)
    {
        var b = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { b[i * 4] = 40; b[i * 4 + 1] = 40; b[i * 4 + 2] = 40; b[i * 4 + 3] = 255; }
        foreach (var s in sq)
            for (int y = s.Y * 10; y < (s.Y + s.S) * 10; y++) for (int x = s.X * 10; x < (s.X + s.S) * 10; x++)
            { int i = (y * W + x) * 4; bool a = s.Alt && x >= s.X * 10 + s.S * 5; b[i] = (byte)(a ? 220 : 20); b[i + 1] = 20; b[i + 2] = (byte)(a ? 20 : 220); }
        return new Frame(++_id, W, H, b, 0);
    }

    [Fact]
    public void Moving_object_is_counted_as_kept_in_its_size_bin_without_suspected_splits()
    {
        var t = new EntityTracker(); t.Update(FineGrid.Of(F()));
        for (int k = 0; k < 12; k++) t.Update(FineGrid.Of(F((2 + k, 10, 3, k % 2 == 1))));
        int b = TrackQuality.Bin(9);
        Assert.True(t.Quality.StrongPred[b] >= 8, t.Quality.Summary());
        Assert.True(t.Quality.Kept[b] >= t.Quality.StrongPred[b] - 1, t.Quality.Summary());
        Assert.Equal(0, t.Quality.SuspectedSplit[b]);
        Assert.Contains("1-2 区画", t.Quality.Summary());
    }

    [Fact]
    public void Lost_and_returned_tracks_are_counted_for_reidentification_rate()
    {
        var t = new EntityTracker(); t.Update(FineGrid.Of(F()));
        for (int k = 0; k < 8; k++) t.Update(FineGrid.Of(F((4 + k, 10, 3, false))));
        t.Update(FineGrid.Of(F()));
        t.Update(FineGrid.Of(F((13, 10, 3, false))));
        int b = TrackQuality.Bin(9);
        Assert.True(t.Quality.Lost[b] >= 1 && t.Quality.Returned[b] >= 1, t.Quality.Summary());
    }

    private static EntityCandidate C(int track, string app, bool stable, bool moved = false, bool isNew = false) =>
        new(0, track, 1, 1, 3, 3, 2, 2, 9, app, "A", moved ? 1 : 0, 0, moved, false, isNew, 1, 3) { Continuous = stable, IdentityPosterior = stable ? 5 : -1 };
    private static EntityLayer L(params EntityCandidate[] c) => new(LayerStatus.Observed, c, Array.Empty<int>(), Array.Empty<RelationObs>());

    [Fact]
    public void Per_entity_appearance_after_action_is_predicted_and_split_by_identity_stability()
    {
        var e = new EntityExperience(); var r = new Random(5);
        for (int i = 0; i < 80; i++)
        {
            string act = r.Next(2) == 0 ? "x" : "y"; bool stable = i % 4 != 0;
            e.Observe(L(C(7, "a", true)), L(C(7, act == "x" ? "b" : "c", stable)), act, false, false);
        }
        Assert.Equal("b", e.Predict(0, 7, "a", "x")); Assert.Equal("c", e.Predict(0, 7, "a", "y"));
        Assert.True(e.Predicted[0, 0] > 40 && e.Predicted[0, 1] > 10);
        Assert.True(e.Hits[0, 0] >= e.Predicted[0, 0] - 2);
        Assert.Contains("安定した番号", e.Summary());
    }

    [Fact]
    public void Entities_are_kept_apart_and_new_tracks_are_not_counted()
    {
        var e = new EntityExperience();
        for (int i = 0; i < 10; i++) e.Observe(L(C(1, "a", true), C(2, "p", true)), L(C(1, "b", true, moved: true), C(2, "p", true)), "x", false, true);
        Assert.Equal("b", e.Predict(0, 1, "a", "x")); Assert.Equal("p", e.Predict(0, 2, "p", "x"));
        Assert.Equal("1,0", e.Predict(1, 1, "0,0", "x")); Assert.Equal("0,0", e.Predict(1, 2, "0,0", "x"));
        Assert.Equal("-n", e.Predict(3, 1, "moved", "x"));
        long before = e.Observed;
        e.Observe(L(C(1, "a", true)), L(C(9, "z", false, isNew: true)), "x", false, false);
        Assert.Equal(before, e.Observed);
    }

    private static EntityCandidate M(int track, int dx) => new(0, track, 1, 1, 3, 3, 2, 2, 9, "a", "A", dx, 0, dx != 0, false, false, 1, 3) { Continuous = true, IdentityPosterior = 5 };

    [Fact]
    public void B_moving_the_same_way_one_step_after_A_is_recorded_as_follow()   // v0.32.1
    {
        var e = new EntityExperience(); var r = new Random(9); bool aPrev = false; EntityLayer? prev = null;
        for (int i = 0; i < 120; i++)
        {
            bool aNow = r.Next(2) == 0;
            var cur = L(M(1, aNow ? 1 : 0), M(2, aPrev ? 1 : 0), M(3, r.Next(2) == 0 ? 1 : 0));   // B(2) は A(1) の 1 手後に同じ向き。C(3) は無関係
            e.Observe(prev, cur, "x", false, false); prev = cur; aPrev = aNow;
        }
        var f = e.Follows();
        Assert.Contains(f, x => x.A == 1 && x.B == 2);
        Assert.DoesNotContain(f, x => x.B == 3 || x.A == 3);
    }
}
