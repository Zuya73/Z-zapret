using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zapret.Desktop.Runtime;

internal static class WindowsService
{
    public static int State(string name) => Query(name).State;
    public static int ProcessId(string name) => Query(name).ProcessId;

    private static ServiceStatus Query(string name)
    {
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, name, 4);
        if (service.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1060) return default;
            throw new Win32Exception(error);
        }
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }

    public static async Task WaitAsync(string name, int expected, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 100; i++)
        {
            if (State(name) == expected) return;
            await Task.Delay(200, cancellationToken);
        }
        throw new TimeoutException($"Служба {name} не перешла в ожидаемое состояние. Проверьте журнал и конфликты драйвера.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public int Type, State, Accepted, Win32ExitCode, ServiceExitCode, Checkpoint, WaitHint, ProcessId, Flags;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
