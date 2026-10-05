using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.System.Power;
using RefractionLab.Capture;
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

    private enum CaptureApi { None, Wgc, Duplication }

    private readonly record struct Client(int Width, int Height, int ScreenX, int ScreenY);

    private const double PanelWidthDip = 700;   // Nex WINDOW_WIDTH
    private const double PanelHeightDip = 396;
    private const double BezelDip = 32;
    private const double CornerDip = 8;
    private const double BlurDip = 3;
    private const double MaxShiftDip = 28;
    private const int FrameBuffers = 3;
    private const long ExclusionSettleTicks = 150 * TimeSpan.TicksPerMillisecond;
    private static readonly TimeSpan AutoStopAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NudgeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(4);

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _verifyTimer;
    private readonly DispatcherQueueTimer _drainTimer;
    private readonly DispatcherQueueTimer _metricsTimer;
    private readonly DispatcherQueueTimer _nudgeTimer;
    private readonly DispatcherQueueTimer _autoStopTimer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly object _gate = new();
    private readonly IntPtr _hwnd;

    private Mode _mode = Mode.Acrylic;
    private double _scale = 1;
    private bool _closing;
    private CaptureApi _api = CaptureApi.None;
    private TimeSpan _minFrameInterval = FramePacing.MinInterval(FramePacing.DefaultFps);

    private DuplicationSource? _dda;
    private int _ddaWake;                   // 1 while a UI wake-up is queued
    private long _ddaPresentTicks;
    private double _ddaCopyMs;
    private bool _ddaProtected;

    private string _borderNote = string.Empty;
    private int _displayHz;
    private GaussianBlurEffect? _blurFx;
    private Transform2DEffect? _mapFx;
    private DisplacementMapEffect? _displaceFx;
    private CanvasGeometry? _clipGeometry;
    private (CanvasBitmap?, CanvasBitmap?, float, float, float, float) _effectKey;

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
        _drainTimer = MakeTimer(_minFrameInterval, false, Drain);
        _metricsTimer = MakeTimer(TimeSpan.FromSeconds(1), true, OnMetricsTick);
        _nudgeTimer = MakeTimer(NudgeInterval, true, OnNudge);
        _autoStopTimer = MakeTimer(AutoStopAfter, false, () => StopCapture("Auto-stopped after 10 minutes; Acrylic restored."));
        _cpuSample = _process.TotalProcessorTime;

        try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
        catch { GlassTint.Background = new SolidColorBrush(Color.FromArgb(245, 30, 30, 30)); }

        ConfigureWindow();
        RootGrid.Loaded += OnLoaded;
        RootGrid.KeyDown += OnKeyDown;
        GlassCanvas.CreateResources += (_, e) =>
        {
            if (e.Reason == CanvasCreateResourcesReason.NewDevice && _mode != Mode.Acrylic)
                StopCapture("Graphics device was reset; Acrylic restored.");
        };
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
        else if (args.DidSizeChange && _api == CaptureApi.Duplication && _mode != Mode.Acrylic)
            StopCapture("Panel was resized; capture stopped.");
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

        bool hideBorder = await RequestBorderlessAsync();
        if (_closing || _mode != Mode.Picking)
            return;

        BeginCapture(item, hideBorder);
    }

    // Windows shows its own consent prompt and decides; the border is only hidden if it reports Allowed.
    private async Task<bool> RequestBorderlessAsync()
    {
        _borderNote = string.Empty;
        if (BorderlessBox.IsChecked != true)
            return false;

        try
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
            {
                _borderNote = "this Windows build cannot hide the border; it stays";
                return false;
            }

            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            if (status == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
            {
                _borderNote = "Windows allowed hiding its border";
                return true;
            }
            _borderNote = $"Windows answered {status}; the border stays";
        }
        catch (Exception ex)
        {
            _borderNote = $"border request failed (0x{ex.HResult:X8}); it stays";
        }
        return false;
    }

    private void BeginCapture(GraphicsCaptureItem item, bool hideBorder)
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
            _api = CaptureApi.Wgc;
            _displayHz = Native.GetRefreshRateHz(_hwnd);
            _pattern = ProbePattern.Create(Random.Shared);
            _padPx = (int)Math.Ceiling(BlurDip * _scale * 3) + 2;
            _acceptedFrames = 0;

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                GlassCanvas.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FrameBuffers, item.Size);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            if (hideBorder && Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                _session.IsBorderRequired = false; // honored only because Windows reported Allowed above
            lock (_gate) { _accepting = true; }

            Native.SetWindowDisplayAffinity(_hwnd, Native.WdaNone); // step 1 needs the panel capturable
            _mode = Mode.ProbeVisible;
            ApplyModeUi();
            SetStatus("Step 1/2: confirming this panel is visible in the capture…");
            _verifyTimer.Start();
            _nudgeTimer.Start();
            _metricsTimer.Start();
            _session.StartCapture();
            GlassCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            StopCapture($"Capture failed to start (0x{ex.HResult:X8}); Acrylic restored.");
        }
    }

    private async void OnDuplicationClick(object sender, RoutedEventArgs args)
    {
        if (_mode != Mode.Acrylic)
            return;
        if (EnergySaverOn())
        {
            StopCapture("Energy saver is on; refraction not started. Acrylic remains active.");
            return;
        }

        _mode = Mode.Picking;
        ApplyModeUi();
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "Capture this display without a Windows border?",
            Content = "Desktop Duplication has no capture border or picker, so Windows will not show that this app is reading the screen. " +
                "While it runs, this panel shows CAPTURE ACTIVE in red. Only the area behind the panel is copied, on the GPU; " +
                "nothing is saved or sent. Capture stops when you press Stop, hide the panel, or after 10 minutes.",
            PrimaryButtonText = "Start capture",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            if (!_closing && _mode == Mode.Picking)
                StopCapture($"Could not show the confirmation (0x{ex.HResult:X8}); Acrylic remains active.");
            return;
        }

        if (_closing || _mode != Mode.Picking)
            return;
        if (result != ContentDialogResult.Primary)
        {
            StopCapture("Capture cancelled; Acrylic remains active.");
            return;
        }

        BeginDuplication();
    }

    private void BeginDuplication()
    {
        try
        {
            if (!Native.TryGetMonitor(_hwnd, out IntPtr hmonitor, out Native.RECT m) || !TryGetClient(out Client client))
                throw new InvalidOperationException("Could not locate the panel.");
            if (Math.Abs(GlassCanvas.Dpi - 96f) > 0.5f)
            {
                StopCapture("Display scaling could not be normalized; Acrylic restored.");
                return;
            }

            var monitor = new PxRect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);
            _padPx = (int)Math.Ceiling(BlurDip * _scale * 3) + 2;
            var panel = new PxRect(0, 0, client.Width, client.Height);
            if (!CropMath.TryToFrame(CropMath.Inflate(panel, _padPx), client.ScreenX, client.ScreenY, monitor, out PxRect crop))
            {
                StopCapture("The panel is too close to the display edge to crop; Acrylic restored.");
                return;
            }

            _pattern = ProbePattern.Create(Random.Shared);
            _acceptedFrames = 0;
            _api = CaptureApi.Duplication;
            _displayHz = Native.GetRefreshRateHz(_hwnd);
            _borderNote = string.Empty;
            _crop = new CanvasRenderTarget(GlassCanvas.Device, crop.Width, crop.Height, 96f);
            Native.SetWindowDisplayAffinity(_hwnd, Native.WdaNone); // step 1 needs the panel capturable
            _dda = DuplicationSource.Start(
                GlassCanvas.Device, _crop, hmonitor, monitor, crop, _minFrameInterval, OnDuplicationFrame, OnDuplicationFault);

            _mode = Mode.ProbeVisible;
            ApplyModeUi();
            SetStatus("Step 1/2: confirming this panel is visible in the capture…");
            _verifyTimer.Start();
            _nudgeTimer.Start();
            _metricsTimer.Start();
            _autoStopTimer.Start();
            GlassCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            StopCapture(ex is InvalidOperationException
                ? $"{ex.Message} Acrylic restored."
                : $"Capture failed to start (0x{ex.HResult:X8}); Acrylic restored.");
        }
    }

    // Worker thread: keep the newest frame's details and wake the UI thread at most once at a time.
    private void OnDuplicationFrame(DuplicationFrame frame)
    {
        Interlocked.Exchange(ref _ddaPresentTicks, frame.PresentTicks);
        Volatile.Write(ref _ddaCopyMs, frame.CopyMs);
        Volatile.Write(ref _ddaProtected, frame.ProtectedMasked);
        if (Interlocked.Exchange(ref _ddaWake, 1) == 0 && !_dispatcher.TryEnqueue(OnDuplicationWake))
            Interlocked.Exchange(ref _ddaWake, 0);
    }

    private void OnDuplicationFault(string message) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_api == CaptureApi.Duplication && _mode != Mode.Acrylic)
                StopCapture(message);
        });

    private void OnDuplicationWake()
    {
        Interlocked.Exchange(ref _ddaWake, 0);
        if (_closing || _api != CaptureApi.Duplication || _crop is null || _mode is Mode.Acrylic or Mode.Picking)
            return;

        try
        {
            long presentTicks = Interlocked.Read(ref _ddaPresentTicks);
            switch (_mode)
            {
                case Mode.ProbeVisible:
                    _controlFraction = MeasureProbeInCrop();
                    if (ProbePattern.Classify(_controlFraction) == ProbeVerdict.Present)
                        EnterExclusionStep();
                    break;

                case Mode.ProbeExcluded:
                    if (presentTicks < _excludedAtTicks + ExclusionSettleTicks)
                        break; // composed before the affinity change could have applied
                    _clearFraction = MeasureProbeInCrop();
                    switch (ProbePattern.Classify(_clearFraction))
                    {
                        case ProbeVerdict.Absent:
                            EnterRefracting("Desktop Duplication");
                            RecordDuplicationFrame(presentTicks);
                            break;
                        case ProbeVerdict.Present:
                            StopCapture("This window is still visible in the capture after exclusion; refraction refused.");
                            break;
                    }
                    break;

                case Mode.Refracting:
                    RecordDuplicationFrame(presentTicks);
                    break;
            }
        }
        catch (Exception ex) { StopCapture($"Capture stopped ({ex.Message}); Acrylic restored."); }
    }

    private void RecordDuplicationFrame(long presentTicks)
    {
        long now100ns = (long)(Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));
        _ageMs = presentTicks <= 0 ? 0 : Math.Max(0, (now100ns - presentTicks) / (double)TimeSpan.TicksPerMillisecond);
        _copyMs = Volatile.Read(ref _ddaCopyMs);
        _acceptedFrames++;
        GlassCanvas.Invalidate();
    }

    // The probe sits inside the panel, so it is also inside the crop already copied on the GPU.
    private double MeasureProbeInCrop()
    {
        if (_crop is null || !TryGetClient(out Client client))
            throw new InvalidOperationException("could not locate the panel");
        PxRect local = CropMath.ProbeRect(client.Width, _scale, out int cell);
        byte[] pixels = _crop.GetPixelBytes(local.X + _padPx, local.Y + _padPx, local.Width, local.Height);
        return ProbePattern.MatchFraction(pixels, cell, cell, _pattern);
    }

    // Capture frames only arrive when the screen changes; redrawing the grid makes DWM compose a fresh one.
    private void OnNudge()
    {
        if (_mode is Mode.ProbeVisible or Mode.ProbeExcluded)
            GlassCanvas.Invalidate();
        else
            _nudgeTimer.Stop();
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
            ? _minFrameInterval - Stopwatch.GetElapsedTime(_lastDrain)
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
                        EnterRefracting(_item!.DisplayName);
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
        GlassCanvas.Invalidate();
    }

    private void EnterRefracting(string source)
    {
        _verifyTimer.Stop();
        _nudgeTimer.Stop();
        _mode = Mode.Refracting;
        string border = _borderNote.Length > 0 ? $"  ·  {_borderNote}" : string.Empty;
        SetStatus($"{source}  ·  exclusion verified (probe seen {_controlFraction:P0} → {_clearFraction:P0})  ·  only the panel crop is kept, in GPU memory{border}");
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
        EnsureEffects(sender, w, h, radius, (float)(BlurDip * _scale));

        _displaceFx!.Amount = 2 * shift; // map spans [-0.5, +0.5] of Amount, see BezelMap
        using CanvasActiveLayer layer = ds.CreateLayer(1f, _clipGeometry!);
        ds.DrawImage(_displaceFx, 0, 0, new Rect(_padPx, _padPx, w, h));
        ds.FillRectangle(0, 0, w, h, Color.FromArgb(56, 12, 14, 20));
    }

    // Built once per size or source change, so the per-frame cost is the draw itself.
    private void EnsureEffects(CanvasControl sender, float w, float h, float radius, float blurPx)
    {
        (CanvasBitmap?, CanvasBitmap?, float, float, float, float) key = (_crop, _bezelMap, w, h, radius, blurPx);
        if (_displaceFx is not null && _effectKey == key)
            return;

        DisposeEffects();
        _clipGeometry = CanvasGeometry.CreateRoundedRectangle(sender, 0, 0, w, h, radius, radius);
        _blurFx = new GaussianBlurEffect { Source = _crop, BlurAmount = blurPx, BorderMode = EffectBorderMode.Hard };
        _mapFx = new Transform2DEffect { Source = _bezelMap, TransformMatrix = Matrix3x2.CreateTranslation(_padPx, _padPx) };
        _displaceFx = new DisplacementMapEffect
        {
            Source = _blurFx,
            Displacement = _mapFx,
            XChannelSelect = EffectChannelSelect.Red,
            YChannelSelect = EffectChannelSelect.Green,
        };
        _effectKey = key;
    }

    private void DisposeEffects()
    {
        _displaceFx?.Dispose();
        _mapFx?.Dispose();
        _blurFx?.Dispose();
        _clipGeometry?.Dispose();
        _displaceFx = null;
        _mapFx = null;
        _blurFx = null;
        _clipGeometry = null;
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

    private void OnFpsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } } || !int.TryParse(tag, out int fps))
            return;
        _minFrameInterval = FramePacing.MinInterval(fps);
        if (_dda is not null)
            _dda.MinInterval = _minFrameInterval;
    }

    private void StopCapture(string status)
    {
        _mode = Mode.Acrylic;
        _verifyTimer.Stop();
        _drainTimer.Stop();
        _metricsTimer.Stop();
        _nudgeTimer.Stop();
        _autoStopTimer.Stop();
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
        _dda?.Dispose(); // joins the worker before the crop it writes to is released
        _dda = null;
        _crop?.Dispose();
        _crop = null;
        DisposeEffects();
        _bezelMap?.Dispose();
        _bezelMap = null;
        _api = CaptureApi.None;
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
        DuplicationButton.IsEnabled = _mode == Mode.Acrylic;
        BendSlider.IsEnabled = _mode == Mode.Refracting;

        (string text, Color color) = _mode switch
        {
            Mode.Picking => ("AWAITING YOUR CHOICE", Color.FromArgb(255, 255, 197, 92)),
            Mode.ProbeVisible or Mode.ProbeExcluded => ("VERIFYING CAPTURE", Color.FromArgb(255, 255, 197, 92)),
            Mode.Refracting => (_api == CaptureApi.Duplication ? "CAPTURE ACTIVE  ·  NO WINDOWS BORDER" : "CAPTURE ACTIVE", Color.FromArgb(255, 255, 90, 80)),
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

        string hz = _displayHz > 0 ? $"{_displayHz} Hz" : "? Hz";
        MetricsText.Text = _mode == Mode.Refracting
            ? $"{(_api == CaptureApi.Duplication ? "DXGI" : "WGC")} · {_fps:0.0} fps (display {hz}) · age {_ageMs:0} ms · copy {_copyMs:0.0} ms · draw {_drawMs:0.0} ms · CPU {_cpuPct:0}% of 1 core · {Environment.WorkingSet / (1024 * 1024)} MB" +
              (Volatile.Read(ref _ddaProtected) ? " · protected content is masked (black) on this display" : string.Empty)
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
