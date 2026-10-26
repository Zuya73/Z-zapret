using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class MainWindow
{
    private readonly ServiceHealthProbe serviceHealth = new();
    private readonly ReconnectPolicy reconnectPolicy = new();
    private readonly DispatcherTimer healthClock = new() { Interval = TimeSpan.FromSeconds(45) };
    private CancellationTokenSource? healthCancellation;
    private ConnectionHealth? connectionHealth;
    private bool healthSampling, connectionWanted;
    private int connectionEpoch;

    private void InitializeConnectionMonitor()
    {
        healthClock.Tick += async (_, _) => await CheckConnectionAsync();
        Closed += (_, _) => { healthClock.Stop(); healthCancellation?.Cancel(); serviceHealth.Dispose(); };
    }
    private void InvalidateConnectionHealth()
    {
        connectionEpoch++; healthCancellation?.Cancel(); connectionHealth = null; reconnectPolicy.ResetEvidence();
    }
    private void ConfigureConnectionMonitor()
    {
        MonitorConnectionSwitch.IsChecked = settings.MonitorConnection;
        AutoReconnectSwitch.IsChecked = settings.AutoReconnect;
        if (!preview && settings.MonitorConnection) { healthClock.Start(); _ = CheckConnectionAsync(); }
        else { healthClock.Stop(); InvalidateConnectionHealth(); }
    }
    private async void SaveConnectionMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy) return;
        try
        {
            bool reconnect = AutoReconnectSwitch.IsChecked == true;
            var updated = settings with { MonitorConnection = MonitorConnectionSwitch.IsChecked == true || reconnect, AutoReconnect = reconnect };
            AtomicFile.WriteJson(paths.Settings, updated); settings = updated;
            ConfigureConnectionMonitor(); RefreshState();
            StatusText.Text = "Настройки проверки подключения сохранены.";
        }
        catch (Exception error) { ShowError(UserMessages.Error(error)); }
        await Task.CompletedTask;
    }
    private async void CheckConnection_Click(object sender, RoutedEventArgs e) => await CheckConnectionAsync(manual: true);
    private async Task CheckConnectionAsync(bool manual = false)
    {
        if (preview || !ready || busy || healthSampling || !manual && !settings.MonitorConnection || engine == null) return;
        healthSampling = true; int epoch = connectionEpoch;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        healthCancellation = cancellation;
        try
        {
            var health = await serviceHealth.CheckAsync(cancellation.Token);
            if (epoch != connectionEpoch || busy || lifetime.IsCancellationRequested) return;
            connectionHealth = health;
            HealthChecksGrid.ItemsSource = health.Checks.Select(x => new { x.Name, State = x.Available ? "Доступен" : "Не подтверждён", Latency = $"{x.Milliseconds:0} мс", x.Detail });
            RefreshState();
            if (reconnectPolicy.Observe(health, engine.IsRunning, connectionWanted, settings.AutoReconnect))
                await RecoverConnectionAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log("Проверка доступности: " + error.Message); }
        finally { if (ReferenceEquals(healthCancellation, cancellation)) healthCancellation = null; healthSampling = false; }
    }
    private void RenderConnectionHealth(bool running)
    {
        if (connectionHealth == null)
        { ConnectionDetail.Text = running ? "Обход включён. Доступ к сайтам ещё не проверен." : "Выберите профиль или запустите автоподбор."; return; }
        bool fresh = DateTimeOffset.UtcNow - connectionHealth.CapturedAt < TimeSpan.FromMinutes(2);
        ConnectionTitle.Text = !running ? "Обход отключён" : !fresh ? "Ожидается проверка" :
            connectionHealth.ServicesAvailable ? "Сайты отвечают" : connectionHealth.InternetAvailable ? "Есть проблемы с доступом" : "Интернет не проверен";
        ConnectionDetail.Text = connectionHealth.Summary + $"\nПроверено в {connectionHealth.CapturedAt.ToLocalTime():HH:mm:ss}" +
            (fresh ? "" : " · результат устарел");
        if (running && (!fresh || !connectionHealth.ServicesAvailable)) StateDot.Fill = (Brush)FindResource("WarningDotBrush");
        if (tray != null) tray.Text = "x2zapret · " + (running && fresh && connectionHealth.ServicesAvailable ? "сайты отвечают" : running ? "обход включён" : "остановлен");
    }
    private async Task RecoverConnectionAsync()
    {
        if (engine == null || busy || !connectionWanted || File.Exists(paths.Recovery) || !reconnectPolicy.BeginAttempt(DateTimeOffset.UtcNow)) return;
        await Execute(async token =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var ct = deadline.Token;
            var snapshot = await engine.CaptureAsync(ct);
            string originalId = snapshot.StrategyId ?? activeStrategy ?? settings.StrategyId;
            snapshot = snapshot with { WasRunning = true, StrategyId = originalId, ServiceMode = snapshot.ServiceMode || engine.OwnsService };
            var previous = settings;
            AtomicFile.WriteJson(paths.Recovery, new RecoveryRecord(snapshot, previous));
            bool committed = false, completedNormally = false;
            try
            {
                var candidates = new[] { originalId, lastSession?.RecommendedStrategyId, "general", "general-alt", "general-alt2" }
                    .Where(x => x != null).Distinct().Select(id => strategies.FirstOrDefault(s => s.Id == id)).OfType<Strategy>();
                committed = await ConnectionRecovery.RunAsync(candidates, engine.StopAsync,
                    (candidate, cancellation) => engine.StartAsync(candidate, previous, cancellation), serviceHealth.CheckAsync,
                    cancellation => engine.RestoreAsync(snapshot, previous, cancellation),
                    (candidate, health) =>
                    {
                        var updated = previous with { StrategyId = candidate.Id };
                        AtomicFile.WriteJson(paths.Settings, updated); settings = updated;
                        activeStrategy = candidate.Id; HomeStrategyPicker.SelectedItem = candidate; connectionHealth = health;
                    }, message => Log("Переподключение: " + message), ct, engineRunning: () => engine.IsRunning);
                completedNormally = true;
                if (committed)
                {
                    File.Delete(paths.Recovery);
                    Log("Подключение восстановлено. Профиль: " + (strategies.FirstOrDefault(s => s.Id == activeStrategy)?.Name ?? activeStrategy));
                    tray?.ShowBalloonTip(5000, "x2zapret", "Доступ к сервисам восстановлен.", System.Windows.Forms.ToolTipIcon.Info);
                }
                else
                {
                    Log("Другой подходящий профиль не найден. Возвращён прежний профиль; перед новой попыткой будет пауза.");
                    tray?.ShowBalloonTip(6000, "x2zapret", "Сайты пока не отвечают на проверку. Возвращён прежний профиль. Откройте «Диагностику».", System.Windows.Forms.ToolTipIcon.Warning);
                }
            }
            finally
            {
                if (!committed)
                {
                    AtomicFile.WriteJson(paths.Settings, previous); settings = previous;
                    activeStrategy = originalId; HomeStrategyPicker.SelectedItem = strategies.FirstOrDefault(x => x.Id == originalId);
                    // A failed or interrupted restoration deliberately retains its durable journal.
                    if (completedNormally) File.Delete(paths.Recovery);
                }
            }
        }, "Восстановление подключения…");
    }
}
