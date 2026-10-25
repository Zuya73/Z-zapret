using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Zapret.Desktop.Runtime;

internal sealed record PcLiveSample(double? CpuPercent, double MemoryPercent, ulong UsedMemory, ulong TotalMemory, double ReceiveBytesPerSecond, double SendBytesPerSecond);

internal sealed class PcLiveMonitor
{
    private ulong previousIdle, previousKernel, previousUser;
    private long previousTime;
    private Dictionary<string, (long Received, long Sent)> previousNetwork = new();

    internal PcLiveSample Read()
    {
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong i = idle.Value, k = kernel.Value, u = user.Value;
            if (previousTime != 0 && k >= previousKernel && u >= previousUser && i >= previousIdle)
            {
                double total = (k - previousKernel) + (double)(u - previousUser);
                if (total > 0) cpu = Math.Clamp(100 * (1 - (i - previousIdle) / total), 0, 100);
            }
            previousIdle = i; previousKernel = k; previousUser = u;
        }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new InvalidOperationException("Windows не предоставила текущую загрузку памяти.");
        long now = Stopwatch.GetTimestamp();
        double seconds = previousTime == 0 ? 0 : (now - previousTime) / (double)Stopwatch.Frequency;
        double received = 0, sent = 0;
        var network = new Dictionary<string, (long Received, long Sent)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = nic.GetIPStatistics();
                network[nic.Id] = (stats.BytesReceived, stats.BytesSent);
                if (seconds > 0 && previousNetwork.TryGetValue(nic.Id, out var old))
                { received += Math.Max(0, stats.BytesReceived - old.Received) / seconds; sent += Math.Max(0, stats.BytesSent - old.Sent) / seconds; }
            }
            catch (NetworkInformationException) { }
        }
        previousNetwork = network; previousTime = now;
        return new(cpu, memory.Load, memory.TotalPhysical - memory.AvailablePhysical, memory.TotalPhysical, received, sent);
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileTime
    { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
