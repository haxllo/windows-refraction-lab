using Microsoft.Graphics.Canvas;
using RefractionLab.Logic;
using SharpGen.Runtime;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.DirectX.Direct3D11;
using DxgiResultCode = Vortice.DXGI.ResultCode;

namespace RefractionLab.Capture;

internal readonly record struct DuplicationFrame(long PresentTicks, double CopyMs, bool ProtectedMasked);

/// <summary>
/// DXGI Desktop Duplication of one display. A worker thread copies only the panel rectangle,
/// on the GPU, into the Win2D render target; no pixels reach the CPU and nothing is stored.
/// </summary>
internal sealed class DuplicationSource : IDisposable
{
    private readonly CanvasDevice _canvasDevice;
    private readonly ID3D11Device _d3d;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _cropTexture;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly PxRect _crop;
    private readonly Box _srcBox;
    private readonly Action<DuplicationFrame> _onFrame;
    private readonly Action<string> _onFault;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private long _sourceUpdates;

    /// <summary>Desktop updates Windows reported on this display (any region), summed over all acquired frames.</summary>
    public long SourceUpdates => Interlocked.Read(ref _sourceUpdates);

    private DuplicationSource(
        CanvasDevice canvasDevice, ID3D11Device d3d, ID3D11Texture2D cropTexture, IDXGIOutputDuplication duplication,
        PxRect crop, Action<DuplicationFrame> onFrame, Action<string> onFault)
    {
        _canvasDevice = canvasDevice;
        _d3d = d3d;
        _context = d3d.ImmediateContext;
        _cropTexture = cropTexture;
        _duplication = duplication;
        _crop = crop;
        _srcBox = new Box(crop.X, crop.Y, 0, crop.X + crop.Width, crop.Y + crop.Height, 1);
        _onFrame = onFrame;
        _onFault = onFault;
        _thread = new Thread(Run) { IsBackground = true, Name = "DesktopDuplication" };
    }

    /// <exception cref="InvalidOperationException">Any setup failure; the message is safe to show.</exception>
    public static DuplicationSource Start(
        CanvasDevice canvasDevice, IDirect3DSurface cropSurface, IntPtr monitor, PxRect monitorRect, PxRect crop,
        Action<DuplicationFrame> onFrame, Action<string> onFault)
    {
        ID3D11Device? d3d = null;
        ID3D11Texture2D? cropTexture = null;
        IDXGIOutputDuplication? duplication = null;
        try
        {
            d3d = new ID3D11Device(DxgiInterop.GetInterface((IDirect3DDevice)canvasDevice, DxgiInterop.IidD3D11Device));
            cropTexture = new ID3D11Texture2D(DxgiInterop.GetInterface(cropSurface, DxgiInterop.IidD3D11Texture2D));
            using (ID3D11Multithread? multithread = d3d.QueryInterfaceOrNull<ID3D11Multithread>())
                multithread?.SetMultithreadProtected(true);

            Texture2DDescription cropDesc = cropTexture.Description;
            if (cropDesc.Width != crop.Width || cropDesc.Height != crop.Height)
                throw new InvalidOperationException("Crop buffer does not match the panel size.");

            duplication = Duplicate(d3d, monitor, monitorRect);
            var source = new DuplicationSource(canvasDevice, d3d, cropTexture, duplication, crop, onFrame, onFault);
            source._thread.Start();
            return source;
        }
        catch (InvalidOperationException)
        {
            Cleanup();
            throw;
        }
        catch (Exception ex)
        {
            Cleanup();
            throw new InvalidOperationException($"Desktop Duplication is unavailable (0x{ex.HResult:X8}).", ex);
        }

        void Cleanup()
        {
            duplication?.Dispose();
            cropTexture?.Dispose();
            d3d?.Dispose();
        }
    }

