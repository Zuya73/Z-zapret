using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Zapret.Core;
using Zapret.Desktop.Runtime;
using Forms = System.Windows.Forms;
using Validation = Zapret.Core.Validation;

namespace Zapret.Desktop;

public sealed record RecoveryRecord(EngineSnapshot Snapshot, AppSettings Settings);
public sealed record ProbeRow(string Strategy, string Target, string Protocol, string Status, string Latency, string Detail);

public partial class MainWindow : Window
{
    private readonly AppPaths paths = new();
    private readonly bool preview;
    private readonly bool startupLaunch;
    private Task? initialization;
    private readonly ObservableCollection<ProbeRow> probeRows = [];
    private readonly object logLock = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly UpdateManager updates = new();
    private AppUpdate? availableUpdate;
    private bool checkingUpdate, downloadingUpdate;
    private IReadOnlyList<Strategy> strategies = [];
    private IReadOnlyList<ProbeTarget> targets = [];
    private AppSettings settings = new();
    private EngineController? engine;
    private Maintenance? maintenance;
    private Forms.NotifyIcon? tray;
    private CancellationTokenSource? operationCancellation;
    private TestSession? lastSession;
    private bool ready, busy, forceExit, exitRequested;
    private string? activeStrategy;
    private static readonly string[] UserFiles = ["list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt", "ipset-include-user.txt"];

    internal bool StartupSucceeded => ready && ErrorBanner.Visibility != Visibility.Visible;
    internal string StartupError => ErrorText.Text;
    internal void SetPreviewTheme(bool light) => SetTheme(light);
    internal void ShowSettingsPreview()
    {
        Navigate_Click(new Button { Tag = "Settings" }, new RoutedEventArgs());
        if (preview) { StartupSwitch.IsChecked = true; StartupConnectSwitch.IsChecked = true; }
    }

    public MainWindow(bool preview = false, bool startupLaunch = false)
    {
        this.preview = preview;
        this.startupLaunch = startupLaunch;
        InitializeComponent();
        AppVersionText.Text = "Версия " + UpdateManifest.CurrentVersion.ToString(3);
        if (startupLaunch) ShowInTaskbar = false;
        ProbeGrid.ItemsSource = probeRows;
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            timer.Stop(); updateTimer.Stop(); lifetime.Cancel(); tray?.Dispose(); engine?.Dispose(); operationCancellation?.Dispose();
            if (startupLaunch) Application.Current.Shutdown();
        };
        timer.Tick += (_, _) => { if (!busy && ready) RefreshState(); };
        updateTimer.Tick += async (_, _) => await CheckAutomaticUpdatesAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await InitializeAsync();

