using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private void HighlightTool(Button active)
    {
        foreach (var button in new[] { PcToolButton, TimerToolButton, DiskToolButton, SensorToolButton, NetworkToolButton, SpaceToolButton })
            button.Style = (Style)FindResource(button == active ? "ChipActive" : "Chip");
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
        if (tool == "Space" && string.IsNullOrWhiteSpace(SpacePathBox.Text)) SpacePathBox.Text = Path.GetPathRoot(Environment.SystemDirectory)!;
    }
    private void SelectExtendedTool_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string tool }) ShowExtendedTool(tool); }
    private void UpdateExtendedControls()
    {
        if (!extendedInitialized) return;
        foreach (var button in new[] { PcToolButton, TimerToolButton, DiskToolButton, SensorToolButton, NetworkToolButton, SpaceToolButton })
            button.IsEnabled = !diskBenchmarkRunning && !extendedRunning;
        QualityStartButton.IsEnabled = SpeedStartButton.IsEnabled = SpaceStartButton.IsEnabled = !busy && !pcExporting;
        NetworkCancelButton.IsEnabled = SpeedCancelButton.IsEnabled = SpaceCancelButton.IsEnabled = extendedRunning;
        SpacePathBox.IsEnabled = SpaceBrowseButton.IsEnabled = SpaceParentButton.IsEnabled = !busy;
        QualityTargetBox.IsEnabled = QualityGatewayPicker.IsEnabled = QualityRouteCheck.IsEnabled = SpeedModePicker.IsEnabled = !busy;
        SaveNetworkButton.IsEnabled = !busy && (qualityReport != null || speedReport != null);
        SpaceOpenButton.IsEnabled = !busy;
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
        RenderSensorChart();
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
        bool route = QualityRouteCheck.IsChecked == true;
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
    }
    internal async Task RunSpeedAsync()
    {
        if (busy || pcExporting) return;
        bool detailed = SpeedModePicker.SelectedIndex == 1;
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
        for (int i = 0; i < 90; i++) sensorHistory.Add(now.AddSeconds((i - 89) * 2), [new("preview.cpu", "CPU · пример", "Температура", "Temperature", 52 + Math.Sin(i * .16) * 7 + Math.Cos(i * .4) * 2)]);
        RenderSensorHistory(); SensorStatusText.Text = "Пример интерфейса · демонстрационные показатели";
        RenderSpeedReport(new(now, 487.6, 94.8, 18.2, 34.7, 31_000_000, 31_000_000, "Completed", ""));
        RenderQualityReport(new("1.1.1.1 · пример", [new(1, 18), new(2, 20), new(3, 19)], [new(1, 1), new(2, 2)], []));
        NetworkStageText.Text = "Пример интерфейса · демонстрационные показатели";
    }
}