    private static IDXGIOutputDuplication Duplicate(ID3D11Device d3d, IntPtr monitor, PxRect monitorRect)
    {
        using IDXGIDevice dxgiDevice = d3d.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput? output).Success; i++)
        {
            using (output)
            {
                OutputDescription description = output.Description;
                if (description.Monitor != monitor)
                    continue;
                if (description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                    throw new InvalidOperationException("Rotated displays are not supported by this prototype.");

                using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                IDXGIOutputDuplication duplication = output1.DuplicateOutput(d3d);
                OutduplDescription info = duplication.Description;
                if (info.DesktopImageInSystemMemory ||
                    info.ModeDescription.Width != monitorRect.Width || info.ModeDescription.Height != monitorRect.Height)
                {
                    duplication.Dispose();
                    throw new InvalidOperationException("Desktop Duplication returned an unsupported surface for this display.");
                }
                return duplication;
            }
        }
        throw new InvalidOperationException("The panel's display is not on the graphics adapter used for rendering (multi-GPU?).");
    }

    private void Run()
    {
        bool first = true;
        var dirty = new RawRect[64];
        var moves = new OutduplMoveRect[32];
        try
        {
            while (!_stop.IsSet)
            {
                Result result = _duplication.AcquireNextFrame(100, out OutduplFrameInfo info, out IDXGIResource? resource);
                if (result.Code == DxgiResultCode.WaitTimeout.Code)
                    continue;
                if (result.Failure)
                {
                    _onFault(result.Code == DxgiResultCode.AccessLost.Code
                        ? "The display switched (secure desktop, mode change or full-screen app); Acrylic restored."
                        : $"Desktop Duplication stopped (0x{(uint)result.Code:X8}); Acrylic restored.");
                    return;
                }

                if (info.LastPresentTime != 0)
                    Interlocked.Add(ref _sourceUpdates, Math.Max(1L, (long)info.AccumulatedFrames));
                try
                {
                    bool changed = first || (info.LastPresentTime != 0 && TouchesCrop(info, ref dirty, ref moves));
                    if (resource is null || !changed)
                        continue;
                    first = false;

                    long copyStart = Stopwatch.GetTimestamp();
                    using (ID3D11Texture2D desktop = resource.QueryInterface<ID3D11Texture2D>())
                    using (_canvasDevice.Lock())
                        _context.CopySubresourceRegion(_cropTexture, 0, 0, 0, 0, desktop, 0, _srcBox);

                    long presentTicks = (long)(info.LastPresentTime * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));
                    _onFrame(new DuplicationFrame(
                        presentTicks, Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds, info.ProtectedContentMaskedOut));
                }
                finally
                {
                    resource?.Dispose();
                    _duplication.ReleaseFrame();
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stop.IsSet)
                _onFault($"Desktop Duplication failed (0x{ex.HResult:X8}); Acrylic restored.");
        }
    }

    // Dirty and move rectangles are relative to the duplicated display. If the metadata is unavailable,
    // assume the panel changed: a redundant redraw is cheaper than a stale backdrop.
    private bool TouchesCrop(OutduplFrameInfo info, ref RawRect[] dirty, ref OutduplMoveRect[] moves)
    {
        if (info.TotalMetadataBufferSize == 0)
            return true;

        int dirtyCount = ReadRects(
            (uint size, RawRect[] buffer, out uint needed) => _duplication.GetFrameDirtyRects(size, buffer, out needed),
            ref dirty);
        int moveCount = ReadRects(
            (uint size, OutduplMoveRect[] buffer, out uint needed) => _duplication.GetFrameMoveRects(size, buffer, out needed),
            ref moves);
        if (dirtyCount < 0 || moveCount < 0)
            return true;

        var list = new PxRect[dirtyCount + moveCount];
        for (int i = 0; i < dirtyCount; i++)
            list[i] = ToPx(dirty[i]);
        for (int i = 0; i < moveCount; i++)
            list[dirtyCount + i] = ToPx(moves[i].DestinationRect);
        return CropMath.AnyIntersects(list, _crop);
    }

    private delegate Result ReadFn<T>(uint sizeBytes, T[] buffer, out uint neededBytes);

    private static int ReadRects<T>(ReadFn<T> read, ref T[] buffer) where T : struct
    {
        int elementSize = Unsafe.SizeOf<T>();
        Result result = read((uint)(buffer.Length * elementSize), buffer, out uint needed);
        if (result.Code == DxgiResultCode.MoreData.Code)
        {
            buffer = new T[(needed + elementSize - 1) / elementSize];
            result = read((uint)(buffer.Length * elementSize), buffer, out needed);
        }
        return result.Failure ? -1 : (int)(needed / elementSize);
    }

    private static PxRect ToPx(RawRect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public void Dispose()
    {
        _stop.Set();
        bool exited = _thread.ThreadState == System.Threading.ThreadState.Unstarted ||
            Thread.CurrentThread == _thread || _thread.Join(2000);
        if (!exited)
            return; // never free GPU objects under a running worker
        _duplication.Dispose();
        _cropTexture.Dispose();
        _context.Dispose();
        _d3d.Dispose();
        _stop.Dispose();
    }
}
