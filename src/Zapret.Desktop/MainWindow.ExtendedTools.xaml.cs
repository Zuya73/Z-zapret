using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class MainWindow
{
    private readonly SensorHistory sensorHistory = new();
    private readonly SensorAlertPolicy sensorAlerts = new();
    private readonly NativeHardwareClient graphHardware = new();
    private readonly PcLiveMonitor graphLive = new();
    private readonly DispatcherTimer graphClock = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? graphCancellation;
    private bool graphSampling, graphRecording, extendedRunning, extendedInitialized;
    private QualityResult? qualityReport;
    private SpeedResult? speedReport;
    private SpaceScan? spaceReport;
    private string graphDriverNote = "";

    private void InitializeExtendedTools()
    {
        extendedInitialized = true;
        graphClock.Tick += async (_, _) => await SampleGraphAsync();
        SensorToolPanel.IsVisibleChanged += (_, _) => RefreshGraphClock();
        Closed += (_, _) => { graphClock.Stop(); graphCancellation?.Cancel(); graphHardware.Dispose(); };
        UpdateExtendedControls();
    }
    private void HideExtendedTools()
    {
        SensorToolPanel.Visibility = NetworkToolPanel.Visibility = SpaceToolPanel.Visibility = Visibility.Collapsed;
    }
    private Button? lastComputerTool, lastDiskTool;
    private bool advancedTools;
    // Four groups at the top level; "Компьютер" and "Диски" show their own sub-tools in the same row.
    private void HighlightTool(Button active)
    {
        bool computer = active == PcToolButton || active == SensorToolButton, disks = active == DiskToolButton || active == SpaceToolButton;
        if (computer) lastComputerTool = active;
        if (disks) lastDiskTool = active;
        foreach (var (button, selected) in new[] { (ComputerGroupButton, computer), (DisksGroupButton, disks), (NetworkToolButton, active == NetworkToolButton), (TimerToolButton, active == TimerToolButton) })
            button.Style = (Style)FindResource(selected ? "ChipActive" : "Chip");
        foreach (var button in new[] { PcToolButton, SensorToolButton, DiskToolButton, SpaceToolButton })
            button.Style = (Style)FindResource(button == active ? "SubChipActive" : "SubChip");
        ComputerSubTools.Visibility = computer ? Visibility.Visible : Visibility.Collapsed;
        DiskSubTools.Visibility = disks ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ComputerGroup_Click(object sender, RoutedEventArgs e)
    { if (lastComputerTool == SensorToolButton) ShowExtendedTool("Sensors"); else ShowPcTool(); }
    private void DisksGroup_Click(object sender, RoutedEventArgs e)
    { if (lastDiskTool == SpaceToolButton) ShowExtendedTool("Space"); else ShowDiskTool(); }

    private void AdvancedTools_Click(object sender, RoutedEventArgs e)
    {
        advancedTools = !advancedTools; ApplyAdvancedTools();
        if (!ready || preview) return;
        try { var updated = settings with { AdvancedTools = advancedTools }; AtomicFile.WriteJson(paths.Settings, updated); settings = updated; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log("Не удалось сохранить режим для опытных: " + error.Message); }
    }
    // Rarely needed options stay out of the way until the user asks for them; hidden options fall back to their defaults.
    private void ApplyAdvancedTools()
    {
        var visibility = advancedTools ? Visibility.Visible : Visibility.Collapsed;
        DiskAdvancedOptions.Visibility = QualityRouteCheck.Visibility = SpeedModePicker.Visibility = SensorCsvButton.Visibility = visibility;
        AdvancedToolsButton.Style = (Style)FindResource(advancedTools ? "ChipActive" : "Chip");
        AdvancedToolsButton.ToolTip = advancedTools ? "Скрыть возможности для опытных" : "Для опытных: дополнительные настройки и инструменты";
        if (!advancedTools)
        { QualityRouteCheck.IsChecked = false; DiskSizePicker.SelectedIndex = 1; DiskModePicker.SelectedIndex = 0; SpeedModePicker.SelectedIndex = 0; }
        UpdatePawnState();
    }

    private readonly Dictionary<string, Verdict?> shownVerdicts = new();
    private void ShowVerdict(string name, Verdict? verdict)
    {
        shownVerdicts[name] = verdict;
        var box = (Border)FindName(name + "Verdict");
        if (verdict == null) { box.Visibility = Visibility.Collapsed; return; }
        var badge = (Border)FindName(name + "VerdictBadge"); var icon = (TextBlock)FindName(name + "VerdictIcon");
        ((TextBlock)FindName(name + "VerdictTitle")).Text = verdict.Title;
        ((TextBlock)FindName(name + "VerdictDetail")).Text = verdict.Detail;
        bool good = verdict.Level == 0;
        badge.Background = good ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        badge.BorderBrush = (Brush)FindResource(good ? "AccentBrush" : verdict.Level == 1 ? "FocusBrush" : "TextBrush");
        icon.Text = verdict.Level switch { 0 => "\uE73E", 1 => "\uE7BA", _ => "\uE711" };
        icon.Foreground = (Brush)FindResource(good ? "AccentForegroundBrush" : "TextBrush");
        bool appearing = box.Visibility != Visibility.Visible;
        box.Visibility = Visibility.Visible;
        if (appearing) Motion.To(box, UIElement.OpacityProperty, 1, 220, 0);
    }
    internal void ShowExtendedTool(string tool)
    {
        if (busy || diskBenchmarkRunning || pcExporting) return;
        HideExtendedTools(); PcInfoPanel.Visibility = ShutdownToolPanel.Visibility = DiskToolPanel.Visibility = Visibility.Collapsed;
        var panel = tool == "Sensors" ? SensorToolPanel : tool == "Network" ? NetworkToolPanel : SpaceToolPanel;
        var button = tool == "Sensors" ? SensorToolButton : tool == "Network" ? NetworkToolButton : SpaceToolButton;
        panel.Visibility = Visibility.Visible; HighlightTool(button); PageScroll.ScrollToTop();
        if (tool == "Network" && QualityGatewayPicker.ItemsSource == null)
        {
            try { QualityGatewayPicker.ItemsSource = NetworkTools.Gateways(); QualityGatewayPicker.SelectedIndex = 0; }
            catch (System.Net.NetworkInformation.NetworkInformationException e) { NetworkStageText.Text = e.Message; }
        }
        if (tool == "Network") RefreshDns();
        if (tool == "Space")
        {
            if (string.IsNullOrWhiteSpace(SpacePathBox.Text)) SpacePathBox.Text = Path.GetPathRoot(Environment.SystemDirectory)!;
            RefreshSpaceDrives();
        }
    }
    private void SelectExtendedTool_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string tool }) ShowExtendedTool(tool); }
    private void UpdateExtendedControls()
    {
        if (!extendedInitialized) return;
        foreach (var button in new[] { ComputerGroupButton, DisksGroupButton, PcToolButton, TimerToolButton, DiskToolButton, SensorToolButton, NetworkToolButton, SpaceToolButton })
            button.IsEnabled = !diskBenchmarkRunning && !extendedRunning;
        foreach (var button in new[] { FlushDnsButton, ResetNetworkButton, DnsCloudflareButton, DnsGoogleButton, DnsAutoButton })
            button.IsEnabled = !busy && !pcExporting;
        QualityStartButton.IsEnabled = SpeedStartButton.IsEnabled = SpaceStartButton.IsEnabled = !busy && !pcExporting;
        NetworkCancelButton.IsEnabled = SpeedCancelButton.IsEnabled = SpaceCancelButton.IsEnabled = extendedRunning;
        SpacePathBox.IsEnabled = SpaceBrowseButton.IsEnabled = SpaceParentButton.IsEnabled = !busy;
        QualityTargetBox.IsEnabled = QualityGatewayPicker.IsEnabled = QualityRouteCheck.IsEnabled = SpeedModePicker.IsEnabled = !busy;
        SaveNetworkButton.IsEnabled = !busy && (qualityReport != null || speedReport != null);
        SpaceOpenButton.IsEnabled = !busy;
        CleanupScanButton.IsEnabled = !busy;
        CleanupButton.IsEnabled = !busy && cleanupSizes.Count > 0;
        foreach (var drive in SpaceDrivesPanel.Children.OfType<Button>()) drive.IsEnabled = !busy;
    }

    private void RefreshGraphClock()
    {
        if (!extendedInitialized || preview) return;
        if (graphRecording || SensorToolPanel.IsVisible) { graphClock.Start(); _ = SampleGraphAsync(); }
        else { graphClock.Stop(); graphCancellation?.Cancel(); graphHardware.Stop(); }
    }
    private async Task SampleGraphAsync()
    {
        if (graphSampling || busy || pcExporting || lifetime.IsCancellationRequested || !graphRecording && !SensorToolPanel.IsVisible) return;
        graphSampling = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); graphCancellation = cancellation;
        try
        {
            var live = await Task.Run(graphLive.Read, cancellation.Token);
            var readings = new List<HardwareReading> { new("x2.cpu.load", "Компьютер", "Загрузка процессора", "Load", live.CpuPercent),
                new("x2.memory.load", "Компьютер", "Загрузка памяти", "Load", live.MemoryPercent) };
            sensorHistory.Add(DateTimeOffset.UtcNow, readings); RenderSensorHistory();
            try
            {
                var frame = await graphHardware.SampleAsync(cancellation.Token); readings.AddRange(frame.Readings);
                sensorHistory.Add(frame.CapturedAt, frame.Readings);
                graphDriverNote = frame.DriverReady ? "" : " · для датчиков процессора и платы может потребоваться драйвер: раздел «О ПК»";
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            { graphDriverNote = " · часть датчиков недоступна: " + error.Message; }
            cancellation.Token.ThrowIfCancellationRequested();
            var alerts = settings.SensorAlerts ?? [];
            foreach (var alert in alerts.Take(50))
            {
                var reading = readings.FirstOrDefault(r => r.Id == alert.SensorId);
                if (reading?.Value is double value && sensorAlerts.Observe(reading.Id, value, alert.Above, DateTimeOffset.UtcNow))
                {
                    string message = reading.Device + " · " + reading.Name + ": " + SensorStatistics.Format(value, reading.Kind);
                    Log("Датчики: превышен порог. " + message);
                    tray?.ShowBalloonTip(6000, "x2zapret · датчики", message, System.Windows.Forms.ToolTipIcon.Warning);
                }
            }
            if (settings.OverheatAlert) CheckOverheat(readings);
            RenderSensorHistory();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SensorStatusText.Text = "Не удалось получить показания: " + error.Message; }
        finally { if (ReferenceEquals(graphCancellation, cancellation)) graphCancellation = null; graphSampling = false; }
    }
    private bool changingSensor;
    internal void RenderSensorHistory()
    {
        string? id = (SensorPicker.SelectedItem as SensorSeries)?.Id;
        var series = sensorHistory.Catalog;
        changingSensor = true;
        SensorPicker.ItemsSource = series;
        SensorPicker.SelectedItem = series.FirstOrDefault(s => s.Id == id) ?? series.FirstOrDefault(s => s.Kind == "Temperature") ?? series.FirstOrDefault();
        changingSensor = false;
        if ((SensorPicker.SelectedItem as SensorSeries)?.Id != id) Sensor_Changed(this, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
        RenderSensorChart(); RenderSensorTiles();
        SensorStatusText.Text = $"Датчиков: {series.Count} · обновление раз в 2 с · история за час" + (graphRecording ? " · сбор продолжается при сворачивании" : " · сбор только в этом разделе") + graphDriverNote;
    }
    private void RenderSensorChart()
    {
        if (SensorPicker.SelectedItem is not SensorSeries sensor) return;
        var points = sensorHistory.Find(sensor.Id)?.Points ?? [];
        SensorPlot.Points = points; SensorPlot.Kind = sensor.Kind;
        SensorPlot.Threshold = (settings.SensorAlerts ?? []).FirstOrDefault(a => a.SensorId == sensor.Id)?.Above;
        SensorPlot.InvalidateVisual();
        SensorValueText.Text = points.Count == 0 ? "—" : SensorStatistics.Format(points[^1].Value, sensor.Kind);
        SensorRangeText.Text = points.Count == 0 ? "" : $"Мин. {SensorStatistics.Format(points.Min(p => p.Value), sensor.Kind)} · Макс. {SensorStatistics.Format(points.Max(p => p.Value), sensor.Kind)}";
    }
    private void RenderSensorTiles()
    {
        var catalog = sensorHistory.Catalog;
        RenderTile("Cpu", SensorTiles.CpuTemperature(catalog), v => SensorTiles.Temperature(v, gpu: false));
        RenderTile("Gpu", SensorTiles.GpuTemperature(catalog), v => SensorTiles.Temperature(v, gpu: true));
        RenderTile("Load", catalog.FirstOrDefault(s => s.Id == "x2.cpu.load"), SensorTiles.Load);
        RenderTile("Memory", catalog.FirstOrDefault(s => s.Id == "x2.memory.load"), SensorTiles.Load);
    }
    private void RenderTile(string name, SensorSeries? sensor, Func<double, (string Text, int Level)> rate)
    {
        double? value = sensor == null ? null : sensorHistory.Find(sensor.Id)?.Points.LastOrDefault()?.Value;
        var tile = (Border)FindName(name + "Tile");
        tile.Tag = sensor?.Id; tile.Cursor = sensor == null ? null : Cursors.Hand;
        ((TextBlock)FindName(name + "TileValue")).Text = value is double v ? SensorStatistics.Format(Math.Round(v), sensor!.Kind) : "—";
        ((ProgressBar)FindName(name + "TileBar")).Value = value is double bar ? Math.Clamp(bar, 0, 100) : 0;
        var (text, level) = value is double reading ? rate(reading) : (sensor == null ? "Нет данных от датчика" : "Ожидание", 0);
        var rating = (TextBlock)FindName(name + "TileRating");
        rating.Text = text;
        rating.Foreground = (Brush)FindResource(level == 0 ? "MutedBrush" : "TextBrush");
        rating.FontWeight = level == 2 ? FontWeights.Bold : FontWeights.Normal;
    }
    private void SensorTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string id } && SensorPicker.Items.OfType<SensorSeries>().FirstOrDefault(s => s.Id == id) is { } series)
            SensorPicker.SelectedItem = series;
    }
    private void CheckOverheat(IReadOnlyList<HardwareReading> readings)
    {
        var catalog = sensorHistory.Catalog;
        foreach (var (series, device) in new[] { (SensorTiles.CpuTemperature(catalog), "Процессор"), (SensorTiles.GpuTemperature(catalog), "Видеокарта") })
        {
            if (series == null || readings.FirstOrDefault(r => r.Id == series.Id)?.Value is not double celsius ||
                !sensorAlerts.Observe("x2.overheat." + series.Id, celsius, SensorTiles.OverheatCelsius, DateTimeOffset.UtcNow)) continue;
            string message = $"{device} нагрелся до {celsius:0} °C. Проверьте охлаждение и закройте тяжёлые программы.";
            Log("Датчики: перегрев. " + message);
            tray?.ShowBalloonTip(7000, "x2zapret · перегрев", message, System.Windows.Forms.ToolTipIcon.Warning);
        }
    }
    private void OverheatAlert_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || preview || settings.OverheatAlert == (OverheatCheck.IsChecked == true)) return;
        try { var updated = settings with { OverheatAlert = OverheatCheck.IsChecked == true }; AtomicFile.WriteJson(paths.Settings, updated); settings = updated; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowError(UserMessages.Error(error)); }
    }
    private void Sensor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!extendedInitialized || changingSensor) return;
        RenderSensorChart();
        var alert = (settings.SensorAlerts ?? []).FirstOrDefault(a => a.SensorId == (SensorPicker.SelectedItem as SensorSeries)?.Id);
        SensorAlertCheck.IsChecked = alert != null; SensorThresholdBox.Text = alert?.Above.ToString(CultureInfo.CurrentCulture) ?? "80";
    }
    private void RecordSensors_Changed(object sender, RoutedEventArgs e)
    {
        if (!extendedInitialized) return;
        graphRecording = SensorRecordCheck.IsChecked == true; RefreshGraphClock(); RenderSensorHistory();
    }
    private void SaveSensorAlert_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || SensorPicker.SelectedItem is not SensorSeries sensor) return;
        try
        {
            var alerts = (settings.SensorAlerts ?? []).Where(a => a.SensorId != sensor.Id).Take(49).ToList();
            if (SensorAlertCheck.IsChecked == true)
            {
                if (!double.TryParse(SensorThresholdBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value) || Math.Abs(value) > 1_000_000_000)
                    throw new ArgumentException("Введите число в единицах выбранного датчика, например 80 для температуры 80 °C.");
                alerts.Add(new(sensor.Id, value));
            }
            var updated = settings with { SensorAlerts = alerts.ToArray() }; AtomicFile.WriteJson(paths.Settings, updated); settings = updated;
            RenderSensorChart(); SensorStatusText.Text = "Предупреждение сохранено. Оно работает, пока собираются показания датчиков.";
        }
        catch (Exception error) { ShowError(UserMessages.Error(error)); }
    }
    private void ExportSensors_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "x2zapret-sensors-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" };
        if (dialog.ShowDialog(this) == true)
            try { AtomicFile.Write(dialog.FileName, sensorHistory.Csv()); } catch (Exception error) { ShowError(UserMessages.Error(error)); }
    }
    private void ClearSensors_Click(object sender, RoutedEventArgs e) { sensorHistory.Clear(); SensorPlot.Points = []; SensorPlot.InvalidateVisual(); RenderSensorHistory(); }

    internal async Task RunQualityAsync()
    {
        if (busy || pcExporting) return;
        string target = QualityTargetBox.Text; var gateway = (QualityGatewayPicker.SelectedItem as NetworkGateway)?.Address;
        bool route = advancedTools && QualityRouteCheck.IsChecked == true;
        ShowVerdict("Quality", null);
        extendedRunning = true;
        await Execute(async token =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
                qualityReport = null; QualityRowsGrid.ItemsSource = null; QualityRouteText.Text = "";
                qualityReport = await NetworkTools.QualityAsync(target, gateway, route, new Progress<string>(s => NetworkStageText.Text = s), timeout.Token);
                RenderQualityReport(qualityReport);
                NetworkStageText.Text = "Проверка завершена. Отсутствие ответа на тестовый запрос само по себе не доказывает обрыв интернета.";
            }
            catch (OperationCanceledException) { NetworkStageText.Text = token.IsCancellationRequested ? "Проверка отменена." : "Проверка заняла слишком много времени и остановлена. Попробуйте ещё раз."; throw; }
            finally { extendedRunning = false; }
        }, "Проверка качества интернета…");
    }
    internal void RenderQualityReport(QualityResult report)
    {
        string Metric(double? value) => value?.ToString("0.0") ?? "—";
        var rows = new List<object>();
        foreach (var item in new[] { ("Сервер · " + report.Target, report.Internet), ("Роутер / выбранный шлюз", report.Gateway) })
        {
            if (item.Item2.Count == 0) continue;
            var stats = NetworkMeasurements.Summarize(item.Item2);
            rows.Add(new { Target = item.Item1, Ping = Metric(stats.AverageMs), Jitter = Metric(stats.JitterMs), Loss = $"{stats.LossPercent:0.0}%", Range = Metric(stats.MinimumMs) + "–" + Metric(stats.MaximumMs) });
        }
        QualityRowsGrid.ItemsSource = rows; QualityRouteText.Text = string.Join("\n", report.Route);
        ShowVerdict("Quality", report.Internet.Count == 0 ? null : ToolVerdicts.Connection(NetworkMeasurements.Summarize(report.Internet),
            report.Gateway.Count == 0 ? null : NetworkMeasurements.Summarize(report.Gateway)));
    }
    internal async Task RunSpeedAsync()
    {
        if (busy || pcExporting) return;
        bool detailed = advancedTools && SpeedModePicker.SelectedIndex == 1;
        ShowVerdict("Speed", null);
        extendedRunning = true;
        await Execute(async token =>
        {
            try
            {
                speedReport = null; SpeedDownText.Text = SpeedUpText.Text = "…"; SpeedLatencyText.Text = "Идёт замер…";
                speedReport = await NetworkTools.SpeedAsync(detailed, new Progress<string>(s => NetworkStageText.Text = s), token);
                RenderSpeedReport(speedReport);
                if (speedReport.Outcome == "Cancelled") throw new OperationCanceledException(token);
                if (speedReport.Outcome == "Failed") throw new IOException(speedReport.Note);
            }
            finally { extendedRunning = false; }
        }, "Измерение скорости интернета…");
    }
    internal void RenderSpeedReport(SpeedResult report)
    {
        SpeedDownText.Text = report.DownloadMbps > 0 ? report.DownloadMbps.ToString("0.0") : "—";
        SpeedUpText.Text = report.UploadMbps > 0 ? report.UploadMbps.ToString("0.0") : "—";
        ShowVerdict("Speed", report.Outcome == "Completed" ? ToolVerdicts.Speed(report.DownloadMbps, report.UploadMbps) : null);
        SpeedLatencyText.Text = $"Время ответа: {report.IdleMs?.ToString("0.0") ?? "—"} мс · под нагрузкой: {report.LoadedMs?.ToString("0.0") ?? "—"} мс";
        NetworkStageText.Text = (report.Outcome == "Completed" ? "Замер завершён." : report.Note) + $" Получено {report.DownloadBytes / 1_000_000d:0.#} МБ · отправлено {report.UploadBytes / 1_000_000d:0.#} МБ.";
    }
    private async void StartQuality_Click(object sender, RoutedEventArgs e) => await RunQualityAsync();
    private async void StartSpeed_Click(object sender, RoutedEventArgs e) => await RunSpeedAsync();
    private void CancelExtended_Click(object sender, RoutedEventArgs e) { if (extendedRunning) operationCancellation?.Cancel(); }
    private void SaveNetwork_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder("x2zapret · сетевые измерения\n");
        if (qualityReport != null)
        {
            text.AppendLine("Проверяемый сервер: " + qualityReport.Target);
            foreach (var item in new[] { ("Сервер в интернете", qualityReport.Internet), ("Роутер / выбранный шлюз", qualityReport.Gateway) })
            {
                var summary = NetworkMeasurements.Summarize(item.Item2);
                text.AppendLine().AppendLine(item.Item1);
                text.AppendLine($"Отправлено запросов: {summary.Sent}. Получено ответов: {summary.Received}.");
                text.AppendLine($"Без ответа: {summary.LossPercent:0.0}%. Среднее время ответа: {ReportNumber(summary.AverageMs)} мс.");
                text.AppendLine($"Минимум: {ReportNumber(summary.MinimumMs)} мс. Максимум: {ReportNumber(summary.MaximumMs)} мс.");
                text.AppendLine($"Изменение задержки между ответами (джиттер): {ReportNumber(summary.JitterMs)} мс.");
            }
            text.AppendLine("Пинг и джиттер: меньше — лучше. Отсутствие тестового ответа не всегда означает сбой интернета.");
            if (qualityReport.Route.Count > 0) text.AppendLine("Путь до сервера:").AppendLine(string.Join("\n", qualityReport.Route));
        }
        if (speedReport != null)
        {
            string result = speedReport.Outcome switch { "Completed" => "Замер завершён", "Cancelled" => "Замер отменён", _ => "Ошибка измерения" };
            text.AppendLine().AppendLine("Скорость до сервера Cloudflare");
            text.AppendLine($"Время: {speedReport.At.ToLocalTime():dd.MM.yyyy HH:mm:ss}. {result}.");
            text.AppendLine($"Скачивание: {(speedReport.DownloadMbps > 0 ? speedReport.DownloadMbps.ToString("0.0") : "—")} Мбит/с.");
            text.AppendLine($"Отправка: {(speedReport.UploadMbps > 0 ? speedReport.UploadMbps.ToString("0.0") : "—")} Мбит/с.");
            text.AppendLine($"Время ответа без нагрузки: {ReportNumber(speedReport.IdleMs)} мс; во время передачи: {ReportNumber(speedReport.LoadedMs)} мс.");
            text.AppendLine($"Получено: {speedReport.DownloadBytes / 1_000_000d:0.##} МБ. Отправлено полных блоков: {speedReport.UploadBytes / 1_000_000d:0.##} МБ.");
            text.AppendLine("Частично отправленный блок может не попасть в счётчик. Скорость зависит от сервера, маршрута и других загрузок.");
            if (speedReport.Note.Length > 0) text.AppendLine("Примечание: " + speedReport.Note);
        }
        SaveText("x2zapret-network.txt", text.ToString());
    }
    private static string ReportNumber(double? value) => value?.ToString("0.0") ?? "—";
    private void SaveText(string name, string text)
    {
        var dialog = new SaveFileDialog { Filter = "Текстовый отчёт (*.txt)|*.txt", FileName = name };
        if (dialog.ShowDialog(this) == true) try { AtomicFile.Write(dialog.FileName, text); } catch (Exception error) { ShowError(UserMessages.Error(error)); }
    }
    private void BrowseSpace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка для анализа занятого места" };
        if (dialog.ShowDialog(this) == true) SpacePathBox.Text = dialog.FolderName;
    }
    private void ParentSpace_Click(object sender, RoutedEventArgs e)
    {
        try { SpacePathBox.Text = Directory.GetParent(Path.GetFullPath(SpacePathBox.Text))?.FullName ?? SpacePathBox.Text; }
        catch (Exception error) { ShowError(UserMessages.Error(error)); }
    }
    internal async Task RunSpaceAsync()
    {
        if (busy || pcExporting) return;
        string root = SpacePathBox.Text;
        extendedRunning = true;
        await Execute(async token =>
        {
            try
            {
                SpaceFolderGrid.ItemsSource = SpaceFilesGrid.ItemsSource = null; spaceReport = null;
                var progress = new Progress<SpaceProgress>(p => SpaceStatusText.Text = $"Файлов: {p.Files:N0} · {p.Bytes / 1073741824d:0.00} ГиБ · пропущено: {p.Skipped:N0}");
                spaceReport = await Task.Run(() => SpaceAnalyzer.Scan(root, progress, token));
                RenderSpaceReport(spaceReport);
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            }
            finally { extendedRunning = false; }
        }, "Анализ занятого места…");
    }
    internal void RenderSpaceReport(SpaceScan report)
    {
        SpaceFolderGrid.ItemsSource = report.Folders.Select(x => new SpaceView(x, report.Bytes)).ToArray();
        SpaceFilesGrid.ItemsSource = report.LargestFiles.Select(x => new SpaceView(x, report.Bytes)).ToArray();
        SpaceStatusText.Text = $"{(report.Partial ? "Частичный результат" : "Готово")} · {report.Files:N0} файлов · {report.Bytes / 1073741824d:0.00} ГиБ · пропущено: {report.Skipped:N0}";
    }
    internal sealed record SpaceView(SpaceEntry Entry, long Total)
    {
        public string Path => Entry.Path; public string Size => Entry.Size;
        public double Percent => Total == 0 ? 0 : 100d * Entry.Bytes / Total;
        public string Share => Percent.ToString("0.0") + "%";
    }
    private void SpaceSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!extendedInitialized || e.AddedItems.Count == 0) return;
        if (ReferenceEquals(sender, SpaceFilesGrid)) SpaceFolderGrid.SelectedItem = null;
        else SpaceFilesGrid.SelectedItem = null;
    }
    private async void StartSpace_Click(object sender, RoutedEventArgs e) => await RunSpaceAsync();
    private async void SpaceFolder_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!busy && SpaceFolderGrid.SelectedItem is SpaceView view && view.Entry.Directory && view.Path != spaceReport?.Root)
        { SpacePathBox.Text = view.Path; await RunSpaceAsync(); }
    }
    private void OpenSpace_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var view = SpaceFilesGrid.SelectedItem as SpaceView ?? SpaceFolderGrid.SelectedItem as SpaceView;
        if (view == null) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(view.Path); Process.Start(start);
        }
        catch (Exception error) { ShowError(UserMessages.Error(error)); }
    }
    internal void PopulateExtendedPreview()
    {
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 90; i++) sensorHistory.Add(now.AddSeconds((i - 89) * 2), [new("/preview-cpu/0/temperature/0", "CPU · пример", "CPU Package", "Temperature", 52 + Math.Sin(i * .16) * 7 + Math.Cos(i * .4) * 2),
            new("/gpu-preview/0/temperature/0", "GPU · пример", "GPU Core", "Temperature", 77 + Math.Sin(i * .1) * 4), new("x2.cpu.load", "Компьютер", "Загрузка процессора", "Load", 24 + Math.Sin(i * .3) * 10),
            new("x2.memory.load", "Компьютер", "Загрузка памяти", "Load", 61)]);
        RenderSensorHistory(); SensorStatusText.Text = "Пример интерфейса · демонстрационные показатели";
        RenderSpeedReport(new(now, 487.6, 94.8, 18.2, 34.7, 31_000_000, 31_000_000, "Completed", ""));
        RenderQualityReport(new("1.1.1.1 · пример", [new(1, 18), new(2, 20), new(3, 19)], [new(1, 1), new(2, 2)], []));
        NetworkStageText.Text = "Пример интерфейса · демонстрационные показатели";
    }
}
