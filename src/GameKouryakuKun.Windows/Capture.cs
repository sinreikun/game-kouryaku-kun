using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using GameKouryakuKun.Core.Capture;
using GameKouryakuKun.Core.Diagnostics;
using GameKouryakuKun.Core.Imaging;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace GameKouryakuKun.Windows;

/// <summary>
/// Windows Graphics Capture（ウィンドウ単位、背面でも取得可、最小化中はフレームが来ない）。
/// フレーム到着時は GPU 側の最新1枚だけ保持し、CPU へのコピーは要求されたときに1回だけ行う（同じフレームはキャッシュ）。
/// </summary>
public sealed unsafe class WgcCaptureBackend : ICaptureBackend
{
    public string Id => "wgc";
    public string DisplayName => "Windows Graphics Capture";
    public CaptureCapabilities Capabilities { get; } = new(Background: true, Minimized: false, PushFrames: true, RequiresForeground: false);

    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private readonly object _gate = new();
    private Direct3D11CaptureFrame? _held;
    private long _heldId;
    private double _heldAt;
    private Frame? _copied;
    private SizeInt32 _poolSize;
    private readonly Queue<double> _arrivals = new();
    public long TotalFrames { get; private set; }
    public long LastFrameId { get { lock (_gate) return _heldId; } }
    public double? LastFrameAtMs { get; private set; }
    public string Notes { get; private set; } = "";
    public bool IsRunning => _session != null;
    public bool ItemClosed { get; private set; }
    public double LastCopyMs { get; private set; }
    /// <summary>タイトルバー・枠を除いたクライアント領域だけを出す（既定）。AI・OCR に OS の表示を混ぜない</summary>
    public bool ClientAreaOnly { get; set; } = true;
    private nint _hwnd;
    private string _cropNote = "";
    /// <summary>直近の切り出し結果（診断用）</summary>
    public string CropNote => _cropNote;

    public static (bool ok, string detail) Supported()
    {
        try { return (GraphicsCaptureSession.IsSupported(), Environment.OSVersion.VersionString); }
        catch (Exception e) { return (false, e.Message); }
    }

