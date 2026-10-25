using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class MainWindow
{
    private IDiskBenchmarkRunner? diskRunner;
    private DiskBenchmarkReport? diskReport;
    private readonly List<DiskMeasurement> diskMeasurements = new();
    private readonly List<DiskBenchmarkRow> diskRows = new();
    private bool diskBenchmarkRunning;
    private int diskRun;

    private void InitializeDiskTool(IDiskBenchmarkRunner? runner)
    {
        diskRunner = runner ?? new DiskBenchmarkRunner();
        foreach (var profile in DiskBenchmarkCase.All) diskRows.Add(new(profile));
        DiskResultGrid.ItemsSource = diskRows;
        UpdateDiskControls();
        Closed += (_, _) => operationCancellation?.Cancel();
    }
    internal void ShowDiskTool()
    {
        if (busy || extendedRunning) return;
        if (pcExporting) { StatusText.Text = "Дождитесь завершения сбора информации или отмените его."; return; }
        HideExtendedTools();
        PcInfoPanel.Visibility = ShutdownToolPanel.Visibility = Visibility.Collapsed;
        DiskToolPanel.Visibility = Visibility.Visible;
        PcToolButton.Style = TimerToolButton.Style = (Style)FindResource(typeof(Button));
        DiskToolButton.Style = (Style)FindResource("Primary");
        PageScroll.ScrollToTop();
        if (DiskDrivePicker.ItemsSource == null) RefreshDiskDrives();
        else UpdateDiskControls();
    }
    private void SelectDiskTool_Click(object sender, RoutedEventArgs e) => ShowDiskTool();
    private void DiskDrive_Changed(object sender, SelectionChangedEventArgs e) { if (diskRunner != null) UpdateDiskControls(); }
    private void RefreshDisks_Click(object sender, RoutedEventArgs e) => RefreshDiskDrives();
    private void RefreshDiskDrives()
    {
        string? previous = (DiskDrivePicker.SelectedItem as DiskDriveChoice)?.Root;
        var choices = new List<DiskDriveChoice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not DriveType.Fixed and not DriveType.Removable || !drive.IsReady) continue;
                choices.Add(new(drive.RootDirectory.FullName, $"{drive.RootDirectory.FullName} · {drive.DriveFormat} · {drive.AvailableFreeSpace / 1073741824d:0.#} ГиБ свободно"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        DiskDrivePicker.ItemsSource = choices;
        DiskDrivePicker.SelectedItem = choices.FirstOrDefault(x => x.Root == previous) ?? choices.FirstOrDefault(x => x.Root == Path.GetPathRoot(Environment.SystemDirectory)) ?? choices.FirstOrDefault();
        UpdateDiskControls();
    }
    private void UpdateDiskControls()
    {
        DiskStartButton.IsEnabled = !busy && !pcExporting && DiskDrivePicker.SelectedItem != null;
        DiskCancelButton.IsEnabled = diskBenchmarkRunning;
        DiskDrivePicker.IsEnabled = DiskSizePicker.IsEnabled = DiskModePicker.IsEnabled = DiskRefreshButton.IsEnabled = !busy;
        DiskExportButton.IsEnabled = !diskBenchmarkRunning && diskReport != null;
        PcToolButton.IsEnabled = TimerToolButton.IsEnabled = !diskBenchmarkRunning;
    }
    internal async Task RunDiskBenchmarkAsync()
    {
        if (busy || pcExporting || diskRunner == null || DiskDrivePicker.SelectedItem is not DiskDriveChoice drive) return;
        var runner = diskRunner;
        long size = DiskSizePicker.SelectedIndex switch { 0 => 256 * DiskBenchmarkOptions.MiB, 2 => 4096 * DiskBenchmarkOptions.MiB, _ => 1024 * DiskBenchmarkOptions.MiB };
        var options = new DiskBenchmarkOptions(size, DiskModePicker.SelectedIndex == 1 ? 5 : 3, DiskModePicker.SelectedIndex == 1 ? 3 : 1);
        int run = ++diskRun;
        diskReport = null; diskMeasurements.Clear(); RenderDiskResults(); diskBenchmarkRunning = true; UpdateDiskControls();
        var elapsed = Stopwatch.StartNew();
        await Execute(async token =>
        {
            OperationProgress.IsIndeterminate = false;
            var progress = new Progress<DiskBenchmarkProgress>(p =>
            {
                if (run != diskRun || !diskBenchmarkRunning) return;
                DiskStageText.Text = p.Stage;
                DiskElapsedText.Text = elapsed.Elapsed.ToString(@"mm\:ss");
                DiskProgress.Value = p.Percent; OperationProgress.Value = p.Percent;
                ProgressCounter.Text = p.Completed + " / " + p.Total;
                if (p.Measurement != null) { diskMeasurements.Add(p.Measurement); RenderDiskResults(); }
            });
            try
            {
                diskReport = await Task.Run(() => runner.RunAsync(drive.Root, options, progress, token));
                diskMeasurements.Clear(); diskMeasurements.AddRange(diskReport.Measurements); RenderDiskResults();
                DiskProgress.Value = diskReport.Outcome == "Completed" ? 100 : DiskProgress.Value;
                DiskStageText.Text = diskReport.Outcome switch { "Completed" => diskReport.CleanupNote.Length == 0 ? "Замер завершён. Тестовый файл удалён." : "Замер завершён.", "Cancelled" => "Замер отменён. Выполненные измерения сохранены.", _ => "Ошибка: " + diskReport.Error };
                if (diskReport.CleanupNote.Length > 0) DiskStageText.Text += " " + diskReport.CleanupNote;
                DiskElapsedText.Text = elapsed.Elapsed.ToString(@"mm\:ss");
                Log("X2 Tools: замер диска " + drive.Root + ": " + diskReport.Outcome + ".");
                if (diskReport.Outcome == "Cancelled") throw new OperationCanceledException(token);
                if (diskReport.Outcome == "Failed") throw new IOException(diskReport.Error);
            }
            finally { diskBenchmarkRunning = false; UpdateDiskControls(); }
        }, "Измерение скорости накопителя…");
        UpdateDiskControls();
    }
    private async void StartDisk_Click(object sender, RoutedEventArgs e) => await RunDiskBenchmarkAsync();
    private void CancelDisk_Click(object sender, RoutedEventArgs e) { if (diskBenchmarkRunning) operationCancellation?.Cancel(); }
    private void RenderDiskResults()
    {
        foreach (var row in diskRows)
            row.Update(DiskBenchmarkPolicy.Aggregate(diskMeasurements.Where(x => x.CaseId == row.Profile.Id && !x.Write)),
                DiskBenchmarkPolicy.Aggregate(diskMeasurements.Where(x => x.CaseId == row.Profile.Id && x.Write)));
    }
    private void SaveDisk_Click(object sender, RoutedEventArgs e)
    {
        if (diskReport == null || diskBenchmarkRunning) return;
        var dialog = new SaveFileDialog { Filter = "Текстовый отчёт (*.txt)|*.txt", FileName = "x2zapret-disk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
        if (dialog.ShowDialog(this) == true) AtomicFile.Write(dialog.FileName, DiskBenchmarkPolicy.Export(diskReport));
    }
    internal sealed record DiskDriveChoice(string Root, string Description)
    { public override string ToString() => Description; }
    internal sealed class DiskBenchmarkRow(DiskBenchmarkCase profile) : INotifyPropertyChanged
    {
        public DiskBenchmarkCase Profile { get; } = profile;
        public string ReadSpeed { get; private set; } = "—";
        public string WriteSpeed { get; private set; } = "—";
        public string ReadDetails { get; private set; } = "Ожидание";
        public string WriteDetails { get; private set; } = "Ожидание";
        public string ReadTooltip { get; private set; } = "";
        public string WriteTooltip { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Update(DiskMeasurement? read, DiskMeasurement? write)
        {
            ReadSpeed = Speed(read); WriteSpeed = Speed(write);
            ReadDetails = Details(read); WriteDetails = Details(write);
            ReadTooltip = Tooltip(read); WriteTooltip = Tooltip(write);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        private static string Speed(DiskMeasurement? value) => value == null ? "—" : value.MegabytesPerSecond.ToString("0.00");
        private static string Latency(double value) => value == 0 ? "<0.001" : value.ToString("0.###");
        private static string Details(DiskMeasurement? value) => value == null ? "Ожидание" : $"{value.Iops:0} IOPS · {Latency(value.AverageLatencyMs)} мс";
        private static string Tooltip(DiskMeasurement? value) => value == null ? "Ещё не измерено" : $"Средняя задержка: {Latency(value.AverageLatencyMs)} мс\nP95 худшего прохода: {(value.P95LatencyMs.HasValue ? Latency(value.P95LatencyMs.Value) : "—")} мс\nЗавершено проходов: {value.Pass}";
    }
}
