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
        if (e.Args.Contains("--uninstall-service") || e.Args.Contains("--stop-service-for-update"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var paths = new AppPaths();
                var strategies = StrategyCatalog.Load(Path.Combine(paths.Bundle, "strategies.json"));
                using var engine = new EngineController(paths, strategies, _ => { });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                if (engine.OwnsService)
                {
                    if (e.Args.Contains("--uninstall-service")) await engine.RemoveServiceAsync(timeout.Token);
                    else await engine.StopAsync(timeout.Token);
                }
                if (e.Args.Contains("--uninstall-service")) new Maintenance(paths).ModifyHosts(null);
                Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "Z-zapret-maintenance-error.txt"), error.ToString());
                Shutdown(1);
            }
            return;
        }
        bool smoke = e.Args.Contains("--smoke-test");
        if (!smoke)
        {
            singleInstance = new Mutex(true, @"Global\Z-zapret.Desktop", out bool created);
            if (!created)
            {
                MessageBox.Show("Z-zapret уже запущен. Откройте его через значок в области уведомлений.", "Z-zapret");
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
        var window = new MainWindow(smoke);
        MainWindow = window;
        window.Show();
        if (smoke)
        {
            await window.Dispatcher.InvokeAsync(() =>
            {
                window.UpdateLayout();
                var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                string destination = e.Args.SkipWhile(x => x != "--smoke-test").Skip(1).FirstOrDefault() ?? "desktop-preview.png";
                using var stream = File.Create(destination); encoder.Save(stream);
                window.ExitForSmokeTest();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    { singleInstance?.Dispose(); base.OnExit(e); }
}
