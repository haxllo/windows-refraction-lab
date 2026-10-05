# Desktop Refraction Lab

A **research prototype** (not production-ready) that compares two backdrops for a compact launcher-style panel on Windows:

- **A – Acrylic only:** the Windows App SDK Desktop Acrylic backdrop. Never starts screen capture. This is the default and the fallback for every failure.
- **B – Display refraction:** the desktop behind the panel is captured with Windows Graphics Capture, cropped to the panel, lightly blurred, and bent at the rim by a Direct2D displacement map (a convex glass bezel; the interior stays undistorted).

It is a separate app. Nothing here modifies or depends on Nex.

## Run

Windows 11 22H2+ (the Acrylic baseline matches what Nex uses), .NET 8 SDK, NuGet access for the first restore. From the repo root:

```powershell
dotnet run --project .\src\RefractionLab.csproj -c Release -p:Platform=x64
```

Or publish a self-contained folder (Windows App SDK is bundled; no separate runtime install):

```powershell
dotnet publish .\src\RefractionLab.csproj -c Release -p:Platform=x64 --self-contained true -o .\publish
.\publish\RefractionLab.exe
```

Logic tests (any OS): `dotnet test .\tests\RefractionLab.Tests\RefractionLab.Tests.csproj`

## Use

1. The panel opens centered (700 DIP wide, as in Nex) on Acrylic. Capture is off.
2. Click **B Display refraction**, then choose **the display that shows the panel** in the Windows picker. Cancelling, picking a window, or picking another display leaves Acrylic active.
3. A two-step check runs for about a second (see below). On success the badge reads **CAPTURE ACTIVE** and Windows draws its capture border around the display. Drag **Bend** to change rim strength.
4. **Stop capture**, **A Acrylic only**, hiding the panel, **Esc** (closes the app), or turning on Energy Saver all stop capture and release every capture resource.

Put something busy behind the panel (Settings → Personalization → Background slideshow, a video, a window with text) and switch A/B.

## Architecture and API rationale

| Concern | Choice | Why / documented limits |
|---|---|---|
| Capture | `Windows.Graphics.Capture` via `GraphicsCapturePicker`, one display, `Direct3D11CaptureFramePool.CreateFreeThreaded` (3 buffers) | Picker is the user's explicit, OS-mediated consent. `IsSupported()` gates it. The system capture border is left at its default (required); disabling it needs a consent API this app does not use. Cursor capture off (`IsCursorCaptureEnabled`, Win10 2004+). Frames arrive only when the display changes, so a static desktop costs almost nothing. |
| Self-exclusion | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | Documented for Win10 2004+: the window is shown on the monitor but "does not appear at all" elsewhere. It only works while DWM is composing, is a content-protection feature and not a security boundary, and on pre-2004 builds it degrades to `WDA_MONITOR`. |
| Proof of exclusion | Runtime two-step probe (below) | The docs do not promise it applies to every capture path, so the app measures it on the live stream every time instead of assuming it. |
| Rendering | Win2D `CanvasControl`: `GaussianBlurEffect` → `DisplacementMapEffect` (map from `BezelMap`) | Win2D formula: `result[p] = Source[p + Amount·(channel − 0.5)]`. One canvas unit = one physical pixel (`DpiScale = 1/RasterizationScale`) so frame, crop and map coordinates agree. Draws only on new frames or slider changes; no render loop. |
| Acrylic | `DesktopAcrylicBackdrop` + Nex's tint/edge tokens | Not byte-identical to Nex, which calls `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` directly. Windows replaces Acrylic with a solid color under Battery Saver, disabled transparency, high contrast, or RDP/VMs. |

Code map: `src/Logic/` is pure (probe matching, crop mapping, bezel map; unit-tested), `src/Native.cs` is the few user32 calls, `src/MainWindow.xaml.cs` is the capture state machine.

### The no-feedback-loop check

After the user picks a display, the app runs this against the live capture (≤ 4 s per step, otherwise it refuses):

1. **Positive control.** Window is *not* excluded. It draws a random 8×8 color grid and waits for a captured frame in which the grid is visible (≥ 75% of cells match). This proves the picked display is the panel's display and the coordinate mapping is right. A wrong display or a black/protected frame fails here.
2. **Exclusion.** It sets `WDA_EXCLUDEFROMCAPTURE` (and reads it back), keeps drawing the same grid on screen, and waits for a frame composed ≥ 150 ms after the change in which the grid is gone (≤ 40% match). A frame that still shows it means refusal. No fresh frame also means refusal.

Only the grid's pixels are read back; the rest of the frame stays in the OS-owned GPU surface. The decision logic (`ProbePattern`) is unit-tested, including blank/black frames, uniform wallpapers in each probe color, and 500 random mismatched patterns.

### Privacy and cost controls

- Capture only starts after an explicit click and picker choice; nothing is saved, streamed, logged or sent. Metrics are shown in the panel only.
- Retained: one panel-sized GPU image (plus a few px for blur), replaced each frame. During capture the OS holds full-display frame buffers for the chosen display; Windows Graphics Capture has no sub-rectangle target.
- ≤ 12 fps via a trailing-edge throttle (the last change is never dropped), 3-frame pool, newest frame wins. Capture stops on hide, Energy Saver, device reset, display-size change, or display removal.
- While refraction is active this window is also hidden from other screenshots and recorders (that is what the exclusion does).

