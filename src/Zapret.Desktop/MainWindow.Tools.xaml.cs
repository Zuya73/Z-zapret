using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class MainWindow
{
    private ShutdownTimerController? shutdownTimer;
    private ShutdownPlan? shutdownPlan;
    private bool shutdownBusy;
    private readonly DispatcherTimer shutdownClock = new() { Interval = TimeSpan.FromSeconds(1) };
    internal ShutdownTimerController? ShutdownTimer => shutdownTimer;
    internal void ShowToolsPreview() => SelectPage("Tools");

    private void InitializeTools(ShutdownTimerController? controller)
    {
        shutdownTimer = controller ?? (preview ? null : ShutdownTimerManager.Create());
        shutdownClock.Tick += (_, _) => UpdateShutdownCountdown();
        shutdownClock.Start();
        Closed += (_, _) => shutdownClock.Stop();
        UpdateShutdownCountdown();
        if (!preview) _ = RefreshShutdownTimerAsync();
        try
        {
            var info = AtomicFile.ReadJson<UpstreamInfo>(Path.Combine(paths.Catalogs, "upstream-info.json"));
            FlowsealVersionText.Text = info == null ? "Компоненты Flowseal" :
                $"Flowseal {info.Version} · профилей: {info.ProfileCount}";
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException)
        { FlowsealVersionText.Text = "Сведения о компонентах недоступны"; }
    }
    private sealed record UpstreamInfo(string Version, int ProfileCount);

    internal async Task RefreshShutdownTimerAsync()
    {
        if (shutdownTimer == null || shutdownBusy) return;
        shutdownBusy = true; UpdateShutdownCountdown();
        try { shutdownPlan = await shutdownTimer.ReadAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShutdownTimerError.Text = error.Message; }
        finally { shutdownBusy = false; UpdateShutdownCountdown(); }
    }
    internal async Task StartShutdownTimerAsync()
    {
        if (shutdownTimer == null || shutdownBusy) return;
        shutdownBusy = true; UpdateShutdownCountdown(); ShutdownTimerError.Text = "";
        try
        {
            var delay = ShutdownTimerPolicy.Delay(ShutdownHoursBox.Text, ShutdownMinutesBox.Text);
            shutdownPlan = await shutdownTimer.ScheduleAsync(delay, DateTimeOffset.UtcNow, lifetime.Token);
            Log("X2 Tools: выключение ПК запланировано на " + shutdownPlan.Deadline.ToLocalTime().ToString("dd.MM HH:mm") + ".");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShutdownTimerError.Text = error.Message; }
        finally { shutdownBusy = false; UpdateShutdownCountdown(); }
    }
    internal async Task CancelShutdownTimerAsync()
    {
        if (shutdownTimer == null || shutdownBusy) return;
        shutdownBusy = true; UpdateShutdownCountdown(); ShutdownTimerError.Text = "";
        try
        {
            await shutdownTimer.CancelAsync(lifetime.Token);
            shutdownPlan = null;
            Log("X2 Tools: таймер выключения отменён.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShutdownTimerError.Text = error.Message; }
        finally { shutdownBusy = false; UpdateShutdownCountdown(); }
    }
    private async void StartShutdownTimer_Click(object sender, RoutedEventArgs e) => await StartShutdownTimerAsync();
    private async void CancelShutdownTimer_Click(object sender, RoutedEventArgs e) => await CancelShutdownTimerAsync();
    private void ShutdownPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!StartShutdownTimerButton.IsEnabled || sender is not Button { Tag: string value }) return;
        int minutes = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        ShutdownHoursBox.Text = (minutes / 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShutdownMinutesBox.Text = (minutes % 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private void UpdateShutdownCountdown()
    {
        bool active = shutdownPlan?.Deadline > DateTimeOffset.UtcNow;
        ShutdownCountdown.Text = shutdownPlan == null ? "— : — : —" :
            ShutdownTimerPolicy.Remaining(shutdownPlan.Deadline, DateTimeOffset.UtcNow);
        ShutdownStatus.Text = shutdownPlan == null ? "Таймер не запущен" :
            active ? "Выключение: " + shutdownPlan.Deadline.ToLocalTime().ToString("dd.MM.yyyy в HH:mm:ss") : "Запланированное время наступило";
        StartShutdownTimerButton.IsEnabled = shutdownTimer != null && !shutdownBusy && !active;
        CancelShutdownTimerButton.IsEnabled = shutdownTimer != null && !shutdownBusy && active;
        ShutdownHoursBox.IsEnabled = ShutdownMinutesBox.IsEnabled = !shutdownBusy && !active;
    }
    private void FlowsealChanges_Click(object sender, RoutedEventArgs e) =>
        Open("https://github.com/Flowseal/zapret-discord-youtube/releases");
}
