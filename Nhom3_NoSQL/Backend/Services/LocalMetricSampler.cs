using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Nhom3_NoSQL.Backend.Models;

namespace Nhom3_NoSQL.Backend.Services
{
    public sealed class LocalMetricSampler : IDisposable
    {
        private readonly PerformanceCounter _cpuCounter;
        private readonly string _diskDrive;
        private long _lastRx, _lastTx;
        private DateTime _lastNetAt;

        public LocalMetricSampler(string diskDrive)
        {
            _diskDrive = new DriveInfo(diskDrive ?? throw new ArgumentNullException(nameof(diskDrive))).Name;
            GetDiskUsagePercent(_diskDrive);
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        }

        public MetricSnapshot Sample(string serverId)
        {
            var (inKbps, outKbps) = GetNetworkKbps();
            return new MetricSnapshot
            {
                ServerId = serverId,
                CpuUsagePercent = Math.Round(_cpuCounter.NextValue(), 1),
                RamUsagePercent = Math.Round(GetRamUsagePercent(), 1),
                DiskUsagePercent = Math.Round(GetDiskUsagePercent(_diskDrive), 1),
                NetworkInKbps = Math.Round(inKbps, 1),
                NetworkOutKbps = Math.Round(outKbps, 1),
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static double GetRamUsagePercent()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return 0;
            return (double)(m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys * 100;
        }

        public static double GetDiskUsagePercent(string driveName)
        {
            if (string.IsNullOrWhiteSpace(driveName))
                throw new ArgumentException("Cần cấu hình ổ đĩa để thu thập dung lượng.", nameof(driveName));

            var drive = new DriveInfo(driveName);
            if (!drive.IsReady)
                throw new InvalidOperationException($"Ổ đĩa '{drive.Name}' không sẵn sàng.");
            if (drive.TotalSize <= 0)
                throw new InvalidOperationException($"Không đọc được dung lượng ổ đĩa '{drive.Name}'.");
            var used = drive.TotalSize - drive.AvailableFreeSpace;
            return (double)used / drive.TotalSize * 100;
        }

        private (double inKbps, double outKbps) GetNetworkKbps()
        {
            long rx = 0, tx = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var s = nic.GetIPv4Statistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            var now = DateTime.UtcNow;
            double seconds = (now - _lastNetAt).TotalSeconds;
            var result = _lastNetAt == default || seconds <= 0
                ? (0d, 0d)
                : ((rx - _lastRx) * 8 / 1000d / seconds, (tx - _lastTx) * 8 / 1000d / seconds);
            _lastRx = rx;
            _lastTx = tx;
            _lastNetAt = now;
            return result;
        }

        public void Dispose() => _cpuCounter.Dispose();
    }
}