    internal Task InitializeAsync() => initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            strategies = StrategyCatalog.Load(Path.Combine(paths.Bundle, "strategies.json"));
            targets = AtomicFile.ReadJson<ProbeTarget[]>(Path.Combine(paths.Bundle, "targets.json"))
                ?? throw new InvalidDataException("Не найден список проверок.");
            HomeStrategyPicker.ItemsSource = strategies;
            StrategiesGrid.ItemsSource = strategies;
            if (preview)
            {
                HomeStrategyPicker.SelectedIndex = 0;
                ConnectionTitle.Text = "Готов к подключению";
                StatusText.Text = "22 стратегии · встроенное автотестирование · Windows x64";
                StrategyDescription.Text = "Подберите профиль для своей сети и включайте обход одной кнопкой.";
                return;
            }
            paths.Initialize();
            settings = AtomicFile.ReadJson<AppSettings>(paths.Settings) ?? new();
            engine = new(paths, strategies, Log);
            maintenance = new(paths);
            LoadSettings();
            SetTheme(settings.LightTheme);
            CreateTray();
            ready = true;
            LoadUserList();
            Log("Z-zapret " + UpdateManifest.CurrentVersion.ToString(3) + ". Подготовлено стратегий: " + strategies.Count);
            if (File.Exists(paths.Recovery))
            {
                await Execute(async token =>
                {
                    var record = AtomicFile.ReadJson<RecoveryRecord>(paths.Recovery)
                        ?? throw new InvalidDataException("Повреждён журнал восстановления.");
                    await engine.StopAsync(token);
                    var snapshot = startupLaunch && settings.Startup == StartupMode.Tray
                        ? record.Snapshot with { WasRunning = false } : record.Snapshot;
                    await engine.RestoreAsync(snapshot, record.Settings, token);
                    File.Delete(paths.Recovery);
                    Log("Восстановление после прерванной проверки завершено.");
                }, "Восстановление после прерванной проверки");
            }
            RefreshState(); timer.Start();
            StatusText.Text = "Готово. Выберите профиль или запустите автоподбор.";
            if (settings.AutoUpdate) { updateTimer.Start(); _ = CheckAutomaticUpdatesAsync(); }
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            StatusText.Text = "Не удалось подготовить приложение. Проверьте комплект файлов.";
            SetBusy(true);
            CancelTestsButton.IsEnabled = false;
        }
    }

    internal async Task ConnectOnStartupAsync()
    {
        if (!StartupSucceeded || engine == null) return;
        await Execute(token => StartupPolicy.ConnectAsync(startupLaunch, settings.Startup, engine.IsRunning, async ct =>
        {
            await engine.StartAsync(Selected, settings, ct);
            activeStrategy = Selected.Id;
        }, token), "Автоматическое подключение");
    }

    internal void NotifyStartupFailure()
    {
        if (tray == null) CreateTray();
        tray!.ShowBalloonTip(7000, "Z-zapret", "Не удалось завершить запуск. Откройте программу из трея, чтобы посмотреть причину.", Forms.ToolTipIcon.Warning);
    }

    private Strategy Selected => HomeStrategyPicker.SelectedItem as Strategy
        ?? throw new InvalidOperationException("Выберите стратегию.");

    private void CreateTray()
    {
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "Z-zapret", Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Z-zapret", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Включить / выключить", null, (_, _) => Dispatcher.Invoke(() => Connect_Click(this, new RoutedEventArgs())));
        menu.Items.Add("Проверить обновления", null, (_, _) => Dispatcher.Invoke(() => CheckUpdates_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() => { forceExit = true; Close(); }));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        { Navigate_Click(new Button { Tag = "Settings" }, new RoutedEventArgs()); ShowFromTray(); });
    }

    private void ShowFromTray() { ShowInTaskbar = true; WindowState = WindowState.Normal; Show(); Activate(); }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (preview) return;
        if (!forceExit && ready && settings.MinimizeToTray)
        { e.Cancel = true; Hide(); return; }
        if (busy && operationCancellation != null)
        {
            e.Cancel = true; exitRequested = true;
            operationCancellation.Cancel();
            StatusText.Text = "Завершение операции и восстановление подключения…";
        }
    }

    public void ExitForSmokeTest() { forceExit = true; Close(); }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        string page = (sender as Button)?.Tag as string ?? "Home";
        foreach (var (panel, name) in new[] { (HomePanel, "Home"), (TestsPanel, "Tests"), (StrategiesPanel, "Strategies"), (DiagnosticsPanel, "Diagnostics"), (SettingsPanel, "Settings"), (LogsPanel, "Logs") })
            panel.Visibility = page == name ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "Tests" => ("Автоподбор", "Рабочая стратегия начинается с проверки."),
            "Strategies" => ("Стратегии", "Все исходные профили в одном месте."),
            "Diagnostics" => ("Диагностика", "Найдём причину проблем с подключением."),
            "Settings" => ("Настройки", "Точная настройка без командной строки."),
            "Logs" => ("Журнал", "История работы и подробности ошибок."),
            _ => ("Обзор", "Ваше подключение. Ваши правила.")
        };
    }

    private async Task Execute(Func<CancellationToken, Task> action, string stage)
    {
        if (busy) return;
        SetBusy(true);
        ErrorBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = stage;
        operationCancellation = new CancellationTokenSource();
        try { await action(operationCancellation.Token); StatusText.Text = "Готово."; }
        catch (OperationCanceledException) { StatusText.Text = "Операция отменена."; Log("Операция отменена."); }
        catch (Exception error) { ShowError(error.Message); Log("ОШИБКА: " + error.Message); StatusText.Text = "Операция завершилась с ошибкой."; }
        finally
        {
            operationCancellation.Dispose(); operationCancellation = null;
            SetBusy(false);
            RefreshState();
            if (exitRequested) { forceExit = true; Close(); }
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        foreach (Control control in new Control[] { ConnectButton, ServiceButton, HomeStrategyPicker, StartTestsButton, TestModePicker,
            OnlySelectedCheck, ChooseStrategyButton, StrategiesGrid, DiagnoseButton, ClearCacheButton, SaveSettingsButton,
            SaveListButton, UserListPicker, UpdateIpSetButton, UpdateHostsButton, RemoveHostsButton, CheckUpdatesButton,
            LegacyButton, TimestampsButton, IpSetPicker, GameFilterPicker, TcpPortsBox, UdpPortsBox, DiscordFakePicker, GameFakePicker, TrayCheck, StartupSwitch, SaveStartupButton, AutoUpdateSwitch, SaveAutoUpdateButton })
            control.IsEnabled = !value;
        StartupConnectSwitch.IsEnabled = !value && StartupSwitch.IsChecked == true;
        CancelTestsButton.IsEnabled = value && TestsPanel.Visibility == Visibility.Visible;
        ApplyRecommendedButton.IsEnabled = !value && lastSession?.RecommendedStrategyId != null;
        InstallUpdateButton.IsEnabled = !value && availableUpdate != null;
        CancelUpdateButton.IsEnabled = value && downloadingUpdate;
        OperationProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        OperationProgress.IsIndeterminate = value;
        if (!value) ProgressCounter.Text = "";
    }

    private void RefreshState()
    {
        if (engine == null) return;
        bool running = engine.IsRunning;
        ConnectionTitle.Text = running ? "Движок работает" : "Готов к запуску";
        StateDot.Fill = running ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(123, 141, 166));
        ConnectionDetail.Text = running ? "Для проверки сервисов запустите сетевые тесты." : "Выберите стратегию или запустите автоподбор.";
        ConnectButton.Content = running ? "Выключить" : "Включить";
        ServiceButton.Content = engine.OwnsService ? "Выключить автозапуск" : "Включить автозапуск";
        ServiceText.Text = engine.OwnsService ? "Служба Windows установлена. Движок может работать при закрытом приложении." :
            engine.ServiceInstalled ? "Обнаружена служба zapret из другой сборки. Подробности в диагностике." : "Движок запускается вручную. Можно включить службу Windows.";
        ActiveStrategyText.Text = "Активная стратегия: " + (running ? strategies.FirstOrDefault(x => x.Id == activeStrategy)?.Name ?? "служба Windows" : "—");
        if (tray != null) tray.Text = running ? "Z-zapret · движок работает" : "Z-zapret · остановлен";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || engine == null || busy) return;
        await Execute(async token =>
        {
            if (engine.IsRunning) { await engine.StopAsync(token); Log("Движок остановлен."); }
            else { await engine.StartAsync(Selected, settings, token); activeStrategy = Selected.Id; }
        }, "Переключение подключения");
    }

    private async void Service_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || engine == null) return;
        await Execute(async token =>
        {
            if (engine.OwnsService) await engine.RemoveServiceAsync(token);
            else { await engine.InstallServiceAsync(Selected, settings, token); activeStrategy = Selected.Id; }
        }, "Настройка автозапуска");
    }

    private void HomeStrategy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HomeStrategyPicker.SelectedItem is not Strategy selected) return;
        StrategyDescription.Text = selected.Description + " · " + selected.Source;
        if (ready && !busy)
        { settings = settings with { StrategyId = selected.Id }; AtomicFile.WriteJson(paths.Settings, settings); }
    }

    private void ChooseStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (StrategiesGrid.SelectedItem is Strategy selected)
        { HomeStrategyPicker.SelectedItem = selected; StatusText.Text = "Выбран профиль: " + selected.Name; }
    }

    private async void StartTests_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || engine == null || maintenance == null) return;
        TestMode mode = TestModePicker.SelectedIndex switch { 1 => TestMode.Full, 2 => TestMode.Dpi, _ => TestMode.Quick };
        var chosen = OnlySelectedCheck.IsChecked == true ? new[] { Selected } : mode == TestMode.Quick
            ? new[] { Selected }.Concat(strategies.Where(x => new[] { "general", "general-alt", "general-alt2", "general-alt6", "general-fake-tls-auto" }.Contains(x.Id))).DistinctBy(x => x.Id).Take(5).ToArray()
            : strategies.ToArray();
        await Execute(async token =>
        {
            if (File.Exists(paths.Recovery)) throw new InvalidOperationException("Сначала завершите восстановление предыдущего теста: перезапустите приложение.");
            var selectedTargets = mode == TestMode.Dpi ? await maintenance.LoadDpiTargetsAsync(token) : mode == TestMode.Quick
                ? targets.Where(x => new[] { "DiscordMain", "DiscordGateway", "DiscordCDN", "YouTubeWeb", "YouTubeImage", "GoogleMain" }.Contains(x.Id)).ToArray()
                : targets;
            string testDirectory = paths.PrepareLists(settings, dpi: mode == TestMode.Dpi, test: true);
            probeRows.Clear(); lastSession = null; ExportTestsButton.IsEnabled = false;
            RecommendationTitle.Text = "Идёт проверка соединения";
            ConnectionTitle.Text = "Идёт автоподбор";
            ConnectionDetail.Text = "Подключение временно переключается между стратегиями.";
            RecommendationDetail.Text = "По завершении прежнее подключение будет восстановлено.";
            OperationProgress.IsIndeterminate = false;
            var progress = new Progress<TestProgress>(p =>
            {
                StatusText.Text = p.Stage;
                OperationProgress.Maximum = Math.Max(1, p.Total);
                OperationProgress.Value = p.Completed;
                ProgressCounter.Text = p.Completed + " / " + p.Total;
                if (p.Result is { } result)
                {
                    probeRows.Add(new(p.Stage, result.TargetName, result.Protocol, StatusLabel(result.Status),
                        result.Milliseconds.ToString("F0"), result.Detail));
                }
            });
            try
            {
                var runner = new TestCoordinator(engine, new CurlProbe(paths.Root));
                lastSession = await runner.RunAsync(chosen, selectedTargets, mode, testDirectory, settings, progress,
                    snapshot => AtomicFile.WriteJson(paths.Recovery, new RecoveryRecord(snapshot, settings)),
                    () => File.Delete(paths.Recovery), token);
                string saved = Path.Combine(paths.Results, "test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-ffff") + ".json");
                AtomicFile.WriteJson(saved, lastSession);
                ExportTestsButton.IsEnabled = true;
                string? recommended = strategies.FirstOrDefault(x => x.Id == lastSession.RecommendedStrategyId)?.Name;
                RecommendationTitle.Text = lastSession.Cancelled ? "Проверка отменена" : recommended != null ? "Рекомендуем: " + recommended : "Рабочая стратегия не подтверждена";
                RecommendationDetail.Text = lastSession.Cancelled ? "Предыдущее подключение восстановлено. Частичные результаты сохранены." :
                    recommended != null ? "Профиль прошёл первичную и повторную проверки. Примените его для подключения." :
                    "Ни один профиль не выполнил условия проверки. Посмотрите ошибки и запустите диагностику.";
                Log("Отчёт сохранён: " + saved);
            }
            finally
            {
                if (!File.Exists(paths.Recovery))
                    try { Directory.Delete(testDirectory, true); } catch (IOException) { }
            }
        }, "Подготовка автоподбора");
    }

    private static string StatusLabel(ProbeStatus status) => status switch
    { ProbeStatus.Passed => "OK", ProbeStatus.Failed => "Ошибка", ProbeStatus.Unsupported => "Не поддерж.", _ => "Не подтверждён" };

    private void CancelTests_Click(object sender, RoutedEventArgs e)
    { operationCancellation?.Cancel(); CancelTestsButton.IsEnabled = false; StatusText.Text = "Остановка и восстановление…"; }

    private async void ApplyRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (lastSession?.RecommendedStrategyId is not string id || engine == null) return;
        var selected = strategies.Single(x => x.Id == id);
        await Execute(async token =>
        {
            var previous = await engine.CaptureAsync(token);
            var old = settings;
            await engine.StopAsync(token);
            try
            {
                await engine.StartAsync(selected, settings, token);
                settings = settings with { StrategyId = id }; AtomicFile.WriteJson(paths.Settings, settings);
                HomeStrategyPicker.SelectedItem = selected; activeStrategy = id;
            }
            catch
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await engine.StopAsync(timeout.Token); await engine.RestoreAsync(previous, old, timeout.Token); throw;
            }
        }, "Применение стратегии");
    }

    private void ExportTests_Click(object sender, RoutedEventArgs e)
    {
        if (lastSession == null) return;
        var dialog = new SaveFileDialog { Filter = "JSON-отчёт|*.json|Текстовый отчёт|*.txt", FileName = "Z-zapret-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmm") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (dialog.FilterIndex == 1) AtomicFile.WriteJson(dialog.FileName, lastSession);
            else
            {
                var lines = new List<string> { "Z-zapret — результаты сетевых проверок", lastSession.Started.ToString("O"), "Рекомендация: " + (lastSession.RecommendedStrategyId ?? "нет"), "" };
                foreach (var report in new[] { lastSession.Baseline }.Concat(lastSession.Reports).Concat(lastSession.Confirmation))
                {
                    lines.Add(report.Name + " | " + report.StartError);
                    lines.AddRange(report.Results.Select(x => $"  {x.TargetName} | {x.Protocol} | {x.Status} | HTTP {x.HttpCode} | {x.Milliseconds:F0} ms | {x.Detail}"));
                }
                AtomicFile.Write(dialog.FileName, string.Join(Environment.NewLine, lines));
            }
            StatusText.Text = "Отчёт сохранён.";
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void LoadSettings()
    {
        HomeStrategyPicker.SelectedItem = strategies.FirstOrDefault(x => x.Id == settings.StrategyId) ?? strategies[0];
        IpSetPicker.SelectedIndex = (int)settings.IpSet; GameFilterPicker.SelectedIndex = (int)settings.GameFilter;
        TcpPortsBox.Text = settings.TcpPorts; UdpPortsBox.Text = settings.UdpPorts; TrayCheck.IsChecked = settings.MinimizeToTray;
        StartupSwitch.IsChecked = settings.Startup != StartupMode.Disabled;
        StartupConnectSwitch.IsChecked = settings.Startup == StartupMode.TrayAndConnect;
        StartupConnectSwitch.IsEnabled = StartupSwitch.IsChecked == true;
        AutoUpdateSwitch.IsChecked = settings.AutoUpdate;
        var fakes = Directory.GetFiles(Path.Combine(paths.Bundle, "bin"), "*.bin").Select(Path.GetFileName)
            .Where(x => x != null && !x.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase)).Order().ToArray();
        DiscordFakePicker.ItemsSource = fakes; GameFakePicker.ItemsSource = fakes;
        DiscordFakePicker.SelectedItem = settings.DiscordFake; GameFakePicker.SelectedItem = settings.GameFake;
    }

    private void StartupSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (StartupConnectSwitch == null) return;
        if (StartupSwitch.IsChecked != true) StartupConnectSwitch.IsChecked = false;
        StartupConnectSwitch.IsEnabled = !busy && StartupSwitch.IsChecked == true;
    }

    private async void SaveStartup_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy) return;
        var mode = StartupSwitch.IsChecked != true ? StartupMode.Disabled :
            StartupConnectSwitch.IsChecked == true ? StartupMode.TrayAndConnect : StartupMode.Tray;
        await Execute(async token =>
        {
            var previous = settings;
            var manager = new StartupTaskManager();
            try
            {
                await manager.ApplyAsync(mode, token);
                var updated = settings with { Startup = mode };
                AtomicFile.WriteJson(paths.Settings, updated);
                settings = updated;
            }
            catch
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await manager.ApplyAsync(previous.Startup, timeout.Token);
                throw;
            }
            Log(mode switch
            {
                StartupMode.Tray => "Включён запуск приложения вместе с Windows в трее.",
                StartupMode.TrayAndConnect => "Включён запуск в трее с автоматическим подключением.",
                _ => "Автозапуск приложения отключён."
            });
        }, "Сохранение автозапуска");
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (engine == null) return;
        await Execute(async token =>
        {
            var updated = settings with
            {
                IpSet = (IpSetMode)IpSetPicker.SelectedIndex, GameFilter = (GameFilterMode)GameFilterPicker.SelectedIndex,
                TcpPorts = Validation.Ports(TcpPortsBox.Text), UdpPorts = Validation.Ports(UdpPortsBox.Text),
                DiscordFake = DiscordFakePicker.SelectedItem as string ?? throw new InvalidOperationException("Выберите фейк Discord."),
                GameFake = GameFakePicker.SelectedItem as string ?? throw new InvalidOperationException("Выберите игровой фейк."),
                MinimizeToTray = TrayCheck.IsChecked == true
            };
            var snapshot = await engine.CaptureAsync(token);
            var previous = settings;
            await engine.StopAsync(token);
            try
            {
                if (snapshot.WasRunning) await engine.StartAsync(Selected, updated, token);
                settings = updated; AtomicFile.WriteJson(paths.Settings, settings);
            }
            catch
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await engine.StopAsync(timeout.Token); await engine.RestoreAsync(snapshot, previous, timeout.Token); throw;
            }
            Log("Настройки сохранены.");
        }, "Применение настроек");
    }

    private void UserList_Changed(object sender, SelectionChangedEventArgs e) { if (ready) LoadUserList(); }
    private void LoadUserList()
    {
        if (UserListPicker.SelectedIndex is >= 0 and < 4) UserListBox.Text = File.ReadAllText(Path.Combine(paths.Lists, UserFiles[UserListPicker.SelectedIndex]));
    }
    private void SaveList_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy) return;
        try
        {
            AtomicFile.Write(Path.Combine(paths.Lists, UserFiles[UserListPicker.SelectedIndex]), Validation.UserList(UserListBox.Text, UserListPicker.SelectedIndex >= 2));
            StatusText.Text = "Список сохранён. Применится после перезапуска движка.";
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if (maintenance == null || engine == null) return;
        await Execute(async token => { DiagnosticsGrid.ItemsSource = await Task.Run(() => maintenance.Diagnose(engine), token); Log("Проверка окружения завершена."); }, "Проверка окружения");
    }
    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (maintenance == null) return;
        if (MessageBox.Show(this, "Будут удалены Cache, Code Cache и GPUCache Discord. Сначала закройте Discord. Продолжить?", "Кэш Discord", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Execute(_ => { maintenance.ClearDiscordCache(); Log("Кэш Discord очищен."); return Task.CompletedTask; }, "Очистка кэша");
    }
    private async void Timestamps_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Для профилей с параметром ts могут требоваться TCP timestamps. Включить этот параметр Windows?", "TCP timestamps", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Execute(async token =>
        {
            var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("netsh.exe"), ["interface", "tcp", "set", "global", "timestamps=enabled"], TimeSpan.FromSeconds(15), token);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Output + result.Error);
            Log("TCP timestamps включены. Для применения перезапустите движок.");
        }, "Настройка TCP timestamps");
    }
    private async void UpdateIpSet_Click(object sender, RoutedEventArgs e)
    {
        if (maintenance != null) await Execute(async token => { await maintenance.UpdateIpSetAsync(token); Log("IPSet обновлён. Изменения применятся после перезапуска движка."); }, "Загрузка IPSet");
    }
    private async void UpdateHosts_Click(object sender, RoutedEventArgs e)
    {
        if (maintenance == null) return;
        if (MessageBox.Show(this, "В hosts будет добавлен отдельный блок адресов Flowseal. Ваши строки сохранятся; будет создана резервная копия. Продолжить?", "Обновление hosts", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Execute(async token => { await maintenance.UpdateHostsAsync(token); Log("Блок Z-zapret в hosts обновлён."); }, "Обновление hosts");
    }
    private async void RemoveHosts_Click(object sender, RoutedEventArgs e)
    { if (maintenance != null) await Execute(_ => { maintenance.ModifyHosts(null); Log("Управляемый блок hosts удалён."); return Task.CompletedTask; }, "Восстановление hosts"); }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy) return;
        await Execute(token => CheckUpdatesCoreAsync(token, automatic: false), "Проверка обновлений");
    }

    private async Task CheckAutomaticUpdatesAsync()
    {
        if (!ready || busy || !settings.AutoUpdate || lifetime.IsCancellationRequested) return;
        try { await CheckUpdatesCoreAsync(lifetime.Token, automatic: true); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log("Не удалось автоматически проверить обновления: " + error.Message); }
    }

    private async Task CheckUpdatesCoreAsync(CancellationToken token, bool automatic)
    {
        if (checkingUpdate) return;
        checkingUpdate = true;
        try
        {
            var update = await updates.CheckAsync(token);
            if (lifetime.IsCancellationRequested || automatic && !settings.AutoUpdate) return;
            availableUpdate = update;
            UpdateStatusText.Text = update == null ? "Новых стабильных версий нет. Установлена версия " + UpdateManifest.CurrentVersion.ToString(3) + "." :
                $"Доступна версия {update.Version.ToString(3)} · {update.Size / 1048576.0:F1} МБ. Установка начнётся после вашего подтверждения.";
            InstallUpdateButton.IsEnabled = !busy && update != null;
            Log(UpdateStatusText.Text);
            if (automatic && update != null)
                tray?.ShowBalloonTip(7000, "Обновление Z-zapret", "Доступна версия " + update.Version.ToString(3) + ". Откройте настройки, чтобы установить её.", Forms.ToolTipIcon.Info);
        }
        finally { checkingUpdate = false; }
    }

    private void SaveAutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy) return;
        try
        {
            var updated = settings with { AutoUpdate = AutoUpdateSwitch.IsChecked == true };
            AtomicFile.WriteJson(paths.Settings, updated); settings = updated;
            if (settings.AutoUpdate) { updateTimer.Start(); _ = CheckAutomaticUpdatesAsync(); }
            else updateTimer.Stop();
            StatusText.Text = settings.AutoUpdate ? "Автоматическая проверка обновлений включена." : "Автоматическая проверка обновлений отключена.";
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || availableUpdate is not { } update || engine == null) return;
        if (MessageBox.Show(this, $"Скачать и установить Z-zapret {update.Version.ToString(3)}? Приложение и подключение будут остановлены для обновления. Настройки и списки сохранятся.",
            "Обновление Z-zapret", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        downloadingUpdate = true;
        try
        {
            await Execute(async token =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                OperationProgress.IsIndeterminate = false; OperationProgress.Maximum = 100;
                var progress = new Progress<DownloadProgress>(p =>
                { OperationProgress.Value = p.Percent; StatusText.Text = $"Загрузка обновления: {p.Percent}%"; });
                string installer = await updates.DownloadAsync(update, paths, progress, timeout.Token);
                var snapshot = await engine.CaptureAsync(token);
                try
                {
                    await engine.StopAsync(token);
                    var launch = new ProcessStartInfo(installer) { UseShellExecute = true };
                    launch.ArgumentList.Add("/UPDATEPID=" + Environment.ProcessId);
                    using var child = Process.Start(launch) ?? throw new InvalidOperationException("Не удалось запустить установщик.");
                    exitRequested = true; forceExit = true;
                }
                catch
                {
                    using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    await engine.StopAsync(restore.Token); await engine.RestoreAsync(snapshot, settings, restore.Token); throw;
                }
            }, "Загрузка обновления");
        }
        finally { downloadingUpdate = false; CancelUpdateButton.IsEnabled = false; }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        settings = settings with { LightTheme = !settings.LightTheme }; SetTheme(settings.LightTheme);
        if (ready) AtomicFile.WriteJson(paths.Settings, settings);
    }
    private static void SetTheme(bool light)
    {
        var values = light
            ? new[] { "#F2F5F9", "#E8EEF5", "#FFFFFF", "#F4F7FB", "#172434", "#53657B", "#D4DFEA", "#31D5A1", "#DAE9F4" }
            : new[] { "#0D121B", "#101722", "#161F2C", "#101824", "#EEF4FA", "#A1AFC2", "#2B3749", "#41E0A9", "#243549" };
        string[] keys = ["BackgroundBrush", "SidebarBrush", "PanelBrush", "FieldBrush", "TextBrush", "MutedBrush", "LineBrush", "AccentBrush", "HoverBrush"];
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
    }

    private void Log(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        lock (logLock)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(paths.Log)!); File.AppendAllText(paths.Log, line + Environment.NewLine); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            if (LogBox.Text.Length > 200_000) LogBox.Text = LogBox.Text[^100_000..];
            LogBox.ScrollToEnd();
        });
    }
    private void ShowError(string text) { ErrorText.Text = text; ErrorBanner.Visibility = Visibility.Visible; }
    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Текстовый журнал|*.txt", FileName = "Z-zapret-log.txt" };
        if (dialog.ShowDialog(this) == true)
            try { AtomicFile.Write(dialog.FileName, LogBox.Text); } catch (Exception error) { ShowError(error.Message); }
    }
    private void Legacy_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine?.IsRunning == true) { ShowError("Сначала остановите движок и завершите проверку."); return; }
        if (engine?.OwnsService == true) { ShowError("Перед переходом к консольным инструментам выключите автозапуск приложения."); return; }
        Process.Start(new ProcessStartInfo(Path.Combine(paths.Bundle, "service.bat")) { UseShellExecute = true, WorkingDirectory = paths.Bundle });
    }
    private void OpenServices_Click(object sender, RoutedEventArgs e) => Open("services.msc");
    private void GitHub_Click(object sender, RoutedEventArgs e) => Open("https://github.com/Zuya73/Z-zapret");
    private void Releases_Click(object sender, RoutedEventArgs e) => Open("https://github.com/Zuya73/Z-zapret/releases");
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
