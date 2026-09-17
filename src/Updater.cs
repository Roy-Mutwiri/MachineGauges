using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

namespace MachineGauges
{
    internal sealed class UpdateInfo
    {
        public Version Version;
        public string DownloadUrl;
        public string Sha256;      // lower-case hex, from GitHub's asset digest
        public long Size;
        public string PageUrl;
    }

    /// <summary>
    /// Checks GitHub for a newer release and installs it. Downloads are only run after their
    /// SHA-256 matches the digest GitHub publishes for the asset and the file's own version
    /// matches the release tag.
    /// </summary>
    internal static class Updater
    {
        private const string LatestApi = "https://api.github.com/repos/Roy-Mutwiri/MachineGauges/releases/latest";
        private const string AssetName = "MachineGauges.exe";

        /// <summary>For testing the update path: pretend to be this version (env MACHINEGAUGES_PRETEND_VERSION).</summary>
        public static Version CurrentVersion
        {
            get
            {
                string pretend = Environment.GetEnvironmentVariable("MACHINEGAUGES_PRETEND_VERSION");
                Version v;
                if (!string.IsNullOrEmpty(pretend) && Version.TryParse(pretend, out v)) return v;
                return Installer.CurrentVersion;
            }
        }

        /// <summary>Returns the latest release if it is newer than this copy, otherwise null. Throws on network errors.</summary>
        public static UpdateInfo CheckForNewer()
        {
            EnableTls12();
            var req = (HttpWebRequest)WebRequest.Create(LatestApi);
            req.UserAgent = "MachineGauges/" + Installer.VersionText(Installer.CurrentVersion);
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;

            string json;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
                json = reader.ReadToEnd();

            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            string tag = Convert.ToString(root["tag_name"]).TrimStart('v', 'V');
            Version latest;
            if (!Version.TryParse(tag, out latest)) return null;
            latest = Normalize(latest);
            if (latest <= Normalize(CurrentVersion)) return null;

            var info = new UpdateInfo { Version = latest, PageUrl = Convert.ToString(root["html_url"]) };
            foreach (object o in (IEnumerable)root["assets"])
            {
                var asset = (Dictionary<string, object>)o;
                if (!string.Equals(Convert.ToString(asset["name"]), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                info.DownloadUrl = Convert.ToString(asset["browser_download_url"]);
                info.Size = Convert.ToInt64(asset["size"]);
                object digest;
                if (asset.TryGetValue("digest", out digest) && digest != null)
                {
                    string d = Convert.ToString(digest);
                    if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) info.Sha256 = d.Substring(7).ToLowerInvariant();
                }
            }
            return info;
        }

        /// <summary>Downloads, verifies and launches the installer. Returns null on success, otherwise why not.</summary>
        public static string DownloadAndInstall(UpdateInfo info)
        {
            string file;
            string error = DownloadAndVerify(info, out file);
            if (error != null) return error;

            DiagLog.Write("update: installing " + info.Version);
            Process.Start(new ProcessStartInfo(file, "--install --updated")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(file)
            });
            return null;
        }

        /// <summary>Downloads the release exe and checks size, SHA-256 and version. Returns null when it passes.</summary>
        public static string DownloadAndVerify(UpdateInfo info, out string file)
        {
            file = null;
            if (string.IsNullOrEmpty(info.DownloadUrl)) return "The release has no MachineGauges.exe attached.";
            if (string.IsNullOrEmpty(info.Sha256)) return "GitHub didn't publish a checksum for this download, so it can't be verified.";

            EnableTls12();
            string dir = Path.Combine(Path.GetTempPath(), "MachineGauges-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, AssetName);

            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "MachineGauges/" + Installer.VersionText(Installer.CurrentVersion);
                client.DownloadFile(info.DownloadUrl, file);
            }

            var fi = new FileInfo(file);
            if (info.Size > 0 && fi.Length != info.Size)
                return Reject(file, "The download was incomplete.");

            string hash;
            using (var sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(file))
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (hash != info.Sha256)
                return Reject(file, "The download didn't match its published checksum.");

            Version fileVersion;
            if (!Version.TryParse(FileVersionInfo.GetVersionInfo(file).FileVersion ?? "", out fileVersion) ||
                Normalize(fileVersion) != info.Version)
                return Reject(file, "The downloaded file's version doesn't match the release.");

            DiagLog.Write("update: verified " + info.Version + " (sha256 " + hash + ")");
            return null;
        }

        private static string Reject(string file, string reason)
        {
            DiagLog.Write("update rejected: " + reason);
            try { File.Delete(file); } catch { }
            return reason;
        }

        private static Version Normalize(Version v)
        {
            return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }

        private static void EnableTls12()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
        }
    }
}