## Verification

**Run in the Linux sandbox that produced this change** (no Windows, GPU, or desktop):

| Check | Result |
|---|---|
| Logic unit tests (`ProbePattern`, `CropMath`, `BezelMap`) | 31 passed. Three deliberate mutations (matcher always matches, bezel pointing outward, ignoring monitor origin) each made tests fail. |
| Package restore | Passes. The committed project previously failed with NU1605 (an explicit `Microsoft.Windows.SDK.BuildTools` pin below the version Windows App SDK requires); the pin was removed. |
| C# type-check of `src/` against Windows App SDK 1.8.260804001, Win2D 1.4.0 and the Windows SDK projections | 0 errors. XAML-generated members were stubbed; an injected error was caught. This is a compile check, not a build: the XAML compiler and `makepri.exe` are Windows-only executables. |
| Displacement-look simulation (numpy, using the real `BezelMap` output and Win2D's documented formula) | Interior unchanged, rim bends, text stays legible at default Bend. This checks the math only; it is not a screenshot of the app. |

**Not run, so not claimed:** XAML compile and publish on Windows (the included `ci` workflow does this on `windows-latest`; check its result), launching the app, the picker, the probe handshake on real hardware, Win2D rendering, DPI scaling, and every latency/CPU/GPU/battery number. The app has in-panel counters (accepted fps, frame age from `SystemRelativeTime`, crop-copy time, draw submission time, process CPU, working set) for collecting those on a target machine. GPU engine time and battery drain need Task Manager, PresentMon or `powercfg`.

### Manual checklist for a Windows machine

1. A works with no capture and no border. Toggle Windows transparency effects and Battery Saver: the panel should fall back to a solid color, not break.
2. B on the correct display ends in **CAPTURE ACTIVE** with the capture border visible. Repeat with: cancel, a window picked, and the wrong display (needs two monitors). Each must end on Acrylic with a message.
3. While active, take a screenshot: the panel should be absent from it. Press Stop: border disappears and the panel is capturable again.
4. Static wallpaper, then a video or slideshow behind the panel: static should show ~0 fps and idle CPU; moving content should hold ≤ 12 fps. Record counters and Task Manager GPU/Power columns.
5. Play protected video behind the panel: it must stay black/blank, with no alternate capture path.
6. Change display scale (100/125/150/200%), unplug/switch the display, lock/unlock, and sleep/resume while capture is active. Each should end on Acrylic or keep working; none should leave the border stuck on.

## Known limitations

- **Unvalidated on hardware.** The riskiest assumptions: that WGC delivers a fresh frame when exclusion removes the panel from the stream (if it does not, the handshake times out and the app refuses; it fails safe but refraction would never start), that `CanvasControl.DpiScale` normalizes to 96 DPI (otherwise the app refuses), and that `DisplacementMapEffect.Amount` is in pixels at 96 DPI.
- Whole-display capture only; the picker must be used every launch. The system capture border is part of the effect at display edges.
- Fixed panel position is assumed. Moving the panel produces no capture frame (the panel is excluded), so the refraction would be stale until the content behind changes. The panel must also be ≥ blur-pad pixels from the display edges.
- Protected content and genuinely black wallpapers look the same (black); the app shows what Windows supplies and does not try another capture API.
- HDR/WCG output, multi-GPU laptops, device loss recovery, and per-monitor DPI moves are untested. The probe's color match may fail under HDR tone mapping, which fails safe to Acrylic.
- The probe grid flashes for about a second at the top-right of the panel. That is intentional visible feedback.
- The effect samples only the pixels behind the panel; it has no depth, parallax, or chromatic dispersion.
- Not production-ready. Integrating into Nex would be a separate design: its panel is HTML in WebView2 on a transparent tao window with the DWM backdrop behind it, so a native compositor layer beneath the WebView would be needed, and none of that is prototyped here.

## Nex reference notes

Inspected read-only in a local checkout of `haxllo/nex` (Cargo version 2.22.10): `apps/core/src/overlay/host.rs` and `apps/core/assets/style.css`. Confirmed there: tao `WindowBuilder` with no decorations, transparent, always-on-top, no redirection bitmap; `WINDOW_WIDTH = 700.0`; a transparent wry/WebView2; `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)` with a `window_vibrancy` Acrylic fallback; acrylic tokens `--panel-surface: rgba(17,20,27,0.12)`, edge `rgba(255,255,255,0.2)`, radius 8px. This prototype copies the width, tint, edge and radius. **The reference screenshot was not available to this task**, so the layout (search row, three result rows, footer) is an approximation from those tokens and the CSS, not a pixel match. No Nex file was changed.

## References

- [Screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) (picker, `IsSupported`, border, `InitializeWithWindow` for desktop apps)
- [GraphicsCaptureSession.IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired?view=winrt-28000), [IsCursorCaptureEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled?view=winrt-28000), [CreateFreeThreaded](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded?view=winrt-28000)
- [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
- [Materials overview](https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials) (Acrylic fallbacks) and [System backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)
- [Win2D DisplacementMapEffect](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Effects_DisplacementMapEffect.htm) and [Direct2D displacement map](https://learn.microsoft.com/en-us/windows/win32/direct2d/displacement-map)
