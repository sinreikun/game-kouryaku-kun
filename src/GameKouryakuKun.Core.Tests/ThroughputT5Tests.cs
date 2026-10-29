using System.Diagnostics;
using GameKouryakuKun.Core.Throughput;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ThroughputT5Tests
{
    private static double Now() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    [Fact]
    public void Observers_run_in_parallel_and_merge_to_original_action()
    {
        var l = new ObservationLedger(); var bus = new ObserverBus(l, Now);
        var gate = new ManualResetEventSlim();
        l.BeginAction(1);
        double cap = Now();
        bus.Submit("frame_diff", 1, 11, 101, cap, FramePosition.Settle, () => { gate.Wait(2000); return "fd"; });
        bus.Submit("noise", 1, 11, 101, cap, FramePosition.Settle, () => { gate.Set(); return "nz"; });   // frame_diff の完了を待たずに動く
        l.BeginAction(2);
        bus.Drain();
        Assert.True(gate.IsSet);
        var tl = bus.Timeline();
        Assert.Equal(2, tl.Count);
        Assert.All(tl, r => { Assert.Equal(1, r.Step); Assert.Equal(11, r.Seq); Assert.Equal(101, r.FrameId); Assert.Equal(cap, r.CaptureMs); });
        Assert.Equal(0, l.Violations);
        Assert.Equal(0, l.PendingCount);
    }

    [Fact]
    public void Settle_frames_are_never_dropped_under_backpressure()
    {
        var l = new ObservationLedger();
        var bus = new ObserverBus(l, Now, new Dictionary<string, ObserverBus.SensorConfig> { ["s"] = new(1, 1000) });
        for (int i = 0; i < 20; i++) { l.BeginAction(i); bus.Submit("s", i, i, i, i, FramePosition.Settle, () => { Thread.Sleep(2); return null; }); }
        bus.Drain();
        Assert.Equal(0, bus.SettleDropped);
        Assert.Equal(0, bus.Coalesced);
        Assert.True(bus.Waited > 0);
        Assert.Equal(20, bus.Timeline().Count);
        Assert.Equal(20, l.CausallyIntact);
    }

    [Fact]
    public void Only_post_frames_may_be_coalesced_and_are_recorded()
    {
        var l = new ObservationLedger();
        var bus = new ObserverBus(l, Now, new Dictionary<string, ObserverBus.SensorConfig> { ["s"] = new(1, 1000) });
        var hold = new ManualResetEventSlim();
        l.BeginAction(1);
        Assert.True(bus.Submit("s", 1, 1, 1, 1, FramePosition.Settle, () => { hold.Wait(2000); return null; }));
        Assert.False(bus.Submit("s", 2, 2, 2, 2, FramePosition.Post, () => null));
        hold.Set(); bus.Drain();
        Assert.Equal(1, bus.Coalesced);
        Assert.Equal(0, l.Violations);
    }

    [Fact]
    public void Deadline_miss_is_late_not_dropped()
    {
        var l = new ObservationLedger();
        var bus = new ObserverBus(l, Now, new Dictionary<string, ObserverBus.SensorConfig> { ["s"] = new(4, 5) });
        l.BeginAction(1);
        bus.Submit("s", 1, 1, 1, Now(), FramePosition.Settle, () => { Thread.Sleep(30); return null; });
        l.BeginAction(2);
        bus.Drain();
        Assert.Equal(1, bus.Late);
        var r = Assert.Single(bus.Timeline());
        Assert.Equal(SensorProvenance.Late, r.Provenance);
        Assert.Equal(1, r.Step);
    }

    [Fact]
    public void Failure_is_missing_and_exception_does_not_stop_lane()
    {
        var l = new ObservationLedger(); var bus = new ObserverBus(l, Now);
        l.BeginAction(1);
        bus.Submit("noise", 1, 1, 1, 1, FramePosition.Settle, () => throw new InvalidOperationException());
        bus.Submit("noise", 2, 2, 2, 2, FramePosition.Settle, () => "ok");
        bus.Drain();
        Assert.Equal(1, l.Missing);
        Assert.Equal(1, l.Observed + l.LateAmended);
        Assert.Contains("fail 1", bus.SensorsSummary());
    }

    [Fact]
    public void Double_open_is_a_violation()
    {
        var l = new ObservationLedger();
        l.Open(1, 1, "noise", 1, 0, FramePosition.Gap, 0);
        l.Open(1, 1, "noise", 2, 0, FramePosition.Settle, 0);
        Assert.Equal(1, l.Violations);
    }

    [Fact]
    public void Reordered_arrivals_are_counted_and_timeline_is_by_capture()
    {
        var l = new ObservationLedger(); var bus = new ObserverBus(l, Now);
        bus.Publish(new SensorResult(2, 200, 2, 2, FramePosition.Settle, "ocr", 200, 900, SensorProvenance.Late));
        bus.Publish(new SensorResult(1, 100, 1, 1, FramePosition.Settle, "ocr", 100, 950, SensorProvenance.Late));
        Assert.Equal(1, bus.Reordered);
        Assert.Equal(new long[] { 1, 2 }, bus.Timeline().Select(r => r.FrameId).ToArray());
    }
}
