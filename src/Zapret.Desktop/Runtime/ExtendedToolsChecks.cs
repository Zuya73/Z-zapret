using System.Net;
using System.Net.Http;
using System.Windows.Controls;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class ExtendedToolsChecks
{
    internal static async Task RunAsync(MainWindow window, string destination)
    {
        var checks = new List<string>();
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var loopback = await NetworkTools.QualityAsync("127.0.0.1", IPAddress.Loopback, true, new Progress<string>(), deadline.Token, packetCount: 3);
            Require(loopback.Internet.Count == 3 && loopback.Gateway.Count == 3 && loopback.Internet.All(x => x.Milliseconds.HasValue) && loopback.Route.Count == 1,
                "Native Windows ICMP and route checks work against loopback without external traffic");
        }
        try { await NetworkTools.ResolveAsync("https://example.com/path", CancellationToken.None); throw new Exception("Invalid target accepted"); }
        catch (ArgumentException) { checks.Add("Network target validation rejects URLs and command fragments"); }
        var transport = new MeasurementHandler();
        var result = await NetworkTools.SpeedAsync(false, new Progress<string>(), CancellationToken.None, transport);
        Require(result.Outcome == "Completed" && result.DownloadMbps > 0 && result.UploadMbps > 0, "HTTP transfer measurements return download and upload throughput");
        Require(result.DownloadBytes == 31_000_000 && result.UploadBytes == 31_000_000 && transport.Uploaded == 31_000_000, "Quick mode respects the 62 MB payload budget and sends the measured upload bytes");
        Require(transport.Urls.All(u => u.Host == "speed.cloudflare.com" && u.Scheme == "https") && result.IdleMs.HasValue,
            "Speed measurements use only the documented fixed HTTPS endpoints");
        var failed = await NetworkTools.SpeedAsync(false, new Progress<string>(), CancellationToken.None, new MeasurementHandler { Fail = true });
        Require(failed.Outcome == "Failed" && failed.DownloadMbps == 0 && failed.UploadMbps == 0, "HTTP service errors never become fabricated throughput");
        var wrongSize = await NetworkTools.SpeedAsync(false, new Progress<string>(), CancellationToken.None, new MeasurementHandler { Short = true });
        Require(wrongSize.Outcome == "Failed", "An incomplete download payload is rejected");
        using var cancel = new CancellationTokenSource();
        var cancelled = await NetworkTools.SpeedAsync(false, new Progress<string>(), cancel.Token, new MeasurementHandler { CancelUpload = cancel });
        Require(cancelled.Outcome == "Cancelled" && cancelled.DownloadMbps > 0 && cancelled.UploadMbps == 0, "Cancelling upload preserves a completed download without claiming unfinished upload throughput");
        using (var probe = new ServiceHealthProbe(new HealthHandler(false)))
        {
            var health = await probe.CheckAsync(CancellationToken.None);
            Require(health.Checks.Count == 6 && health.ServicesAvailable && health.InternetAvailable, "Service health evaluates main sites, CDN endpoints and independent controls");
        }
        using (var probe = new ServiceHealthProbe(new HealthHandler(true)))
        {
            var health = await probe.CheckAsync(CancellationToken.None);
            Require(!health.ServicesAvailable && health.ConfirmedFailure, "A responding CDN does not mask failure of the main service");
        }
        window.SelectPage("Tools"); window.PopulateExtendedPreview();
        window.ShowExtendedTool("Sensors"); await Task.Delay(450);
        Require(window.SensorToolPanel.IsVisible && window.SensorPicker.Items.Count > 0 && window.SensorPlot.ActualHeight >= 190, "Sensor history renders in a dedicated X2 Tools panel");
        InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "sensor-history-preview.png"));
        window.ShowExtendedTool("Network"); await Task.Delay(100);
        Require(window.NetworkToolPanel.IsVisible && !window.SensorToolPanel.IsVisible && window.QualityRowsGrid.Items.Count == 2, "Network tools show separate external-node and gateway results");
        InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "network-tools-preview.png"));
        window.ShowExtendedTool("Space");
        string root = Path.Combine(Path.GetTempPath(), "x2-space-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "sub", "test.bin"), new byte[500]);
            window.SpacePathBox.Text = root;
            await window.RunSpaceAsync();
            await Task.Delay(100); window.UpdateLayout();
            Require(window.SpaceFolderGrid.Items.Count == 1 && window.SpaceFilesGrid.Items.Count == 1 && window.SpaceStartButton.IsEnabled && !window.SpaceCancelButton.IsEnabled,
                "Space analysis runs asynchronously and releases operation controls");
            Require(window.SpaceFolderGrid.Columns[0].GetCellContent(window.SpaceFolderGrid.Items[0]) is TextBlock { Text: var folderPath } && folderPath == Path.Combine(root, "sub") &&
                window.SpaceFilesGrid.Columns[0].GetCellContent(window.SpaceFilesGrid.Items[0]) is TextBlock { Text: var filePath } && filePath == Path.Combine(root, "sub", "test.bin"),
                "Folder and file paths bind to visible WPF cells after analysis");
            Require(File.Exists(Path.Combine(root, "sub", "test.bin")), "Space analysis leaves existing files untouched");
            window.RenderSpaceReport(new(@"C:\", 750_000_000_000, 153_270, 4, false,
                [new(@"C:\Games", 420_000_000_000, true), new(@"C:\Users", 180_000_000_000, true), new(@"C:\Windows", 95_000_000_000, true), new(@"C:\Program Files", 55_000_000_000, true)],
                [new(@"C:\Games\Example\data.pak", 25_000_000_000, false)]));
            window.SpaceStatusText.Text = "Пример интерфейса · демонстрационные показатели";
            window.SpacePathBox.Text = @"C:\";
            await Task.Delay(100); window.UpdateLayout();
            InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "space-analyzer-preview.png"));
        }
        finally { Directory.Delete(root, true); }
        window.ShowDiskTool(); Require(window.DiskToolPanel.IsVisible && !window.SpaceToolPanel.IsVisible, "Disk benchmark remains reachable");
        window.ShowPcTool(); Require(window.PcInfoPanel.IsVisible && !window.NetworkToolPanel.IsVisible, "PC information remains reachable");
        window.ShowShutdownTool(); Require(window.ShutdownToolPanel.IsVisible, "Shutdown timer remains reachable");
        window.ShowStartupTool(); await Task.Delay(300); window.UpdateLayout();
        Require(window.StartupToolPanel.IsVisible && !window.PcInfoPanel.IsVisible && !window.ShutdownToolPanel.IsVisible && window.StartupStatusText.Text.Length > 0,
            "Autostart programs are listed read-only in their own tool");
        InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "startup-programs-preview.png"));
        window.SelectPage("Strategies"); await Task.Delay(450); window.UpdateLayout();
        var profileItem = window.StrategiesGrid.Items[0];
        var profileText = window.StrategiesGrid.Columns[2].GetCellContent(profileItem) as TextBlock;
        var profileRow = window.StrategiesGrid.ItemContainerGenerator.ContainerFromItem(profileItem) as DataGridRow;
        Require(profileText != null && profileRow != null && profileText.ActualHeight > profileText.FontSize && profileRow.ActualHeight >= profileText.ActualHeight,
            "Full profile descriptions wrap inside their visible rows on a small window");
        InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "profiles-preview.png"));
        window.DiagnosticsGrid.ItemsSource = new Diagnostic[]
        {
            new("Модуль обхода", "ПРИМЕР", "Работа модуля обхода не гарантирует доступ к сайтам. Их ответы проверяются отдельно."),
            new("Драйвер WinDivert", "ПРИМЕР", "Проверяем файл сетевого драйвера, через который работает обход. Если его нет, распакуйте полный архив программы."),
            new("DNS", "ПРИМЕР", "DNS помогает находить адреса сайтов. Если сайты не открываются, проверьте настройки DNS в браузере и Windows. x2zapret не меняет их автоматически.")
        };
        window.SelectPage("Diagnostics"); await Task.Delay(450); window.UpdateLayout();
        var diagnosticItem = window.DiagnosticsGrid.Items[0];
        var diagnosticText = window.DiagnosticsGrid.Columns[2].GetCellContent(diagnosticItem) as TextBlock;
        var diagnosticRow = window.DiagnosticsGrid.ItemContainerGenerator.ContainerFromItem(diagnosticItem) as DataGridRow;
        Require(diagnosticText != null && diagnosticRow != null && diagnosticText.ActualHeight > diagnosticText.FontSize && diagnosticRow.ActualHeight >= diagnosticText.ActualHeight,
            "Diagnostic explanations fit their row height instead of being clipped");
        InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "diagnostics-preview.png"));
        try
        {
            ThemeManager.Apply(ThemeManager.Light);
            window.SelectPage("Home"); await Task.Delay(450); window.UpdateLayout();
            Require(System.Windows.Application.Current.FindResource("BackgroundBrush") is System.Windows.Media.SolidColorBrush { Color.R: > 200 } && window.PageScroll.Opacity == 1,
                "The light theme replaces the palette while the window is open");
            InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "light-theme-preview.png"));
            window.SelectPage("Tools"); window.ShowExtendedTool("Sensors"); await Task.Delay(450); window.UpdateLayout();
            InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "light-tools-preview.png"));
        }
        finally { ThemeManager.Apply(ThemeManager.Dark); }
        AtomicFile.WriteJson(destination, new { passed = checks.Count, checks });
    }
    private sealed class HealthHandler(bool failMain) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool cdn = request.RequestUri!.Host is "cdn.discordapp.com" or "i.ytimg.com";
            return Task.FromResult(new HttpResponseMessage(cdn || failMain && request.RequestUri.Host == "discord.com" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent([]) });
        }
    }
    private sealed class MeasurementHandler : HttpMessageHandler
    {
        public bool Fail, Short;
        public CancellationTokenSource? CancelUpload;
        public long Uploaded;
        public List<Uri> Urls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; Urls.Add(uri);
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) return new(HttpStatusCode.ServiceUnavailable) { RequestMessage = request, Content = new ByteArrayContent([]) };
            int size = int.Parse(uri.Query.Split("bytes=")[1]);
            if (request.Method == HttpMethod.Post)
            {
                CancelUpload?.Cancel(); cancellationToken.ThrowIfCancellationRequested();
                byte[] payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                if (payload.Length != size) throw new InvalidDataException("Incorrect upload payload");
                Uploaded += payload.Length; size = 0;
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(new byte[Short && size > 0 ? size - 1 : size]) };
        }
    }
}
