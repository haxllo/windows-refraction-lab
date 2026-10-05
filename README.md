# Desktop Refraction Lab

A separate Windows research demo for a compact, dark launcher panel. It compares the operating-system desktop Acrylic backdrop with a captured-display displacement effect. It does not reference or modify Nex.

## Run

Requires Windows 11 22H2 or later, .NET 8 SDK, and network access for the first NuGet restore. From this directory, run in PowerShell:

```powershell
dotnet run --project .\src\RefractionLab.csproj -c Release -r win-x64
```

Or publish a self-contained folder and launch the executable:

```powershell
dotnet publish .\src\RefractionLab.csproj -c Release -r win-x64 --self-contained true -o .\publish
.\publish\RefractionLab.exe
```

The project pins [Microsoft.WindowsAppSDK package version 1.8.260804001](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/1.8.260804001) and Win2D 1.4.0. Windows App SDK's unpackaged self-contained deployment is enabled, so the published app does not require the Windows App Runtime to be installed separately. .NET 8 is included by the `--self-contained` publish command.

## Use

1. The panel opens centered on the current display. **Acrylic only** is the default and never starts screen capture.
2. Choose **Display refraction** and approve the Windows Graphics Capture picker. Select the display containing this panel, not an individual app window.
3. The demo checks the selected item's dimensions, confirms the display choice, applies `WDA_EXCLUDEFROMCAPTURE` to its own top-level window, and briefly shows a randomized color probe. It only enables refraction if the probe verifies that the panel is absent from the returned display frame.
4. While active, the system capture border and an in-panel **CAPTURE ACTIVE** badge remain visible. Select **Stop capture** or **Acrylic only** to stop and release capture resources. Closing the window also stops capture.

The probe's brief color pattern is intentional. If capture startup, the HWND exclusion API, display matching, or the probe fails, the app stops capture and stays on Acrylic.

## Design and API rationale

- **Capture:** `Windows.Graphics.Capture` with the Windows picker, one selected display, a two-buffer D3D11 frame pool, and a 12 fps processing ceiling. The picker is the user's explicit selection step; the system capture border is never disabled. `GraphicsCaptureSession.IsSupported`, frame dimensions, item closure, and capture/render exceptions are handled fail-closed.
- **No feedback loop:** Before capture, the app must set and read back `WDA_EXCLUDEFROMCAPTURE` for its own top-level HWND. A fresh captured frame is sampled against a temporary, randomized 8×8 on-screen color probe. A match, an inconclusive timeout, or any setup error returns to Acrylic. This validates the current public Windows capture path, not every recorder or a security boundary. `SetWindowDisplayAffinity` is documented as a DWM capture feature, not DRM or guaranteed protection.
- **Refraction:** Win2D wraps each incoming D3D11 surface, copies only the panel-sized crop (plus a small sampling margin) to the retained GPU bitmap, releases the full-frame wrapper, then applies `DisplacementMapEffect` with a low-amplitude `TurbulenceEffect` map. Only the most recent cropped image is held; there is no file output, recording, network access, or telemetry.
- **Acrylic comparison:** The baseline uses WinUI `DesktopAcrylicBackdrop`, matching Nex's transient DWM Acrylic material, with a dark tint, restrained edge, compact search field, and sample launcher rows. The capture path is only activated by the explicit display-refraction action.
- **Cost controls:** A single display is captured, the WGC pool is limited to two buffers, frame processing is capped at 12 fps, stale frames are dropped, and the app stops when minimized. The app reports compositor-frame age, crop/copy time, draw-command CPU time, accepted FPS, process CPU, and working set while capture is active.

## Limitations and privacy notes

- Windows Graphics Capture provides a whole-display surface; it has no panel-rectangle capture target. The OS therefore has two transient full-display GPU buffers for the selected display. The app copies only the panel crop into its own retained image and never reads or saves the rest of the display. Memory use still scales with display resolution.
- Protected or otherwise unavailable content is left as Windows supplies it (commonly black/blank). The app does not try alternate capture APIs or bypass protection. A genuinely black wallpaper cannot be distinguished from a protected black frame, so the demo does not guess or overwrite it.
- `WDA_EXCLUDEFROMCAPTURE` requires DWM composition and is not a security guarantee; it cannot prevent a physical camera and is not promised to affect every capture mechanism. The probe only confirms the active Windows Graphics Capture path. If it fails, refraction stays disabled.
- The capture system border is user-visible and may affect pixels at the display edge. The app's own status badge is the second active-capture indicator.
- Frame age is measured from `Direct3D11CaptureFrame.SystemRelativeTime` (QPC) to crop completion. Draw time measures CPU command submission, not a GPU fence or display-present time. Process CPU and working set are sampled locally; dedicated GPU memory and battery drain are not measured.
- This Linux environment has no `dotnet` CLI, Windows desktop, or GPU, so the demo could not be built or exercised here. No representative-machine capture/render latency or resource numbers are claimed. Use the in-app counters and the manual checks below on the target Windows machine.
- This is a research prototype, not production-ready. It has not been validated for multiple-DPI monitor transitions, HDR output, device loss recovery, all protected-content providers, or third-party capture tools.

## Verification checklist (Windows)

1. Build and launch using the commands above. Confirm Acrylic works before granting capture access.
2. Start refraction, select the display containing the panel, and verify the colored system border and in-panel active badge appear. The self-exclusion probe should finish with **Self-exclusion verified**.
3. Put a changing, high-contrast window or wallpaper behind the panel; compare Acrylic only and Display refraction. Verify the text/controls stay legible and the capture is distorted only under the panel.
4. Use a second display, select the wrong display, cancel the picker, deny capture, and select an app window. Each unsupported/mismatched case should leave Acrylic active with no capture session.
5. During capture, verify the random probe pattern does not appear in the captured panel. If it does, the app must stop and report an exclusion failure. Select **Stop capture**, switch to Acrylic, minimize, and close; confirm the system border disappears and the active badge clears.
6. Test protected video if available: it must remain black/blank where Windows protects it, with no alternate read path. Observe the latency/FPS/CPU/working-set counters on the intended representative machine; record those values outside this app if needed. The app itself writes no measurements to disk.

## Windows API references

- [Windows screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) — picker, display/window targets, system border, frame pool.
- [GraphicsCaptureSession.IsSupported](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.issupported?view=winrt-28000) and [CreateFreeThreaded](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded?view=winrt-28000).
- [Capture-session border requirements](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired?view=winrt-28000) — borderless capture requires additional user consent and capability; this demo leaves it required.
- [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity) — `WDA_EXCLUDEFROMCAPTURE` is supported starting with Windows 10 version 2004; documented scope and non-security limitation.
- [Windows 11 system backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops) — Desktop Acrylic for transient UI.
- [Win2D displacement-map effect](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Effects_DisplacementMapEffect.htm) and [Direct2D displacement map](https://learn.microsoft.com/en-us/windows/win32/direct2d/displacement-map).
- Nex reference inspected read-only: `apps/core/src/overlay/host.rs` (transparent, topmost tao/wry window and `DWMSBT_TRANSIENTWINDOW`) and `apps/core/assets/style.css` (compact 700px dark launcher panel). No files in Nex were edited.