    public void Start(nint hwnd)
    {
        Stop();
        _hwnd = hwnd;
        _device = CreateDevice(out var kind);
        _item = CreateItemForWindow(hwnd);
        _item.Closed += (_, _) => ItemClosed = true;
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        var notes = new List<string> { "device=" + kind };
        try { _session.IsCursorCaptureEnabled = false; notes.Add("cursor=off"); } catch (Exception e) { notes.Add("cursorOff不可:" + e.GetType().Name); }
        try
        {
            var task = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
            string access = task.Wait(TimeSpan.FromSeconds(3)) ? task.Result.ToString() : "応答なし";
            _session.IsBorderRequired = false; notes.Add("border=off(" + access + ")");
        }
        catch (Exception e) { notes.Add("border非表示不可:" + e.GetType().Name); }
        Notes = string.Join(", ", notes);
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var f = sender.TryGetNextFrame();
        if (f == null) return;
        var cs = f.ContentSize;
        double now = MonoClock.Now;
        lock (_gate)
        {
            _held?.Dispose();
            _held = f; _heldId = Frame.NextId(); TotalFrames++; _heldAt = now; LastFrameAtMs = now;
            _arrivals.Enqueue(now);
            while (_arrivals.Count > 0 && now - _arrivals.Peek() > 10_000) _arrivals.Dequeue();
            Monitor.PulseAll(_gate);
        }
        if (cs.Width > 0 && cs.Height > 0 && (cs.Width != _poolSize.Width || cs.Height != _poolSize.Height))
        {
            _poolSize = cs;
            try { sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, cs); } catch { }
        }
    }

    public double FpsOver(double windowMs)
    {
        double now = MonoClock.Now;
        lock (_gate) return Math.Round(_arrivals.Count(t => now - t <= windowMs) / (windowMs / 1000.0), 2);
    }

    public Frame? LatestFrame()
    {
        lock (_gate) return CopyLocked();
    }

    public Frame? WaitForNewFrame(long afterFrameId, int timeoutMs)
    {
        var deadline = MonoClock.Now + timeoutMs;
        lock (_gate)
        {
            while (_heldId <= afterFrameId || _held == null)
            {
                double rest = deadline - MonoClock.Now;
                if (rest <= 0) return null;
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(Math.Max(1, rest)));
            }
            return CopyLocked();
        }
    }

    private Frame? CopyLocked()
    {
        if (_held == null) return null;
        if (_copied != null && _copied.Id == _heldId) return _copied;
        var sw = Stopwatch.StartNew();
        var cs = _held.ContentSize;
        using var sb = SoftwareBitmap.CreateCopyFromSurfaceAsync(_held.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        var src = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? sb : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8);
        try
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            var buf = new global::Windows.Storage.Streams.Buffer((uint)(w * h * 4));
            src.CopyToBuffer(buf);
            var all = buf.ToArray();
            int stride = h > 0 ? all.Length / h : w * 4;
            int cw = Math.Min(w, Math.Max(1, cs.Width)), ch = Math.Min(h, Math.Max(1, cs.Height));
            byte[] outb;
            if (cw == w && stride == w * 4 && ch == h) outb = all;
            else { outb = new byte[cw * ch * 4]; for (int y = 0; y < ch; y++) Buffer.BlockCopy(all, y * stride, outb, y * cw * 4, cw * 4); }
            _copied = new Frame(_heldId, cw, ch, outb, _heldAt, Id);
            if (ClientAreaOnly) _copied = ClientArea.CropToClient(_copied, _hwnd, useDwmFrame: true, out _cropNote);
            LastCopyMs = sw.Elapsed.TotalMilliseconds;
            return _copied;
        }
        finally { if (!ReferenceEquals(src, sb)) src.Dispose(); }
    }

    public void Stop()
    {
        try { _session?.Dispose(); } catch { }
        try { if (_pool != null) { _pool.FrameArrived -= OnFrameArrived; _pool.Dispose(); } } catch { }
        lock (_gate) { try { _held?.Dispose(); } catch { } _held = null; _copied = null; _heldId = 0; }
        _session = null; _pool = null; _item = null;
        try { (_device as IDisposable)?.Dispose(); } catch { }
        _device = null;
    }

    public void Dispose() => Stop();

    private static IDirect3DDevice CreateDevice(out string kind)
    {
        int hr = Native.D3D11CreateDevice(IntPtr.Zero, Native.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, Native.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, Native.D3D11_SDK_VERSION, out var d3d, out _, out var ctx);
        kind = "Hardware";
        if (hr < 0)
        {
            hr = Native.D3D11CreateDevice(IntPtr.Zero, Native.D3D_DRIVER_TYPE_WARP, IntPtr.Zero, Native.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, Native.D3D11_SDK_VERSION, out d3d, out _, out ctx);
            kind = "WARP";
        }
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in Native.IID_IDXGIDevice, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var insp));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(insp); }
                finally { Marshal.Release(insp); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { if (ctx != IntPtr.Zero) Marshal.Release(ctx); Marshal.Release(d3d); }
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(Native.WindowsCreateString(cls, cls.Length, out var hs));
        try
        {
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hs, ref Native.IID_IGraphicsCaptureItemInterop, out var factory));
            try
            {
                var vtbl = *(IntPtr**)factory;
                // IGraphicsCaptureItemInterop: [0..2]=IUnknown, [3]=CreateForWindow, [4]=CreateForMonitor
                var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                Guid iid = Native.IID_IGraphicsCaptureItem; IntPtr itemPtr;
                Marshal.ThrowExceptionForHR(createForWindow(factory, hwnd, &iid, &itemPtr));
                try { return GraphicsCaptureItem.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { Native.WindowsDeleteString(hs); }
    }
}

/// <summary>
/// 予備: PrintWindow(PW_RENDERFULLCONTENT) のポーリング取得。最小化中・極小画像は「取得不可」としてフレームを出さない。
/// </summary>
public sealed unsafe class PrintWindowCaptureBackend : ICaptureBackend
{
    public const int MinUsableWidth = 200, MinUsableHeight = 150;
    public string Id => "printwindow";
    public string DisplayName => "PrintWindow (予備)";
    public CaptureCapabilities Capabilities { get; } = new(Background: true, Minimized: false, PushFrames: false, RequiresForeground: false);
    public int IntervalMs { get; set; } = 100;
    /// <summary>タイトルバー・枠を除いたクライアント領域だけを出す（既定）。AI・OCR に OS の表示を混ぜない</summary>
    public bool ClientAreaOnly { get; set; } = true;
    public string CropNote { get; private set; } = "";

    private readonly object _gate = new();
    private Frame? _latest;
    private Thread? _thread;
    private volatile bool _run;
    private nint _hwnd;
    private long _count, _lastId;
    private readonly Queue<double> _arrivals = new();
    public long TotalFrames => Interlocked.Read(ref _count);
    public long LastFrameId => Interlocked.Read(ref _lastId);
    public double? LastFrameAtMs { get; private set; }
    public string Notes { get; private set; } = "";
    public bool IsRunning => _run;
    public double LastCopyMs { get; private set; }

