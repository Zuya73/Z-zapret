using System.Windows;
using System.Windows.Controls;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class MainWindow
{
    private const string RecycleId = "recycle-bin";
    private IReadOnlyList<CleanupTarget> cleanupTargets = [];
    private readonly Dictionary<string, (CheckBox Box, TextBlock Size)> cleanupRows = new();
    private readonly Dictionary<string, long> cleanupSizes = new();

    private void InitializeCleanup()
    {
        cleanupTargets = DiskCleanup.Targets(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        foreach (var (id, title, description) in cleanupTargets.Select(t => (t.Id, t.Title, t.Description))
            .Append((RecycleId, "Корзина", "Файлы, которые вы уже удалили. Восстановить их после очистки будет нельзя.")))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            var note = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); // Follows theme switches.
            text.Children.Add(note);
            var box = new CheckBox { Content = text, IsChecked = id != RecycleId, Margin = new Thickness(0, 8, 16, 8), VerticalContentAlignment = VerticalAlignment.Top };
            var size = new TextBlock { Text = "—", FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(size, 1);
            row.Children.Add(box); row.Children.Add(size);
            CleanupList.Children.Add(row);
            cleanupRows[id] = (box, size);
        }
    }

    private static string CleanupSize(long bytes) => bytes < 1024 * 1024 ? (bytes == 0 ? "0" : "< 1 МиБ") :
        bytes < 1024L * 1024 * 1024 ? $"{bytes / 1048576d:0} МиБ" : $"{bytes / 1073741824d:0.0} ГиБ";

    private async void EstimateCleanup_Click(object sender, RoutedEventArgs e) => await EstimateCleanupAsync();
    internal async Task EstimateCleanupAsync()
    {
        if (busy || preview) return;
        await Execute(async token =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var target in cleanupTargets)
            {
                cleanupRows[target.Id].Size.Text = "…";
                var estimate = await Task.Run(() => DiskCleanup.Measure(target, now, token), token);
                cleanupSizes[target.Id] = estimate.Bytes; cleanupRows[target.Id].Size.Text = CleanupSize(estimate.Bytes);
            }
            var bin = RecycleBin.Query();
            cleanupSizes[RecycleId] = bin.Bytes; cleanupRows[RecycleId].Size.Text = CleanupSize(bin.Bytes);
            CleanupStatusText.Text = $"Можно освободить до {CleanupSize(cleanupSizes.Values.Sum())}. Отметьте нужное и нажмите «Очистить выбранное».";
        }, "Считаем, сколько места можно освободить…");
        CleanupButton.IsEnabled = cleanupSizes.Count > 0 && !busy;
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || preview) return;
        var chosen = cleanupRows.Where(r => r.Value.Box.IsChecked == true).Select(r => r.Key).ToArray();
        if (chosen.Length == 0) { CleanupStatusText.Text = "Отметьте хотя бы один пункт."; return; }
        string list = string.Join(", ", chosen.Select(id => id == RecycleId ? "корзина" : cleanupTargets.First(t => t.Id == id).Title.ToLowerInvariant()));
        if (MessageBox.Show(this, $"Удалить: {list}? Файлы будут удалены без возможности восстановления.", "Освободить место",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        long freed = 0;
        await Execute(async token =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var target in cleanupTargets.Where(t => chosen.Contains(t.Id)))
            {
                CleanupStatusText.Text = "Очищаем: " + target.Title.ToLowerInvariant() + "…";
                freed += await Task.Run(() => DiskCleanup.Clean(target, now, token), token);
            }
            if (chosen.Contains(RecycleId)) { freed += RecycleBin.Query().Bytes; await Task.Run(RecycleBin.Empty, token); }
            Log("Освобождение места: удалено " + CleanupSize(freed) + ".");
        }, "Освобождаем место…");
        await EstimateCleanupAsync();
        CleanupStatusText.Text = $"Освобождено {CleanupSize(freed)}. Файлы, занятые другими программами, пропущены.";
    }

    private void RefreshSpaceDrives()
    {
        SpaceDrivesPanel.Children.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not DriveType.Fixed and not DriveType.Removable || !drive.IsReady) continue;
                string root = drive.RootDirectory.FullName;
                var button = new Button
                {
                    Content = $"{root.TrimEnd('\\')}  ·  свободно {drive.AvailableFreeSpace / 1073741824d:0} из {drive.TotalSize / 1073741824d:0} ГиБ",
                    Tag = root, Style = (Style)FindResource("Chip"), ToolTip = "Проанализировать диск " + root
                };
                button.Click += async (_, _) => { if (busy) return; SpacePathBox.Text = root; await RunSpaceAsync(); };
                SpaceDrivesPanel.Children.Add(button);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
