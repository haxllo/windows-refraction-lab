# Desktop Refraction Lab

A **research prototype** (not production-ready) that compares backdrops for a compact launcher-style panel on Windows:

- **A – Acrylic only:** the Windows App SDK Desktop Acrylic backdrop. Never starts screen capture. This is the default and the fallback for every failure.
- **B – Refraction, Windows capture:** the desktop behind the panel is captured with Windows Graphics Capture (you pick the display; Windows draws its yellow capture border), cropped to the panel, lightly blurred, and bent at the rim by a Direct2D displacement map (a convex glass bezel; the interior stays undistorted).
- **C – Refraction, no border:** the same rendering fed by DXGI Desktop Duplication. No picker and no Windows border, so the app confirms with you first and shows its own red badge. See the research summary for the trade-off.

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
2. **B:** click it, then choose **the display that shows the panel** in the Windows picker. Cancelling, picking a window, or picking another display leaves Acrylic active. **C:** click it and confirm the dialog; the display is found automatically.
3. A two-step check runs for about a second (see below). On success the badge reads **CAPTURE ACTIVE** (B also shows Windows' capture border; C adds **NO WINDOWS BORDER**). Drag **Bend** to change rim strength and use **Max FPS** (15, 30, 60 or Unlimited) to trade smoothness for GPU use. The stats line shows the achieved fps next to your display's refresh rate; capture cannot usefully exceed that rate.
4. Optional, mode B only: tick **Ask Windows to hide border (B)** before starting. Windows shows its own consent prompt and decides; the status line reports its answer, and the border stays unless Windows says Allowed.
5. **Stop capture**, **A Acrylic only**, hiding the panel, **Esc** (closes the app), or turning on Energy Saver all stop capture and release every capture resource. C also stops after 10 minutes.

Put something busy behind the panel (Settings → Personalization → Background slideshow, a video, a window with text) and switch A/B.

## Can Windows do real refraction? Research summary

Yes. It is not available from Windows' own Acrylic, so an app has to capture and render it itself.

- **Acrylic can't be bent.** Win2D's `DisplacementMapEffect` is documented as supported by Win2D but not by Windows.UI.Composition, and DWM exposes backdrop blur, not distortion. Real refraction needs the app's own copy of the pixels behind the panel.
- **Two public APIs can supply them:**

| | Windows Graphics Capture (mode B) | DXGI Desktop Duplication (mode C) |
|---|---|---|
| User consent and indicator | Picker plus a yellow system border. The border can be turned off only after `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` shows a user prompt, and that call requires the `graphicsCaptureWithoutBorder` capability in a package manifest. | None from Windows. The app has to provide its own indicator. |
| Availability | Win10 1803 and later; cursor toggle 2004; border opt-out API 10.0.20348+ | Windows 8 and later |
| Frames | Up to the display refresh rate, only when content changes | Same, plus dirty/move rectangles and a `ProtectedContentMaskedOut` flag |
| Fails when | The captured item closes | `DXGI_ERROR_ACCESS_LOST` on a desktop switch (UAC, lock), mode change or full-screen app |

- **Others do this today.** From their READMEs only (I have not run them or checked their performance claims): `electron-liquid-glass` uses Desktop Duplication, D3D11 shaders and DirectComposition, excludes its own window with `WDA_EXCLUDEFROMCAPTURE`, and drops DWM "echo" updates; `liquidDX11` uses DXGI capture with HLSL refraction; `liquid-glass-WinUI` uses a Win2D displacement chain.
- **The border is a policy choice, not a bug.** Mode C removes the only OS-level signal that the screen is being read, so this prototype adds a confirmation dialog, a persistent red badge, a 10-minute auto-stop and the same self-exclusion check. Whether that is acceptable for a shipped launcher is a product decision. The OS-approved borderless route needs a packaged (MSIX) app; the Nex repo has an Inno Setup script (`scripts/windows/nex.iss`) and I saw no MSIX packaging.
- **Low fps in the first build was my cap.** It limited processing to 12 fps to keep GPU and battery cost down. That was a conservative choice, not a platform limit. The default is now 30 fps. The second build's top option was labelled "60" but actually meant uncapped; it is now a real 60 cap, and **Unlimited** is its own option. Capture rate is bounded by content changes and the display's refresh rate (the Microsoft sample maintainer: capture "will go up to the refresh rate", [GitHub issue #142](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/issues/142)), so on a 60 Hz display about 60 is the ceiling. The stats line now shows the display's refresh rate to make that visible.
- **Borderless on B is an experiment.** Microsoft documents the opt-out as `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` plus a `graphicsCaptureWithoutBorder` capability in a package manifest. This app is unpackaged, so the checkbox simply asks and reports Windows' answer; whether an unpackaged app can be allowed is exactly what it tests.
- **Known risk for C:** on Windows 11 24H2 with multiplane overlay, an excluded window's own redraws can wake Desktop Duplication ([Win32CaptureSample#83](https://github.com/robmikh/Win32CaptureSample/issues/83)). Here that could make the panel re-trigger itself on a static desktop. The in-panel fps counter shows it: if fps stays near the cap with nothing moving, that loop exists.

## Architecture and API rationale

| Concern | Choice | Why / documented limits |
|---|---|---|
| Capture | `Windows.Graphics.Capture` via `GraphicsCapturePicker`, one display, `Direct3D11CaptureFramePool.CreateFreeThreaded` (3 buffers) | Picker is the user's explicit, OS-mediated consent. `IsSupported()` gates it. The system capture border is left at its default (required); disabling it needs a consent API this app does not use. Cursor capture off (`IsCursorCaptureEnabled`, Win10 2004+). Frames arrive only when the display changes, so a static desktop costs almost nothing. |
| Self-exclusion | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | Documented for Win10 2004+: the window is shown on the monitor but "does not appear at all" elsewhere. It only works while DWM is composing, is a content-protection feature and not a security boundary, and on pre-2004 builds it degrades to `WDA_MONITOR`. |
| Proof of exclusion | Runtime two-step probe (below) | The docs do not promise it applies to every capture path, so the app measures it on the live stream every time instead of assuming it. |
| Rendering | Win2D `CanvasControl`: `GaussianBlurEffect` → `DisplacementMapEffect` (map from `BezelMap`) | Win2D formula: `result[p] = Source[p + Amount·(channel − 0.5)]`. One canvas unit = one physical pixel (`DpiScale = 1/RasterizationScale`) so frame, crop and map coordinates agree. Draws only on new frames or slider changes; no render loop. |
| Acrylic | `DesktopAcrylicBackdrop` + Nex's tint/edge tokens | Not byte-identical to Nex, which calls `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` directly. Windows replaces Acrylic with a solid color under Battery Saver, disabled transparency, high contrast, or RDP/VMs. |

**Mode C capture path.** `DuplicationSource` (worker thread) reuses Win2D's own D3D11 device (obtained through `IDirect3DDxgiInterfaceAccess`), finds the output whose monitor matches the panel's, and duplicates it. It sleeps for the Max FPS interval between acquires (updates accumulate in DXGI meanwhile, so the newest state is delivered), skips frames whose dirty or move rectangles miss the panel, and copies only the panel rectangle on the GPU into the Win2D render target. The probe reads back just the 48-pixel grid from that crop. It refuses rotated displays, system-memory desktop images and outputs on another GPU. D3D11/DXGI bindings come from [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) 3.8.3.

Code map: `src/Logic/` is pure (probe matching, crop mapping, bezel map, frame pacing; unit-tested), `src/Capture/` is Desktop Duplication and its D3D11 interop, `src/Native.cs` is the few user32 calls, `src/MainWindow.xaml.cs` is the UI and capture state machine.

### The no-feedback-loop check

After capture starts (B: after you pick a display; C: after you confirm), the app runs this against the live capture. It has 4 s in total and a redraw every 250 ms so Windows keeps producing frames; otherwise it refuses:

1. **Positive control.** Window is *not* excluded. It draws a random 8×8 color grid and waits for a captured frame in which the grid is visible (≥ 75% of cells match). This proves the picked display is the panel's display and the coordinate mapping is right. A wrong display or a black/protected frame fails here.
2. **Exclusion.** It sets `WDA_EXCLUDEFROMCAPTURE` (and reads it back), keeps drawing the same grid on screen, and waits for a frame composed ≥ 150 ms after the change in which the grid is gone (≤ 40% match). A frame that still shows it means refusal. No fresh frame also means refusal.

Only the grid's pixels are read back; the rest of the frame stays in the OS-owned GPU surface. The decision logic (`ProbePattern`) is unit-tested, including blank/black frames, uniform wallpapers in each probe color, and 500 random mismatched patterns.

### Privacy and cost controls

- Capture only starts after an explicit click (B: and a picker choice; C: and a confirmation dialog); nothing is saved, streamed, logged or sent. Metrics are shown in the panel only.
- Retained: one panel-sized GPU image (plus a few px for blur), replaced each frame. B: the OS holds full-display frame buffers because Windows Graphics Capture has no sub-rectangle target. C: the full desktop image exists only while each frame is acquired; just the panel rectangle is copied out.
- Max FPS (default 30, options 15/30/60/Unlimited) is a trailing-edge throttle, so the last change is never dropped. Capped values wait on a UI-thread timer, so the achieved rate can land below the cap (not measured); Unlimited skips the wait. Capture stops on hide, Energy Saver, device reset, display-size change, or display removal.
- While refraction is active this window is also hidden from other screenshots and recorders (that is what the exclusion does).

## Verification

**Run in the Linux sandbox that produced this change** (no Windows, GPU, or desktop):

| Check | Result |
|---|---|
| Logic unit tests (`ProbePattern`, `CropMath`, `BezelMap`, `FramePacing`) | 49 passed. For the original 31, three deliberate mutations (matcher always matches, bezel pointing outward, ignoring monitor origin) each made tests fail. |
| Package restore | Passes. The committed project previously failed with NU1605 (an explicit `Microsoft.Windows.SDK.BuildTools` pin below the version Windows App SDK requires); the pin was removed. |
| C# type-check of `src/` against Windows App SDK 1.8.260804001, Win2D 1.4.0, Vortice 3.8.3 and the Windows SDK projections | 0 errors. XAML-generated members were stubbed; injected errors were caught, and it found two real mistakes in the new mode C code before commit. This is a compile check only: the XAML compiler and `makepri.exe` are Windows-only executables, so it could not see XAML errors. |
| Windows build in CI (`windows-latest`): `dotnet build` and self-contained `dotnet publish`, `RefractionLab.exe` present | Passes. The first run failed with a XAML parse error (WMC9997: `--` inside an XML comment) that the Linux type-check could not detect; fixed. This proves the project compiles and packages, not that it runs. |
| Displacement-look simulation (numpy, using the real `BezelMap` output and Win2D's documented formula) | Interior unchanged, rim bends, text stays legible at default Bend. This checks the math only; it is not a screenshot of the app. |

Mode C compiles and builds but has **never been run**: the D3D11 interop, `WDA_EXCLUDEFROMCAPTURE` with Desktop Duplication, and the dirty-rectangle filter are unverified. The Windows interop IID it uses was confirmed only by finding its bytes in Win2D's native DLLs.

**User-reported, one run (not independently verified; hardware and refresh rate not recorded):** mode B with a video playing reached about 40 fps, frame age 0 ms, crop copy 0.3 ms, 12-30% CPU of one core and 139 MB. That RAM figure came from a build that read `Process.WorkingSet64` without refreshing it, so it may have been a stale first reading; the app now uses `Environment.WorkingSet`. The same build rebuilt the blur and displacement effect graph on every frame; it is now built once per size change. How much CPU that saves has not been measured.

**Not run, so not claimed:** mode C on any machine, the probe handshake on real hardware for either mode, DPI scaling, the borderless request outcome, and every GPU and battery number (CI runners have no interactive desktop or GPU). The app has in-panel counters (accepted fps, frame age from `SystemRelativeTime`, crop-copy time, draw submission time, process CPU, working set) for collecting those on a target machine. GPU engine time and battery drain need Task Manager, PresentMon or `powercfg`.

### Manual checklist for a Windows machine

1. A works with no capture and no border. Toggle Windows transparency effects and Battery Saver: the panel should fall back to a solid color, not break.
2. B on the correct display ends in **CAPTURE ACTIVE** with the capture border visible. Repeat with: cancel, a window picked, and the wrong display (needs two monitors). Each must end on Acrylic with a message.
3. While active, take a screenshot: the panel should be absent from it. Press Stop: border disappears and the panel is capturable again.
4. Static wallpaper, then a video or slideshow behind the panel, for B and C at 15/30/60/Unlimited: static should show ~0 fps and idle CPU; moving content should approach the chosen rate, never above the displayed refresh rate. If Unlimited stays well below the display rate while content is moving, note the display rate and the fps; on Windows 11 24H2 the next thing to try would be `GraphicsCaptureSession.MinUpdateInterval` (not used yet). For C, a static desktop that still shows fps near the cap is the 24H2 self-trigger loop. Record the counters and Task Manager GPU/Power columns.
5. Play protected video behind the panel: it must stay black/blank, with no alternate capture path.
6. Change display scale (100/125/150/200%), unplug/switch the display, lock/unlock, trigger a UAC prompt, and sleep/resume while capture is active. Each should end on Acrylic or keep working; none should leave the border stuck on (B) or capture running unnoticed (C).

## Known limitations

- **Unvalidated on hardware.** The riskiest assumptions: that the capture stream delivers a fresh frame when exclusion removes the panel (if not, the handshake times out and the app refuses; it fails safe but refraction would never start), that `WDA_EXCLUDEFROMCAPTURE` is honored by Desktop Duplication, that Win2D's D3D11 device can be shared with a second thread under its device lock, that `CanvasControl.DpiScale` normalizes to 96 DPI (otherwise the app refuses), and that `DisplacementMapEffect.Amount` is in pixels at 96 DPI.
- **Mode C specifics.** One GPU only: if the panel's display is on another adapter than Win2D's, it refuses. Rotated displays are refused. A UAC prompt, lock screen or mode change ends capture (`ACCESS_LOST`) and returns to Acrylic rather than reconnecting. No echo-rectangle filtering beyond the panel-rectangle test, so the 24H2 self-trigger risk above is open.
- Whole-display capture only; the picker must be used every launch. The system capture border is part of the effect at display edges.
- Mode B processes every frame Windows delivers for the whole display, including changes far from the panel (it has no dirty-region filter; mode C does). A video elsewhere on screen therefore costs work even though the panel is unaffected.
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
- [GraphicsCaptureAccess.RequestAccessAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync?view=winrt-28000) (borderless consent and the `graphicsCaptureWithoutBorder` capability)
- [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api), [AcquireNextFrame](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe), [DXGI_OUTDUPL_FRAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info)
- Prior art (READMEs only): [electron-liquid-glass](https://github.com/hicccc77/electron-liquid-glass), [liquidDX11](https://github.com/Pondot/liquidDX11), [liquid-glass-WinUI](https://github.com/pratikone/liquid-glass-WinUI)
- [Materials overview](https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials) (Acrylic fallbacks) and [System backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)
- [Win2D DisplacementMapEffect](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Effects_DisplacementMapEffect.htm) and [Direct2D displacement map](https://learn.microsoft.com/en-us/windows/win32/direct2d/displacement-map)
