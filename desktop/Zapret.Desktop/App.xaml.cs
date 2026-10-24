using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class App : Application
{
    private Mutex? singleInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--uninstall-service") || e.Args.Contains("--stop-service-for-update") || e.Args.Contains("--refresh-startup"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var paths = new AppPaths();
                var strategies = StrategyCatalog.Load(Path.Combine(paths.Bundle, "strategies.json"));
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
        bool smoke = e.Args.Contains("--smoke-test") || startupCheck;
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
                if (!startupInvocation) MessageBox.Show("Z-zapret уже запущен. Откройте его через значок в области уведомлений.", "Z-zapret");
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
            MessageBox.Show(error.Exception.Message, "Ошибка Z-zapret", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        var window = new MainWindow(preview: smoke && !startupCheck, startupLaunch: startupInvocation);
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
            if (e.Args.Contains("--settings-preview")) { window.ShowSettingsPreview(); await Task.Delay(250); }
            await window.Dispatcher.InvokeAsync(() =>
            {
                if (startupCheck && !window.StartupSucceeded)
                    throw new InvalidOperationException("Application initialization failed: " + window.StartupError);
                string option = startupCheck ? "--startup-check" : "--smoke-test";
                string destination = e.Args.SkipWhile(x => x != option).Skip(1).FirstOrDefault() ?? "desktop-preview.png";
                SavePreview(destination);
                window.SetPreviewTheme(true);
                SavePreview(Path.Combine(Path.GetDirectoryName(destination) ?? ".", Path.GetFileNameWithoutExtension(destination) + "-light.png"));
                window.ExitForSmokeTest();

                void SavePreview(string path)
                {
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    image.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = File.Create(path); encoder.Save(stream);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    { singleInstance?.Dispose(); base.OnExit(e); }
}
