using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Core.Capture;

public enum CaptureHealth
{
    Stopped, Running,
    /// <summary>ウィンドウが最小化されている（どの方式でも取得不可 → 入力も止める）</summary>
    Minimized,
    /// <summary>クローク（別の仮想デスクトップ等）</summary>
    Cloaked,
    WindowLost,
    /// <summary>フレームが一定時間来ない</summary>
    Stalled,
    Black,
    Error,
}

public sealed record CaptureStatus(CaptureHealth Health, string Detail, double AtMs)
{
    /// <summary>この状態ではAIを一時停止すべきか（見えないまま操作しない）</summary>
    public bool RequiresPause => Health is CaptureHealth.Minimized or CaptureHealth.Cloaked or CaptureHealth.WindowLost
                                 or CaptureHealth.Error or CaptureHealth.Stopped or CaptureHealth.Black;
    public override string ToString() => $"{Health}: {Detail}";
}

/// <summary>Backend の性質（設計書 §2.1.1）。PushFrames=true は変化時にフレームが届く方式（WGC）、false はポーリング（PrintWindow）。</summary>
public sealed record CaptureCapabilities(bool Background, bool Minimized, bool PushFrames, bool RequiresForeground);

public sealed record CaptureProbeResult(string BackendId, bool Available, double Fps, double CopyMsP50, double BlackRatio,
                                        int Width, int Height, string Detail)
{
    public bool Usable => Available && BlackRatio < 0.5 && Width > 0;
}

/// <summary>
/// 取得バックエンド（設計書 §2.1.5）。対象はウィンドウ単位（デスクトップ全体は取らない）。
/// </summary>
public interface ICaptureBackend : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    CaptureCapabilities Capabilities { get; }
    bool IsRunning { get; }
    void Start(nint hwnd);
    void Stop();
    /// <summary>最新フレーム（CPU側にコピー）。まだ無ければ null</summary>
    Frame? LatestFrame();
    /// <summary>afterFrameId より新しいフレームを最大 timeoutMs 待つ。来なければ null</summary>
    Frame? WaitForNewFrame(long afterFrameId, int timeoutMs);
    double FpsOver(double windowMs);
    long TotalFrames { get; }
    /// <summary>最後に届いたフレームのID（Frame.NextId 由来。未着なら 0）</summary>
    long LastFrameId { get; }
    double? LastFrameAtMs { get; }
    string Notes { get; }
}

public static class CaptureBackendSelector
{
    /// <summary>使える（黒画面でない・フレームが来る）ものを優先順位どおりに選ぶ。preference は Id の並び</summary>
    public static CaptureProbeResult? SelectAuto(IEnumerable<CaptureProbeResult> results, IReadOnlyList<string> preference)
    {
        var usable = results.Where(r => r.Usable && r.Fps > 0).ToList();
        foreach (var id in preference)
            if (usable.FirstOrDefault(r => r.BackendId == id) is { } hit) return hit;
        return usable.OrderByDescending(r => r.Fps).FirstOrDefault();
    }
}
