using System.Diagnostics;
using System.Runtime.InteropServices;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal sealed class DiskBenchmarkRunner : IDiskBenchmarkRunner
{
    internal string? LastWorkspace { get; private set; }
    internal int? ActiveProcessId { get; private set; }
    public async Task<DiskBenchmarkReport> RunAsync(string root, DiskBenchmarkOptions options, IProgress<DiskBenchmarkProgress> progress, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow; var results = new List<DiskMeasurement>();
        string outcome = "Completed", error = "", cleanup = "";
        BenchmarkWorkspace? workspace = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(6));
        int total = DiskBenchmarkCase.All.Count * 2 * Math.Clamp(options.Repeats, 1, 3);
        try
        {
            options.Validate(); root = DiskBenchmarkPolicy.Root(root);
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType is not DriveType.Fixed and not DriveType.Removable)
                throw new IOException("Нужен доступный локальный или съёмный накопитель.");
            if (drive.AvailableFreeSpace < options.FileBytes + DiskBenchmarkPolicy.ReserveBytes)
                throw new IOException("Недостаточно места: нужен тестовый файл и ещё 1 ГиБ свободного резерва.");
            if (drive.DriveFormat == "FAT32" && options.FileBytes >= 4L * 1024 * 1024 * 1024)
                throw new IOException("FAT32 не поддерживает файл 4 ГиБ. Выберите меньший размер.");
            if (!GetDiskFreeSpace(root, out _, out uint sector, out _, out _) || sector == 0 || 4096 % sector != 0)
                throw new IOException("Размер сектора этого тома не поддерживает выбранные проверки 4 КиБ.");
            deadline.Token.ThrowIfCancellationRequested();
            progress.Report(new("Подготовка движка DiskSpd 2.3…", 0, total, 0));
            using var binary = DiskSpdBinary.OpenVerified();
            workspace = new(root); LastWorkspace = workspace.DirectoryPath;
            await workspace.PrepareAsync(options.FileBytes, progress, total, deadline.Token);
            for (int pass = 1; pass <= options.Repeats; pass++)
                foreach (var profile in DiskBenchmarkCase.All)
                    foreach (bool write in new[] { false, true })
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (drive.AvailableFreeSpace < 128L * 1024 * 1024) throw new IOException("На томе осталось слишком мало свободного места.");
                        string stage = $"{results.Count + 1}/{total} · {profile.Label} · {(write ? "Запись" : "Чтение")} · проход {pass}/{options.Repeats}";
                        progress.Report(new(stage, results.Count, total, 15 + 85 * results.Count / total));
                        string xml = await RunCaseAsync(binary.Name, DiskBenchmarkPolicy.Arguments(workspace.FilePath, profile, write, options.Seconds), options.Seconds, deadline.Token);
                        var item = DiskBenchmarkPolicy.Parse(xml, profile, write, pass, workspace.FilePath);
                        results.Add(item);
                        progress.Report(new(stage, results.Count, total, Math.Min(98, 15 + 85 * results.Count / total), item));
                    }
        }
        catch (OperationCanceledException)
        {
            outcome = token.IsCancellationRequested ? "Cancelled" : "Failed";
            error = token.IsCancellationRequested ? "" : "Превышено общее время теста. Измерения остановлены.";
        }
        catch (Exception caught) { outcome = "Failed"; error = caught.Message; }
        finally
        {
            progress.Report(new("Очистка временного файла…", results.Count, total, Math.Min(99, 15 + 85 * results.Count / total)));
            workspace?.Dispose(); cleanup = workspace?.CleanupNote ?? "";
        }
        return new(started, DateTimeOffset.UtcNow, root, options, outcome, results.ToArray(), error, cleanup);
    }
    private async Task<string> RunCaseAsync(string executable, IReadOnlyList<string> arguments, int seconds, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить DiskSpd.");
        ActiveProcessId = process.Id;
        ChildProcessJob? job = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 20));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            job = new ChildProcessJob(process);
            await process.WaitForExitAsync(deadline.Token);
            string xml = await output, detail = await errors;
            if (process.ExitCode != 0) throw new IOException("DiskSpd: " + (detail.Length > 0 ? detail.Trim() : "ошибка " + process.ExitCode));
            return xml;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(stop.Token); }
            catch (OperationCanceledException) { throw new IOException("Windows не завершила процесс измерения. Проверьте временную папку, указанную в отчёте."); }
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("Накопитель не завершил измерение вовремя.");
            throw;
        }
        finally { job?.Dispose(); ActiveProcessId = null; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint clusters);
}
