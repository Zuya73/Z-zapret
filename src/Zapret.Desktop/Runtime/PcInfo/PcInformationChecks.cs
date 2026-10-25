using System.Windows;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class PcInformationChecks
{
    internal static IPcInformationSource CreateSource() => new PreviewSource();

    internal static async Task RunAsync(MainWindow window, string destination)
    {
        var checks = new List<string>();
        window.SelectPage("Tools"); window.ShowPcTool(); await Task.Delay(450);
        await window.LoadPcSectionAsync(true);
        Require(window.PcInfoPanel.IsVisible && window.ShutdownToolPanel.Visibility == Visibility.Collapsed, "PC information is reachable inside X2 Tools");
        Require(window.PcCategoryPicker.Items.Count == 16 && window.PcRowsGrid.Items.Count == 6, "All hardware and software categories are available");
        Require(!window.PcRowsGrid.Items.Cast<PcRow>().Any(r => r.Value.Contains("PRIVATE-TEST")), "Private values start hidden in the grid and clipboard source");
        window.PcSearchBox.Text = "PRIVATE-TEST";
        Require(window.PcRowsGrid.Items.Count == 0, "Search does not leak hidden values");
        window.PcPrivateCheck.IsChecked = true;
        Require(window.PcRowsGrid.Items.Count == 1, "Explicit consent reveals private values");
        window.PcPrivateCheck.IsChecked = false; window.PcSearchBox.Text = "Ryzen";
        Require(window.PcRowsGrid.Items.Count == 1, "Search filters rows immediately");
        window.ShowShutdownTool();
        Require(window.ShutdownToolPanel.IsVisible && !window.PcInfoPanel.IsVisible, "Shutdown timer remains a separate working tool");
        window.ShowPcTool();
        Require(window.PcSearchBox.Text == "Ryzen" && window.PcRowsGrid.Items.Count == 1, "Switching tools preserves PC search and data");
        window.PcSearchBox.Text = "";
        window.PcCategoryPicker.SelectedItem = PcCategory.Find("cpu");
        await Task.Delay(60);
        window.PcCategoryPicker.SelectedItem = PcCategory.Find("memory");
        await WaitFor(() => !window.PcCancelButton.IsEnabled);
        Require(window.PcRowsGrid.Items.Cast<PcRow>().All(r => r.Device == "Память"), "Rapid category changes show only the last requested result");
        window.PcCategoryPicker.SelectedItem = PcCategory.Find("sensors");
        await WaitFor(() => !window.PcCancelButton.IsEnabled);
        Require(window.PcRowsGrid.Items.Count == 0 && window.PcWarningText.Text.Contains("недоступны"), "Missing sensor providers show an explanation without invented readings");
        window.PcCategoryPicker.SelectedItem = PcCategory.Find("summary");
        await WaitFor(() => !window.PcCancelButton.IsEnabled);
        InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "pc-information-preview.png"));
        var monitor = new PcLiveMonitor(); var first = monitor.Read(); await Task.Delay(150); var second = monitor.Read();
        Require(first.CpuPercent == null && second.CpuPercent is >= 0 and <= 100 && second.TotalMemory > 0 && second.MemoryPercent is >= 0 and <= 100,
            "Native live CPU and memory counters return valid values");
        var native = new WindowsPcInformation();
        var counts = new List<object>();
        foreach (var category in PcCategory.All)
        {
            var section = await native.ReadAsync(category.Id, CancellationToken.None);
            Require(section.Rows.Count > 0 || section.Warnings.Count > 0, "Native collector returns data or an explicit availability note: " + category.Id);
            Require(!section.Warnings.Any(w => w.StartsWith("Сбор раздела завершён частично")), "Windows PowerShell collector completes its category: " + category.Id);
            if (category.Id is "summary" or "cpu" or "windows" or "software") Require(section.Rows.Count > 0, "Required Windows inventory is available: " + category.Id);
            counts.Add(new { category = category.Id, rows = section.Rows.Count, warnings = section.Warnings.Count });
        }
        AtomicFile.WriteJson(destination, new { passed = checks.Count, checks, native = counts });
        void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
    }
    private static async Task WaitFor(Func<bool> predicate)
    {
        for (int i = 0; i < 100; i++) { if (predicate()) return; await Task.Delay(50); }
        throw new TimeoutException("PC information UI did not finish its request.");
    }
    private sealed class PreviewSource : IPcInformationSource
    {
        public async Task<PcSection> ReadAsync(string category, CancellationToken token)
        {
            await Task.Delay(category == "cpu" ? 800 : 30, token);
            if (category == "sensors") return new(category, PcCategory.Find(category).Title, DateTimeOffset.UtcNow, [], ["Датчики недоступны: нет источника показаний."]);
            if (category != "summary") return new(category, PcCategory.Find(category).Title, DateTimeOffset.UtcNow, [new("Память", "Модуль", "16 ГиБ DDR5")], []);
            return new(category, PcCategory.Find(category).Title, DateTimeOffset.UtcNow,
                [new("Процессор", "Модель", "AMD Ryzen 7 7800X3D"), new("Процессор", "Ядра / потоки", "8 / 16"),
                 new("Память", "Объём", "32 ГиБ DDR5"), new("Видеокарта", "Модель", "NVIDIA GeForce RTX 4070 SUPER"),
                 new("Windows", "Система", "Windows 11 Pro · x64"), new("Компьютер", "Имя ПК", "PRIVATE-TEST", true)], ["Пример интерфейса. Реальные сведения берутся с вашего компьютера."]);
        }
    }
}
