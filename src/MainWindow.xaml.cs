using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.System.Power;
using RefractionLab.Logic;
using System.Diagnostics;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI;
using WinRT.Interop;

namespace RefractionLab;

public sealed partial class MainWindow : Window
{
    // Acrylic = no capture. Picking = Windows picker open. ProbeVisible/ProbeExcluded = the two-step
    // self-exclusion handshake. Refracting = verified; frames are cropped and displaced.
    private enum Mode { Acrylic, Picking, ProbeVisible, ProbeExcluded, Refracting }

    private readonly record struct Client(int Width, int Height, int ScreenX, int ScreenY);

    private const double PanelWidthDip = 700;   // Nex WINDOW_WIDTH
    private const double PanelHeightDip = 360;
    private const double BezelDip = 32;
    private const double CornerDip = 8;
    private const double BlurDip = 3;
    private const double MaxShiftDip = 28;
    private const int FrameBuffers = 3;
    private const long ExclusionSettleTicks = 150 * TimeSpan.TicksPerMillisecond;
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(83); // ~12 fps ceiling
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(4);

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _verifyTimer;
    private readonly DispatcherQueueTimer _drainTimer;
    private readonly DispatcherQueueTimer _metricsTimer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly object _gate = new();
    private readonly IntPtr _hwnd;

    private Mode _mode = Mode.Acrylic;
    private double _scale = 1;
    private bool _closing;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFrame? _pending;   // newest undelivered frame; guarded by _gate
    private bool _drainQueued;                  // guarded by _gate
    private bool _accepting;                    // guarded by _gate
    private long _lastDrain;

    private Rgb[] _pattern = [];
    private long _excludedAtTicks;
    private double _controlFraction;
    private double _clearFraction;
    private int _padPx;
    private CanvasRenderTarget? _crop;
    private CanvasBitmap? _bezelMap;
    private (int Width, int Height, float Radius, float Bezel) _bezelKey;

    private int _acceptedFrames;
    private long _fpsWindowStart = Stopwatch.GetTimestamp();
    private long _cpuSampleStart = Stopwatch.GetTimestamp();
    private TimeSpan _cpuSample;
    private double _fps, _ageMs, _copyMs, _drawMs, _cpuPct;

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue;
        _hwnd = WindowNative.GetWindowHandle(this);
        _verifyTimer = MakeTimer(VerifyTimeout, false, OnVerifyTimeout);
        _drainTimer = MakeTimer(MinFrameInterval, false, Drain);
        _metricsTimer = MakeTimer(TimeSpan.FromSeconds(1), true, OnMetricsTick);
        _cpuSample = _process.TotalProcessorTime;

        try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
        catch { GlassTint.Background = new SolidColorBrush(Color.FromArgb(245, 30, 30, 30)); }

