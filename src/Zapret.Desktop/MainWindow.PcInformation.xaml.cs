using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class MainWindow
{
    private PcInformationSession? pcSession;
    private PcSection? pcSection;
    private CancellationTokenSource? pcCancellation;
    private int pcRequest;
    private bool pcInitialized, pcExporting, pcSampling;
    private readonly PcLiveMonitor pcMonitor = new();
    private readonly DispatcherTimer pcClock = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitializePcInformation(IPcInformationSource? source)
    {
        pcSession = new(source ?? new WindowsPcInformation());
        PcCategoryPicker.ItemsSource = PcCategory.All;
        PcCategoryPicker.SelectedIndex = 0;
        pcInitialized = true;
        PcInfoPanel.IsVisibleChanged += async (_, _) =>
        {
            if (PcInfoPanel.IsVisible)
            {
                if (!preview) { pcClock.Start(); await SamplePcAsync(); }
                if (!preview && pcSection == null) await LoadPcSectionAsync(false);
            }
            else { pcClock.Stop(); pcCancellation?.Cancel(); }
        };
        pcClock.Tick += async (_, _) => await SamplePcAsync();
        Closed += (_, _) => { pcClock.Stop(); pcCancellation?.Cancel(); };
    }

    internal void ShowShutdownTool()
    {
        PcInfoPanel.Visibility = Visibility.Collapsed; ShutdownToolPanel.Visibility = Visibility.Visible;
        PcToolButton.Style = (Style)FindResource(typeof(Button));
        TimerToolButton.Style = (Style)FindResource("Primary");
        PageScroll.ScrollToTop();
    }
    internal void ShowPcTool()
    {
        ShutdownToolPanel.Visibility = Visibility.Collapsed; PcInfoPanel.Visibility = Visibility.Visible;
        PcToolButton.Style = (Style)FindResource("Primary");
        TimerToolButton.Style = (Style)FindResource(typeof(Button));
        PageScroll.ScrollToTop();
    }
    private void SelectPcTool_Click(object sender, RoutedEventArgs e) => ShowPcTool();
    private void SelectTimerTool_Click(object sender, RoutedEventArgs e) => ShowShutdownTool();
    private async void PcCategory_Changed(object sender, SelectionChangedEventArgs e)
    { if (pcInitialized && !pcExporting && (!preview || IsVisible)) await LoadPcSectionAsync(false); }
    private async void RefreshPc_Click(object sender, RoutedEventArgs e) => await LoadPcSectionAsync(true);
    private void CancelPc_Click(object sender, RoutedEventArgs e) => pcCancellation?.Cancel();
    private void FilterPc_Changed(object sender, RoutedEventArgs e) { if (pcInitialized) ApplyPcFilter(); }
    private void SearchPc_Changed(object sender, TextChangedEventArgs e) { if (pcInitialized) ApplyPcFilter(); }

    internal async Task LoadPcSectionAsync(bool refresh)
    {
        if (pcSession == null || pcExporting || PcCategoryPicker.SelectedItem is not PcCategory category) return;
        pcCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pcCancellation = cancellation;
        int request = ++pcRequest;
        pcSection = null; PcRowsGrid.ItemsSource = null; PcWarningText.Text = "";
        PcStatusText.Text = "Сбор: " + category.Title + "…";
        PcCancelButton.IsEnabled = true;
        try
        {
            var section = await pcSession.ReadAsync(category.Id, refresh, cancellation.Token);
            if (request != pcRequest) return;
            pcSection = section; ApplyPcFilter();
        }
        catch (OperationCanceledException) { if (request == pcRequest) PcStatusText.Text = "Сбор отменён. Нажмите «Обновить», чтобы повторить."; }
        catch (Exception error) { if (request == pcRequest) PcStatusText.Text = "Не удалось собрать раздел: " + error.Message; }
        finally
        {
            if (request == pcRequest) { PcCancelButton.IsEnabled = false; pcCancellation = null; }
        }
    }
    internal void ApplyPcFilter()
    {
        if (pcSection == null) return;
        var rows = PcInformation.Visible(pcSection, PcSearchBox.Text, PcPrivateCheck.IsChecked == true);
        PcRowsGrid.ItemsSource = rows;
        PcStatusText.Text = $"Показано: {rows.Count}/{pcSection.Rows.Count} · Снимок {pcSection.CapturedAt.ToLocalTime():HH:mm:ss}";
        PcWarningText.Text = string.Join("\n", pcSection.Warnings);
    }
    private async void ExportPc_Click(object sender, RoutedEventArgs e)
    {
        if (pcSession == null || pcExporting) return;
        var dialog = new SaveFileDialog { Filter = "Текстовый отчёт (*.txt)|*.txt", FileName = "x2zapret-PC-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
        if (dialog.ShowDialog(this) != true) return;
        pcCancellation?.Cancel(); ++pcRequest;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pcCancellation = cancellation; pcExporting = true;
        bool includePrivate = PcPrivateCheck.IsChecked == true;
        PcExportButton.IsEnabled = PcRefreshButton.IsEnabled = PcCategoryPicker.IsEnabled = false;
        PcCancelButton.IsEnabled = true;
        try
        {
            var sections = new List<PcSection>();
            foreach (var category in PcCategory.All)
            {
                PcStatusText.Text = $"Отчёт {sections.Count + 1}/{PcCategory.All.Count}: {category.Title}…";
                sections.Add(await pcSession.ReadAsync(category.Id, true, cancellation.Token));
            }
            cancellation.Token.ThrowIfCancellationRequested();
            string report = PcInformation.Report(sections, includePrivate);
            await Task.Run(() => { cancellation.Token.ThrowIfCancellationRequested(); AtomicFile.Write(dialog.FileName, report); }, cancellation.Token);
            PcStatusText.Text = "Полный отчёт сохранён. Недоступные данные отмечены в примечаниях.";
        }
        catch (OperationCanceledException) { PcStatusText.Text = "Сохранение отчёта отменено."; }
        catch (Exception error) { PcStatusText.Text = "Не удалось сохранить отчёт: " + error.Message; }
        finally
        {
            pcCancellation = null; pcExporting = false; PcCancelButton.IsEnabled = false;
            PcExportButton.IsEnabled = PcRefreshButton.IsEnabled = PcCategoryPicker.IsEnabled = true;
        }
    }
    private async Task SamplePcAsync()
    {
        if (pcSampling || !PcInfoPanel.IsVisible) return;
        pcSampling = true;
        try
        {
            var sample = await Task.Run(pcMonitor.Read);
            if (!PcInfoPanel.IsVisible) return;
            PcCpuText.Text = sample.CpuPercent.HasValue ? sample.CpuPercent.Value.ToString("0", CultureInfo.CurrentCulture) + "%" : "…";
            PcMemoryText.Text = sample.MemoryPercent.ToString("0") + "%";
            PcMemoryDetail.Text = $"{sample.UsedMemory / 1073741824d:0.#} / {sample.TotalMemory / 1073741824d:0.#} ГиБ";
            PcCpuBar.Value = sample.CpuPercent ?? 0; PcMemoryBar.Value = sample.MemoryPercent;
            PcNetworkText.Text = $"↓ {sample.ReceiveBytesPerSecond / 1048576:0.00}   ↑ {sample.SendBytesPerSecond / 1048576:0.00}";
        }
        catch (Exception error) when (error is InvalidOperationException or System.Net.NetworkInformation.NetworkInformationException)
        { PcCpuText.Text = PcMemoryText.Text = "—"; PcNetworkText.Text = "Недоступно"; }
        finally { pcSampling = false; }
    }
}
