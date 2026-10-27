using GameKouryakuKun.Core.Capture;
using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Imaging;

namespace GameKouryakuKun.Windows;

/// <summary>
/// 取得の窓口（設計書 ICaptureManager の Phase 1 版）。
///  - 候補（既定: WGC → PrintWindow）を順に試し、フレームが来た最初の Backend を使う
///  - 150ms ごとにウィンドウ状態を監視: 消失 / 最小化 / クローク → StatusChanged（RequiresPause=true）で一時停止を要求
///  - 最小化から戻ったらフレーム再開を確認し、来なければ Backend を再起動、それでも駄目なら次の候補へ切替
///  - 最小化でないのにフレームが StallMs 来ない → Stalled（警告）→ 再起動 → 切替
/// </summary>
public sealed class CaptureSupervisor : IDisposable
{
    private readonly List<ICaptureBackend> _candidates;
    private readonly object _gate = new();
    private Thread? _watch;
    private volatile bool _run;
    private int _index = -1;
    public nint Target { get; }
    public double StallMs { get; set; } = 4000;
    public double StartupWaitMs { get; set; } = 2500;
    public ICaptureBackend? Current { get; private set; }
    public CaptureStatus Status { get; private set; } = new(CaptureHealth.Stopped, "未開始", 0);
    public event Action<CaptureStatus>? StatusChanged;
    public event Action<string>? Log;

    public CaptureSupervisor(nint target, IEnumerable<ICaptureBackend> candidates)
    {
        Target = target; _candidates = candidates.ToList();
        if (_candidates.Count == 0) throw new ArgumentException("取得バックエンドがありません");
    }

    public IReadOnlyList<ICaptureBackend> Candidates => _candidates;

    /// <summary>preferredId を先頭にして順に起動を試す。成功した Backend を返す（全滅なら null）</summary>
    public ICaptureBackend? Start(string? preferredId = null)
    {
        if (preferredId != null)
        {
            int i = _candidates.FindIndex(c => c.Id == preferredId);
            if (i > 0) { var c = _candidates[i]; _candidates.RemoveAt(i); _candidates.Insert(0, c); }
        }
        for (int i = 0; i < _candidates.Count; i++)
            if (TryActivate(i)) break;
        _run = true;
        _watch = new Thread(WatchLoop) { IsBackground = true, Name = "CaptureSupervisor" };
        _watch.Start();
        return Current;
    }

    private bool TryActivate(int i)
    {
        var b = _candidates[i];
        try
        {
            Current?.Stop();
            b.Start(Target);
            var f = b.WaitForNewFrame(0, (int)StartupWaitMs);
            if (f == null && WindowLocator.IsMinimized(Target))
            {
                lock (_gate) { Current = b; _index = i; }
                Log?.Invoke($"{b.DisplayName} を開始（最小化中のためフレーム未確認）");
                return true;
            }
            if (f == null) { b.Stop(); Log?.Invoke($"{b.DisplayName}: {StartupWaitMs:0}ms 以内にフレームが来ません → 次の候補へ"); return false; }
            lock (_gate) { Current = b; _index = i; }
            Log?.Invoke($"{b.DisplayName} を使用（{f.Width}x{f.Height}、{b.Notes}）");
            Set(CaptureHealth.Running, $"{b.DisplayName} 取得中");
            return true;
        }
        catch (Exception e)
        {
            try { b.Stop(); } catch { }
            Log?.Invoke($"{b.DisplayName} を開始できません: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private void Set(CaptureHealth h, string detail)
    {
        CaptureStatus s;
        lock (_gate)
        {
            if (Status.Health == h) return;
            s = Status = new CaptureStatus(h, detail, MonoClock.Now);
        }
        StatusChanged?.Invoke(s);
    }

    private void WatchLoop()
    {
        bool wasMinimized = false; int restarts = 0;
        while (_run)
        {
            Thread.Sleep(150);
            try
            {
                if (!WindowLocator.IsAlive(Target)) { Set(CaptureHealth.WindowLost, "ゲームウィンドウが見つかりません（終了した可能性）"); continue; }
                if (WindowLocator.IsMinimized(Target)) { wasMinimized = true; Set(CaptureHealth.Minimized, "最小化中は観測できません（元に戻すと自動で再開）"); continue; }
                if (WindowLocator.IsCloaked(Target)) { Set(CaptureHealth.Cloaked, "ウィンドウがクローク中（別の仮想デスクトップ等）"); continue; }
                var cur = Current;
                if (cur == null) { if (!TryNext()) Set(CaptureHealth.Error, "使える取得方式がありません"); continue; }
                double now = MonoClock.Now;
                if (wasMinimized)
                {
                    wasMinimized = false;
                    if (cur.WaitForNewFrame(cur.LastFrameId, (int)StartupWaitMs) == null)
                    {
                        Log?.Invoke($"最小化から復帰後に {cur.DisplayName} のフレームが来ないため再起動");
                        if (!Restart(cur) && !TryNext()) { Set(CaptureHealth.Error, "復帰後に取得を再開できません"); continue; }
                    }
                    Set(CaptureHealth.Running, "最小化から復帰");
                    continue;
                }
                double last = cur.LastFrameAtMs ?? 0;
                if (now - last > StallMs)
                {
                    Set(CaptureHealth.Stalled, $"{(now - last) / 1000:0.0}秒フレームなし");
                    if (restarts++ < 1 ? !Restart(cur) : true) { restarts = 0; TryNext(); }
                    continue;
                }
                restarts = 0;
                Set(CaptureHealth.Running, $"{cur.DisplayName} 取得中");
            }
            catch (Exception e) { Log?.Invoke($"監視エラー: {e.GetType().Name}: {e.Message}"); }
        }
    }

    private bool Restart(ICaptureBackend b)
    {
        try
        {
            b.Stop(); b.Start(Target);
            bool ok = b.WaitForNewFrame(0, (int)StartupWaitMs) != null;
            Log?.Invoke($"{b.DisplayName} 再起動 → {(ok ? "成功" : "フレームなし")}");
            return ok;
        }
        catch (Exception e) { Log?.Invoke($"{b.DisplayName} 再起動失敗: {e.Message}"); return false; }
    }

    private bool TryNext()
    {
        for (int k = 1; k <= _candidates.Count; k++)
        {
            int i = (_index + k + _candidates.Count) % _candidates.Count;
            if (i == _index && k < _candidates.Count) continue;
            if (TryActivate(i)) { Log?.Invoke($"取得方式を {_candidates[i].DisplayName} に切替"); return true; }
        }
        return false;
    }

    public Frame? LatestFrame() => Current?.LatestFrame();
    public Frame? WaitForNewFrame(long afterId, int timeoutMs)
    {
        var c = Current;
        if (c == null) { Thread.Sleep(Math.Max(1, timeoutMs)); return null; }
        return c.WaitForNewFrame(afterId, timeoutMs);
    }

    public void Dispose()
    {
        _run = false; _watch?.Join(2000);
        foreach (var c in _candidates) { try { c.Dispose(); } catch { } }
    }
}
