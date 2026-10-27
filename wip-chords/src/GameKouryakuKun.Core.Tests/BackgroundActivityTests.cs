using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Turn;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>v0.17.0: 位置の一定しない動き（さまよう小領域）が続く画面で、ふだんの動きの量を学んで時間切れを減らす。合成データだけ</summary>
public class BackgroundActivityTests
{
    private static readonly Canvas Base = new(320, 200);
    // 40x30 の領域が画面の中をさまよう（位置が毎フレーム変わるので、位置のマスクでは隠せない）
    private static Canvas Wander(Canvas c, int i) { int x = 10 + (i * 10) % 260, y = 10 + (i * 7) % 60; return c.Rect(x, y, 40, 30, 200, 60, 60); }
    private static TurnDetector Make() => new(new TurnDetectorOptions { NoChangeTimeoutMs = 600, StableMs = 200, MaxMs = 3000, NoFrameStillMs = 200 });

    private static TurnBoundary Turn(TurnDetector d, bool respond = true)
    {
        int i = 0;
        d.Begin(Wander(Base.Clone(), i++).ToFrame(0), 0, "button1");
        TurnBoundary? b = null;
        for (double t = 30; b == null && t < 6000; t += 30, i++)
        {
            var c = Base.Clone();
            if (respond && t >= 90) c.Rect(20, 130, 280, 60, 250, 250, 250);
            b = d.OnFrame(Wander(c, i).ToFrame(t));
        }
        return b!;
    }

    [Fact]
    public void Wandering_motion_times_out_until_background_level_is_learned_then_settles()
    {
        var d = Make(); var book = new BackgroundActivityBook(); var baseC = ChangeCriteria.Default;
        var b1 = Turn(d);
        Assert.Equal(TurnKind.Timeout, b1.Kind);
        Assert.NotNull(b1.LateStats); Assert.NotEmpty(b1.LateStats!);
        Assert.False(book.Observe("m1", b1.LateStats, baseC).Learned);   // 1 回では変えない
        Assert.Equal(baseC, book.CriteriaFor("m1", baseC));
        var b2 = Turn(d);
        var u = book.Observe("m1", b2.LateStats, baseC);
        Assert.True(u.Learned);
        d.Criteria = book.CriteriaFor("m1", baseC);
        Assert.True(d.Criteria.StillRatio > baseC.StillRatio && d.Criteria.StillRatio <= book.MaxStillRatio);
        var b3 = Turn(d);
        Assert.Equal(TurnKind.Normal, b3.Kind);
        Assert.True(b3.NetChange);                     // 入力による大きな変化はそのまま「変わった」
        Assert.Equal(baseC, book.CriteriaFor("m2", baseC));   // 別の画面には使わない
    }

    [Fact]
    public void Background_level_is_capped_so_large_changes_never_count_as_still()
    {
        var book = new BackgroundActivityBook(); var baseC = ChangeCriteria.Default;
        var big = Enumerable.Repeat((0.4, 0.3), 10).ToList();
        book.Observe("m1", big, baseC); var u = book.Observe("m1", big, baseC);
        Assert.True(u.Capped);
        var c = book.CriteriaFor("m1", baseC);
        Assert.Equal(book.MaxStillRatio, c.StillRatio); Assert.Equal(book.MaxStillComponent, c.StillComponent);
        Assert.Equal(baseC.ChangeRatio, c.ChangeRatio);   // 変化の判定は変えない
    }

    [Fact]
    public void Background_level_survives_restart_through_the_memory_db()
    {
        var book = new BackgroundActivityBook(); var baseC = ChangeCriteria.Default;
        var lv = Enumerable.Repeat((0.01, 0.012), 8).ToList();
        book.Observe("m4", lv, baseC); book.Observe("m4", lv, baseC);
        var dir = Path.Combine(Path.GetTempPath(), "gkk-bg-" + Guid.NewGuid().ToString("N"));
        using (var s = GameKouryakuKun.Storage.ExperienceStore.Open(dir, "p1", "0.1.0", "0.2.0")) s.SaveScreenBackground(book.Export("m4"));
        var book2 = new BackgroundActivityBook();
        using (var s = GameKouryakuKun.Storage.ExperienceStore.Open(dir, "p1", "0.1.0", "0.2.0")) Assert.Equal(1, book2.Import(s.LoadScreenBackground()));
        Assert.Equal(book.CriteriaFor("m4", baseC), book2.CriteriaFor("m4", baseC));
        Assert.True(book2.CriteriaFor("m4", baseC).StillRatio > baseC.StillRatio);   // 次の起動では 1 回目の手から使える
    }

    [Fact]
    public void Cap_widens_only_when_responses_are_clearly_larger_than_background_motion()
    {
        var book = new BackgroundActivityBook(); var baseC = ChangeCriteria.Default;
        var bg = Enumerable.Repeat((0.05, 0.05), 10).ToList();   // 画面の 5% が動き続ける
        book.Observe("m8", bg, baseC); book.Observe("m8", bg, baseC);
        Assert.Equal(book.MaxStillRatio, book.CriteriaFor("m8", baseC).StillRatio);   // 応答の大きさが分かるまでは 3% のまま
        for (int i = 0; i < 6; i++) book.ObserveResponse("m8", 0.4);                  // 入力への応答は画面の 40%
        var c = book.CriteriaFor("m8", baseC);
        Assert.InRange(c.StillRatio, 0.0625 - 1e-9, 0.0625 + 1e-9);                   // 5%×1.25（上限は 40% の半分＝20% → 10% で頭打ち）
        for (int i = 0; i < 50; i++) book.ObserveResponse("m9", 0.04);
        book.Observe("m9", bg, baseC); book.Observe("m9", bg, baseC);
        Assert.Equal(book.MaxStillRatio, book.CriteriaFor("m9", baseC).StillRatio);   // 応答が小さい画面では広げない
    }
}
