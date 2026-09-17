using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MachineGauges
{
    internal sealed class SettingsForm : Form
    {
        private static readonly string[] SegmentNames =
        {
            "cpu", "CPU usage", "ram", "Memory", "gpu", "GPU usage, memory and temperature",
            "disk", "Disk activity", "net", "Network speed", "fps", "Game FPS (experimental)",
            "ping", "Ping", "battery", "Battery", "clock", "Clock", "uptime", "Uptime"
        };
        private static readonly string[] PositionNames = { "Top centre", "Top left", "Top right", "Bottom centre", "Bottom left", "Bottom right" };
        private static readonly string[] HotkeyChoices = { "Ctrl+Shift+G", "Ctrl+Alt+G", "Ctrl+Shift+M", "Win+Shift+G", "Off" };

        private readonly OverlayForm _host;
        private readonly Timer _statusTimer = new Timer { Interval = 1000 };

        // Appearance
        private ComboBox _style, _orientation, _position, _monitor, _theme;
        private NumericUpDown _scale, _opacity, _offset;
        // Gauges
        private CheckedListBox _segments;
        private ComboBox _gpu;
        // Behaviour
        private RadioButton _visAlways, _visFullscreen, _visGames;
        private CheckBox _hoverHide, _startup;
        private ComboBox _hotkey;
        private Label _hotkeyStatus;
        private NumericUpDown _interval;
        private TextBox _pingHost;
        // Alerts
        private CheckBox _alerts;
        private NumericUpDown _alertCpu, _alertRam, _alertGpu, _alertGpuTemp, _alertCpuTemp, _alertDisk, _alertSustain;
        // Advanced
        private CheckBox _log, _updates, _fps, _cpuTemp;
        private NumericUpDown _logInterval, _logDays;
        private Label _fpsStatus, _cpuTempStatus;
        private Button _fpsGrant;

        public SettingsForm(OverlayForm host)
        {
            _host = host;
            Config c = host.CurrentConfig;

            Text = "MachineGauges settings";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            try { Icon = Icon.ExtractAssociatedIcon(Installer.CurrentExe); } catch { }

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
            tabs.TabPages.Add(AppearanceTab(c));
            tabs.TabPages.Add(GaugesTab(c));
            tabs.TabPages.Add(BehaviourTab(c));
            tabs.TabPages.Add(AlertsTab(c));
            tabs.TabPages.Add(AdvancedTab(c));

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(8)
            };
            var apply = new Button { Text = "Apply", AutoSize = true, MinimumSize = new Size(84, 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(84, 0), DialogResult = DialogResult.Cancel };
            var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(84, 0) };
            var reset = new Button { Text = "Reset to defaults", AutoSize = true };
            apply.Click += delegate { Apply(); };
            ok.Click += delegate { Apply(); Close(); };
            cancel.Click += delegate { Close(); };
            reset.Click += delegate
            {
                if (MessageBox.Show(this, "Reset every setting to its default?", Installer.AppName,
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _host.ApplyConfig(new Config());
                Close();
            };
            buttons.Controls.Add(apply);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(new Label { Width = 40 });
            buttons.Controls.Add(reset);

            Controls.Add(tabs);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(560, 520);

            _statusTimer.Tick += delegate { RefreshStatus(); };
            _statusTimer.Start();
            RefreshStatus();
        }

        // ---------- tabs ----------

        private TabPage AppearanceTab(Config c)
        {
            TableLayoutPanel t;
            TabPage page = Page("Appearance", out t);
            _style = Combo(new[] { "Gauges", "Compact text" }, Array.IndexOf(Config.Styles, c.Style));
            _orientation = Combo(Config.Orientations, Array.IndexOf(Config.Orientations, c.Orientation));
            _position = Combo(PositionNames, Array.IndexOf(Config.Positions, c.Position));
            var monitors = new List<string>();
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
                monitors.Add((i + 1) + ":  " + all[i].Bounds.Width + " × " + all[i].Bounds.Height + (all[i].Primary ? "  (main)" : ""));
            _monitor = Combo(monitors.ToArray(), Math.Min(c.Monitor, all.Length - 1));
            _theme = Combo(Config.Themes, Array.IndexOf(Config.Themes, c.Theme));
            _scale = Number(60, 300, (int)Math.Round(c.Scale * 100), 5);
            _opacity = Number(20, 100, (int)Math.Round(c.Opacity * 100), 5);
            _offset = Number(0, 500, c.YOffset, 1);

            Row(t, "Style", _style);
            Row(t, "Layout", _orientation);
            Row(t, "Position", _position);
            Row(t, "Screen", _monitor);
            Row(t, "Colour theme", _theme);
            Row(t, "Size (%)", _scale);
            Row(t, "Opacity (%)", _opacity);
            Row(t, "Distance from screen edge (px)", _offset);
            return page;
        }

        private TabPage GaugesTab(Config c)
        {
            TableLayoutPanel t;
            TabPage page = Page("Gauges", out t);

            Note(t, "Tick the readings to show in the strip. Order them with the buttons.");
            _segments = new CheckedListBox { CheckOnClick = true, Height = 230, Width = 300, IntegralHeight = false };
            List<string> enabled = c.SegmentList();
            foreach (string id in enabled) _segments.Items.Add(new SegItem(id), true);
            foreach (string id in Config.AllSegments)
                if (!enabled.Contains(id)) _segments.Items.Add(new SegItem(id), false);

            var up = new Button { Text = "Move up", AutoSize = true };
            var down = new Button { Text = "Move down", AutoSize = true };
            up.Click += delegate { MoveSelected(-1); };
            down.Click += delegate { MoveSelected(1); };
            var moves = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
            moves.Controls.Add(up);
            moves.Controls.Add(down);

            var list = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            list.Controls.Add(_segments);
            list.Controls.Add(moves);
            Span(t, list);

            var gpuChoices = new List<string> { "Automatic (card with the most video memory)" };
            gpuChoices.AddRange(_host.GpuNames());
            int gpuIndex = string.IsNullOrEmpty(c.Gpu) ? 0 : Math.Max(0, gpuChoices.IndexOf(c.Gpu));
            _gpu = Combo(gpuChoices.ToArray(), gpuIndex);
            _gpu.Width = 300;
            Row(t, "GPU shown in the strip", _gpu);
            return page;
        }

        private TabPage BehaviourTab(Config c)
        {
            TableLayoutPanel t;
            TabPage page = Page("Behaviour", out t);

            Note(t, "Show the strip:");
            _visAlways = new RadioButton { Text = "Always", AutoSize = true, Checked = c.Visibility == "Always" };
            _visFullscreen = new RadioButton { Text = "Hide it while a full-screen app is in front (video, games, slideshows)", AutoSize = true, Checked = c.Visibility == "HideFullscreen" };
            _visGames = new RadioButton { Text = "Only while a game is running", AutoSize = true, Checked = c.Visibility == "GamesOnly" };
            var radios = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(12, 0, 0, 8) };
            radios.Controls.Add(_visAlways);
            radios.Controls.Add(_visFullscreen);
            radios.Controls.Add(_visGames);
            Span(t, radios);

            _hoverHide = new CheckBox { Text = "Fade out when the mouse is over it", AutoSize = true, Checked = c.HoverHide };
            Span(t, _hoverHide);
            _startup = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = Installer.IsStartupEnabled() };
            Span(t, _startup);

            _hotkey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 160 };
            _hotkey.Items.AddRange(HotkeyChoices);
            _hotkey.Text = c.Hotkey;
            _hotkeyStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6) };
            Row(t, "Details panel shortcut", _hotkey);
            Row(t, "", _hotkeyStatus);

            _interval = Number(250, 5000, c.IntervalMs, 250);
            Row(t, "Update every (ms)", _interval);
            _pingHost = new TextBox { Width = 200, Text = c.PingHost };
            Row(t, "Ping gauge measures", _pingHost);
            return page;
        }

        private TabPage AlertsTab(Config c)
        {
            TableLayoutPanel t;
            TabPage page = Page("Alerts", out t);

            _alerts = new CheckBox { Text = "Show a Windows notification when something needs attention", AutoSize = true, Checked = c.AlertsEnabled };
            Span(t, _alerts);
            Note(t, "Set any threshold to 0 to turn that alert off.");
            _alertCpu = Number(0, 100, c.AlertCpu, 5);
            _alertRam = Number(0, 100, c.AlertRam, 5);
            _alertGpu = Number(0, 100, c.AlertGpu, 5);
            _alertGpuTemp = Number(0, 120, c.AlertGpuTemp, 1);
            _alertCpuTemp = Number(0, 120, c.AlertCpuTemp, 1);
            _alertDisk = Number(0, 50, c.AlertDiskFree, 1);
            _alertSustain = Number(0, 3600, c.AlertSustainSec, 10);
            Row(t, "CPU usage at or above (%)", _alertCpu);
            Row(t, "Memory usage at or above (%)", _alertRam);
            Row(t, "GPU usage at or above (%)", _alertGpu);
            Row(t, "GPU temperature at or above (°C)", _alertGpuTemp);
            Row(t, "CPU temperature at or above (°C)", _alertCpuTemp);
            Row(t, "A drive's free space below (%)", _alertDisk);
            Row(t, "...for at least (seconds)", _alertSustain);
            Note(t, "Each alert repeats at most every 15 minutes (drive space: every 6 hours).");
            return page;
        }

        private TabPage AdvancedTab(Config c)
        {
            TableLayoutPanel t;
            TabPage page = Page("Advanced", out t);

            // History log
            _log = new CheckBox { Text = "Record readings to a CSV file (one per day)", AutoSize = true, Checked = c.LogEnabled };
            var openLogs = new LinkLabel { Text = "Open folder", AutoSize = true, Margin = new Padding(12, 5, 0, 0) };
            openLogs.LinkClicked += delegate { OpenFolder(HistoryLog.Folder); };
            Span(t, Flow(_log, openLogs));
            _logInterval = Number(1, 3600, c.LogIntervalSec, 1);
            _logDays = Number(1, 365, c.LogKeepDays, 1);
            Row(t, "Record every (seconds)", _logInterval);
            Row(t, "Keep files for (days)", _logDays);

            // Updates
            _updates = new CheckBox { Text = "Check for updates automatically", AutoSize = true, Checked = c.UpdateCheck, Margin = new Padding(3, 12, 3, 3) };
            var checkNow = new Button { Text = "Check now", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
            checkNow.Click += delegate { _host.CheckForUpdatesInteractive(); };
            Span(t, Flow(_updates, checkNow));

            // FPS
            _fps = new CheckBox { Text = "Game FPS counter (experimental)", AutoSize = true, Checked = c.FpsEnabled, Margin = new Padding(3, 12, 3, 3) };
            _fpsGrant = new Button { Text = "Grant permission...", AutoSize = true, Margin = new Padding(12, 8, 0, 0), Visible = false };
            _fpsGrant.Click += delegate { _host.RequestFpsPermission(); };
            Span(t, Flow(_fps, _fpsGrant));
            _fpsStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(500, 0), Margin = new Padding(20, 0, 0, 4) };
            Span(t, _fpsStatus);

            // CPU temperature
            _cpuTemp = new CheckBox { Text = "Show CPU temperature from LibreHardwareMonitor", AutoSize = true, Checked = c.CpuTempEnabled, Margin = new Padding(3, 12, 3, 3) };
            Span(t, _cpuTemp);
            _cpuTempStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(500, 0), Margin = new Padding(20, 0, 0, 4) };
            Span(t, _cpuTempStatus);

            var diag = new LinkLabel { Text = "Open diagnostic log", AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
            diag.LinkClicked += delegate
            {
                if (File.Exists(DiagLog.FilePath)) Process.Start(new ProcessStartInfo(DiagLog.FilePath) { UseShellExecute = true });
                else MessageBox.Show(this, "Nothing has been logged yet.", Installer.AppName);
            };
            Span(t, diag);
            return page;
        }

        // ---------- status ----------

        private void RefreshStatus()
        {
            if (IsDisposed) return;
            uint mods, vk;
            bool validHotkey = OverlayForm.ParseHotkey(_hotkey.Text, out mods, out vk);
            if (_hotkey.Text.Equals("Off", StringComparison.OrdinalIgnoreCase))
                _hotkeyStatus.Text = "No shortcut. Left-click the tray icon to open details.";
            else if (!validHotkey)
                _hotkeyStatus.Text = "Use a form like Ctrl+Shift+G.";
            else if (_hotkey.Text == _host.CurrentConfig.Hotkey && !_host.HotkeyRegistered)
                _hotkeyStatus.Text = "Another app is already using this shortcut. Pick a different one.";
            else
                _hotkeyStatus.Text = "Left-clicking the tray icon also opens details.";

            FpsState fps = _host.FpsStatus;
            _fpsGrant.Visible = _host.CurrentConfig.FpsEnabled && fps == FpsState.NeedsPermission;
            if (!_host.CurrentConfig.FpsEnabled)
                _fpsStatus.Text = "Counts frames for Direct3D 9-12 games in front. Add the FPS gauge on the Gauges tab.";
            else if (fps == FpsState.Running)
                _fpsStatus.Text = "Running. The FPS gauge shows the game in front.";
            else if (fps == FpsState.NeedsPermission)
                _fpsStatus.Text = "Windows needs to allow your account to read frame timing (a one-time step that asks for administrator approval).";
            else
                _fpsStatus.Text = "The FPS counter couldn't start. See the diagnostic log.";

            if (!_host.CurrentConfig.CpuTempEnabled)
                _cpuTempStatus.Text = "Off.";
            else if (_host.CpuTempNow >= 0)
                _cpuTempStatus.Text = "Reading " + _host.CpuTempNow + " °C from " + _host.CpuTempSource + ".";
            else
                _cpuTempStatus.Text = "Windows has no built-in CPU temperature. Run LibreHardwareMonitor (free) and it's picked up automatically.";
        }

        // ---------- apply ----------

        private void Apply()
        {
            Config c = _host.CurrentConfig.Clone();

            c.Style = Config.Styles[Math.Max(0, _style.SelectedIndex)];
            c.Orientation = Config.Orientations[Math.Max(0, _orientation.SelectedIndex)];
            c.Position = Config.Positions[Math.Max(0, _position.SelectedIndex)];
            c.Monitor = Math.Max(0, _monitor.SelectedIndex);
            c.Theme = Config.Themes[Math.Max(0, _theme.SelectedIndex)];
            c.Scale = (double)_scale.Value / 100.0;
            c.Opacity = (double)_opacity.Value / 100.0;
            c.YOffset = (int)_offset.Value;

            var ids = new List<string>();
            for (int i = 0; i < _segments.Items.Count; i++)
                if (_segments.GetItemChecked(i)) ids.Add(((SegItem)_segments.Items[i]).Id);
            if (ids.Count == 0) ids.Add("cpu");
            c.Segments = string.Join(",", ids.ToArray());
            c.Gpu = _gpu.SelectedIndex <= 0 ? "" : _gpu.SelectedItem.ToString();

            c.Visibility = _visGames.Checked ? "GamesOnly" : _visFullscreen.Checked ? "HideFullscreen" : "Always";
            c.HoverHide = _hoverHide.Checked;
            uint mods, vk;
            string hk = _hotkey.Text.Trim();
            c.Hotkey = hk.Equals("Off", StringComparison.OrdinalIgnoreCase) || OverlayForm.ParseHotkey(hk, out mods, out vk) ? hk : c.Hotkey;
            c.IntervalMs = (int)_interval.Value;
            c.PingHost = string.IsNullOrWhiteSpace(_pingHost.Text) ? "1.1.1.1" : _pingHost.Text.Trim();

            c.AlertsEnabled = _alerts.Checked;
            c.AlertCpu = (int)_alertCpu.Value;
            c.AlertRam = (int)_alertRam.Value;
            c.AlertGpu = (int)_alertGpu.Value;
            c.AlertGpuTemp = (int)_alertGpuTemp.Value;
            c.AlertCpuTemp = (int)_alertCpuTemp.Value;
            c.AlertDiskFree = (int)_alertDisk.Value;
            c.AlertSustainSec = (int)_alertSustain.Value;

            c.LogEnabled = _log.Checked;
            c.LogIntervalSec = (int)_logInterval.Value;
            c.LogKeepDays = (int)_logDays.Value;
            c.UpdateCheck = _updates.Checked;
            c.FpsEnabled = _fps.Checked;
            c.CpuTempEnabled = _cpuTemp.Checked;

            if (_startup.Checked != Installer.IsStartupEnabled())
                Installer.SetStartup(_startup.Checked, Installer.CurrentExe);
            c.StartupOff = !_startup.Checked;

            _host.ApplyConfig(c);
            RefreshStatus();
        }

        // ---------- helpers ----------

        private sealed class SegItem
        {
            public readonly string Id;
            public SegItem(string id) { Id = id; }
            public override string ToString()
            {
                for (int i = 0; i < SegmentNames.Length; i += 2)
                    if (SegmentNames[i] == Id) return SegmentNames[i + 1];
                return Id;
            }
        }

        private void MoveSelected(int delta)
        {
            int i = _segments.SelectedIndex;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _segments.Items.Count) return;
            object item = _segments.Items[i];
            bool isChecked = _segments.GetItemChecked(i);
            _segments.Items.RemoveAt(i);
            _segments.Items.Insert(j, item);
            _segments.SetItemChecked(j, isChecked);
            _segments.SelectedIndex = j;
        }

        private static TabPage Page(string title, out TableLayoutPanel table)
        {
            var page = new TabPage(title) { Padding = new Padding(10), AutoScroll = true };
            table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            page.Controls.Add(table);
            return page;
        }

        private static void Row(TableLayoutPanel t, string label, Control control)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) }, 0, r);
            control.Anchor = AnchorStyles.Left;
            t.Controls.Add(control, 1, r);
        }

        private static void Span(TableLayoutPanel t, Control control)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(control, 0, r);
            t.SetColumnSpan(control, 2);
        }

        private static void Note(TableLayoutPanel t, string text)
        {
            Span(t, new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(500, 0), Margin = new Padding(3, 4, 3, 6) });
        }

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            f.Controls.AddRange(controls);
            return f;
        }

        private static ComboBox Combo(string[] items, int selected)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            c.Items.AddRange(items);
            c.SelectedIndex = Math.Max(0, Math.Min(selected, items.Length - 1));
            return c;
        }

        private static NumericUpDown Number(int min, int max, int value, int step)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, value)),
                Increment = step,
                Width = 80
            };
        }

        private static void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _statusTimer.Stop();
            _statusTimer.Dispose();
            base.OnFormClosed(e);
        }
    }
}
