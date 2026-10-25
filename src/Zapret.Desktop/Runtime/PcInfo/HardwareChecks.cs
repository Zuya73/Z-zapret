using System.Diagnostics;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class HardwareChecks
{
    internal static async Task RunAsync(string destination)
    {
        var checks = new List<string>();
        using var client = new NativeHardwareClient();
        Require(!client.Running, "Opening the application does not start the hardware worker");
        var frame = await client.SampleAsync(CancellationToken.None);
        int pid = client.ProcessId ?? throw new InvalidOperationException("Worker PID missing");
        Require(pid != Environment.ProcessId && client.Running, "Native sensor access runs in a separate owned process");
        Require(frame.Readings.Count > 0 || frame.Warnings.Count > 0, "Real hardware returns readings or an availability explanation");
        await client.SampleAsync(CancellationToken.None);
        Require(client.ProcessId == pid, "Polling reuses one hardware session");
        client.Stop();
        await Task.Delay(150);
        Require(!client.Running && !Exists(pid), "Closing monitoring terminates its process and releases device handles");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await client.SampleAsync(cancelled.Token); throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
        }
        Require(!client.Running, "An already-cancelled request never starts a worker");
        using var stalled = new NativeHardwareClient("--hardware-worker-test", TimeSpan.FromSeconds(2));
        try { await stalled.SampleAsync(CancellationToken.None); throw new InvalidOperationException("Expected timeout"); }
        catch (TimeoutException) { }
        Require(!stalled.Running, "A stalled reader times out and is terminated");
        var restarted = await client.SampleAsync(CancellationToken.None);
        Require(client.Running && restarted.CapturedAt >= frame.CapturedAt, "Monitoring can restart after leaving its page");
        try { PawnDriver.Verify(new byte[] { 77, 90, 0 }); throw new InvalidOperationException("Accepted invalid installer"); }
        catch (InvalidDataException) { }
        Require(true, "Driver installation rejects wrong bytes before execution");
        Require(!PawnDriver.CanInstallOver("2.3.0") && PawnDriver.CanInstallOver("2.2.0.0"), "Installing the pinned driver cannot downgrade a newer version");
        AtomicFile.WriteJson(destination, new { passed = checks.Count, checks, readings = frame.Readings.Count,
            smartRows = frame.Smart.Count, spdRows = frame.Spd.Count, driverReady = frame.DriverReady });
        void Require(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
    }
    private static bool Exists(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
