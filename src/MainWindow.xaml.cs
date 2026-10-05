using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI;
using WinRT.Interop;

namespace RefractionLab;

public sealed partial class MainWindow : Window
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;
    private const uint MonitorDefaultToNearest = 2;
    private const int FrameBufferCount = 2;
    private const int ProbeCells = 8;
    private const float ProbeCellSize = 7;
    private const float ProbeLeft = 710;
    private const float ProbeTop = 104;
    private const float CropPadding = 14;
    private const double MaxProcessFps = 12;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _probeTimer;
    private readonly DispatcherQueueTimer _metricsTimer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Color[] _probeColors = CreateProbeColors();
    private IntPtr _hwnd;
    private IntPtr _monitor;
    private MonitorInfoEx _monitorInfo;
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private CanvasRenderTarget? _latestCrop;
    private bool _captureActive;
    private bool _probeActive;
    private bool _closing;
    private int _probeClearFrames;
    private int _frameQueued;
    private long _lastAcceptedFrame;
    private long _acceptedFrames;
    private long _fpsWindowStart;
    private long _lastCpuSample;
    private TimeSpan _lastProcessCpu;
    private double _acceptedFps;
    private double _frameAgeMs;
    private double _cropMs;
    private double _drawCpuMs;
    private double _processCpuPct;

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue;
        _probeTimer = _dispatcher.CreateTimer();
        _probeTimer.Interval = TimeSpan.FromSeconds(2);
        _probeTimer.IsRepeating = false;
        _probeTimer.Tick += OnProbeTimeout;
        _metricsTimer = _dispatcher.CreateTimer();
        _metricsTimer.Interval = TimeSpan.FromSeconds(1);
        _metricsTimer.IsRepeating = true;
        _metricsTimer.Tick += OnMetricsTick;
        _fpsWindowStart = Stopwatch.GetTimestamp();
        _lastCpuSample = _fpsWindowStart;
        _lastProcessCpu = _process.TotalProcessorTime;

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            GlassTint.Background = new SolidColorBrush(Color.FromArgb(245, 18, 21, 28));
        }

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        RootGrid.Loaded += (_, _) => PositionPanel();
        _metricsTimer.Start();
        UpdateModeUi();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_hwnd != IntPtr.Zero)
            return;

        _hwnd = WindowNative.GetWindowHandle(this);
        PositionPanel();
    }

    private void PositionPanel()
    {
        if (_hwnd == IntPtr.Zero)
            _hwnd = WindowNative.GetWindowHandle(this);
        if (_hwnd == IntPtr.Zero)
            return;

        _monitor = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
        _monitorInfo = NewMonitorInfo();
        if (_monitor == IntPtr.Zero || !GetMonitorInfo(_monitor, ref _monitorInfo))
            return;

        uint dpi = GetDpiForWindow(_hwnd);
        float scale = Math.Max(1, dpi) / 96f;
        int width = (int)Math.Round(820 * scale);
        int height = (int)Math.Round(440 * scale);
        int x = _monitorInfo.Monitor.Left + (_monitorInfo.Monitor.Right - _monitorInfo.Monitor.Left - width) / 2;
        int y = _monitorInfo.Monitor.Top + (_monitorInfo.Monitor.Bottom - _monitorInfo.Monitor.Top - height) / 2;
        SetWindowPos(_hwnd, new IntPtr(-1), x, y, width, height, 0x0010 | 0x0040);
    }

    private async void OnRefractionClick(object sender, RoutedEventArgs args)
    {
        if (_captureActive)
            return;

        if (!GraphicsCaptureSession.IsSupported())
        {
            SetAcrylic("Windows Graphics Capture is unavailable; Acrylic remains active.");
            return;
        }

        if (!SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture) ||
            !GetWindowDisplayAffinity(_hwnd, out uint affinity) || affinity != WdaExcludeFromCapture)
        {
            SetAcrylic("Could not verify this window's capture exclusion; refraction was not started.");
            return;
        }

        try
        {
            var picker = new GraphicsCapturePicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            GraphicsCaptureItem? item = await picker.PickSingleItemAsync();
            if (item is null)
            {
                SetAcrylic("Capture cancelled; Acrylic remains active.");
                return;
            }

            if (!SelectedDisplayMatches(item))
            {
                SetAcrylic("Select the display containing this panel (not an app window); Acrylic remains active.");
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Start display capture?",
                Content = $"Windows selected {item.DisplayName} ({item.Size.Width} × {item.Size.Height}). Confirm that this is the display containing this panel. Only the panel crop is retained in memory; capture can be stopped at any time.",
                PrimaryButtonText = "Start capture",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                SetAcrylic("Capture not started; Acrylic remains active.");
                return;
            }

            StartCapture(item);
        }
        catch (Exception ex)
        {
            SetAcrylic($"Capture unavailable ({ex.HResult:X8}); Acrylic remains active.");
        }
    }

    private bool SelectedDisplayMatches(GraphicsCaptureItem item)
    {
        _monitor = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
        _monitorInfo = NewMonitorInfo();
        if (_monitor == IntPtr.Zero || !GetMonitorInfo(_monitor, ref _monitorInfo))
            return false;

        int monitorWidth = _monitorInfo.Monitor.Right - _monitorInfo.Monitor.Left;
        int monitorHeight = _monitorInfo.Monitor.Bottom - _monitorInfo.Monitor.Top;
        string deviceIndex = TrailingDigits(_monitorInfo.DeviceName);
        string itemIndex = TrailingDigits(item.DisplayName);
        bool displayName = Regex.IsMatch(item.DisplayName, @"^(display|monitor)\s*\d+", RegexOptions.IgnoreCase);
        return displayName && deviceIndex.Length > 0 && deviceIndex == itemIndex &&
            item.Size.Width == monitorWidth && item.Size.Height == monitorHeight;
    }

    private void StartCapture(GraphicsCaptureItem item)
    {
        try
        {
            _captureItem = item;
            _captureItem.Closed += OnCaptureItemClosed;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                GlassCanvas.Device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FrameBufferCount,
                item.Size);
            _framePool.FrameArrived += OnFrameArrived;
            _captureSession = _framePool.CreateCaptureSession(item);
            _captureSession.IsCursorCaptureEnabled = false;
            _captureSession.IsBorderRequired = true;

            _captureActive = true;
            _probeActive = true;
            _probeClearFrames = 0;
            _acceptedFps = 0;
            _acceptedFrames = 0;
            CaptureBadge.Text = "CAPTURE STARTING";
            CaptureBadge.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 197, 92));
            CaptureDot.Fill = new SolidColorBrush(Color.FromArgb(255, 255, 197, 92));
            StatusText.Text = "Checking the live capture for this window's exclusion…";
            GlassTint.Background = new SolidColorBrush(Color.FromArgb(82, 17, 20, 27));
            Panel.SetZIndex(GlassTint, 0);
            Panel.SetZIndex(Controls, 1);
            Panel.SetZIndex(GlassCanvas, 2);
            _probeTimer.Start();
            _captureSession.StartCapture();
            UpdateModeUi();
            GlassCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            SetAcrylic($"Capture failed to start ({ex.HResult:X8}); Acrylic remains active.");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
                return;

            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastAcceptedFrame);
            if (last != 0 && Stopwatch.GetElapsedTime(last, now).TotalSeconds < 1d / MaxProcessFps)
            {
                frame.Dispose();
                return;
            }
            Interlocked.Exchange(ref _lastAcceptedFrame, now);

            if (Interlocked.Exchange(ref _frameQueued, 1) != 0)
            {
                frame.Dispose();
                return;
            }

            Direct3D11CaptureFrame queuedFrame = frame;
            frame = null;
            if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    ProcessFrame(queuedFrame);
                }
                catch (Exception ex)
                {
                    StopCapture($"Capture frame failed ({ex.HResult:X8}); Acrylic restored.");
                }
                finally
                {
                    queuedFrame.Dispose();
                    Interlocked.Exchange(ref _frameQueued, 0);
                }
            }))
            {
                queuedFrame.Dispose();
                Interlocked.Exchange(ref _frameQueued, 0);
            }
        }
        catch (Exception ex)
        {
            frame?.Dispose();
            _dispatcher.TryEnqueue(() => StopCapture($"Capture stopped ({ex.HResult:X8}); Acrylic restored."));
        }
    }

    private void ProcessFrame(Direct3D11CaptureFrame frame)
    {
        if (!_captureActive || _closing)
            return;
        if (IsIconic(_hwnd))
        {
            StopCapture("Capture stopped because the panel was minimized.");
            return;
        }

        if (frame.ContentSize.Width != _captureItem!.Size.Width || frame.ContentSize.Height != _captureItem.Size.Height)
            throw new InvalidOperationException("Captured display dimensions changed.");

        long started = Stopwatch.GetTimestamp();
        using CanvasBitmap source = CanvasBitmap.CreateFromDirect3D11Surface(
            GlassCanvas.Device,
            frame.Surface,
            GlassCanvas.Dpi,
            CanvasAlphaMode.Ignore);

        if (_probeActive && !CheckSelfExclusion(source, frame.ContentSize))
        {
            StopCapture("Self-exclusion probe failed; refraction was disabled.");
            return;
        }

        RECT client = default;
        POINT origin = new();
        if (!GetClientRect(_hwnd, out client) || !ClientToScreen(_hwnd, ref origin))
            throw new InvalidOperationException("Could not locate the panel crop.");

        float dipWidth = (float)GlassCanvas.ActualWidth;
        float dipHeight = (float)GlassCanvas.ActualHeight;
        if (dipWidth <= 0 || dipHeight <= 0 || client.Right <= 0 || client.Bottom <= 0)
            return;

        float pxPerDipX = client.Right / dipWidth;
        float pxPerDipY = client.Bottom / dipHeight;
        float sourceLeft = (origin.X - _monitorInfo.Monitor.Left) * 96f / GlassCanvas.Dpi - CropPadding;
        float sourceTop = (origin.Y - _monitorInfo.Monitor.Top) * 96f / GlassCanvas.Dpi - CropPadding;
        float cropWidth = dipWidth + CropPadding * 2;
        float cropHeight = dipHeight + CropPadding * 2;
        if (sourceLeft < 0 || sourceTop < 0 || sourceLeft + cropWidth > source.Size.Width || sourceTop + cropHeight > source.Size.Height)
            throw new InvalidOperationException("Panel crop is outside the selected display.");

        var crop = new CanvasRenderTarget(
            GlassCanvas.Device,
            cropWidth,
            cropHeight,
            GlassCanvas.Dpi,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Ignore);
        using (CanvasDrawingSession drawing = crop.CreateDrawingSession())
        {
            drawing.DrawImage(
                source,
                new Windows.Foundation.Rect(0, 0, cropWidth, cropHeight),
                new Windows.Foundation.Rect(sourceLeft, sourceTop, cropWidth, cropHeight));
        }

        _latestCrop?.Dispose();
        _latestCrop = crop;
        _frameAgeMs = GetFrameAgeMs(frame.SystemRelativeTime);
        _cropMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Increment(ref _acceptedFrames);

        if (_probeActive)
        {
            _probeClearFrames++;
            if (_probeClearFrames >= 3)
            {
                _probeActive = false;
                _probeTimer.Stop();
                Panel.SetZIndex(GlassCanvas, 0);
                Panel.SetZIndex(GlassTint, 1);
                Panel.SetZIndex(Controls, 2);
                CaptureBadge.Text = "CAPTURE ACTIVE";
                CaptureBadge.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 197, 92));
                CaptureDot.Fill = new SolidColorBrush(Color.FromArgb(255, 255, 120, 92));
                StatusText.Text = $"{_captureItem.DisplayName}  ·  Self-exclusion verified  ·  latest panel crop in RAM";
            }
        }

        UpdateModeUi();
        GlassCanvas.Invalidate();
    }

    private bool CheckSelfExclusion(CanvasBitmap source, SizeInt32 frameSize)
    {
        if (_monitorInfo.Monitor.Right - _monitorInfo.Monitor.Left != frameSize.Width ||
            _monitorInfo.Monitor.Bottom - _monitorInfo.Monitor.Top != frameSize.Height)
            return false;

        RECT client = default;
        POINT origin = new();
        if (!GetClientRect(_hwnd, out client) || !ClientToScreen(_hwnd, ref origin))
            return false;

        float pxPerDipX = client.Right / (float)GlassCanvas.ActualWidth;
        float pxPerDipY = client.Bottom / (float)GlassCanvas.ActualHeight;
        int cellPixelsX = Math.Max(1, (int)Math.Round(ProbeCellSize * pxPerDipX));
        int cellPixelsY = Math.Max(1, (int)Math.Round(ProbeCellSize * pxPerDipY));
        int left = origin.X - _monitorInfo.Monitor.Left + (int)Math.Round(ProbeLeft * pxPerDipX);
        int top = origin.Y - _monitorInfo.Monitor.Top + (int)Math.Round(ProbeTop * pxPerDipY);
        int width = cellPixelsX * ProbeCells;
        int height = cellPixelsY * ProbeCells;
        if (left < 0 || top < 0 || left + width > frameSize.Width || top + height > frameSize.Height)
            return false;

        byte[] pixels = source.GetPixelBytes(left, top, width, height);
        int matching = 0;
        for (int y = 0; y < ProbeCells; y++)
        for (int x = 0; x < ProbeCells; x++)
        {
            int offset = ((y * cellPixelsY + cellPixelsY / 2) * width + x * cellPixelsX + cellPixelsX / 2) * 4;
            Color expected = _probeColors[y * ProbeCells + x];
            if (Math.Abs(pixels[offset] - expected.B) < 45 &&
                Math.Abs(pixels[offset + 1] - expected.G) < 45 &&
                Math.Abs(pixels[offset + 2] - expected.R) < 45)
                matching++;
        }

        if (matching >= ProbeCells * ProbeCells * 3 / 4)
            return false;
        return true;
    }

    private void OnGlassDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            CanvasDrawingSession drawing = args.DrawingSession;
            if (_captureActive && _latestCrop is not null)
            {
                using var turbulence = new TurbulenceEffect
                {
                    Frequency = new Vector2(0.018f, 0.012f),
                    Octaves = 2,
                    Seed = 11
                };
                using var displacement = new DisplacementMapEffect
                {
                    Source = _latestCrop,
                    Displacement = turbulence,
                    Amount = 9,
                    XChannelSelect = EffectChannelSelect.Red,
                    YChannelSelect = EffectChannelSelect.Green
                };

                float width = (float)sender.ActualWidth;
                float height = (float)sender.ActualHeight;
                drawing.DrawImage(
                    displacement,
                    new Windows.Foundation.Rect(0, 0, width, height),
                    new Windows.Foundation.Rect(CropPadding, CropPadding, width, height));
                drawing.FillRoundedRectangle(
                    new Windows.Foundation.Rect(0, 0, width, height),
                    14,
                    14,
                    Color.FromArgb(_probeActive ? (byte)96 : (byte)48, 13, 16, 23));
            }

            if (_probeActive)
            {
                for (int y = 0; y < ProbeCells; y++)
                for (int x = 0; x < ProbeCells; x++)
                {
                    drawing.FillRectangle(
                        new Windows.Foundation.Rect(
                            ProbeLeft + x * ProbeCellSize,
                            ProbeTop + y * ProbeCellSize,
                            ProbeCellSize,
                            ProbeCellSize),
                        _probeColors[y * ProbeCells + x]);
                }
            }

            _drawCpuMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            if (_captureActive)
                _dispatcher.TryEnqueue(() => StopCapture($"Refraction renderer failed ({ex.HResult:X8}); Acrylic restored."));
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        _dispatcher.TryEnqueue(() => StopCapture("The selected display closed; Acrylic restored."));
    }

    private void OnProbeTimeout(DispatcherQueueTimer sender, object args)
    {
        if (_probeActive)
            StopCapture("Self-exclusion could not be verified; Acrylic restored.");
    }

    private void OnMetricsTick(DispatcherQueueTimer sender, object args)
    {
        if (_captureActive && IsIconic(_hwnd))
        {
            StopCapture("Capture stopped because the panel was minimized.");
            return;
        }

        if (!_captureActive)
        {
            MetricsText.Text = string.Empty;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_fpsWindowStart, now).TotalSeconds;
        if (elapsed >= 1)
        {
            _acceptedFps = Interlocked.Exchange(ref _acceptedFrames, 0) / elapsed;
            _fpsWindowStart = now;
        }

        TimeSpan cpu = _process.TotalProcessorTime;
        double wallMs = Stopwatch.GetElapsedTime(_lastCpuSample, now).TotalMilliseconds;
        _processCpuPct = wallMs <= 0 ? 0 : Math.Max(0, (cpu - _lastProcessCpu).TotalMilliseconds / wallMs * 100);
        _lastCpuSample = now;
        _lastProcessCpu = cpu;
        MetricsText.Text = $"{_acceptedFps:0} fps · age {_frameAgeMs:0} ms · crop {_cropMs:0.0} ms · draw {_drawCpuMs:0.0} ms · CPU {_processCpuPct:0}% · RAM {_process.WorkingSet64 / (1024 * 1024)} MB";
    }

    private void OnAcrylicClick(object sender, RoutedEventArgs args)
    {
        StopCapture("Acrylic only  ·  desktop capture is off");
    }

    private void OnStopClick(object sender, RoutedEventArgs args)
    {
        StopCapture("Capture stopped by user; Acrylic restored.");
    }

    private void StopCapture(string status)
    {
        _probeTimer.Stop();
        _probeActive = false;
        _captureActive = false;
        ReleaseCaptureResources();
        if (_hwnd != IntPtr.Zero)
            SetWindowDisplayAffinity(_hwnd, WdaNone);

        Panel.SetZIndex(GlassCanvas, 0);
        Panel.SetZIndex(GlassTint, 1);
        Panel.SetZIndex(Controls, 2);
        GlassTint.Background = new SolidColorBrush(Color.FromArgb(184, 17, 20, 27));
        CaptureBadge.Text = "CAPTURE OFF";
        CaptureBadge.Foreground = new SolidColorBrush(Color.FromArgb(255, 155, 163, 176));
        CaptureDot.Fill = new SolidColorBrush(Color.FromArgb(255, 104, 113, 127));
        StatusText.Text = status;
        MetricsText.Text = string.Empty;
        UpdateModeUi();
        GlassCanvas.Invalidate();
    }

    private void SetAcrylic(string status) => StopCapture(status);

    private void ReleaseCaptureResources()
    {
        if (_captureItem is not null)
        {
            _captureItem.Closed -= OnCaptureItemClosed;
            _captureItem = null;
        }
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }
        _captureSession?.Dispose();
        _captureSession = null;
        _latestCrop?.Dispose();
        _latestCrop = null;
        Interlocked.Exchange(ref _frameQueued, 0);
    }

    private void UpdateModeUi()
    {
        StopButton.Visibility = _captureActive ? Visibility.Visible : Visibility.Collapsed;
        RefractionButton.IsEnabled = !_captureActive;
        AcrylicButton.IsEnabled = true;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _probeTimer.Stop();
        _metricsTimer.Stop();
        _captureActive = false;
        _probeActive = false;
        ReleaseCaptureResources();
        if (_hwnd != IntPtr.Zero)
            SetWindowDisplayAffinity(_hwnd, WdaNone);
        _process.Dispose();
    }

    private static double GetFrameAgeMs(TimeSpan systemRelativeTime)
    {
        long now100ns = (long)(Stopwatch.GetTimestamp() * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        return Math.Max(0, (now100ns - systemRelativeTime.Ticks) / (double)TimeSpan.TicksPerMillisecond);
    }

    private static Color[] CreateProbeColors()
    {
        Color[] palette =
        [
            Color.FromArgb(255, 244, 70, 83),
            Color.FromArgb(255, 62, 220, 133),
            Color.FromArgb(255, 72, 130, 248),
            Color.FromArgb(255, 248, 205, 63),
            Color.FromArgb(255, 211, 79, 225),
            Color.FromArgb(255, 54, 211, 222),
            Color.FromArgb(255, 244, 245, 246)
        ];
        var random = new Random(Environment.TickCount ^ Environment.ProcessId);
        Color[] result = new Color[ProbeCells * ProbeCells];
        for (int i = 0; i < result.Length; i++)
            result[i] = palette[random.Next(palette.Length)];
        return result;
    }

    private static string TrailingDigits(string value)
    {
        Match match = Regex.Match(value, @"(\d+)\s*$");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static MonitorInfoEx NewMonitorInfo() => new() { Size = Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
