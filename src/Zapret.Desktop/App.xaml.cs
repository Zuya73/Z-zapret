using System.Windows;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class App : Application
{
    private Mutex? singleInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--hardware-worker-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await Task.Delay(Timeout.Infinite); return;
        }
        if (e.Args.Contains("--hardware-worker"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await Task.Run(HardwareWorker.Run); Shutdown(0); }
            catch (Exception) { Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--disk-native-check"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await DiskBenchmarkChecks.RunNativeAsync(e.Args.SkipWhile(x => x != "--disk-native-check").Skip(1).First()); Shutdown(0); }
            catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); }
            return;
        }
        base.OnStartup(e);
        if (e.Args.Contains("--uninstall-service") || e.Args.Contains("--stop-service-for-update") || e.Args.Contains("--refresh-startup"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var paths = new AppPaths();
                var strategies = StrategyCatalog.Load(Path.Combine(paths.Catalogs, "strategies.json"));
                using var engine = new EngineController(paths, strategies, _ => { });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                if (e.Args.Contains("--refresh-startup"))
                {
                    var settings = AtomicFile.ReadJson<AppSettings>(paths.Settings) ?? new();
                    await new StartupTaskManager().ApplyAsync(settings.Startup, timeout.Token);
                    Shutdown(0); return;
                }
                if (engine.OwnsService)
                {
                    if (e.Args.Contains("--uninstall-service")) await engine.RemoveServiceAsync(timeout.Token);
                    else await engine.StopAsync(timeout.Token);
                }
                if (e.Args.Contains("--uninstall-service"))
                {
                    var shutdownTimer = ShutdownTimerManager.Create();
                    try { await shutdownTimer.CancelAsync(timeout.Token); }
                    catch (InvalidOperationException) { /* Leave a foreign or manually modified task untouched. */ }
                    new Maintenance(paths).ModifyHosts(null);
                    await new StartupTaskManager().ApplyAsync(StartupMode.Disabled, timeout.Token);
                }
                Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "Z-zapret-maintenance-error.txt"), error.ToString());
                Shutdown(1);
            }
            return;
        }
        bool startupCheck = e.Args.Contains("--startup-check");
        bool boundsCheck = e.Args.Contains("--window-bounds-check");
        bool motionCheck = e.Args.Contains("--motion-check");
        bool toolsCheck = e.Args.Contains("--tools-check");
        bool pcCheck = e.Args.Contains("--pc-check");
        bool hardwareCheck = e.Args.Contains("--hardware-check");
        bool diskCheck = e.Args.Contains("--disk-check");
        bool extendedCheck = e.Args.Contains("--extended-tools-check");
        bool smoke = e.Args.Contains("--smoke-test") || startupCheck || boundsCheck || motionCheck || toolsCheck || pcCheck || hardwareCheck || diskCheck || extendedCheck;
        bool startupInvocation = e.Args.Contains(StartupPolicy.Arguments);
        if (startupInvocation)
        {
            try
            {
                var settings = AtomicFile.ReadJson<AppSettings>(new AppPaths().Settings) ?? new();
                if (!StartupPolicy.StartInTray(true, settings.Startup)) { Shutdown(); return; }
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "Z-zapret-startup-error.txt"), error.ToString());
                Shutdown(1); return;
            }
        }
        if (!smoke)
        {
            singleInstance = new Mutex(true, @"Global\Z-zapret.Desktop", out bool created);
            if (!created)
            {
                if (!startupInvocation) MessageBox.Show("x2zapret уже запущен. Откройте его через значок в области уведомлений.", "x2zapret");
                Shutdown(); return;
            }
        }
        DispatcherUnhandledException += (_, error) =>
        {
            if (smoke)
            {
                File.WriteAllText("smoke-error.txt", error.Exception.ToString());
                error.Handled = true; Shutdown(1); return;
            }
            MessageBox.Show(UserMessages.Error(error.Exception), "Не удалось выполнить действие", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        // Apply the saved theme before the first frame so the window never flashes in the wrong palette.
        string? theme = null;
        try { theme = AtomicFile.ReadJson<AppSettings>(new AppPaths().Settings)?.Theme; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        ThemeManager.Apply(theme); ThemeManager.Follow();
        var window = new MainWindow(preview: smoke && !startupCheck, startupLaunch: startupInvocation, shutdownTimer: toolsCheck ? ToolsChecks.CreateController() : null, pcInformation: pcCheck ? PcInformationChecks.CreateSource() : null, diskBenchmark: diskCheck ? DiskBenchmarkChecks.CreateRunner() : null);
        MainWindow = window;
        if (startupInvocation)
        {
            await window.InitializeAsync();
            await window.ConnectOnStartupAsync();
            if (!window.StartupSucceeded) window.NotifyStartupFailure();
            if (e.Args.Contains("--tray-check"))
            {
                string receipt = e.Args.SkipWhile(x => x != "--tray-check").Skip(1).First();
                AtomicFile.WriteJson(receipt, new { ready = window.StartupSucceeded, hidden = !window.IsVisible, taskbar = window.ShowInTaskbar });
                window.ExitForSmokeTest();
            }
            return;
        }
        window.Show();
        if (smoke)
        {
            if (extendedCheck)
            {
                try { await ExtendedToolsChecks.RunAsync(window, e.Args.SkipWhile(x => x != "--extended-tools-check").Skip(1).First()); }
                catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); return; }
            }
            if (diskCheck)
            {
                try { await DiskBenchmarkChecks.RunAsync(window, e.Args.SkipWhile(x => x != "--disk-check").Skip(1).First()); }
                catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); return; }
            }
            if (hardwareCheck)
            {
                try { await HardwareChecks.RunAsync(e.Args.SkipWhile(x => x != "--hardware-check").Skip(1).First()); }
                catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); return; }
            }
            if (pcCheck)
            {
                try { await PcInformationChecks.RunAsync(window, e.Args.SkipWhile(x => x != "--pc-check").Skip(1).First()); }
                catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); return; }
            }
            if (toolsCheck)
            {
                try { await ToolsChecks.RunAsync(window, e.Args.SkipWhile(x => x != "--tools-check").Skip(1).First()); }
                catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); return; }
            }
            if (motionCheck)
            {
                try { await MotionChecks.RunAsync(window, e.Args.SkipWhile(x => x != "--motion-check").Skip(1).First()); }
                catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); return; }
            }
            if (boundsCheck)
            {
                try
                {
                    string receipt = e.Args.SkipWhile(x => x != "--window-bounds-check").Skip(1).First();
                    await WindowBoundsCheck.RunAsync(window, receipt);
                }
                catch (Exception error)
                {
                    File.WriteAllText("smoke-error.txt", error.ToString());
                    Shutdown(1); return;
                }
            }
            if (e.Args.Contains("--settings-preview")) window.ShowSettingsPreview();
            await Task.Delay(450);
            await window.Dispatcher.InvokeAsync(() =>
            {
                if (startupCheck && !window.StartupSucceeded)
                    throw new InvalidOperationException("Application initialization failed: " + window.StartupError);
                string option = startupCheck ? "--startup-check" : "--smoke-test";
                string destination = e.Args.SkipWhile(x => x != option).Skip(1).FirstOrDefault() ?? "desktop-preview.png";
                InterfacePreview.Save(window, destination);
                window.ExitForSmokeTest();

            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    { singleInstance?.Dispose(); base.OnExit(e); }
}
