using System.Windows;
using System.Windows.Controls;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class MainWindow
{
    internal sealed record StartupRow(StartupEntry Entry)
    {
        public string Name => Entry.Name;
        public bool Enabled => Entry.Enabled;
        public string Command => Entry.Command;
        public string Detail => Entry.Scope + " · " + StartupApproval.Executable(Entry.Command);
    }

    internal void ShowStartupTool()
    {
        if (diskBenchmarkRunning || extendedRunning) return;
        HideExtendedTools();
        PcInfoPanel.Visibility = ShutdownToolPanel.Visibility = DiskToolPanel.Visibility = Visibility.Collapsed;
        StartupToolPanel.Visibility = Visibility.Visible;
        HighlightTool(StartupToolButton); PageScroll.ScrollToTop();
        RefreshStartupList();
    }
    private void SelectStartupTool_Click(object sender, RoutedEventArgs e) => ShowStartupTool();
    private void RefreshStartup_Click(object sender, RoutedEventArgs e) => RefreshStartupList();

    private void RefreshStartupList()
    {
        try
        {
            var rows = StartupPrograms.List().Select(e => new StartupRow(e)).ToArray();
            StartupList.ItemsSource = rows;
            StartupStatusText.Text = rows.Length == 0 ? "Программ в автозагрузке не найдено." :
                $"Программ: {rows.Length} · запускаются с Windows: {rows.Count(r => r.Enabled)}";
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { StartupStatusText.Text = "Не удалось прочитать список автозагрузки: " + error.Message; }
    }

    private void StartupItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StartupRow row } box) return;
        bool enabled = box.IsChecked == true;
        if (preview) { box.IsChecked = !enabled; return; }
        try
        {
            StartupPrograms.SetEnabled(row.Entry, enabled);
            Log($"Автозагрузка: «{row.Name}» {(enabled ? "включена" : "отключена")}.");
            RefreshStartupList();
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { box.IsChecked = !enabled; ShowError(UserMessages.Error(error)); }
    }
}
