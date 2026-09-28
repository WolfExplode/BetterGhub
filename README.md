# BetterGhub

A lightweight Windows replacement for G HUB for the **G502 X LIGHTSPEED** on its `046d:c547` LIGHTSPEED receiver: macros, button assignments, DPI and per-app profiles. It does not write onboard memory or control lighting.

## For users

BetterGhub is a single portable `BetterGhub.exe`. Nothing is installed, and it needs no .NET, Python or admin rights.

1. Put `BetterGhub.exe` wherever you like and run it.
2. **Quit G HUB completely** (right-click its tray icon → Quit). BetterGhub connects to the mouse by itself once G HUB is gone.
3. Open **Assignments → Calibrate buttons** and press each button once when asked.

Closing the window keeps BetterGhub running in the notification area so macros keep working. Right-click the tray icon to switch profile, connect/disconnect or quit. **Settings → Start with Windows** adds a per-user autostart entry for the exe's current location. If you move the exe, run it once from the new place and the entry follows. To remove BetterGhub, turn that option off and delete the exe (and `%APPDATA%\BetterGhub` if you want your settings gone too).

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

Needs the .NET 8 SDK on Windows. Open `BetterGhub.sln`, or:

```powershell
.\build.ps1          # dist\BetterGhub.exe, portable self-contained single file (~64 MB)
.\build.ps1 -Small   # ~2 MB, requires the .NET 8 Desktop Runtime
dotnet run --project src\BetterGhub
```

```
BetterGhub.sln
build.ps1                 publishes dist\BetterGhub.exe
src/BetterGhub/           the app
  Core/                   settings model, physical control catalog, macro engine
  Device/                 native HID++ 2.0 over Win32 HID (0x8100 host mode, 0x8110 button spy, 0x2201 DPI, 0x8060 report rate)
  Input/                  SendInput with scan codes, key names, Raw Input wheel, optional low-level mouse hook
  Services/               MouseService (device <-> UI: buttons -> macros, profiles, DPI, calibration), AutoStart
  Views/, Theme/          WPF UI and tray icon
  Assets/                 generated app icon (tools/make-icon.ps1)
assets/artwork/           source artwork: Logitech logo, G502 X drawings
docs/                     hardware findings, G HUB reference screenshots (captures/ is local-only)
tools/                    make-icon.ps1, BetterGhub.Probe (read-only Raw Input probe)
reference/                third-party repos for protocol reference (git-ignored)
```

Command-line switches:

| Switch | What it does |
|---|---|
| `--minimized` | Start in the tray. Used by autostart. |
| `--probe <file>` | Read-only receiver diagnostic. Lists HID interfaces and reads mode, DPI and rate. |
| `--snapshot <folder> [--small]` | Render every page to PNG for design review. Set `BETTERGHUB_SETTINGS` to a scratch settings file first. |

## Current limits

- Verified on the target hardware: button reports and scroll (earlier Python bridge), and native HID++ enumeration plus feature, mode, DPI and rate reads (`--probe`). Still needs live testing: host-mode switching and button streaming through the native bridge, macros, calibration, G-Shift, DPI switching, profile auto-switching, default-action blocking, tray and autostart.
- Blocking assumes G4 sends Windows "back" (X1) and G5 "forward" (X2). Primary click is never blocked.
- G-numbers follow the G502 X manual (G4 back, G5 forward, G6 DPI Shift, G7/G8 beside primary click, G9 behind the wheel). The bit for each control comes from calibration.
- The exe is not code-signed, so Windows SmartScreen may warn on first run ("More info → Run anyway").
- The Logitech logo is a Logitech trademark. Fine for personal use; replace it before publishing the app.
- Lighting, onboard memory, cloud profiles, integrations and firmware updates are out of scope.
