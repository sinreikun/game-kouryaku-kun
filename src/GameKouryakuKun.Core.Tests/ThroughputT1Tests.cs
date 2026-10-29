using GameKouryakuKun.Core.Throughput;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ThroughputT1Tests
{
    [Fact]
    public void Result_before_next_action_is_observed()
    {
        var l = new ObservationLedger();
        l.BeginAction(1);
        l.Open(1, 100, "ocr", 123, 5000, FramePosition.Settle, 5000);
        var r = l.Complete(1, "ocr", 123, 100, 5000, 5470);
        Assert.NotNull(r);
        Assert.Equal(SensorProvenance.Observed, r!.Provenance);
        Assert.Equal(470, r.LatencyMs);
        Assert.Equal(0, l.PendingCount);
    }

    [Fact]
    public void Result_after_later_action_is_late_amended_to_original_capture()
    {
        // 「Action A1 後に取得した Frame F123 の OCR」が A2 の実行後に完了 → late として元の A1 / F123 / 取得時刻に合流
        var l = new ObservationLedger();
        l.BeginAction(1);
        l.Open(1, 100, "ocr", 123, 5000, FramePosition.Settle, 5000);
        l.BeginAction(2);
        Assert.Equal(1, l.DecidedWithPending);
        var r = l.Complete(1, "ocr", 123, 100, 5000, 6300)!;
        Assert.Equal(SensorProvenance.Late, r.Provenance);
        Assert.Equal(1, r.Step);
        Assert.Equal(123, r.FrameId);
        Assert.Equal(5000, r.CaptureMs);   // 帰属は取得時刻（完了時刻 6300 ではない）
        Assert.Equal(1, l.LateAmended);
        Assert.Equal(1.0, l.LateAmendSuccessRate());
        Assert.Equal(1, l.CausallyIntact);
    }

    [Fact]
    public void Mismatched_frame_or_seq_is_a_violation_and_not_merged()
    {
        var l = new ObservationLedger();
        l.BeginAction(1);
        l.Open(1, 100, "ocr", 123, 5000, FramePosition.Settle, 5000);
        Assert.Null(l.Complete(1, "ocr", 999, 100, 5000, 5400));
        Assert.Null(l.Complete(1, "ocr", 123, 101, 5000, 5400));
        Assert.Null(l.Complete(2, "ocr", 123, 100, 5000, 5400));
        Assert.Equal(3, l.Violations);
        Assert.Equal(1, l.PendingCount);   // 元の手の観測は開いたまま（別の手に混ぜない）
        var m = new ThroughputMetrics(1, 1, l.CausallyIntact, l.Violations, 0, 1, 0, 1, 0);
        Assert.Equal(0, m.CausalObservationsPerMinute);   // 壊れた系列は数えない
    }

    [Fact]
    public void Missing_only_when_never_produced_and_unresolved_is_reported()
    {
        var l = new ObservationLedger();
        l.BeginAction(1); l.Open(1, 1, "ocr", 1, 0, FramePosition.Settle, 0);
        l.BeginAction(2); l.Open(2, 2, "ocr", 2, 10, FramePosition.Settle, 10);
        Assert.Equal(SensorProvenance.Missing, l.Fail(1, "ocr", 20, "ocr_failed")!.Provenance);
        l.BeginAction(3);
        var un = l.Unresolved();
        Assert.Single(un);
        Assert.Equal(2, un[0].Step);
        Assert.Equal(0.0, l.LateAmendSuccessRate());
    }

    [Fact]
    public void Inherited_result_is_kept_as_inherited()
    {
        var l = new ObservationLedger();
        l.BeginAction(1); l.Open(1, 1, "ocr", 1, 0, FramePosition.Settle, 0);
        Assert.Equal(SensorProvenance.Inherited, l.Complete(1, "ocr", 1, 1, 0, 2, inherited: true)!.Provenance);
    }

    [Fact]
    public void Timeline_orders_by_capture_not_completion()
    {
        var l = new ObservationLedger();
        l.BeginAction(1); l.Open(1, 1, "ocr", 10, 100, FramePosition.Settle, 100); l.Open(1, 1, "frame_diff", 10, 100, FramePosition.Settle, 100);
        l.BeginAction(2); l.Open(2, 2, "frame_diff", 20, 900, FramePosition.Settle, 900);
        l.Complete(2, "frame_diff", 20, 2, 900, 910);
        l.Complete(1, "frame_diff", 10, 1, 100, 120);
        l.Complete(1, "ocr", 10, 1, 100, 1500);   // 最後に完了
        var tl = l.Timeline();
        Assert.Equal(new[] { 1, 1, 2 }, tl.Select(r => r.Step).ToArray());
        Assert.Equal(SensorProvenance.Late, tl.First(r => r.Sensor == "frame_diff" && r.Step == 1).Provenance);
    }

    [Fact]
    public void Metrics_true_throughput()
    {
        var m = new ThroughputMetrics(Minutes: 10, Actions: 600, CausallyIntact: 600, Violations: 0, LateAmended: 500, Unresolved: 0, Missing: 0, RawRows: 600, DecidedWithPending: 500);
        Assert.Equal(60, m.ActionsPerMinute);
        Assert.Equal(60, m.CausalObservationsPerMinute);
        Assert.Equal(1, m.RawRetentionRatio);
        Assert.Equal(1, m.LateAmendSuccessRate);
        Assert.Equal(0, m.PendingNeverResolvedRate);
        Assert.Contains("CIO/min 60.0", m.Summary());
    }

    [Fact]
    public void Concurrent_completion_keeps_counts_consistent()
    {
        var l = new ObservationLedger();
        for (int i = 0; i < 200; i++) { l.BeginAction(i); l.Open(i, i, "ocr", i, i, FramePosition.Settle, i); }
        Parallel.For(0, 200, i => l.Complete(i, "ocr", i, i, i, i + 1));
        Assert.Equal(200, l.CausallyIntact);
        Assert.Equal(0, l.Violations);
        Assert.Equal(0, l.PendingCount);
    }
}
