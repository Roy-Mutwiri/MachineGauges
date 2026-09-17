using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MachineGauges
{
    /// <summary>
    /// Lets the single downloadable exe install, update and uninstall itself for the
    /// current user - no admin rights and no separate setup program.
    /// </summary>
    internal static class Installer
    {
        public const string AppName = "MachineGauges";
        public const string ProjectUrl = "https://github.com/Roy-Mutwiri/MachineGauges";
        public const string MutexName = @"Local\MachineGauges_SingleInstance";
        public const string QuitEventName = @"Local\MachineGauges_Quit";
        public const string ShowDetailsEventName = @"Local\MachineGauges_ShowDetails";
        public const string ShowSettingsEventName = @"Local\MachineGauges_ShowSettings";

        /// <summary>Asks the running copy to open a window. Returns false if nothing is listening.</summary>
        public static bool SignalRunning(string eventName)
        {
            EventWaitHandle ev;
            if (!EventWaitHandle.TryOpenExisting(eventName, out ev)) return false;
            using (ev) ev.Set();
            return true;
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MachineGauges";
        private const string ValueName = "MachineGauges";

        private static string LocalAppData
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        }

        private static string StartMenuPrograms
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    @"Microsoft\Windows\Start Menu\Programs");
            }
        }

        public static string InstallDir { get { return Path.Combine(LocalAppData, @"Programs\MachineGauges"); } }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "MachineGauges.exe"); } }
        private static string ShortcutPath { get { return Path.Combine(StartMenuPrograms, "MachineGauges.lnk"); } }
        public static string CurrentExe { get { return Assembly.GetExecutingAssembly().Location; } }
        public static Version CurrentVersion { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        public static string VersionText(Version v)
        {
            return v.ToString(3);
        }

        public static bool IsRunningInstalledCopy()
        {
            try
            {
                return string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static Version InstalledVersion()
        {
            try
            {
                if (!File.Exists(InstalledExe)) return null;
                return new Version(FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion);
            }
            catch { return null; }
        }

        public static bool IsRunning()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(MutexName, out m)) return false;
            m.Dispose();
            return true;
        }

        // ---------- first run of the downloaded exe ----------

        /// <summary>
        /// Called when the exe is started from anywhere other than its install folder.
        /// Returns true when this process should exit, false to keep running portably.
        /// </summary>
        public static bool OfferInstall()
        {
            Version installed = InstalledVersion();
            Version current = CurrentVersion;

            if (installed != null && installed >= current)
            {
                RepairRegistration(Config.Load().StartupOff);
                if (!IsRunning()) Launch(InstalledExe, "");
                else if (!SignalRunning(ShowDetailsEventName)) ShowAlreadyRunning();
                return true;
            }

            if (installed == null)
            {
                DialogResult r = MessageBox.Show(
                    "Install MachineGauges on this PC?\n\n" +
                    "Live CPU, RAM, GPU, disk and network gauges pinned to the top of your screen. " +
                    "It starts with Windows, needs no admin rights, and can be removed at any time " +
                    "from Settings > Apps.\n\n" +
                    "Yes\t- Install (recommended)\n" +
                    "No\t- Just run it this time\n" +
                    "Cancel\t- Do nothing",
                    AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.No) return false;
                if (r != DialogResult.Yes) return true;
            }
            else
            {
                DialogResult r = MessageBox.Show(
                    "Update MachineGauges from version " + VersionText(installed) +
                    " to " + VersionText(current) + "?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    if (!IsRunning()) Launch(InstalledExe, "");
                    return true;
                }
            }

            try
            {
                Install();
            }
            catch (Exception ex)
            {
                MessageBox.Show("MachineGauges could not be installed:\n\n" + ex.Message,
                                AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return true;
            }

            Launch(InstalledExe, "--welcome");
            return true;
        }

        public static void ShowAlreadyRunning()
        {
            MessageBox.Show(
                "MachineGauges is already running.\n\n" +
                "The gauges are at the top centre of your screen. Right-click the gauge icon " +
                "in the taskbar tray for settings.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------- install / uninstall ----------

        public static void Install()
        {
            StopOtherInstances();
            RemoveLegacyInstall();

            Directory.CreateDirectory(InstallDir);
            if (!IsRunningInstalledCopy()) CopyWithRetry(CurrentExe, InstalledExe);

            SetStartup(true, InstalledExe);
            CreateShortcut();
            RegisterUninstallEntry();
        }

        public static void Uninstall(bool quiet)
        {
            if (!quiet)
            {
                DialogResult r = MessageBox.Show(
                    "Remove MachineGauges from this PC?\n\n" +
                    "It will be closed, removed from startup, and its files and settings deleted.",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            StopOtherInstances();
            RemoveLegacyInstall();
            SetStartup(false, null);
            TryDeleteFile(ShortcutPath);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            TryDeleteDirectory(Config.Dir);

            if (Directory.Exists(InstallDir))
            {
                if (IsRunningInstalledCopy())
                {
                    // A running exe can't delete itself: hand the folder to a hidden shell
                    // that waits a moment for this process to exit.
                    var psi = new ProcessStartInfo("cmd.exe",
                        "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + InstallDir + "\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    try { Process.Start(psi); } catch { }
                }
                else
                {
                    TryDeleteDirectory(InstallDir);
                }
            }

            if (!quiet)
                MessageBox.Show("MachineGauges has been removed.", AppName,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Asks any running copy to close, then force-closes stragglers.</summary>
        private static void StopOtherInstances()
        {
            EventWaitHandle quit;
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out quit))
            {
                using (quit) quit.Set();
                for (int i = 0; i < 40 && IsRunning(); i++) Thread.Sleep(100);
            }

            int self = Process.GetCurrentProcess().Id;
            foreach (string name in new[] { "MachineGauges", "PerfOverlay" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.Id != self)
                        {
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
        }

        /// <summary>Cleans up the earlier "PC Performance Overlay" (PerfOverlay) install.</summary>
        private static void RemoveLegacyInstall()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null) k.DeleteValue("PerfOverlay", false);
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    if (k != null) k.DeleteValue("PerfOverlay", false);
            }
            catch { }

            TryDeleteFile(Path.Combine(StartMenuPrograms, "PC Performance Overlay.lnk"));

            string legacyConfigDir = Path.Combine(LocalAppData, "PerfOverlay");
            string legacyConfig = Path.Combine(legacyConfigDir, "config.ini");
            string newConfig = Path.Combine(Config.Dir, "config.ini");
            try
            {
                if (File.Exists(legacyConfig) && !File.Exists(newConfig))
                {
                    Directory.CreateDirectory(Config.Dir);
                    File.Copy(legacyConfig, newConfig);
                }
            }
            catch { }
            TryDeleteDirectory(legacyConfigDir);
            TryDeleteDirectory(Path.Combine(LocalAppData, @"Programs\PerfOverlay"));
        }

        /// <summary>
        /// Restores the startup entry, the Settings > Apps entry and the Start Menu shortcut if they have
        /// gone missing: registry cleaners remove them, and an install run from inside a sandboxed
        /// (packaged) app writes them to that app's private registry, where Windows never looks.
        /// A user who turned "Start with Windows" off keeps it off.
        /// </summary>
        public static void RepairRegistration(bool startupOff)
        {
            if (!File.Exists(InstalledExe)) return;
            try
            {
                string expected = "\"" + InstalledExe + "\" --autostart";
                string current;
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    current = run == null ? null : run.GetValue(ValueName) as string;
                if (!startupOff && !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                {
                    SetStartup(true, InstalledExe);
                    DiagLog.Write("repair: startup entry " + (current == null ? "restored" : "corrected"));
                }

                bool listed;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey, false)) listed = k != null;
                if (!listed)
                {
                    RegisterUninstallEntry();
                    DiagLog.Write("repair: Settings > Apps entry restored");
                }

                if (!File.Exists(ShortcutPath))
                {
                    CreateShortcut();
                    DiagLog.Write("repair: Start Menu shortcut restored");
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write("repair failed: " + ex.Message);
            }
        }

        // ---------- startup ----------

        public static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (run == null || string.IsNullOrEmpty(run.GetValue(ValueName) as string)) return false;
                }
                // Settings > Apps > Startup can switch an entry off without removing it.
                using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
                {
                    var bytes = approved == null ? null : approved.GetValue(ValueName) as byte[];
                    return bytes == null || bytes.Length == 0 || (bytes[0] & 1) == 0;
                }
            }
            catch { return false; }
        }

        public static void SetStartup(bool enabled, string exePath)
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
                using (RegistryKey approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
                {
                    if (enabled)
                    {
                        run.SetValue(ValueName, "\"" + exePath + "\" --autostart");
                        approved.SetValue(ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                                          RegistryValueKind.Binary);
                    }
                    else
                    {
                        run.DeleteValue(ValueName, false);
                        approved.DeleteValue(ValueName, false);
                    }
                }
            }
            catch { }
        }

        // ---------- shell integration ----------

        private static void CreateShortcut()
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                                     new object[] { ShortcutPath });
                try
                {
                    Type linkType = link.GetType();
                    SetProp(linkType, link, "TargetPath", InstalledExe);
                    SetProp(linkType, link, "WorkingDirectory", InstallDir);
                    SetProp(linkType, link, "IconLocation", InstalledExe + ",0");
                    SetProp(linkType, link, "Description", "Live CPU, RAM, GPU, disk and network gauges");
                    linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        private static void SetProp(Type t, object target, string name, object value)
        {
            t.InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }

        /// <summary>Lists the app under Settings > Apps so it uninstalls like any other program.</summary>
        private static void RegisterUninstallEntry()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                if (k == null) return;
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", VersionText(CurrentVersion));
                k.SetValue("Publisher", "MachineGauges");
                k.SetValue("DisplayIcon", "\"" + InstalledExe + "\",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" --uninstall --quiet");
                k.SetValue("URLInfoAbout", ProjectUrl);
                k.SetValue("HelpLink", ProjectUrl + "/issues");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                try
                {
                    int kb = (int)Math.Ceiling(new FileInfo(InstalledExe).Length / 1024.0);
                    k.SetValue("EstimatedSize", kb, RegistryValueKind.DWord);
                }
                catch { }
            }
        }

        // ---------- helpers ----------

        public static void Launch(string exe, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                });
            }
            catch { }
        }

        public static void OpenProjectPage()
        {
            try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); }
            catch { }
        }

        private static void CopyWithRetry(string from, string to)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(from, to, true);
                    return;
                }
                catch (IOException)
                {
                    if (attempt >= 15) throw;
                    Thread.Sleep(300);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= 15) throw;
                    Thread.Sleep(300);
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
