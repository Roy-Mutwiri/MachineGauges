using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MachineGauges
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int pid);

        /// <summary>Nominal (base) CPU clock in MHz, the figure Task Manager scales "Speed" from.</summary>
        internal static double BaseMhz()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("~MHz");
                        if (v != null)
                        {
                            double mhz = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                            if (mhz > 100) return mhz;
                        }
                    }
                }
            }
            catch { }
            return 3000;
        }

        //  (no args)              first run: offer to install; installed copy: run the overlay
        //  --autostart            launched by Windows at sign-in (never shows dialogs)
        //  --install [--quiet]    install / update for the current user and start it
        //  --install --updated    same, used by the auto-updater
        //  --uninstall [--quiet]
        //  --probe [N]            print N raw samples for comparing against Task Manager
        //  --preview <png> [key=value ...]    render the strip to an image
        //  --panel-preview <png> [seconds]    render the details panel to an image
        //  --grant-fps <account>  (elevated) allow an account to run the FPS counter
        //  --settings             open Settings (in the running copy if there is one)
        //  --check-update [--download]   report / verify the latest release without installing
        [STAThread]
        private static int Main(string[] args)
        {
            var flags = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
            bool quiet = flags.Contains("--quiet");
            string first = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (first == "--probe")
            {
                int count = 5;
                if (args.Length > 1) int.TryParse(args[1], out count);
                Probe(Math.Max(1, count));
                return 0;
            }

            if (first == "--check-update") return CheckUpdateCli(flags.Contains("--download"));

            Win32.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (first == "--grant-fps" && args.Length > 1)
            {
                string error = FpsMonitor.GrantPermission(args[1]);
                if (error == null) return 0;
                MessageBox.Show("Couldn't grant FPS counter access:\n\n" + error, Installer.AppName,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            if (first == "--preview" && args.Length > 1)
            {
                var overrides = new List<string>();
                for (int i = 2; i < args.Length; i++) overrides.Add(args[i]);
                OverlayForm.SavePreview(args[1], BaseMhz(), overrides);
                return 0;
            }

            if (first == "--panel-preview" && args.Length > 1)
            {
                int seconds = 60;
                if (args.Length > 2) int.TryParse(args[2], out seconds);
                PanelPreview(args[1], Math.Max(2, seconds));
                return 0;
            }

            if (flags.Contains("--uninstall"))
            {
                Installer.Uninstall(quiet);
                return 0;
            }

            if (flags.Contains("--install"))
            {
                Installer.Install();
                Installer.Launch(Installer.InstalledExe,
                                 flags.Contains("--updated") ? "--updated" : quiet ? "--autostart" : "--welcome");
                return 0;
            }

            bool autostart = flags.Contains("--autostart");
            if (!autostart && !Installer.IsRunningInstalledCopy() && Installer.OfferInstall())
                return 0;

            RunOverlay(autostart, flags.Contains("--welcome"), flags.Contains("--updated"), flags.Contains("--settings"));
            return 0;
        }

        // Windows' own processes only, so a published screenshot shows nothing personal.
        private static readonly HashSet<string> SystemProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Registry", "Memory Compression", "svchost", "dwm", "explorer", "csrss", "lsass", "services",
            "wininit", "winlogon", "MsMpEng", "SearchHost", "SearchIndexer", "StartMenuExperienceHost", "audiodg",
            "RuntimeBroker", "ShellExperienceHost", "WmiPrvSE", "spoolsv", "fontdrvhost", "smss", "ctfmon",
            "TextInputHost", "sihost", "taskhostw", "SecurityHealthService", "WidgetService", "Widgets", "conhost"
        };

        private static void PanelPreview(string path, int seconds)
        {
            using (var host = new OverlayForm(Config.Load(), BaseMhz(), false))
            {
                var history = new History();
                host.Sampler.DetailRequested = true;
                Snapshot s = null;
                for (int i = 0; i < seconds; i++)
                {
                    Thread.Sleep(1000);
                    s = host.Sampler.Sample();
                    history.Add(s);
                }
                if (s.Processes != null) s.Processes.RemoveAll(p => !SystemProcessNames.Contains(p.Name));
                DetailPanel.SavePreview(path, host, history, s);
            }
        }

        private static void RunOverlay(bool autostart, bool welcome, bool updated, bool settings)
        {
            bool created;
            using (var mutex = new Mutex(true, Installer.MutexName, out created))
            {
                if (!created)
                {
                    // Already running: opening the app again brings up its window instead.
                    if (autostart) return;
                    string ev = settings ? Installer.ShowSettingsEventName : Installer.ShowDetailsEventName;
                    if (!Installer.SignalRunning(ev)) Installer.ShowAlreadyRunning();
                    return;
                }

                // Lets the installer / uninstaller ask this copy to close, and later launches open windows.
                using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEventName))
                using (var showDetails = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.ShowDetailsEventName))
                using (var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.ShowSettingsEventName))
                {
                    Application.ThreadException += (s, e) => DiagLog.Write("ui error: " + e.Exception);
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => DiagLog.Write("fatal: " + e.ExceptionObject);

                    Config cfg = Config.Load();
                    if (Installer.IsRunningInstalledCopy()) Installer.RepairRegistration(cfg.StartupOff);

                    using (var form = new OverlayForm(cfg, BaseMhz(), true))
                    {
                        var waits = new List<RegisteredWaitHandle>
                        {
                            OnSignal(quit, form, form.Close, true),
                            OnSignal(showDetails, form, () => form.ToggleDetails(true), false),
                            OnSignal(showSettings, form, form.ShowSettings, false)
                        };

                        if (updated) form.Shown += delegate { form.ShowUpdated(); };
                        else if (welcome) form.Shown += delegate { form.ShowWelcome(); };
                        if (settings) form.Shown += delegate { form.ShowSettings(); };

                        // Tie the message loop to the form so "Exit" really ends the process.
                        Application.Run(form);
                        foreach (RegisteredWaitHandle w in waits) w.Unregister(null);
                    }
                }
            }
        }

        private static RegisteredWaitHandle OnSignal(WaitHandle handle, Form form, Action action, bool once)
        {
            return ThreadPool.RegisterWaitForSingleObject(handle, delegate
            {
                try { form.BeginInvoke(new MethodInvoker(action)); } catch { }
            }, null, Timeout.Infinite, once);
        }

        /// <summary>--check-update: exercises the real update check and verified download without installing.</summary>
        private static int CheckUpdateCli(bool download)
        {
            AttachConsole(-1);
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("current: " + Updater.CurrentVersion);
                UpdateInfo info = Updater.CheckForNewer();
                if (info == null)
                {
                    sb.AppendLine("no newer release");
                }
                else
                {
                    sb.AppendLine("newer: " + info.Version + "  url: " + info.DownloadUrl);
                    sb.AppendLine("size: " + info.Size + "  sha256: " + (info.Sha256 ?? "(none)"));
                    if (download)
                    {
                        string file;
                        string error = Updater.DownloadAndVerify(info, out file);
                        sb.AppendLine(error == null ? "verified: " + file : "rejected: " + error);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("error: " + ex.Message);
            }
            Console.Write(sb.ToString());
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "machinegauges-update-check.txt"), sb.ToString()); } catch { }
            return 0;
        }

        private static void AppendDetail(StringBuilder sb, Sampler sampler, Snapshot s)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            sb.AppendLine("--- detail (last sample) ---");
            sb.AppendLine("cpu: " + sampler.CpuName + ", " + s.CoreUtil.Length + " logical processors");
            var cores = new StringBuilder();
            foreach (double c in s.CoreUtil) cores.Append(c.ToString("0", ic)).Append(' ');
            sb.AppendLine("cores %: " + cores);
            for (int i = 0; i < s.Gpus.Count; i++)
            {
                GpuReading g = s.Gpus[i];
                sb.AppendLine(string.Format(ic,
                    "gpu{0}{1}: {2} [{3}] util {4:0.0}% vram {5:0.00}/{6:0.00}GB temp {7} power {8:0.0}/{9:0}W fan {10}% core {11}MHz",
                    i, i == s.SelectedGpu ? "*" : "", g.Name, g.LuidKey, g.Util, g.VramUsedGB, g.VramTotalGB,
                    g.TempC, g.PowerW, g.PowerLimitW, g.FanPct, g.CoreMhz));
            }
            foreach (DiskReading d in s.Disks)
                sb.AppendLine(string.Format(ic, "disk: {0} active {1:0.0}% read {2:0.00}MB/s write {3:0.00}MB/s",
                    d.Name, d.Active, d.ReadBps / 1048576.0, d.WriteBps / 1048576.0));
            foreach (VolumeReading v in s.Volumes)
                sb.AppendLine(string.Format(ic, "volume: {0} {1:0.0} GB free of {2:0.0} GB", v.Name, v.FreeGB, v.TotalGB));
            sb.AppendLine(string.Format(ic, "uptime {0}  battery {1}%  foreground pid {2}", s.Uptime, s.BatteryPct, s.ForegroundPid));
            if (s.Processes != null)
            {
                s.Processes.Sort((a, b) => b.MemGB.CompareTo(a.MemGB));
                sb.AppendLine("processes (" + s.Processes.Count + " names), top by memory:");
                for (int i = 0; i < Math.Min(8, s.Processes.Count); i++)
                {
                    ProcReading p = s.Processes[i];
                    sb.AppendLine(string.Format(ic, "  {0,-28} x{1,-3} cpu {2,5:0.0}%  mem {3,6:0.000}GB  gpu {4,5:0.0}%",
                        p.Name, p.Count, p.Cpu, p.MemGB, p.Gpu));
                }
                s.Processes.Sort((a, b) => b.Cpu.CompareTo(a.Cpu));
                sb.AppendLine("top by cpu:");
                for (int i = 0; i < Math.Min(5, s.Processes.Count); i++)
                    sb.AppendLine(string.Format(ic, "  {0,-28} cpu {1,5:0.0}%", s.Processes[i].Name, s.Processes[i].Cpu));
            }
        }

        /// <summary>Prints raw samples so the readings can be diffed against Task Manager.</summary>
        private static void Probe(int count)
        {
            AttachConsole(-1);
            var sb = new StringBuilder();
            using (var sampler = new Sampler(BaseMhz()))
            {
                sampler.DetailRequested = true;
                Snapshot s = null;
                for (int i = 0; i < count; i++)
                {
                    Thread.Sleep(1000);
                    s = sampler.Sample();
                    string line = string.Format(CultureInfo.InvariantCulture,
                        "CPU {0,5:0.0}% {1,5:0.00}GHz | RAM {2,5:0.0}% {3,5:0.00}/{4,5:0.00}GB | GPU {5,5:0.0}% VRAM {6,5:0.00}/{7,5:0.00}GB {8}C | DISK {9,5:0.0}% {10,8:0.0}MB/s | NET {11,8:0.0}Mb/s (down {12:0.0} up {13:0.0})",
                        s.CpuPercent, s.CpuGhz, s.RamPercent, s.RamUsedGB, s.RamTotalGB,
                        s.GpuPercent, s.VramUsedGB, s.VramTotalGB, s.GpuTempC,
                        s.DiskPercent, s.DiskBytesPerSec / (1024.0 * 1024.0), s.NetBitsPerSec / 1e6,
                        s.NetRecvBitsPerSec / 1e6, s.NetSentBitsPerSec / 1e6);
                    Console.WriteLine(line);
                    sb.AppendLine(line);
                }
                AppendDetail(sb, sampler, s);
                Console.Write(sb.ToString().Substring(sb.ToString().IndexOf("--- detail", StringComparison.Ordinal)));
            }
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "machinegauges-probe.txt"), sb.ToString());
            }
            catch { }
        }
    }
}
