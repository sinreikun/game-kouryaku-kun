using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace GameKouryakuKun.Phase0;

/// <summary>
/// Windows Graphics Capture によるウィンドウ単位キャプチャ。
/// デスクトップ全体ではなく HWND 指定の GraphicsCaptureItem を使う。
/// WGC は「内容が変化したときだけ」フレームを届けるため、静止画面ではフレームレートが低いのが正常。
/// 最新フレームを1枚だけ保持し、Snapshot() 時に CPU 側へコピーする。
/// </summary>
public sealed unsafe class WgcSession : IDisposable
{
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private readonly object _gate = new();
    private Direct3D11CaptureFrame? _held;
    private SizeInt32 _poolSize;
    private readonly List<DateTime> _arrivals = new();
    public long TotalFrames { get; private set; }
    public DateTime? LastFrameAt { get; private set; }
    public string LastContentSize { get; private set; } = "";
    public bool ItemClosed { get; private set; }
    public string Notes { get; private set; } = "";
    public string DeviceKind { get; private set; } = "";

    public static (bool ok, string detail) Supported()
    {
        try { return (GraphicsCaptureSession.IsSupported(), Environment.OSVersion.VersionString); }
        catch (Exception e) { return (false, e.Message); }
    }

    public void Start(IntPtr hwnd)
    {
        _device = CreateDevice();
        _item = CreateItemForWindow(hwnd);
        _item.Closed += (_, _) => ItemClosed = true;
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        var notes = new List<string>();
        try { _session.IsCursorCaptureEnabled = false; notes.Add("cursor=off"); } catch (Exception e) { notes.Add("cursorOff不可:" + e.GetType().Name); }
        try
        {
            // 黄色枠の非表示（Win11 + 許可が必要。失敗しても検証には影響しない）
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
        lock (_gate)
        {
            _held?.Dispose();
            _held = f;
            TotalFrames++;
            var now = DateTime.Now;
            LastFrameAt = now;
            if (_arrivals.Count > 20000) _arrivals.RemoveRange(0, 10000);
            _arrivals.Add(now);
            LastContentSize = $"{cs.Width}x{cs.Height}";
        }
        if (cs.Width > 0 && cs.Height > 0 && (cs.Width != _poolSize.Width || cs.Height != _poolSize.Height))
        {
            _poolSize = cs;
            try { sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, cs); } catch { }
        }
    }

    public int FramesBetween(DateTime from, DateTime to)
    {
        lock (_gate) return _arrivals.Count(t => t >= from && t <= to);
    }

    public double FpsOver(TimeSpan span) { var now = DateTime.Now; return Math.Round(FramesBetween(now - span, now) / span.TotalSeconds, 2); }

    /// <summary>保持中の最新フレームを BGRA で取得（内容が変わらない限り同じフレーム）。</summary>
    public FrameImage? Snapshot()
    {
        SoftwareBitmap sb; SizeInt32 cs; DateTime? at;
        lock (_gate)
        {
            if (_held == null) return null;
            cs = _held.ContentSize; at = LastFrameAt;
            sb = SoftwareBitmap.CreateCopyFromSurfaceAsync(_held.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        }
        using (sb)
        {
            var src = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? sb : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8);
            int w = src.PixelWidth, h = src.PixelHeight;
            var buf = new Windows.Storage.Streams.Buffer((uint)(w * h * 4));
            src.CopyToBuffer(buf);
            var all = buf.ToArray();
            int stride = h > 0 ? all.Length / h : w * 4;
            int cw = Math.Min(w, Math.Max(1, cs.Width)), ch = Math.Min(h, Math.Max(1, cs.Height));
            var outb = new byte[cw * ch * 4];
            for (int y = 0; y < ch; y++) Buffer.BlockCopy(all, y * stride, outb, y * cw * 4, cw * 4);
            if (!ReferenceEquals(src, sb)) src.Dispose();
            return new FrameImage(cw, ch, outb) { Source = "WGC", CapturedAt = at ?? DateTime.Now };
        }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { if (_pool != null) { _pool.FrameArrived -= OnFrameArrived; _pool.Dispose(); } } catch { }
        lock (_gate) { try { _held?.Dispose(); } catch { } _held = null; }
        _session = null; _pool = null; _item = null;
        try { (_device as IDisposable)?.Dispose(); } catch { }
    }

    private IDirect3DDevice CreateDevice()
    {
        int hr = Native.D3D11CreateDevice(IntPtr.Zero, Native.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, Native.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, Native.D3D11_SDK_VERSION, out var d3d, out _, out var ctx);
        DeviceKind = "Hardware";
        if (hr < 0)
        {
            hr = Native.D3D11CreateDevice(IntPtr.Zero, Native.D3D_DRIVER_TYPE_WARP, IntPtr.Zero, Native.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, Native.D3D11_SDK_VERSION, out d3d, out _, out ctx);
            DeviceKind = "WARP";
        }
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, ref Native.IID_IDXGIDevice, out var dxgi));
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

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
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

/// <summary>比較用: PrintWindow(PW_RENDERFULLCONTENT)。毎回その時点の内容を要求する。GDI の DIB に描かせて BGRA を得る。</summary>
public static unsafe class PrintWindowCapture
{
    /// <summary>最小化中などで得られる極小画像（タイトルバーのみ等）を「取得不可」とみなす下限</summary>
    public const int MinUsableWidth = 200, MinUsableHeight = 150;

    public static (FrameImage? img, string detail) Capture(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return (null, "GetWindowRect失敗");
        int w = r.Width, h = r.Height;
        if (w <= 0 || h <= 0) return (null, $"ウィンドウサイズ0 ({r})");
        var sw = Stopwatch.StartNew();
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen), dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            var bi = new Native.BITMAPINFOHEADER { biSize = (uint)sizeof(Native.BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            dib = Native.CreateDIBSection(screen, ref bi, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return (null, "CreateDIBSection失敗");
            old = Native.SelectObject(mem, dib);
            bool ok = Native.PrintWindow(hwnd, mem, Native.PW_RENDERFULLCONTENT);
            sw.Stop();
            if (!ok) return (null, $"PrintWindowがFALSEを返した ({w}x{h})");
            var buf = new byte[w * h * 4];
            Marshal.Copy(bits, buf, 0, buf.Length);
            bool iconic = Native.IsIconic(hwnd);
            bool tiny = w < MinUsableWidth || h < MinUsableHeight;
            string detail = $"{w}x{h} {sw.ElapsedMilliseconds}ms";
            if (iconic || tiny)
                return (null, $"取得不可: {(iconic ? "最小化中" : "")}{(tiny ? "極小画像" : "")} ({detail}、タイトルバー程度しか得られない)");
            return (new FrameImage(w, h, buf) { Source = "PrintWindow" }, detail);
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(mem, old);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>極小判定を無視して生画像を得る（記録用）</summary>
    public static FrameImage? CaptureRaw(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0) return null;
        int w = r.Width, h = r.Height;
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
        var bi = new Native.BITMAPINFOHEADER { biSize = (uint)sizeof(Native.BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = Native.CreateDIBSection(screen, ref bi, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) return null;
            var old = Native.SelectObject(mem, dib);
            bool ok = Native.PrintWindow(hwnd, mem, Native.PW_RENDERFULLCONTENT);
            Native.SelectObject(mem, old);
            if (!ok) return null;
            var buf = new byte[w * h * 4]; Marshal.Copy(bits, buf, 0, buf.Length);
            return new FrameImage(w, h, buf) { Source = "PrintWindow(raw)" };
        }
        finally { if (dib != IntPtr.Zero) Native.DeleteObject(dib); Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen); }
    }
}
