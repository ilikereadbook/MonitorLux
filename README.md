# MonitorLux

**Brightness and display-mode switching for Windows, driven by the mouse wheel and a few hotkeys — no window, no admin rights, no tray clutter.**

[![Platform](https://img.shields.io/badge/platform-Windows%2011%20%7C%2010-0067C0?logo=windows11&logoColor=white)](#compatibility)
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.x-512BD4?logo=dotnet&logoColor=white)](#build-from-source)
[![License](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)

English · [简体中文](README.zh-CN.md)

![Brightness OSD](docs/osd.png) ![Display mode OSD](docs/osd-display.png)

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
| ⌨️ **Hotkeys** | `Ctrl + Alt + ↑ / ↓`, ±5% per press. Falls back to `Ctrl + Shift + Alt + ↑ / ↓` if something else owns the combination |
| 🖥️ **Display modes** | `Ctrl + Alt + 1` laptop panel only · `Ctrl + Alt + 2` external monitor only · `Ctrl + Alt + 3` extend. What `Win+P` does, in one keystroke |
| 🖼️ **Frosted-glass OSD** | A small capsule fades in at the corner with the current percentage or the new mode, then disappears. Click-through, never steals focus |
| 🪟 **Tray-native** | Lives in the notification area. No main window at all — the panel only appears if you ask for it |
| 🌗 **Light / dark** | Follows your Windows theme automatically, checked each time it opens |
| 🔓 **No admin rights** | DDC/CI writes and display switching both work as a normal user, so no UAC prompt on every launch |
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
| `Ctrl + Alt + 1` | Only the internal (laptop) panel |
| `Ctrl + Alt + 2` | Only the external monitor |
| `Ctrl + Alt + 3` | Extend — both displays, side by side |
| **Wheel** over the tray icon | Adjust without a modifier key |
| **Left-click** tray icon | Open the full panel (all monitors, sliders) |
| **Esc** / click elsewhere | Dismiss the panel |
| **Right-click** tray icon | Display modes · Refresh monitors · Start with Windows · Exit |

The display-mode items only light up for hardware that is actually present — plug the external monitor in and *Only external monitor* becomes available; unplug it and the item greys out.

If `Ctrl + Alt + 1/2/3` is already taken by another app, all three move to `Ctrl + Shift + Alt + 1/2/3` together. The tray menu always prints the combination that is actually live, and says **hotkey taken** if none could be registered — a global hotkey that loses the race is otherwise silent.

![Full panel, light and dark](docs/panel-light.png)

## Command line

Useful for scripts, shortcuts, or Stream Deck buttons. These work without the tray app running.

```bat
MonitorLux.exe --set 20            :: set to 20%
MonitorLux.exe --step -5           :: decrease by 5%
MonitorLux.exe --step +10          :: increase by 10%
MonitorLux.exe --display internal  :: laptop panel only
MonitorLux.exe --display external  :: external monitor only
MonitorLux.exe --display extend    :: both displays
MonitorLux.exe --display clone     :: mirrored
MonitorLux.exe --dispinfo          :: dump the display topology and raw paths
MonitorLux.exe --selftest          :: enumerate monitors and print current values
MonitorLux.exe --autostart-on      :: enable autostart
MonitorLux.exe --uninstall         :: remove autostart + registry entries
```

## How it works

- **Brightness** goes through `dxva2.dll` (`GetNumberOfPhysicalMonitorsFromHMONITOR` → `SetMonitorBrightness`). The DDC/CI *capability* query is deliberately skipped — see [above](#why-this-exists). Writes are debounced, because consecutive DDC/CI commands too close together get dropped by the monitor, and failed writes are retried once.
- **Display modes** call `SetDisplayConfig` with the same topology flags Windows itself uses for `Win+P` (`SDC_TOPOLOGY_INTERNAL` / `_EXTERNAL` / `_EXTEND`). No path or mode arrays are built by hand, so the OS is free to pick valid modes for whatever hardware answers. The whole switch is one API call — no external tools, no admin rights.
- **Internal vs external** is read back from `QueryDisplayConfig`: an embedded panel is the target whose `outputTechnology` has the high bit set (`0x80000000`). A source is keyed by *adapter + id*, not id alone — on hybrid laptops the iGPU and the dGPU each number their sources from zero, so comparing ids alone makes extend look like clone.
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
- Switching to *internal only* powers the DP link down. Getting the external monitor back takes a second or so of link retraining, and the monitor's own deep-sleep setting can stretch that.

## FAQ

**Does it need to run in the background?**
Yes — something has to listen for the wheel and hotkeys. But it starts silently with Windows, so in practice you install it once and never think about it again.

**Does it conflict with Twinkle Tray?**
Both hold global hooks and both write to the same VCP code. Pick one.

**Nothing happens when I press the hotkeys.**
`Ctrl+Alt+↑/↓` is owned by something else on a lot of machines — Intel graphics rotation, IMEs, GPU overlays. MonitorLux finds out at startup and re-registers the brightness pair as `Ctrl+Shift+Alt+↑/↓`. The tray menu always shows the combination that actually got registered, so check there rather than assuming.

**"External monitor not ready" when switching back.**
Switching to *internal only* takes the DP link down, and the monitor disappears from the display bus until it retrains — a few seconds. Asking for it back during that window fails with `ERROR_GEN_FAILURE` (31). MonitorLux now retries four times over roughly 1.5 s **and** verifies the topology actually changed, because `SetDisplayConfig` can return success while quietly leaving the old layout in place. If it still fails, the OSD says so plainly instead of showing a raw error code: wake the monitor, or replug the cable. A monitor that drops off the bus after waking is usually a deep-sleep setting in its own OSD menu.

**Does switching display modes need admin rights?**
No. `SetDisplayConfig` is a per-user operation, exactly like `Win+P`.

**The screen goes black for a second when I switch.**
That is the OS renegotiating the link with the monitor. Same thing happens with `Win+P`; it is not this app.

## License

[MIT](LICENSE)
