using System.Diagnostics;
using Microsoft.Win32;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

public sealed class EngineController(AppPaths paths, IReadOnlyList<Strategy> strategies, Action<string> log) : IEngineController, IDisposable
{
    private const string ServiceName = "zapret";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\zapret";
    private Process? process;
    private ChildProcessJob? job;
    private string? currentStrategy;
    private readonly SemaphoreSlim operations = new(1, 1);
    public bool TestProcessRunning => process is { HasExited: false };
    public bool ServiceInstalled => WindowsService.State(ServiceName) != 0;
    public bool OwnsService
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            string? image = key?.GetValue("ImagePath") as string;
            return key?.GetValue("ZZapretOwner") as string == AppPaths.Owner && image != null &&
                image.StartsWith(StrategyCatalog.QuoteWindowsArgument(Path.Combine(paths.Bin, "winws.exe")) + " ", StringComparison.OrdinalIgnoreCase);
        }
    }
    public bool IsRunning => process is { HasExited: false } || OwnsService && WindowsService.State(ServiceName) == 4;

    public Task<EngineSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckForeign();
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        bool service = OwnsService && WindowsService.State(ServiceName) == 4;
        return Task.FromResult(new EngineSnapshot(service ? key?.GetValue("ZZapretStrategy") as string : currentStrategy,
            IsRunning, service));
    }

    public async Task StartAsync(Strategy strategy, AppSettings settings, CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken);
        try
        {
            CheckForeign();
            if (IsRunning) throw new InvalidOperationException("Сначала отключите обход, затем включите выбранный профиль.");
            string lists = await PrepareAsync(settings, cancellationToken);
            if (OwnsService)
            {
                await ConfigureService(strategy, lists, settings, cancellationToken);
                await ScAsync(["start", ServiceName], cancellationToken);
                await WindowsService.WaitAsync(ServiceName, 4, cancellationToken);
            }
            else await StartProcess(strategy, lists, settings, cancellationToken);
            currentStrategy = strategy.Id;
            log($"Обход включён с профилем: {strategy.Name}.");
        }
        finally { operations.Release(); }
    }

    public async Task StartTestAsync(Strategy strategy, string listsDirectory, AppSettings settings, CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken);
        try
        {
            CheckForeign();
            await Task.Run(() => paths.ApplyFakes(settings), cancellationToken);
            await StartProcess(strategy, listsDirectory, settings, cancellationToken);
            currentStrategy = strategy.Id;
        }
        finally { operations.Release(); }
    }

    private async Task StartProcess(Strategy strategy, string lists, AppSettings settings, CancellationToken cancellationToken)
    {
        if (process is { HasExited: false }) throw new InvalidOperationException("Обход уже включён. Чтобы сменить профиль, сначала отключите его.");
        var info = new ProcessStartInfo(Path.Combine(paths.Bin, "winws.exe"))
        {
            WorkingDirectory = paths.Bin, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in StrategyCatalog.Expand(strategy, paths.Bin, lists, settings)) info.ArgumentList.Add(arg);
        job?.Dispose(); job = null;
        process?.Dispose();
        process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
        process.Start();
        try
        {
            job = new ChildProcessJob(process);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await Task.Delay(1200, cancellationToken);
            if (process.HasExited) throw new InvalidOperationException($"Модуль обхода неожиданно завершился. Код ошибки: {process.ExitCode}. Подробности в журнале.");
        }
        catch
        {
            job?.Dispose(); job = null;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken);
        try
        {
            if (process is { HasExited: false })
            {
                // The engine may exit on its own between the check and the kill.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(cancellationToken);
            }
            job?.Dispose(); job = null;
            process?.Dispose(); process = null;
            if (OwnsService && WindowsService.State(ServiceName) is not (0 or 1))
            {
                using var serviceProcess = TryGetServiceProcess();
                if (WindowsService.State(ServiceName) != 3) await ScAsync(["stop", ServiceName], cancellationToken);
                await WindowsService.WaitAsync(ServiceName, 1, cancellationToken);
                if (serviceProcess != null) await serviceProcess.WaitForExitAsync(cancellationToken);
            }
        }
        finally { operations.Release(); }
    }

    public async Task RestoreAsync(EngineSnapshot snapshot, AppSettings settings, CancellationToken cancellationToken)
    {
        if (!snapshot.WasRunning) return;
        var strategy = strategies.SingleOrDefault(x => x.Id == snapshot.StrategyId)
            ?? throw new InvalidOperationException("Не найден прежний профиль подключения. Сведения для восстановления сохранены; перезапустите программу и откройте «Диагностику».");
        // The service configuration is never changed during a test. Restore the same launch mode.
        if (snapshot.ServiceMode)
        {
            if (!OwnsService) throw new InvalidOperationException("Настройки фонового подключения изменились во время проверки. Автоматическое восстановление остановлено; проверьте «Диагностику».");
            await ConfigureService(strategy, await PrepareAsync(settings, cancellationToken), settings, cancellationToken);
            await ScAsync(["start", ServiceName], cancellationToken);
            await WindowsService.WaitAsync(ServiceName, 4, cancellationToken);
            currentStrategy = strategy.Id;
        }
        else
        {
            await StartProcess(strategy, await PrepareAsync(settings, cancellationToken), settings, cancellationToken);
            currentStrategy = strategy.Id;
        }
        log("Предыдущее подключение восстановлено.");
    }

    public async Task InstallServiceAsync(Strategy strategy, AppSettings settings, CancellationToken cancellationToken)
    {
        CheckForeign();
        await StopAsync(cancellationToken);
        string lists = await PrepareAsync(settings, cancellationToken);
        if (!ServiceInstalled)
        {
            string command = ServiceCommand(strategy, lists, settings);
            await ScAsync(["create", ServiceName, "binPath=", command, "DisplayName=", "x2zapret", "start=", "auto"], cancellationToken);
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true)!;
            key.SetValue("ZZapretOwner", AppPaths.Owner);
        }
        await ConfigureService(strategy, lists, settings, cancellationToken);
        await ScAsync(["description", ServiceName, "x2zapret desktop - network engine"], cancellationToken);
        await ScAsync(["start", ServiceName], cancellationToken);
        await WindowsService.WaitAsync(ServiceName, 4, cancellationToken);
        currentStrategy = strategy.Id;
        log("Фоновое подключение включено. Обход работает как служба Windows и может работать без открытого окна.");
    }

    public async Task RemoveServiceAsync(CancellationToken cancellationToken)
    {
        if (!OwnsService) throw new InvalidOperationException("Эта служба не принадлежит x2zapret, поэтому программа не может её отключить. Используйте программу, которая её установила.");
        await StopAsync(cancellationToken);
        await ScAsync(["delete", ServiceName], cancellationToken);
        await WindowsService.WaitAsync(ServiceName, 0, cancellationToken);
        log("Фоновое подключение отключено. Общий сетевой драйвер WinDivert оставлен: его могут использовать другие программы.");
    }

    private async Task ConfigureService(Strategy strategy, string lists, AppSettings settings, CancellationToken cancellationToken)
    {
        await ScAsync(["config", ServiceName, "binPath=", ServiceCommand(strategy, lists, settings), "start=", "auto"], cancellationToken);
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true)!;
        key.SetValue("ZZapretStrategy", strategy.Id);
        key.SetValue("zapret-discord-youtube", strategy.Source);
    }

    // Copying fakes and lists touches several megabytes; keep it off the UI thread so the window stays responsive.
    private Task<string> PrepareAsync(AppSettings settings, CancellationToken cancellationToken) =>
        Task.Run(() => { paths.ApplyFakes(settings); return paths.PrepareLists(settings); }, cancellationToken);

    private string ServiceCommand(Strategy strategy, string lists, AppSettings settings) =>
        StrategyCatalog.QuoteWindowsArgument(Path.Combine(paths.Bin, "winws.exe")) + " " +
        string.Join(' ', StrategyCatalog.Expand(strategy, paths.Bin, lists, settings).Select(StrategyCatalog.QuoteWindowsArgument));

    private void CheckForeign()
    {
        if (ServiceInstalled && !OwnsService)
            throw new InvalidOperationException("На компьютере уже установлена служба другой версии zapret. Откройте прежнюю программу и отключите в ней фоновое подключение. Если использовали BAT-файлы, воспользуйтесь её меню service.bat. Затем повторите запуск x2zapret.");
        foreach (var other in Process.GetProcessesByName("winws"))
        {
            using (other)
                if (process == null || other.Id != process.Id)
                {
                    if (OwnsService && other.Id == WindowsService.ProcessId(ServiceName)) continue;
                    throw new InvalidOperationException("Другая программа уже запустила модуль обхода winws.exe. Закройте её или отключите в ней обход, затем повторите запуск.");
                }
        }
    }

    private static Process? TryGetServiceProcess()
    {
        int id = WindowsService.ProcessId(ServiceName);
        if (id == 0) return null;
        try
        {
            var value = Process.GetProcessById(id);
            _ = value.SafeHandle; // Capture this process before the service stops; never follow a reused PID.
            return value;
        }
        catch (ArgumentException) { return null; }
    }

    private static async Task ScAsync(string[] args, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("sc.exe"), args, TimeSpan.FromSeconds(25), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Ошибка службы ({result.ExitCode}): {result.Output} {result.Error}");
    }

    public void Dispose()
    {
        job?.Dispose();
        process?.Dispose();
        operations.Dispose();
    }
}
