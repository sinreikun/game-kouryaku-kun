using GameKouryakuKun.Core.Imaging;
using GameKouryakuKun.Core.Turn;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class TurnDetectorTests
{
    private static readonly Canvas Base = new(320, 200);
    private static Frame Idle(double t) => Base.ToFrame(t);
    private static Frame Boxed(double t, int h = 60) => Base.Clone().Rect(20, 130, 280, h, 250, 250, 250).ToFrame(t);
    private static TurnDetector Make() => new(new TurnDetectorOptions { NoChangeTimeoutMs = 600, StableMs = 200, MaxMs = 3000, NoFrameStillMs = 200 });

    [Fact]
    public void Change_then_settle_gives_normal_boundary_with_latencies()
    {
        var d = Make();
        d.Begin(Idle(0), 10, "button8");
        TurnBoundary? b = null;
        b ??= d.OnFrame(Idle(30));
        b ??= d.OnFrame(Idle(60));
        b ??= d.OnFrame(Boxed(110, 20)); // アニメで伸びる
        b ??= d.OnFrame(Boxed(140, 40));
        b ??= d.OnFrame(Boxed(170, 60));
        for (double t = 200; b == null && t < 1000; t += 30) b = d.OnFrame(Boxed(t, 60));
        Assert.NotNull(b);
        Assert.Equal(TurnKind.Normal, b!.Kind);
        Assert.Equal(100, b.FirstChangeMs);
        Assert.Equal(160, b.SettledMs);         // 最後に動いたフレーム(170) − 入力(10)
        Assert.InRange(b.BoundaryMs, 360, 400); // 静止開始 + 安定200ms
        Assert.True(b.NetChange);
        Assert.Equal(TurnState.Idle, d.State);
    }

    [Fact]
    public void Default_options_accept_late_reaction_seen_on_real_machine()
    {
        // 実機 Phase 1: 敵の行動による画面変化が入力から 735ms 後に始まった例があった → 既定の NoChange 判定はそれより十分長い
        var d = new TurnDetector();
        Assert.True(d.Options.NoChangeTimeoutMs >= 1100);
        d.Begin(Idle(0), 0, "button6");
        TurnBoundary? b = null;
        for (double t = 30; t < 950; t += 30) b ??= d.OnFrame(Idle(t));
        Assert.Null(b);
        for (double t = 960; b == null && t < 3000; t += 30) b = d.OnFrame(Boxed(t));
        Assert.Equal(TurnKind.Normal, b!.Kind);
        Assert.InRange(b.FirstChangeMs!.Value, 950, 1000);
    }

    [Fact]
    public void No_change_gives_nochange_after_timeout()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        TurnBoundary? b = null;
        for (double t = 30; b == null && t < 2000; t += 30) b = d.OnFrame(Idle(t));
        Assert.Equal(TurnKind.NoChange, b!.Kind);
        Assert.InRange(b.BoundaryMs, 600, 640);
        Assert.False(b.NetChange);
    }

    [Fact]
    public void No_frames_at_all_also_gives_nochange_via_ticks()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        Assert.Null(d.OnTick(300));
        var b = d.OnTick(650);
        Assert.Equal(TurnKind.NoChange, b!.Kind);
    }

    [Fact]
    public void Wgc_style_no_frames_after_change_settles_via_ticks()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        Assert.Null(d.OnFrame(Boxed(80)));
        Assert.Null(d.OnTick(200));                  // 120ms フレームなし → まだ動いている扱い
        var b = d.OnTick(290);                       // 210ms フレームなし → 静止開始=80、そこから 210ms ≥ 200 で境界
        Assert.Equal(TurnKind.Normal, b!.Kind);
        Assert.Equal(80, b.SettledMs);
    }

    [Fact]
    public void Continuous_animation_times_out()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        TurnBoundary? b = null;
        int i = 0;
        for (double t = 30; b == null && t < 5000; t += 30, i++) b = d.OnFrame(Base.Clone().Rect((i * 37) % 200, 20, 100, 100, (byte)(i * 50), 0, 0).ToFrame(t));
        Assert.Equal(TurnKind.Timeout, b!.Kind);
        Assert.InRange(b.BoundaryMs, 3000, 3040);
    }

    [Fact]
    public void Small_scattered_motion_after_input_is_nochange()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        TurnBoundary? b = null;
        int i = 0;
        for (double t = 30; b == null && t < 2000; t += 30, i++)
            b = d.OnFrame(Base.Clone().Rect(10 + (i % 5) * 60, 10, 6, 6, 255, 0, 0).Rect(300, 180 - (i % 3) * 20, 6, 6, 0, 255, 0).ToFrame(t));
        Assert.Equal(TurnKind.NoChange, b!.Kind);
    }

    [Fact]
    public void Masked_idle_animation_does_not_block_settling()
    {
        var mask = new BlockMask(new BlockGrid(320, 200, 8));
        for (int by = 10; by < 14; by++) for (int bx = 18; bx < 22; bx++) mask[bx, by] = true; // (144..176, 80..112)
        var d = new TurnDetector(new TurnDetectorOptions { NoChangeTimeoutMs = 600, StableMs = 200, MaxMs = 3000 }, ChangeCriteria.Default, mask);
        Frame F(double t, bool menu, int phase) => (menu ? Base.Clone().Rect(20, 130, 280, 60, 250, 250, 250) : Base.Clone()).Rect(150, 86, 20, 20, (byte)(phase * 60), 0, 0).ToFrame(t);
        d.Begin(F(0, false, 0), 0);
        TurnBoundary? b = null; int i = 1;
        for (double t = 30; b == null && t < 2000; t += 30, i++) b = d.OnFrame(F(t, t >= 90, i % 4));
        Assert.Equal(TurnKind.Normal, b!.Kind);
    }

    [Fact]
    public void Abort_returns_aborted_and_frames_before_input_are_ignored()
    {
        var d = Make();
        d.Begin(Idle(100), 100);
        Assert.Null(d.OnFrame(Boxed(50)));           // 入力前のフレームは無視
        Assert.Equal(TurnState.InputSent, d.State);
        var b = d.Abort(200, "最小化");
        Assert.Equal(TurnKind.Aborted, b!.Kind);
        Assert.Equal("最小化", b.Note);
        Assert.Null(d.Abort(300, "x"));
    }

    [Fact]
    public void Window_resize_aborts()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        var b = d.OnFrame(new Canvas(300, 200).ToFrame(50));
        Assert.Equal(TurnKind.Aborted, b!.Kind);
    }

    [Fact]
    public void Begin_twice_throws()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        Assert.Throws<InvalidOperationException>(() => d.Begin(Idle(1), 1));
    }

    [Fact]
    public void Runner_drives_detector_from_frame_source()
    {
        var d = Make();
        var src = new Queue<Frame>(new[] { Boxed(50), Boxed(80), Boxed(110) });
        double now = 0;
        Frame? Wait(long after, int ms) { now += ms; return src.Count > 0 ? src.Dequeue() : null; }
        d.Begin(Idle(0), 0);
        var b = TurnRunner.Await(d, 0, Wait, () => now, null, 20);
        Assert.Equal(TurnKind.Normal, b.Kind);
    }

    [Fact]
    public void Runner_aborts_on_pause()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        var b = TurnRunner.Await(d, 0, (_, _) => null, () => 10, () => "ユーザー操作");
        Assert.Equal(TurnKind.Aborted, b.Kind);
    }

    [Fact]
    public void NoChange_with_small_local_motion_is_annotated()
    {
        // 小さな物体（画面の約0.6%）だけが変わった → 変化率は閾値超え、連結領域は小さい → NoChange＋注記
        var d = new TurnDetector(new TurnDetectorOptions { NoChangeTimeoutMs = 600, StableMs = 200 }, ChangeCriteria.Default);
        d.Begin(Idle(0), 0, "button6");
        TurnBoundary? b = null;
        for (double t = 30; b == null && t < 2000; t += 30) b = d.OnFrame(Base.Clone().Rect(100, 100, 20, 20, 255, 0, 0).ToFrame(t));
        Assert.Equal(TurnKind.NoChange, b!.Kind);
        Assert.Contains(TurnDetector.LocalChangeNote, b.Note);
    }

    [Fact]
    public void Plain_NoChange_has_no_local_change_note()
    {
        var d = Make();
        d.Begin(Idle(0), 0);
        TurnBoundary? b = null;
        for (double t = 30; b == null && t < 2000; t += 30) b = d.OnFrame(Idle(t));
        Assert.True(b!.Note == null || !b.Note.Contains(TurnDetector.LocalChangeNote));
    }

    [Fact]
    public void NoChange_run_guard_counts_consecutive_and_ignores_aborted()
    {
        var g = new NoChangeRunGuard(3);
        Assert.False(g.Record(TurnKind.NoChange));
        Assert.False(g.Record(TurnKind.Aborted));   // 中断は数えない・リセットしない
        Assert.False(g.Record(TurnKind.NoChange));
        Assert.False(g.Record(TurnKind.Normal));    // リセット
        Assert.False(g.Record(TurnKind.NoChange));
        Assert.False(g.Record(TurnKind.NoChange));
        Assert.True(g.Record(TurnKind.NoChange));
        Assert.False(new NoChangeRunGuard(0).Record(TurnKind.NoChange));
    }
}
