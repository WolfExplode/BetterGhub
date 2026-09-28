# BetterGhub

A lightweight Windows replacement for G HUB for the **G502 X LIGHTSPEED** on its `046d:c547` LIGHTSPEED receiver: macros, button assignments, DPI and per-app profiles. It does not write onboard memory or control lighting.

## For users

1. Download `BetterGhub.exe` and run it. It needs no .NET, Python or admin rights.
2. Choose **Install**. BetterGhub is copied to `%LOCALAPPDATA%\Programs\BetterGhub`, a Start menu shortcut is added, and it can start with Windows. **Just run it** uses the file where it is.
3. **Quit G HUB completely** (right-click its tray icon → Quit). BetterGhub connects to the mouse by itself once G HUB is gone.
4. Open **Assignments → Calibrate buttons** and press each button once when asked.

Closing the window keeps BetterGhub running in the notification area so macros keep working. Right-click the tray icon to switch profile, connect/disconnect or quit. To uninstall, use **Windows Settings › Apps**, or **Settings › Uninstall** inside BetterGhub. You'll be asked whether to keep your macros.

Settings live in `%APPDATA%\BetterGhub\settings.json`. If that file can't be read, it is kept as `settings.json.unreadable-<time>` and a fresh one is started. Connecting only changes the mouse's working memory; power-cycle the mouse to return to its onboard profile.

## Features

- **Assignments:** G HUB-style mouse diagram (top and side views). Click a control to assign a macro or a mouse function (DPI Shift/Up/Down/Cycle, G-Shift, Do nothing). Pressed buttons light up live. A G-Shift layer is included.
- **Calibration:** walks through G2–G9 and wheel tilt, and saves each button's HID bit.
- **Macros:** four types (no repeat, repeat while holding, toggle, sequence). Colour-coded action timeline with key down/up markers. Keystroke recording with timing. Actions for shortcuts, text, mouse, volume/media, launching apps and delays. An optional standard delay, and *Test in 3 s*.
- **Sensitivity:** logarithmic DPI slider with up to five draggable speeds, a DPI Shift speed, and report rate. Changes apply instantly.
- **Profiles:** per-application profiles with automatic switching on focus.
- **Settings:** connection, auto-connect, start with Windows, keep running in the tray, live input and event log.
- Optional blocking of Windows' default action for assigned right/middle/back/forward/wheel controls. Off by default; applies to every mouse.

## For developers

Needs the .NET 8 SDK on Windows.

```powershell
.\build.ps1          # dist\BetterGhub.exe, self-contained single file (~64 MB)
.\build.ps1 -Small   # ~2 MB, requires the .NET 8 Desktop Runtime
dotnet run --project src\BetterGhub   # developer build: never shows the install prompt
```

| Path | What it is |
|---|---|
| `src/BetterGhub/Device` | Native HID++ 2.0 over Win32 HID: host mode `0x8100`, button spy `0x8110`, DPI `0x2201`, report rate `0x8060` |
| `src/BetterGhub/Services` | `MouseService` (device ↔ UI: buttons → macros, profiles, DPI, calibration) and `Installer` |
| `src/BetterGhub/Core` | Settings model, physical control catalog, macro engine |
| `src/BetterGhub/Input` | SendInput with scan codes, key names, Raw Input wheel, optional low-level mouse hook |
| `src/BetterGhub/Views`, `Theme` | WPF UI, tray icon, install prompt |
| `tools/make-icon.ps1` | Regenerates `Assets/BetterGhub.ico` |
| `tools/BetterGhub.Probe` | Original read-only Raw Input probe |
| `docs`, `logs` | Hardware findings and captures |

Command-line switches:

| Switch | What it does |
|---|---|
| `--minimized` | Start in the tray. Used by autostart. |
| `--portable` | Skip the install prompt. |
| `--uninstall [--quiet]` | Remove the installed copy. |
| `--probe <file>` | Read-only receiver diagnostic. Lists HID interfaces and reads mode, DPI and rate. |
| `--snapshot <folder> [--small]` | Render every page to PNG for design review. Set `BETTERGHUB_SETTINGS` to a scratch file first. |

## Current limits

- Verified on the target hardware: button reports and scroll (earlier Python bridge), and native HID++ enumeration plus feature, mode, DPI and rate reads (`--probe`). Still needs live testing: host-mode switching and button streaming through the native bridge, macros, calibration, G-Shift, DPI switching, profile auto-switching, default-action blocking, install and uninstall.
- Blocking assumes G4 sends Windows "back" (X1) and G5 "forward" (X2). Primary click is never blocked.
- G-numbers follow the G502 X manual (G4 back, G5 forward, G6 DPI Shift, G7/G8 beside primary click, G9 behind the wheel). The bit for each control comes from calibration.
- The exe is not code-signed, so Windows SmartScreen may warn on first run ("More info → Run anyway").
- Lighting, onboard memory, cloud profiles, integrations and firmware updates are out of scope.
