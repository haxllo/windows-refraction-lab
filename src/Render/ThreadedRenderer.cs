using Microsoft.Graphics.Canvas;
using RefractionLab.Logic;
using System.Diagnostics;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace RefractionLab.Render;

internal readonly record struct RenderFrame(double WaitMs, double DrawMs, double PaintMs, double BlitMs, double PresentMs);

/// <summary>
/// Draws the refraction on its own thread and presents it through a swap chain, so the UI thread is
/// not in the per-frame path. The scene is painted into a 96-DPI render target exactly as on the UI
/// canvas, then copied 1:1 into the (DPI-scaled) swap chain. Requests coalesce: if several arrive
/// while a frame is being drawn, only the newest state is drawn next.
/// </summary>
internal sealed class ThreadedRenderer : IDisposable
{
    // The swap chain is opaque: WinUI 3 SwapChainPanel does not support transparency.
    private static readonly Color Backdrop = Color.FromArgb(255, 17, 20, 27);

    private readonly CanvasDevice _device;
    private readonly CanvasBitmap _crop;
    private readonly CanvasRenderTarget _target;
    private readonly CanvasSwapChain _swapChain;
    private readonly GlassPainter _painter = new();
    private readonly int _padPx;
    private readonly float _scale;
    private readonly float _radiusPx, _bezelPx, _blurPx;
    private readonly Func<float> _shiftPx;
    private readonly Action<RenderFrame> _onFrame;
    private readonly Action<string> _onFault;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private long _requestedAt;
    private volatile bool _disposed;

    public ThreadedRenderer(
        CanvasDevice device, CanvasBitmap crop, int widthPx, int heightPx, float scale, int padPx,
        float radiusPx, float bezelPx, float blurPx, Func<float> shiftPx,
        Action<RenderFrame> onFrame, Action<string> onFault)
    {
        _device = device;
        _crop = crop;
        _padPx = padPx;
        _scale = scale;
        _radiusPx = radiusPx;
        _bezelPx = bezelPx;
        _blurPx = blurPx;
        _shiftPx = shiftPx;
        _onFrame = onFrame;
        _onFault = onFault;

        _target = new CanvasRenderTarget(device, widthPx, heightPx, 96f);
        _swapChain = new CanvasSwapChain(
            device, DpiMath.DipsForPixels(widthPx, scale), DpiMath.DipsForPixels(heightPx, scale), 96f * scale,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Ignore);
        _thread = new Thread(Run) { IsBackground = true, Name = "RefractionRender" };
    }

    public CanvasSwapChain SwapChain => _swapChain;

    /// <summary>Call after the swap chain has been attached to its panel.</summary>
    public void Start() => _thread.Start();

    /// <summary>Thread-safe and cheap: marks that a newer frame should be drawn.</summary>
    public void Request()
    {
        if (_disposed)
            return;
        Interlocked.CompareExchange(ref _requestedAt, Stopwatch.GetTimestamp(), 0);
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void Run()
    {
        WaitHandle[] handles = [_wake, _stop.WaitHandle];
        try
        {
            while (WaitHandle.WaitAny(handles) == 0)
            {
                long requested = Interlocked.Exchange(ref _requestedAt, 0);
                if (requested == 0)
                    continue;

                long started = Stopwatch.GetTimestamp();
                (double paintMs, double blitMs) = DrawOnce();
                long beforePresent = Stopwatch.GetTimestamp();
                _swapChain.Present(); // paced to the display; blocks this thread, never the UI thread
                double presentMs = Stopwatch.GetElapsedTime(beforePresent).TotalMilliseconds;
                _onFrame(new RenderFrame(
                    Stopwatch.GetElapsedTime(requested, started).TotalMilliseconds, paintMs + blitMs, paintMs, blitMs, presentMs));
            }
        }
        catch (Exception ex)
        {
            if (!_stop.IsSet)
                _onFault($"The render thread failed (0x{ex.HResult:X8}); Acrylic restored.");
        }
    }

    // Each phase includes its drawing session's disposal, which is where Direct2D submits the work.
    private (double PaintMs, double BlitMs) DrawOnce()
    {
        var size = _target.SizeInPixels;
        float w = size.Width, h = size.Height;

        long t0 = Stopwatch.GetTimestamp();
        using (CanvasDrawingSession ds = _target.CreateDrawingSession())
        {
            ds.Clear(Backdrop);
            _painter.Draw(_device, ds, _crop, _padPx, w, h, _radiusPx, _bezelPx, _blurPx, _shiftPx());
        }
        long t1 = Stopwatch.GetTimestamp();

        // One plain copy with explicit rectangles: the whole target (pixels at 96 DPI) onto the whole
        // swap chain (DIPs at the display's DPI), sized from its real pixel count so the copy is exactly
        // 1:1. Nearest-neighbour so it cannot soften anything.
        var swapPx = _swapChain.SizeInPixels;
        using (CanvasDrawingSession present = _swapChain.CreateDrawingSession(Backdrop))
        {
            present.DrawImage(
                _target,
                new Rect(0, 0, swapPx.Width / _scale, swapPx.Height / _scale),
                new Rect(0, 0, w, h),
                1f,
                CanvasImageInterpolation.NearestNeighbor);
        }
        long t2 = Stopwatch.GetTimestamp();

        return (Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds, Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Set();

        bool exited = _thread.ThreadState == System.Threading.ThreadState.Unstarted ||
            Thread.CurrentThread == _thread || _thread.Join(3000);
        if (!exited)
            return; // never free GPU objects under a running render thread

        _painter.Dispose();
        _swapChain.Dispose();
        _target.Dispose();
        _wake.Dispose();
        _stop.Dispose();
    }
}
