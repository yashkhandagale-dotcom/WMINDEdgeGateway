using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WMINDEdgeGateway.Infrastructure.Diagnostics
{
    public sealed class ResourceMonitorService : BackgroundService
    {
        private readonly ILogger<ResourceMonitorService> _log;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

        // Network baseline
        private long _prevRxBytes;
        private long _prevTxBytes;
        private DateTime _prevNetTime = DateTime.UtcNow;
        private bool _netWarmedUp = false;



        public ResourceMonitorService(ILogger<ResourceMonitorService> log)
        {
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("ResourceMonitor started.");
            Console.WriteLine($"ResourceMonitor: Platform = {RuntimeInformation.OSDescription}");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var snap = new ResourceSnapshot
                    {
                        CapturedUtc   = DateTime.UtcNow,
                        ProcessAlive  = true,
                        CpuPercent    = await ReadCpuAsync(),
                        RamPercent    = ReadRam(),
                        DiskPercent   = ReadDisk(),
                    };

                    (snap.NetInKbps, snap.NetOutKbps) = ReadNetwork();

                    GatewayDiagnosticsState.Instance.Resources    = snap;
                    GatewayDiagnosticsState.Instance.LastSeenUtc  = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "ResourceMonitor poll failed.");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        // ── CPU ───────────────────────────────────────────────────────────────
        private async Task<double> ReadCpuAsync()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return await ReadCpuLinuxAsync();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return await ReadCpuWindowsAsync();

            return 0;
        }

        // Linux: /proc/stat diff over 200ms
        private static async Task<double> ReadCpuLinuxAsync()
        {
            static (long idle, long total) ParseStat()
            {
                var line  = File.ReadLines("/proc/stat").First(l => l.StartsWith("cpu "));
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                long user    = long.Parse(parts[1]);
                long nice    = long.Parse(parts[2]);
                long system  = long.Parse(parts[3]);
                long idle    = long.Parse(parts[4]);
                long iowait  = long.Parse(parts[5]);
                long irq     = long.Parse(parts[6]);
                long softirq = long.Parse(parts[7]);
                long total   = user + nice + system + idle + iowait + irq + softirq;

                return (idle + iowait, total);
            }

            var (idle1, total1) = ParseStat();
            await Task.Delay(200);
            var (idle2, total2) = ParseStat();

            long dTotal = total2 - total1;
            long dIdle  = idle2  - idle1;

            if (dTotal == 0) return 0;
            return Math.Round((1.0 - (double)dIdle / dTotal) * 100.0, 1);
        }

        // Windows: GetSystemTimes — system-wide CPU, no PerformanceCounter, no NuGet
        private static async Task<double> ReadCpuWindowsAsync()
{
    try
    {
        GetSystemTimes(out var idle1, out var kernel1, out var user1);
        await Task.Delay(500);
        GetSystemTimes(out var idle2, out var kernel2, out var user2);

        long idle   = idle2.ToLong() - idle1.ToLong();
        long kernel = kernel2.ToLong() - kernel1.ToLong();  // includes idle
        long user   = user2.ToLong() - user1.ToLong();
        long total  = kernel + user;   // kernel + user = total (kernel has idle in it)

        if (total <= 0) return 0;

        // CORRECT: busy = total - idle  (not 1 - idle/total of kernel+user)
        long busy = total - idle;
        double cpu = (double)busy / total * 100.0;
        return Math.Round(Math.Max(0, Math.Min(100, cpu)), 1);
    }
    catch { return 0; }
}

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(
            out FILETIME lpIdleTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
            public long ToLong() => ((long)dwHighDateTime << 32) | dwLowDateTime;
        }

        // ── RAM ───────────────────────────────────────────────────────────────
        private static double ReadRam()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ReadRamLinux();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ReadRamWindows();

            return 0;
        }

        private static double ReadRamLinux()
        {
            try
            {
                var lines = File.ReadAllLines("/proc/meminfo");

                long GetKb(string key) =>
                    long.Parse(
                        lines.First(l => l.StartsWith(key))
                             .Split(':')[1].Trim()
                             .Split(' ')[0]);

                long total     = GetKb("MemTotal:");
                long available = GetKb("MemAvailable:");

                if (total == 0) return 0;
                return Math.Round((1.0 - (double)available / total) * 100.0, 1);
            }
            catch { return 0; }
        }

        private static double ReadRamWindows()
        {
            try
            {
                // TotalAvailableMemoryBytes works on .NET 5+ (all versions)
                var  info  = GC.GetGCMemoryInfo();
                long total = info.TotalAvailableMemoryBytes;

                if (total <= 0) return 0;

                // System-wide committed memory via Environment.WorkingSet is
                // process-only, so use GlobalMemoryStatusEx via P/Invoke-free
                // alternative: MemoryLoad from MEMORYSTATUSEX via kernel32
                // Simplest cross-version approach: use PerformanceInfo (no NuGet)
                var memStatus = new MemoryStatusEx();
                memStatus.dwLength = (uint)System.Runtime.InteropServices
                    .Marshal.SizeOf(typeof(MemoryStatusEx));

                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    double used = total - (long)memStatus.ullAvailPhys;
                    return Math.Round(used / total * 100.0, 1);
                }

                // Fallback: process working set / total RAM
                var proc = Process.GetCurrentProcess();
                proc.Refresh();
                return Math.Round((double)proc.WorkingSet64 / total * 100.0, 1);
            }
            catch { return 0; }
        }

        // kernel32 P/Invoke — no NuGet needed, built into Windows
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Auto,
            SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(
            ref MemoryStatusEx lpBuffer);

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, CharSet =
            System.Runtime.InteropServices.CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint   dwLength;
            public uint   dwMemoryLoad;
            public ulong  ullTotalPhys;
            public ulong  ullAvailPhys;
            public ulong  ullTotalPageFile;
            public ulong  ullAvailPageFile;
            public ulong  ullTotalVirtual;
            public ulong  ullAvailVirtual;
            public ulong  ullAvailExtendedVirtual;
        }

        // ── Disk ──────────────────────────────────────────────────────────────
        private static double ReadDisk()
        {
            try
            {
                var drive = new DriveInfo(
                    Path.GetPathRoot(AppContext.BaseDirectory) ?? "/");

                double used = (double)(drive.TotalSize - drive.AvailableFreeSpace);
                return Math.Round(used / drive.TotalSize * 100.0, 1);
            }
            catch { return 0; }
        }

        // ── Network ───────────────────────────────────────────────────────────
        private (double inKbps, double outKbps) ReadNetwork()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ReadNetworkLinux();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ReadNetworkWindows();

            return (0, 0);
        }

        private (double inKbps, double outKbps) ReadNetworkLinux()
        {
            try
            {
                long rxTotal = 0, txTotal = 0;

                foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("lo:")) continue;

                    var cols = trimmed.Split(
                        new[] { ' ', ':' },
                        StringSplitOptions.RemoveEmptyEntries);

                    if (cols.Length < 10) continue;

                    rxTotal += long.Parse(cols[1]);
                    txTotal += long.Parse(cols[9]);
                }

                var    now     = DateTime.UtcNow;
                double secs    = (now - _prevNetTime).TotalSeconds;

                if (!_netWarmedUp)
                {
                    _prevRxBytes = rxTotal;
                    _prevTxBytes = txTotal;
                    _prevNetTime = now;
                    _netWarmedUp = true;
                    return (0, 0);
                }

                double inKbps  = secs > 0 ? Math.Round((rxTotal - _prevRxBytes) / 1024.0 / secs, 1) : 0;
                double outKbps = secs > 0 ? Math.Round((txTotal - _prevTxBytes) / 1024.0 / secs, 1) : 0;

                _prevRxBytes  = rxTotal;
                _prevTxBytes  = txTotal;
                _prevNetTime  = now;

                return (Math.Max(0, inKbps), Math.Max(0, outKbps));
            }
            catch { return (0, 0); }
        }

        private (double inKbps, double outKbps) ReadNetworkWindows()
        {
            try
            {
                // Use System.Net.NetworkInformation — no extra NuGet needed
                long rxTotal = 0, txTotal = 0;

                foreach (var nic in System.Net.NetworkInformation
                    .NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType ==
                        System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;

                    if (nic.OperationalStatus !=
                        System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;

                    var stats = nic.GetIPStatistics();
                    rxTotal  += stats.BytesReceived;
                    txTotal  += stats.BytesSent;
                }

                var    now     = DateTime.UtcNow;
                double secs    = (now - _prevNetTime).TotalSeconds;

                if (!_netWarmedUp)
                {
                    _prevRxBytes = rxTotal;
                    _prevTxBytes = txTotal;
                    _prevNetTime = now;
                    _netWarmedUp = true;
                    return (0, 0);
                }

                double inKbps  = secs > 0 ? Math.Round((rxTotal - _prevRxBytes) / 1024.0 / secs, 1) : 0;
                double outKbps = secs > 0 ? Math.Round((txTotal - _prevTxBytes) / 1024.0 / secs, 1) : 0;

                _prevRxBytes = rxTotal;
                _prevTxBytes = txTotal;
                _prevNetTime = now;

                return (Math.Max(0, inKbps), Math.Max(0, outKbps));
            }
            catch { return (0, 0); }
        }
    }
}