using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Observation;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.32.2: 関係の表を相手ごとに数える・遅れて来た変化（P-7 temporal.delayed）</summary>
public class BlockV0322DelayedTests
{
    private const int W = 320, H = 240;
    private static Frame F(double ts, bool box)
    {
        var b = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { b[i * 4] = 40; b[i * 4 + 1] = 40; b[i * 4 + 2] = 40; b[i * 4 + 3] = 255; }
        if (box) for (int y = 100; y < 140; y++) for (int x = 100; x < 140; x++) { int i = (y * W + x) * 4; b[i] = 20; b[i + 1] = 20; b[i + 2] = 220; }
        return new Frame(Frame.NextId(), W, H, b, ts);
    }
    private static EntityCandidate C(int track) =>
        new(0, track, 1, 1, 3, 3, 2, 2, 9, "a", "A", 0, 0, false, false, false, 1, 3) { Continuous = true, IdentityPosterior = 5 };

    [Fact]
    public void Region_changing_one_second_after_the_action_is_recorded_as_delayed()
    {
        var d = DelayedChange.Of(F(1000, false), F(2000, true), 7, true);
        Assert.NotNull(d);
        Assert.Equal(7, d!.FromStep); Assert.Equal(1000, d.AfterMs); Assert.True(d.Cells > 0); Assert.True(d.Late);
        Assert.Null(DelayedChange.Of(F(1000, false), F(2000, false), 7, false));
        var t = new TemporalLayer(500, false, null, LayerStatus.Observed) { Delayed = d };
        Assert.Contains("\"delayed\"", System.Text.Json.JsonSerializer.Serialize(t, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }));
    }

    [Fact]
    public void Relation_is_predicted_per_partner_even_when_the_nearest_partner_alternates()
    {
        var e = new EntityExperience();
        for (int i = 0; i < 40; i++)
        {
            bool near2 = i % 2 == 0;
            var rel = new[] {
                new RelationObs(1, 2, 0, 0, near2 ? 1 : 5, "approach", false, false, false),
                new RelationObs(1, 3, 0, 0, near2 ? 5 : 1, "recede", false, false, false),
                new RelationObs(1, 4, 0, 0, 9, "new", false, false, false) };
            var c = new[] { C(1), C(2), C(3) };
            e.Observe(new EntityLayer(LayerStatus.Observed, c, Array.Empty<int>(), Array.Empty<RelationObs>()), new EntityLayer(LayerStatus.Observed, c, Array.Empty<int>(), rel), "x", false, false);
        }
        Assert.Equal("approach", e.Predict(2, 1, "#2", "x"));
        Assert.Equal("recede", e.Predict(2, 1, "#3", "x"));
        Assert.Null(e.Predict(2, 1, "#4", "x"));
        Assert.True(e.Hits[2, 0] >= e.Predicted[2, 0] - 4, e.Summary());
    }
}