        ConfigureWindow();
        RootGrid.Loaded += OnLoaded;
        RootGrid.KeyDown += OnKeyDown;
        GlassCanvas.CreateResources += (_, _) => { if (_mode != Mode.Acrylic) StopCapture("Graphics device was reset; Acrylic restored."); };
        AppWindow.Changed += OnAppWindowChanged;
        PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged;
        Closed += OnClosed;
        ApplyModeUi();
    }

    private DispatcherQueueTimer MakeTimer(TimeSpan interval, bool repeating, Action tick)
    {
        DispatcherQueueTimer timer = _dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private void ConfigureWindow()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        _scale = Math.Max(1, Native.GetDpiForWindow(_hwnd)) / 96.0;
        RectInt32 bounds = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
        int width = (int)Math.Round(PanelWidthDip * _scale);
        int height = (int)Math.Round(PanelHeightDip * _scale);
        AppWindow.MoveAndResize(new RectInt32(
            bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2,
            width,
            height));
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        UpdateScale();
        RootGrid.XamlRoot.Changed += (_, _) => UpdateScale();
        SearchBox.Focus(FocusState.Programmatic);
    }

    // One canvas unit == one physical pixel, so captured-frame, crop and map coordinates all agree.
    private void UpdateScale()
    {
        _scale = RootGrid.XamlRoot.RasterizationScale;
        GlassCanvas.DpiScale = (float)(1.0 / _scale);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape)
            Close();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange && !sender.IsVisible && _mode != Mode.Acrylic)
            StopCapture("Panel hidden; capture stopped.");
    }

    private void OnEnergySaverChanged(object? sender, object args) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_mode != Mode.Acrylic && EnergySaverOn())
                StopCapture("Energy saver turned on; capture stopped.");
        });

    private static bool EnergySaverOn()
    {
        try { return PowerManager.EnergySaverStatus == EnergySaverStatus.On; }
        catch { return false; }
    }

    private async void OnRefractionClick(object sender, RoutedEventArgs args)
    {
        if (_mode != Mode.Acrylic)
            return;
        if (!GraphicsCaptureSession.IsSupported())
        {
            StopCapture("Windows Graphics Capture is not supported here; Acrylic remains active.");
            return;
        }
        if (EnergySaverOn())
        {
            StopCapture("Energy saver is on; refraction not started. Acrylic remains active.");
            return;
        }

        _mode = Mode.Picking;
        ApplyModeUi();
        SetStatus("Choose the display that shows this panel in the Windows picker…");

        GraphicsCaptureItem? item;
        try
        {
            var picker = new GraphicsCapturePicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            item = await picker.PickSingleItemAsync();
        }
        catch (Exception ex)
        {
            if (!_closing && _mode == Mode.Picking)
                StopCapture($"Capture picker failed (0x{ex.HResult:X8}); Acrylic remains active.");
            return;
        }

        if (_closing || _mode != Mode.Picking)
            return;
        if (item is null)
        {
            StopCapture("Capture cancelled; Acrylic remains active.");
            return;
        }

        BeginCapture(item);
    }

    private void BeginCapture(GraphicsCaptureItem item)
    {
        try
        {
            if (!Native.TryGetMonitorRect(_hwnd, out Native.RECT monitor))
                throw new InvalidOperationException("Could not read this panel's display.");
            if (item.Size.Width != monitor.Right - monitor.Left || item.Size.Height != monitor.Bottom - monitor.Top)
            {
                StopCapture("That item is not the display showing this panel; Acrylic restored.");
                return;
            }
            if (Math.Abs(GlassCanvas.Dpi - 96f) > 0.5f)
            {
                StopCapture("Display scaling could not be normalized; Acrylic restored.");
                return;
            }

            _item = item;
            _item.Closed += OnItemClosed;
            _pattern = ProbePattern.Create(Random.Shared);
            _padPx = (int)Math.Ceiling(BlurDip * _scale * 3) + 2;
            _acceptedFrames = 0;

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                GlassCanvas.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FrameBuffers, item.Size);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false; // the system capture border stays at its default (required)
            lock (_gate) { _accepting = true; }

            Native.SetWindowDisplayAffinity(_hwnd, Native.WdaNone); // step 1 needs the panel capturable
            _mode = Mode.ProbeVisible;
            ApplyModeUi();
            SetStatus("Step 1/2: confirming this panel is visible in the capture…");
            _verifyTimer.Start();
            _metricsTimer.Start();
            _session.StartCapture();
            GlassCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            StopCapture($"Capture failed to start (0x{ex.HResult:X8}); Acrylic restored.");
        }
    }

    // Free-threaded pool thread: keep only the newest frame and hand it to the UI thread.
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame;
        try { frame = sender.TryGetNextFrame(); }
        catch { return; }
        if (frame is null)
            return;

        bool queue;
        lock (_gate)
        {
            if (!_accepting)
            {
                frame.Dispose();
                return;
            }
            _pending?.Dispose();
            _pending = frame;
            queue = !_drainQueued;
            _drainQueued = true;
        }

        if (queue && !_dispatcher.TryEnqueue(ScheduleDrain))
            lock (_gate) { _drainQueued = false; }
    }

    // Trailing-edge throttle: the last change is always processed, never dropped.
    private void ScheduleDrain()
    {
        TimeSpan wait = _mode == Mode.Refracting
            ? MinFrameInterval - Stopwatch.GetElapsedTime(_lastDrain)
            : TimeSpan.Zero;
        if (wait <= TimeSpan.Zero)
            Drain();
        else
        {
            _drainTimer.Interval = wait;
            _drainTimer.Start();
        }
    }

    private void Drain()
    {
        Direct3D11CaptureFrame? frame;
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
            _drainQueued = false;
        }
        if (frame is null)
            return;

        _lastDrain = Stopwatch.GetTimestamp();
        try { ProcessFrame(frame); }
        catch (Exception ex) { StopCapture($"Capture stopped ({ex.Message}); Acrylic restored."); }
        finally { frame.Dispose(); }
    }

    private void ProcessFrame(Direct3D11CaptureFrame frame)
    {
        if (_closing || _mode is Mode.Acrylic or Mode.Picking)
            return;
        if (!TryGetClient(out Client client) || !Native.TryGetMonitorRect(_hwnd, out Native.RECT m))
            throw new InvalidOperationException("could not locate the panel");
        var monitor = new PxRect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);
        if (frame.ContentSize.Width != monitor.Width || frame.ContentSize.Height != monitor.Height)
            throw new InvalidOperationException("captured display no longer matches the panel's display");

        long started = Stopwatch.GetTimestamp();
        using CanvasBitmap source = CanvasBitmap.CreateFromDirect3D11Surface(
            GlassCanvas.Device, frame.Surface, 96f, CanvasAlphaMode.Ignore);

        switch (_mode)
        {
            case Mode.ProbeVisible:
                _controlFraction = MeasureProbe(source, client, monitor);
                if (ProbePattern.Classify(_controlFraction) == ProbeVerdict.Present)
                    EnterExclusionStep();
                break;

            case Mode.ProbeExcluded:
                if (frame.SystemRelativeTime.Ticks < _excludedAtTicks + ExclusionSettleTicks)
                    break; // composed before the affinity change could have applied
                _clearFraction = MeasureProbe(source, client, monitor);
                switch (ProbePattern.Classify(_clearFraction))
                {
                    case ProbeVerdict.Absent:
                        EnterRefracting(item: _item!);
                        if (!CopyCrop(source, client, monitor, frame, started))
                            StopCapture("The panel is too close to the display edge to crop; Acrylic restored.");
                        break;
                    case ProbeVerdict.Present:
                        StopCapture("This window is still visible in the capture after exclusion; refraction refused.");
                        break;
                }
                break;

            case Mode.Refracting:
                CopyCrop(source, client, monitor, frame, started);
                break;
        }
    }

    private double MeasureProbe(CanvasBitmap source, Client client, PxRect monitor)
    {
        PxRect local = CropMath.ProbeRect(client.Width, _scale, out int cell);
        if (!CropMath.TryToFrame(local, client.ScreenX, client.ScreenY, monitor, out PxRect f))
            throw new InvalidOperationException("probe lies outside the captured display");
        byte[] pixels = source.GetPixelBytes(f.X, f.Y, f.Width, f.Height); // only the probe grid is read back
        return ProbePattern.MatchFraction(pixels, cell, cell, _pattern);
    }

    private void EnterExclusionStep()
    {
        if (!Native.ExcludeFromCapture(_hwnd))
            throw new InvalidOperationException("Windows refused to exclude this window from capture");
        _excludedAtTicks = (long)(Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));
        _mode = Mode.ProbeExcluded;
        _verifyTimer.Stop();
        _verifyTimer.Start();
        SetStatus("Step 2/2: confirming this window is now absent from the capture…");
        ApplyModeUi();
    }

    private void EnterRefracting(GraphicsCaptureItem item)
    {
        _verifyTimer.Stop();
        _mode = Mode.Refracting;
        SetStatus($"{item.DisplayName}  ·  exclusion verified (probe seen {_controlFraction:P0} → {_clearFraction:P0})  ·  latest panel crop in RAM only");
        ApplyModeUi();
    }

    private bool CopyCrop(CanvasBitmap source, Client client, PxRect monitor, Direct3D11CaptureFrame frame, long started)
    {
        PxRect panel = new(0, 0, client.Width, client.Height);
        if (!CropMath.TryToFrame(CropMath.Inflate(panel, _padPx), client.ScreenX, client.ScreenY, monitor, out PxRect f))
            return false;

        if (_crop is null || (int)_crop.SizeInPixels.Width != f.Width || (int)_crop.SizeInPixels.Height != f.Height)
        {
            _crop?.Dispose();
            _crop = new CanvasRenderTarget(GlassCanvas.Device, f.Width, f.Height, 96f);
        }

        using (CanvasDrawingSession ds = _crop.CreateDrawingSession())
        {
            ds.Blend = CanvasBlend.Copy;
            ds.DrawImage(source, 0, 0, new Rect(f.X, f.Y, f.Width, f.Height));
        }

        _copyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long now100ns = (long)(Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));
        _ageMs = Math.Max(0, (now100ns - frame.SystemRelativeTime.Ticks) / (double)TimeSpan.TicksPerMillisecond);
        _acceptedFrames++;
        GlassCanvas.Invalidate();
        return true;
    }

    private void OnGlassDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            float w = (float)sender.Size.Width;
            float h = (float)sender.Size.Height;
            if (_mode is Mode.ProbeVisible or Mode.ProbeExcluded)
                DrawProbe(args.DrawingSession);
            else if (_mode == Mode.Refracting && _crop is not null)
                DrawRefraction(sender, args.DrawingSession, w, h);
            _drawMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            if (_mode != Mode.Acrylic)
                _dispatcher.TryEnqueue(() => StopCapture($"Renderer failed (0x{ex.HResult:X8}); Acrylic restored."));
        }
    }

    private void DrawProbe(CanvasDrawingSession ds)
    {
        if (!TryGetClient(out Client client))
            return;
        PxRect r = CropMath.ProbeRect(client.Width, _scale, out int cell);
        for (int y = 0; y < ProbePattern.Cells; y++)
        for (int x = 0; x < ProbePattern.Cells; x++)
        {
            Rgb c = _pattern[y * ProbePattern.Cells + x];
            ds.FillRectangle(r.X + x * cell, r.Y + y * cell, cell, cell, Color.FromArgb(255, c.R, c.G, c.B));
        }
    }

    private void DrawRefraction(CanvasControl sender, CanvasDrawingSession ds, float w, float h)
    {
        float radius = (float)(CornerDip * _scale);
        float shift = (float)(BendSlider.Value / 100.0 * MaxShiftDip * _scale);
        EnsureBezelMap(sender.Device, (int)Math.Round(w), (int)Math.Round(h), radius, (float)(BezelDip * _scale));

        using CanvasGeometry clip = CanvasGeometry.CreateRoundedRectangle(sender, 0, 0, w, h, radius, radius);
        using CanvasActiveLayer layer = ds.CreateLayer(1f, clip);
        using var blur = new GaussianBlurEffect
        {
            Source = _crop,
            BlurAmount = (float)(BlurDip * _scale),
            BorderMode = EffectBorderMode.Hard,
        };
        using var map = new Transform2DEffect
        {
            Source = _bezelMap,
            TransformMatrix = Matrix3x2.CreateTranslation(_padPx, _padPx),
        };
        using var displaced = new DisplacementMapEffect
        {
            Source = blur,
            Displacement = map,
            Amount = 2 * shift, // map spans [-0.5, +0.5] of Amount, see BezelMap
            XChannelSelect = EffectChannelSelect.Red,
            YChannelSelect = EffectChannelSelect.Green,
        };
        ds.DrawImage(displaced, 0, 0, new Rect(_padPx, _padPx, w, h));
        ds.FillRectangle(0, 0, w, h, Color.FromArgb(56, 12, 14, 20));
    }

    private void EnsureBezelMap(CanvasDevice device, int width, int height, float radius, float bezel)
    {
        var key = (width, height, radius, bezel);
        if (_bezelMap is not null && _bezelKey == key)
            return;
        _bezelMap?.Dispose();
        _bezelMap = CanvasBitmap.CreateFromBytes(
            device, BezelMap.Generate(width, height, radius, bezel), width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f);
        _bezelKey = key;
    }

    private bool TryGetClient(out Client client)
    {
        client = default;
        var origin = new Native.POINT();
        if (!Native.GetClientRect(_hwnd, out Native.RECT rect) || !Native.ClientToScreen(_hwnd, ref origin) ||
            rect.Right <= 0 || rect.Bottom <= 0)
            return false;
        client = new Client(rect.Right, rect.Bottom, origin.X, origin.Y);
        return true;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) =>
        _dispatcher.TryEnqueue(() => StopCapture("The captured display went away; Acrylic restored."));

    private void OnVerifyTimeout()
    {
        StopCapture(_mode == Mode.ProbeVisible
            ? "Could not see this panel in the capture (wrong display, or capture blocked); Acrylic restored."
            : "Could not confirm this window is excluded from capture; refraction refused, Acrylic restored.");
    }

    private void OnAcrylicClick(object sender, RoutedEventArgs args) =>
        StopCapture("Acrylic only  ·  desktop capture is off");

    private void OnStopClick(object sender, RoutedEventArgs args) =>
        StopCapture("Capture stopped by user; Acrylic restored.");

    private void OnBendChanged(object sender, RangeBaseValueChangedEventArgs args) => GlassCanvas?.Invalidate();

    private void StopCapture(string status)
    {
        _mode = Mode.Acrylic;
        _verifyTimer.Stop();
        _drainTimer.Stop();
        _metricsTimer.Stop();
        lock (_gate)
        {
            _accepting = false;
            _pending?.Dispose();
            _pending = null;
            _drainQueued = false;
        }

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
            _item = null;
        }
        if (_pool is not null)
            _pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        _crop?.Dispose();
        _crop = null;
        _bezelMap?.Dispose();
        _bezelMap = null;
        Native.SetWindowDisplayAffinity(_hwnd, Native.WdaNone);

        if (_closing)
            return;
        SetStatus(status);
        MetricsText.Text = string.Empty;
        ApplyModeUi();
        GlassCanvas.Invalidate();
    }

    private void ApplyModeUi()
    {
        bool probing = _mode is Mode.ProbeVisible or Mode.ProbeExcluded;
        Canvas.SetZIndex(GlassCanvas, probing ? 3 : 0); // probe must not sit under the tint or controls
        Canvas.SetZIndex(GlassTint, 1);
        Canvas.SetZIndex(Controls, 2);

        StopButton.Visibility = _mode is Mode.Acrylic or Mode.Picking ? Visibility.Collapsed : Visibility.Visible;
        RefractionButton.IsEnabled = _mode == Mode.Acrylic;
        BendSlider.IsEnabled = _mode == Mode.Refracting;

        (string text, Color color) = _mode switch
        {
            Mode.Picking => ("CHOOSING DISPLAY", Color.FromArgb(255, 255, 197, 92)),
            Mode.ProbeVisible or Mode.ProbeExcluded => ("VERIFYING CAPTURE", Color.FromArgb(255, 255, 197, 92)),
            Mode.Refracting => ("CAPTURE ACTIVE", Color.FromArgb(255, 255, 120, 92)),
            _ => ("CAPTURE OFF", Color.FromArgb(255, 155, 163, 176)),
        };
        CaptureBadge.Text = text;
        CaptureBadge.Foreground = new SolidColorBrush(color);
        CaptureDot.Fill = new SolidColorBrush(color);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void OnMetricsTick()
    {
        long now = Stopwatch.GetTimestamp();
        double window = Stopwatch.GetElapsedTime(_fpsWindowStart, now).TotalSeconds;
        if (window > 0)
            _fps = _acceptedFrames / window;
        _acceptedFrames = 0;
        _fpsWindowStart = now;

        TimeSpan cpu = _process.TotalProcessorTime;
        double wallMs = Stopwatch.GetElapsedTime(_cpuSampleStart, now).TotalMilliseconds;
        _cpuPct = wallMs <= 0 ? 0 : (cpu - _cpuSample).TotalMilliseconds / wallMs * 100;
        _cpuSample = cpu;
        _cpuSampleStart = now;

        MetricsText.Text = _mode == Mode.Refracting
            ? $"{_fps:0.0} fps · age {_ageMs:0} ms · copy {_copyMs:0.0} ms · draw {_drawMs:0.0} ms · CPU {_cpuPct:0}% · {_process.WorkingSet64 / (1024 * 1024)} MB"
            : string.Empty;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged;
        StopCapture(string.Empty);
        _process.Dispose();
    }
}
