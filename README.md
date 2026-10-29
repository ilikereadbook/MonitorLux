# MonitorLux

**Control your external monitor's brightness with the mouse wheel — no window, no admin rights, no tray clutter.**

[![Platform](https://img.shields.io/badge/platform-Windows%2011%20%7C%2010-0067C0?logo=windows11&logoColor=white)](#compatibility)
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.x-512BD4?logo=dotnet&logoColor=white)](#build-from-source)
[![License](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)

English · [简体中文](README.zh-CN.md)

![Brightness OSD](docs/osd.png)

---

## Why this exists

If you have ever installed **Twinkle Tray** or **Monitorian** on a laptop with an **NVIDIA GPU + DisplayPort** external monitor and found the brightness slider dead, you already know the bug. Here is what actually happens:

Windows exposes `GetMonitorCapabilities()` from `dxva2.dll` so apps can ask *"can this monitor do brightness?"*. On some NVIDIA driver + DP combinations that call **lies** — it returns a capabilities mask of `0x0`, i.e. *"this display cannot be dimmed"*.

Most brightness tools trust that answer, grey out the slider, and give up.

But the monitor is perfectly controllable. The writes work fine — the *capability report* is wrong.

**MonitorLux skips the capability check entirely and talks to DDC/CI directly** (`GetMonitorBrightness` / `SetMonitorBrightness`). On the exact setup where those apps fail, this one just works.

```
> MonitorLux.exe --selftest
Monitor=[ViewSonic VX2780-2K-Pro] Ok=True cur=50 max=100
```

## Features

| | |
|---|---|
| 🖱️ **Mouse wheel anywhere** | `Ctrl + Alt + wheel` adjusts brightness from any window, no clicking, nothing to open |
| ⌨️ **Hotkeys** | `Ctrl + Alt + ↑ / ↓`, ±5% per press |
| 🖼️ **Frosted-glass OSD** | A small capsule fades in at the corner with the current percentage, then disappears. Click-through, never steals focus |
| 🪟 **Tray-native** | Lives in the notification area. No main window at all — the panel only appears if you ask for it |
| 🌗 **Light / dark** | Follows your Windows theme automatically, checked each time it opens |
| 🔓 **No admin rights** | DDC/CI writes work as a normal user, so no UAC prompt on every launch |
| 🚀 **Single instance** | Launch it twice and the second copy exits quietly |
| 📦 **Green binary** | One `.exe`, no runtime install, no registry mess beyond an optional autostart entry |

## Install

**Option A — installer (recommended)**

Download `MonitorLux-Setup.exe` from [Releases](../../releases), run it. It installs to `%LOCALAPPDATA%\Programs\MonitorLux`, creates Start-menu and desktop shortcuts, enables autostart, and launches. Uninstall from **Settings → Apps**.

**Option B — portable**

Download `MonitorLux.exe` and run it. It appears in the tray and stays there. Right-click the tray icon → *Start with Windows* if you want it to come back after a reboot.

That's the whole setup. You never need to "open" it again.

## Usage

| Action | Result |
|---|---|
| `Ctrl + Alt` + **mouse wheel** | Adjust brightness from anywhere |
| `Ctrl + Alt + ↑` / `↓` | ±5% |
| **Wheel** over the tray icon | Adjust without a modifier key |
| **Left-click** tray icon | Open the full panel (all monitors, sliders) |
| **Esc** / click elsewhere | Dismiss the panel |
| **Right-click** tray icon | Refresh monitors · Start with Windows · Exit |

![Full panel, light and dark](docs/panel-light.png)

## Command line

Useful for scripts, shortcuts, or Stream Deck buttons. These work without the tray app running.

```bat
MonitorLux.exe --set 20          :: set to 20%
MonitorLux.exe --step -5         :: decrease by 5%
MonitorLux.exe --step +10        :: increase by 10%
MonitorLux.exe --selftest        :: enumerate monitors and print current values
MonitorLux.exe --autostart-on    :: enable autostart
MonitorLux.exe --uninstall       :: remove autostart + registry entries
```

## How it works

- **Brightness** goes through `dxva2.dll` (`GetNumberOfPhysicalMonitorsFromHMONITOR` → `SetMonitorBrightness`). The DDC/CI *capability* query is deliberately skipped — see [above](#why-this-exists). Writes are debounced, because consecutive DDC/CI commands too close together get dropped by the monitor, and failed writes are retried once.
- **The OSD** grabs the desktop behind itself, downsamples it, blurs it, and paints the result inside a rounded capsule with a tint, a top highlight, and a 1px inner border. (WinForms cannot use DWM acrylic once you paint translucent content — the blur layer gets destroyed — so the glass is rendered by hand.)
- **Mouse wheel** is captured with a low-level hook (`WH_MOUSE_LL`); the tray icon's screen rectangle is cached so the hook never calls into the shell per event.
- **DPI aware** via a `PerMonitorV2` manifest, so text and icons render at native pixels on scaled displays instead of being bitmap-stretched.
- **The icon** is generated programmatically — nine sizes from 16px to 256px, each with geometry tuned for its size instead of downscaled from one big bitmap.

## Build from source

No SDK, no NuGet, no Visual Studio required — just Windows PowerShell 5.1 and the .NET Framework that ships with Windows.

```powershell
git clone https://github.com/ilikereadbook/MonitorLux.git
cd MonitorLux

# app -> dist\MonitorLux.exe
powershell -ExecutionPolicy Bypass -File .\build\build.ps1

# single-file installer -> dist\MonitorLux-Setup.exe
powershell -ExecutionPolicy Bypass -File .\build\build-setup.ps1

# regenerate assets\app.ico + docs\icon.png
powershell -ExecutionPolicy Bypass -File .\build\make-icon.ps1
```

```
src/       MonitorLux.cs (app)  ·  Setup.cs (installer)  ·  app.manifest
build/     build.ps1  ·  build-setup.ps1  ·  make-icon.ps1
assets/    app.ico
docs/      screenshots
```

## Compatibility

- **Windows 11** (tested on build 26200) and **Windows 10**
- Requires a monitor with **DDC/CI enabled** in its OSD menu — most do by default
- Works over **DisplayPort, HDMI, DVI and VGA**, as long as the cable passes the DDC channel
- **Laptop internal panels** usually have no DDC/CI; use the standard Windows brightness slider for those
- `ddcutil`-style brightness is not exposed by every monitor — if `--selftest` reports `Ok=False`, the display simply does not accept DDC writes

**Known limitations**

- KVM switches, HDMI splitters and some USB-C docks strip the DDC channel. Connect the monitor directly.
- HDR mode can make the master brightness control behave inconsistently on some panels.
- Only the *luminance* VCP code (0x10) is used — contrast, input source and other VCP features are not exposed yet.

## FAQ

**Does it need to run in the background?**
Yes — something has to listen for the wheel and hotkeys. But it starts silently with Windows, so in practice you install it once and never think about it again.

**Does it conflict with Twinkle Tray?**
Both hold global hooks and both write to the same VCP code. Pick one.

**Nothing happens when I press the hotkeys.**
Check that the tray icon is present. If it is, another app may already own `Ctrl+Alt+↑/↓` — those registrations are global and first-come-first-served.

## License

[MIT](LICENSE)
