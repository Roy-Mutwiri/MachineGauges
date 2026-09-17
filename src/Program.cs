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

        //  (no args)            first run: offer to install; installed copy: run the overlay
        //  --autostart          launched by Windows at sign-in (never shows dialogs)
        //  --install [--quiet]  install / update for the current user and start it
        //  --uninstall [--quiet]
        //  --probe [N]          print N raw samples for comparing against Task Manager
        //  --preview <file.png> render the overlay to an image
        [STAThread]
        private static void Main(string[] args)
        {
            var flags = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
            bool quiet = flags.Contains("--quiet");

            if (args.Length > 0 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
            {
                int count = 5;
                if (args.Length > 1) int.TryParse(args[1], out count);
                Probe(Math.Max(1, count));
                return;
            }

            Win32.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 1 && args[0].Equals("--preview", StringComparison.OrdinalIgnoreCase))
            {
                OverlayForm.SavePreview(args[1], BaseMhz());
                return;
            }

            if (flags.Contains("--uninstall"))
            {
                Installer.Uninstall(quiet);
                return;
            }

            if (flags.Contains("--install"))
            {
                Installer.Install();
                Installer.Launch(Installer.InstalledExe, quiet ? "--autostart" : "--welcome");
                return;
            }

            bool autostart = flags.Contains("--autostart");
            if (!autostart && !Installer.IsRunningInstalledCopy() && Installer.OfferInstall())
                return;

            RunOverlay(autostart, flags.Contains("--welcome"));
        }

        private static void RunOverlay(bool autostart, bool welcome)
        {
            bool created;
            using (var mutex = new Mutex(true, Installer.MutexName, out created))
            {
                if (!created)
                {
                    if (!autostart) Installer.ShowAlreadyRunning();
                    return;
                }

                // Lets the installer / uninstaller ask this copy to close cleanly.
                using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEventName))
                {
                    Application.ThreadException += delegate { };
                    AppDomain.CurrentDomain.UnhandledException += delegate { };

                    using (var form = new OverlayForm(Config.Load(), BaseMhz(), true))
                    {
                        RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(quit, delegate
                        {
                            try { form.BeginInvoke(new MethodInvoker(form.Close)); } catch { }
                        }, null, Timeout.Infinite, true);

                        if (welcome) form.Shown += delegate { form.ShowWelcome(); };

                        // Tie the message loop to the form so "Exit" really ends the process.
                        Application.Run(form);
                        wait.Unregister(null);
                    }
                }
            }
        }

        /// <summary>Prints raw samples so the readings can be diffed against Task Manager.</summary>
        private static void Probe(int count)
        {
            AttachConsole(-1);
            var sb = new StringBuilder();
            using (var sampler = new Sampler(BaseMhz()))
            {
                for (int i = 0; i < count; i++)
                {
                    Thread.Sleep(1000);
                    Snapshot s = sampler.Sample();
                    string line = string.Format(CultureInfo.InvariantCulture,
                        "CPU {0,5:0.0}% {1,5:0.00}GHz | RAM {2,5:0.0}% {3,5:0.00}/{4,5:0.00}GB | GPU {5,5:0.0}% VRAM {6,5:0.00}/{7,5:0.00}GB {8}C | DISK {9,5:0.0}% {10,8:0.0}MB/s | NET {11,8:0.0}Mb/s (down {12:0.0} up {13:0.0})",
                        s.CpuPercent, s.CpuGhz, s.RamPercent, s.RamUsedGB, s.RamTotalGB,
                        s.GpuPercent, s.VramUsedGB, s.VramTotalGB, s.GpuTempC,
                        s.DiskPercent, s.DiskBytesPerSec / (1024.0 * 1024.0), s.NetBitsPerSec / 1e6,
                        s.NetRecvBitsPerSec / 1e6, s.NetSentBitsPerSec / 1e6);
                    Console.WriteLine(line);
                    sb.AppendLine(line);
                }
            }
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "machinegauges-probe.txt"), sb.ToString());
            }
            catch { }
        }
    }
}
