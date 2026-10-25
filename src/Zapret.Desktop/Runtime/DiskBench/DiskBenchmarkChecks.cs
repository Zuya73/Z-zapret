using System.Diagnostics;
using System.Windows;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class DiskBenchmarkChecks
{
    internal static IDiskBenchmarkRunner CreateRunner() => new PreviewRunner();
    internal static async Task RunAsync(MainWindow window, string destination)
    {
        var checks = new List<string>();
        window.SelectPage("Tools"); window.ShowDiskTool(); await Task.Delay(450);
        Require(window.DiskToolPanel.IsVisible && !window.PcInfoPanel.IsVisible && !window.ShutdownToolPanel.IsVisible, "Disk speed is a separate X2 Tools tool");
        Require(window.DiskResultGrid.Items.Count == 4 && window.DiskDrivePicker.Items.Count > 0, "Local drives and all four benchmark profiles are available");
        Task run = window.RunDiskBenchmarkAsync();
        await Task.Delay(40);
        Require(window.DiskCancelButton.IsEnabled && !window.DiskStartButton.IsEnabled && !window.DiskDrivePicker.IsEnabled && !window.PcToolButton.IsEnabled,
            "Concurrent benchmark, drive changes and hardware polling are blocked during a run");
        await run;
        Require(window.DiskResultGrid.Items.Cast<MainWindow.DiskBenchmarkRow>().All(r => r.ReadSpeed != "—" && r.WriteSpeed != "—") && window.DiskExportButton.IsEnabled,
            "All read/write results render and completed reports can be saved");
        Require(window.PcToolButton.IsEnabled && window.DiskStartButton.IsEnabled && !window.DiskCancelButton.IsEnabled, "Controls recover after the run");
        var buttonBottom = window.DiskCancelButton.PointToScreen(new(0, window.DiskCancelButton.ActualHeight));
        var viewportBottom = window.PageScroll.PointToScreen(new(0, window.PageScroll.ActualHeight));
        Require(buttonBottom.Y <= viewportBottom.Y, "Start/cancel controls fit the initial viewport");
        window.DiskStageText.Text = "Пример интерфейса · демонстрационные показатели";
        InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "disk-benchmark-preview.png"));
        window.ShowShutdownTool(); Require(window.ShutdownToolPanel.IsVisible, "Shutdown timer remains reachable");
        window.ShowPcTool(); Require(window.PcInfoPanel.IsVisible, "PC information remains reachable");
        AtomicFile.WriteJson(destination, new { passed = checks.Count, checks });
        void Require(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
    }
    internal static async Task RunNativeAsync(string destination)
    {
        var checks = new List<string>();
        string root = DiskBenchmarkPolicy.Root(Path.GetPathRoot(Environment.SystemDirectory)!);
        string sentinel = Path.Combine(root, "x2zapret-benchmark-sentinel-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(sentinel, "unrelated user data");
        try
        {
            var runner = new DiskBenchmarkRunner();
            var options = new DiskBenchmarkOptions(256 * DiskBenchmarkOptions.MiB, 1, 1);
            var report = await runner.RunAsync(root, options, new InlineProgress(_ => { }), CancellationToken.None);
            Require(report.Outcome == "Completed", "Native DiskSpd succeeds: " + report.Error);
            Require(report.Measurements.Count == 8 && report.Measurements.All(x => x.Bytes > 0 && x.Iops > 0 && x.AverageLatencyMs >= 0), "All eight native unbuffered read/write cases return valid metrics");
            Require(runner.LastWorkspace != null && !Directory.Exists(runner.LastWorkspace) && report.CleanupNote.Length == 0, "Successful native run removes its test file and directory");
            Require(await File.ReadAllTextAsync(sentinel) == "unrelated user data", "Files outside the owned test directory are unchanged");
            using var cancelled = new CancellationTokenSource();
            var cancelRun = runner.RunAsync(root, options with { Seconds = 5 }, new InlineProgress(_ => { }), cancelled.Token);
            for (int i = 0; i < 500 && runner.ActiveProcessId == null && !cancelRun.IsCompleted; i++) await Task.Delay(20);
            int? pid = runner.ActiveProcessId;
            Require(pid.HasValue, "Cancellation test reached a running native child process");
            cancelled.Cancel(); var stopped = await cancelRun;
            Require(stopped.Outcome == "Cancelled" && !Directory.Exists(runner.LastWorkspace) && stopped.CleanupNote.Length == 0, "Cancellation removes the test file after stopping I/O");
            Require(!Exists(pid!.Value), "Cancellation terminates only the owned DiskSpd child");
            using var afterFirst = new CancellationTokenSource();
            var partial = await runner.RunAsync(root, options, new InlineProgress(p => { if (p.Measurement != null) afterFirst.Cancel(); }), afterFirst.Token);
            Require(partial.Outcome == "Cancelled" && partial.Measurements.Count == 1, "Cancellation between stages retains completed measurements");
            var area = new BenchmarkWorkspace(root);
            await area.PrepareAsync(256 * DiskBenchmarkOptions.MiB, new InlineProgress(_ => { }), 8, CancellationToken.None);
            string foreign = Path.Combine(area.DirectoryPath, "keep.txt");
            await File.WriteAllTextAsync(foreign, "keep this file"); area.Dispose();
            Require(File.Exists(foreign) && await File.ReadAllTextAsync(foreign) == "keep this file" && area.CleanupNote.Length > 0,
                "Cleanup never recursively removes a foreign file added to its directory");
            File.Delete(foreign); Directory.Delete(area.DirectoryPath);
            AtomicFile.WriteJson(destination, new { passed = checks.Count, checks, measured = report.Measurements.Count });
        }
        finally { File.Delete(sentinel); }
        void Require(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
    }
    private static bool Exists(int pid)
    { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    private sealed class InlineProgress(Action<DiskBenchmarkProgress> callback) : IProgress<DiskBenchmarkProgress>
    { public void Report(DiskBenchmarkProgress value) => callback(value); }
    private sealed class PreviewRunner : IDiskBenchmarkRunner
    {
        public async Task<DiskBenchmarkReport> RunAsync(string root, DiskBenchmarkOptions options, IProgress<DiskBenchmarkProgress> progress, CancellationToken token)
        {
            var start = DateTimeOffset.UtcNow; var data = new List<DiskMeasurement>();
            foreach (var profile in DiskBenchmarkCase.All)
                foreach (bool write in new[] { false, true })
                {
                    await Task.Delay(35, token);
                    ulong count = profile.Random ? (write ? 260000UL : 320000UL) : (write ? 7500UL : 9600UL);
                    var value = new DiskMeasurement(profile.Id, write, 1, count * (ulong)profile.BlockBytes, count, 3,
                        profile.Random ? .032 : .27, profile.Random ? .08 : .5);
                    data.Add(value); progress.Report(new(profile.Label, data.Count, 8, data.Count * 12, value));
                }
            return new(start, DateTimeOffset.UtcNow, root, options, "Completed", data, "", "");
        }
    }
}
