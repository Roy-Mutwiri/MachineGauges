using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading;

namespace MachineGauges
{
    /// <summary>Background latency check against one host, every 2 seconds.</summary>
    internal sealed class PingMonitor : IDisposable
    {
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly Queue<bool> _recent = new Queue<bool>();
        private readonly object _lock = new object();
        private Thread _thread;
        private int _lastMs = -1;
        private int _lossPct;

        public string Host { get; private set; }

        public int LastMs { get { lock (_lock) return _lastMs; } }
        public int LossPct { get { lock (_lock) return _lossPct; } }

        public void Start(string host)
        {
            if (_thread != null && host == Host) return;
            Stop();
            Host = string.IsNullOrWhiteSpace(host) ? "1.1.1.1" : host.Trim();
            lock (_lock) { _recent.Clear(); _lastMs = -1; _lossPct = 0; }
            _stop.Reset();
            _thread = new Thread(Loop) { IsBackground = true, Name = "MachineGauges ping" };
            _thread.Start();
        }

        private void Loop()
        {
            using (var ping = new Ping())
            {
                do
                {
                    bool ok = false;
                    long ms = -1;
                    try
                    {
                        PingReply reply = ping.Send(Host, 1000);
                        ok = reply != null && reply.Status == IPStatus.Success;
                        if (ok) ms = reply.RoundtripTime;
                    }
                    catch { }

                    lock (_lock)
                    {
                        _recent.Enqueue(ok);
                        while (_recent.Count > 20) _recent.Dequeue();
                        int lost = 0;
                        foreach (bool r in _recent) if (!r) lost++;
                        _lossPct = lost * 100 / _recent.Count;
                        _lastMs = ok ? (int)ms : -1;
                    }
                } while (!_stop.WaitOne(2000));
            }
        }

        public void Stop()
        {
            if (_thread == null) return;
            _stop.Set();
            _thread.Join(3000);
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
            _stop.Close();
        }
    }

    /// <summary>
    /// CPU temperature from LibreHardwareMonitor (or OpenHardwareMonitor) when it is running.
    /// Windows has no reliable built-in CPU temperature source, and reading the sensors directly
    /// needs a kernel driver, so this reuses the one those tools already load.
    /// </summary>
    internal sealed class CpuTempReader : IDisposable
    {
        private static readonly string[] Namespaces = { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };

        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _thread;
        private int _latest = -1;
        private string _source = "";

        public int LatestC { get { return Volatile.Read(ref _latest); } }
        public string Source { get { return Volatile.Read(ref _source); } }
        public bool Running { get { return _thread != null; } }

        public void Start()
        {
            if (_thread != null) return;
            _stop.Reset();
            _thread = new Thread(Loop) { IsBackground = true, Name = "MachineGauges cpu temp" };
            _thread.Start();
        }

        private void Loop()
        {
            do
            {
                int value = -1;
                string source = "";
                foreach (string ns in Namespaces)
                {
                    value = Read(ns);
                    if (value >= 0)
                    {
                        source = ns.Substring(5);
                        break;
                    }
                }
                Volatile.Write(ref _latest, value);
                Volatile.Write(ref _source, source);
                // Poll every 2 s while a source is live; check back every 30 s otherwise.
            } while (!_stop.WaitOne(_latest >= 0 ? 2000 : 30000));
        }

        private static int Read(string ns)
        {
            try
            {
                var scope = new ManagementScope(ns);
                scope.Connect();
                var query = new ObjectQuery("SELECT Name, Identifier, Value FROM Sensor WHERE SensorType='Temperature'");
                double best = -1, hottestCore = -1;
                using (var searcher = new ManagementObjectSearcher(scope, query))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject o in results)
                    {
                        using (o)
                        {
                            string id = Convert.ToString(o["Identifier"], CultureInfo.InvariantCulture) ?? "";
                            if (id.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string name = Convert.ToString(o["Name"], CultureInfo.InvariantCulture) ?? "";
                            double v = Convert.ToDouble(o["Value"], CultureInfo.InvariantCulture);
                            if (v <= 0 || v > 150) continue;

                            if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                                best = Math.Max(best, v);
                            else
                                hottestCore = Math.Max(hottestCore, v);
                        }
                    }
                }
                double pick = best >= 0 ? best : hottestCore;
                return pick >= 0 ? (int)Math.Round(pick) : -1;
            }
            catch
            {
                return -1;   // namespace absent: the tool isn't running
            }
        }

        public void Stop()
        {
            if (_thread == null) return;
            _stop.Set();
            _thread.Join(3000);
            _thread = null;
            Volatile.Write(ref _latest, -1);
        }

        public void Dispose()
        {
            Stop();
            _stop.Close();
        }
    }
}
