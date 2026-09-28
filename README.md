# BetterGhub

A lightweight Windows replacement for G HUB for **one G502 X LIGHTSPEED** on the `046d:c547` LIGHTSPEED receiver. It uses the receiver's HID++ `0x8100` host mode and `0x8110` button-spy reports. It does not write onboard memory or control lighting.

## Run

From PowerShell in this folder:

```powershell
.\setup.ps1
.\run.ps1
```

Needs the .NET 8 Windows Desktop runtime and Python 3. `setup.ps1` creates `.venv`, installs `hidapi`, and builds `src\BetterGhub\bin\Release\net8.0-windows\BetterGhub.exe`.

**Exit G HUB completely (including its tray icon).** G HUB and BetterGhub can't safely control the same receiver at once. BetterGhub detects G HUB and connects by itself once G HUB has closed. Connecting changes only volatile device RAM, and reconnects after the mouse sleeps or is power cycled.

## Layout

| Path | What it is |
|---|---|
| `src/BetterGhub/Core` | Settings model, physical control catalog (`MouseControls`), macro engine |
| `src/BetterGhub/Input` | SendInput, key names, Raw Input wheel, optional low-level mouse hook |
| `src/BetterGhub/Device` | Runs the Python HID++ bridge and exchanges JSON lines |
| `src/BetterGhub/Services/MouseService.cs` | Everything between device and UI: connection, button → macro dispatch, profiles, DPI, calibration |
| `src/BetterGhub/Views`, `Theme` | WPF UI |
| `bridge/hidpp_bridge.py` | HID++ bridge (`--service`) and one-shot diagnostic |
| `tools/BetterGhub.Probe` | Original read-only Raw Input probe |
| `docs`, `logs` | Hardware findings and captures |

## Features

- **Assignments:** G HUB-style mouse diagram (top and side views). Click a control to assign a macro or a mouse function (DPI Shift/Up/Down/Cycle, G-Shift, Do nothing). Pressed buttons light up live. A G-Shift layer is included.
- **Calibration:** *Calibrate buttons* walks through G2–G9 and wheel tilt. Press each one and its HID bit is saved. Controls that haven't been calibrated show in amber.
- **Macros:** four types (no repeat, repeat while holding, toggle, sequence). There's a colour-coded action timeline with key down/up markers, keystroke recording with timing, and actions for shortcuts, text, mouse, volume/media, launching apps, and delays. An optional standard delay can replace recorded timing. *Test in 3 s* plays a macro once after a countdown.
- **Sensitivity:** logarithmic DPI slider with up to five draggable speeds, a DPI Shift speed, and report rate. Changes apply instantly.
- **Profiles:** per-application profiles with automatic switching on focus. Desktop is the fallback.
- **Device:** connection control, auto-connect, a live input panel and an event log with HID bits.
- Optional blocking of Windows' default action for assigned right/middle/back/forward/wheel controls. It is off by default and applies to every mouse.

Settings are stored at `%APPDATA%\BetterGhub\settings.json`. If that file can't be read, it is kept as `settings.json.unreadable-<time>` and a fresh one is started. Set `BETTERGHUB_SETTINGS` to use a different file.

## Design review without the mouse

```powershell
$env:BETTERGHUB_SETTINGS = "$env:TEMP\bg-demo.json"
.\src\BetterGhub\bin\Release\net8.0-windows\BetterGhub.exe --snapshot "$env:TEMP\bg-shots" [--small]
```

This renders every page to PNG and exits.

## Current limits

- Button detection, scroll, and DPI/report-rate read-back are verified on the target hardware. Macro playback, calibration, G-Shift, DPI switching, profile auto-switching and default-action blocking in the new UI still need live testing.
- Blocking assumes G4 sends Windows "back" (X1) and G5 "forward" (X2). Primary click is never blocked.
- G-numbers follow the G502 X manual (G4 back, G5 forward, G6 DPI Shift, G7/G8 beside primary click, G9 behind the wheel). The bit for each control comes from calibration, not from guesses.
- Closing the window exits BetterGhub. A tray mode is future work.
- Lighting, onboard memory, cloud profiles, integrations and firmware updates are out of scope.
