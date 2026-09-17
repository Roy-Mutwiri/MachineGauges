# Changelog

## [1.4.0] - 2026-09-17

### New
- **Details panel.** Press `Ctrl+Shift+G`, left-click the tray icon, or open MachineGauges again while it's running. It shows:
  - 1- or 5-minute history charts for CPU, memory, GPU, disk and network, with hover readouts
  - per-core CPU usage
  - every GPU with usage, video memory, temperature, power draw, fan speed and clock speed
  - every disk's activity and read/write speed, plus free space on each drive
  - top processes by CPU, memory or GPU, the same columns Task Manager shows
- **Settings window** (tray menu > Settings):
  - Style: gauges or compact text. Layout: horizontal or vertical.
  - Six screen positions, choice of monitor, size, opacity, and four colour themes
  - Which gauges to show, in what order, and which GPU the strip follows
- **New gauges:** game FPS (experimental), ping, battery, clock and uptime.
- **Alerts:** Windows notifications for sustained high CPU, memory or GPU usage, high GPU or CPU temperature, and drives running out of space. The thresholds are adjustable.
- **Hide in fullscreen, or show only while gaming.**
- **CPU temperature**, read from LibreHardwareMonitor when it's running.
- **History log:** optionally records readings to one CSV file per day.
- **Auto-update:** checks GitHub daily and offers a one-click update. Each download is checked against its published SHA-256 checksum before it runs.
- **Automatic builds:** every release is built and published by GitHub Actions from the public source.

### Changed
- Video memory totals now come from the GPU driver for every vendor, not just NVIDIA.
- The tray menu is shorter: detailed options moved into Settings.

## [1.3.0] - 2026-09-17

### New
- First public release: live CPU, RAM, GPU, disk and network gauges pinned to the top of the screen, with figures that match Task Manager.
- Self-installing exe: install, update and uninstall without admin rights. Appears in Settings > Apps and starts with Windows.
- Animated gauges, fade-on-hover, click-through strip and a live CPU gauge in the tray.