    public void Start(nint hwnd)
    {
        Stop();
        _hwnd = hwnd; _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "PrintWindowCapture" };
        _thread.Start();
    }

    private void Loop()
    {
        while (_run)
        {
            double t0 = MonoClock.Now;
            var (img, detail) = CaptureOnce(_hwnd);
            Notes = detail;
            if (img != null)
            {
                double now = MonoClock.Now;
                var f = new Frame(Frame.NextId(), img.Value.w, img.Value.h, img.Value.bgra, now, Id);
                if (ClientAreaOnly) { f = ClientArea.CropToClient(f, _hwnd, useDwmFrame: false, out var note); CropNote = note; }
                lock (_gate)
                {
                    _latest = f; LastFrameAtMs = now; _arrivals.Enqueue(now); Interlocked.Increment(ref _count); Interlocked.Exchange(ref _lastId, f.Id);
                    while (_arrivals.Count > 0 && now - _arrivals.Peek() > 10_000) _arrivals.Dequeue();
                    Monitor.PulseAll(_gate);
                }
            }
            double rest = IntervalMs - (MonoClock.Now - t0);
            if (rest > 0) Thread.Sleep((int)rest);
        }
    }

    public (( int w, int h, byte[] bgra)? img, string detail) CaptureOnce(nint hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return (null, "GetWindowRect失敗");
        int w = r.Width, h = r.Height;
        bool iconic = Native.IsIconic(hwnd);
        if (iconic || w < MinUsableWidth || h < MinUsableHeight) return (null, $"取得不可: {(iconic ? "最小化中 " : "")}{w}x{h}");
        var sw = Stopwatch.StartNew();
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen), dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            var bi = new Native.BITMAPINFOHEADER { biSize = (uint)sizeof(Native.BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            dib = Native.CreateDIBSection(screen, ref bi, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return (null, "CreateDIBSection失敗");
            old = Native.SelectObject(mem, dib);
            if (!Native.PrintWindow(hwnd, mem, Native.PW_RENDERFULLCONTENT)) return (null, "PrintWindow が FALSE");
            var buf = new byte[w * h * 4];
            Marshal.Copy(bits, buf, 0, buf.Length);
            LastCopyMs = sw.Elapsed.TotalMilliseconds;
            return ((w, h, buf), $"{w}x{h} {LastCopyMs:0}ms");
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(mem, old);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public Frame? LatestFrame() { lock (_gate) return _latest; }

    public Frame? WaitForNewFrame(long afterFrameId, int timeoutMs)
    {
        var deadline = MonoClock.Now + timeoutMs;
        lock (_gate)
        {
            while (_latest == null || _latest.Id <= afterFrameId)
            {
                double rest = deadline - MonoClock.Now;
                if (rest <= 0) return null;
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(Math.Max(1, rest)));
            }
            return _latest;
        }
    }

    public double FpsOver(double windowMs)
    {
        double now = MonoClock.Now;
        lock (_gate) return Math.Round(_arrivals.Count(t => now - t <= windowMs) / (windowMs / 1000.0), 2);
    }

    public void Stop() { _run = false; _thread?.Join(2000); _thread = null; lock (_gate) _latest = null; Interlocked.Exchange(ref _lastId, 0); }
    public void Dispose() => Stop();
}

/// <summary>キャプチャ画像をクライアント領域に切り出す（設計: AI が見るのはゲームの描画だけ。タイトルバーの文字はゲーム画面ではない）</summary>
internal static class ClientArea
{
    public static Frame CropToClient(Frame f, nint hwnd, bool useDwmFrame, out string note)
    {
        note = "";
        try
        {
            Native.RECT origin;
            bool ok = useDwmFrame
                ? Native.DwmGetWindowAttributeRect(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out origin, Marshal.SizeOf<Native.RECT>()) == 0
                : Native.GetWindowRect(hwnd, out origin);
            if (!ok || !Native.GetClientRect(hwnd, out var cr)) { note = "クライアント領域を取得できず（切り出しなし）"; return f; }
            var p = new Native.POINT();
            if (!Native.ClientToScreen(hwnd, ref p)) { note = "ClientToScreen 失敗（切り出しなし）"; return f; }
            var c = ClientCrop.Compute(f.Width, f.Height, origin.Left, origin.Top, p.X, p.Y, cr.Width, cr.Height);
            if (c is not var (x, y, w, h)) { note = "切り出し不要または不整合（そのまま）"; return f; }
            note = $"クライアント領域 ({x},{y}) {w}x{h} を切り出し";
            var o = f.Crop(x, y, w, h);
            return new Frame(f.Id, o.Width, o.Height, o.Bgra, f.TimestampMs, f.Source);
        }
        catch (Exception e) { note = "切り出し失敗: " + e.Message; return f; }
    }
}
