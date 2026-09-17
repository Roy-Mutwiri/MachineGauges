<p align="center">
  <img src="docs/icon.png" width="112" alt="MachineGauges icon">
</p>

<h1 align="center">MachineGauges</h1>

<p align="center">
  Live CPU, RAM, GPU, disk and network gauges pinned to the top of your screen —<br>
  with the same numbers as Windows Task Manager.
</p>

<p align="center">
  <a href="https://github.com/Roy-Mutwiri/MachineGauges/releases/latest/download/MachineGauges.exe"><b>⬇ Download MachineGauges.exe</b></a>
  &nbsp;·&nbsp; Windows 10 / 11 (64-bit) &nbsp;·&nbsp; free, ~110 KB, no admin rights needed
</p>

![MachineGauges showing live gauges](docs/preview.png)

## Install

1. **[Download MachineGauges.exe](https://github.com/Roy-Mutwiri/MachineGauges/releases/latest/download/MachineGauges.exe)**
2. Double-click it and choose **Yes – Install**.
3. The gauges appear at the top centre of your screen. They start automatically every time you sign in.

Prefer not to install? Choose **No – Just run it this time** and it runs once from wherever you saved it.

> **"Windows protected your PC"?** The app isn't code-signed yet, so Windows SmartScreen
> warns about new downloads. Click **More info → Run anyway**. The full source is in this
> repository if you'd like to check it or build it yourself.

## What you get

| Gauge | Shows |
|---|---|
| **CPU** | Utilisation and current speed (GHz) |
| **RAM** | Percent used and GB in use / total |
| **GPU** | Utilisation, video memory used / total, and temperature (NVIDIA) |
| **DSK** | Active time of your busiest disk, and throughput |
| **NET** | Throughput of your busiest network adapter, download ↓ and upload ↑ |

- **Matches Task Manager.** Every figure comes from the same Windows performance counters Task Manager reads.
- **Never in the way.** Clicks pass straight through the gauges, and they fade to fully invisible while your mouse is over them, so you can always see and use what's underneath.
- **Can't be dragged off.** The strip stays fixed at the top centre of your screen.
- **Animated.** Needles glide between readings, colours shift from green to amber to red as load rises, and a gauge glows when it passes 90%.
- **Light.** About 1% of one CPU core while animating.

## Settings

Right-click the gauge icon in the taskbar tray:

- **Fade out when mouse is over it** – on or off
- **Opacity** and **Size**
- **Move up / down** a few pixels
- **Monitor** – which screen to show on (multi-monitor setups)
- **Start with Windows** – on or off
- **Project page on GitHub**, **Uninstall…**, **Exit**

Closed it with **Exit**? Start it again from the Start Menu: **MachineGauges**.

## Update

Download the newest `MachineGauges.exe` and double-click it. It offers to update your installed copy, keeping your settings.

## Uninstall

**Settings → Apps → Installed apps → MachineGauges → Uninstall**, or **Uninstall…** from the tray menu.
This removes the app, its startup entry, Start Menu shortcut and settings.

---

## For developers

<details>
<summary>How the numbers match Task Manager</summary>

All counters are read through PDH (Performance Data Helper) in a single query, using English
counter names so it works on any Windows language.

| Reading | Source |
|---|---|
| CPU % | `\Processor Information(_Total)\% Processor Utility` — the turbo-aware counter Task Manager has used since Windows 10 (not `% Processor Time`) |
| CPU GHz | base clock (`HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\~MHz`) × `% Processor Performance` |
| RAM | `GlobalMemoryStatusEx`: total − available |
| GPU % | `\GPU Engine(*)\Utilization Percentage`, summed per engine type; the busiest engine type wins |
| VRAM | `\GPU Adapter Memory(*)\Dedicated Usage`; capacity and temperature from NVIDIA NVML when present |
| Disk % | `100 − \PhysicalDisk(*)\% Idle Time`, busiest disk |
| Network | `\Network Interface(*)\Bytes Total/sec`, busiest adapter, with its received / sent counters |

On systems with several GPUs, the one holding the most dedicated video memory is shown —
normally the discrete card rather than integrated graphics or a virtual display adapter.

</details>

<details>
<summary>Build from source</summary>

No SDK needed — it builds with the C# compiler that ships with Windows (.NET Framework 4.x).

```powershell
.\build.ps1      # renders the icon, then builds build\MachineGauges.exe
.\install.ps1    # installs that build for the current user and starts it
.\uninstall.ps1  # removes it
```

| Path | Purpose |
|---|---|
| `src/Sampler.cs` | Performance counter readings |
| `src/OverlayForm.cs` | The overlay window, animation, hover fade and tray menu |
| `src/GaugeArt.cs` | Gauge and icon drawing |
| `src/Installer.cs` | Self-install, update, startup and uninstall |
| `src/IconGen.cs` | Build-time tool that renders `app.ico` |
| `add-watchdog.ps1` | Optional (admin): relaunch within 5 minutes if the app ever stops |

Command-line options:

```text
MachineGauges.exe                    first run offers to install; installed copy shows the gauges
MachineGauges.exe --install [--quiet]
MachineGauges.exe --uninstall [--quiet]
MachineGauges.exe --probe 10         print 10 raw samples to compare against Task Manager
MachineGauges.exe --preview out.png  render the gauges to an image
```

Settings are stored in `%LOCALAPPDATA%\MachineGauges\config.ini`
(`opacity`, `scale`, `yoffset`, `monitor`, `interval`, `hoverhide`).

</details>
