using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Zapret.Core;
using Zapret.Desktop.Runtime;

namespace Zapret.Desktop;

public partial class MainWindow
{
    private async void FlushDns_Click(object sender, RoutedEventArgs e) => await RepairNetworkAsync(full: false);
    private async void ResetNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Будут сброшены сетевые настройки Windows (Winsock и TCP/IP). Вручную заданные IP-адреса и DNS станут автоматическими, сохранённые сети Wi-Fi и пароли останутся. VPN может понадобиться подключить заново. После сброса нужно перезагрузить компьютер. Продолжить?",
            "Полный сброс сети", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RepairNetworkAsync(full: true);
    }

    internal async Task RepairNetworkAsync(bool full)
    {
        if (!ready || busy || preview) return;
        await Execute(async token =>
        {
            var steps = NetworkRepair.Steps(full);
            var failed = new List<string>();
            foreach (var step in steps)
            {
                ShowRepairStatus(step.Description + "…");
                var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool(step.Tool), step.Arguments, TimeSpan.FromSeconds(30), token);
                if (result.ExitCode == 0) Log("Починка сети: " + step.Description + " — выполнено.");
                else { failed.Add(step.Description); Log($"Починка сети: {step.Description} — код {result.ExitCode}. {result.Output} {result.Error}".Trim()); }
            }
            if (failed.Count == steps.Count) throw new InvalidOperationException("Windows не выполнила починку сети. Подробности — в разделе «Журнал».");
            string text = full ? "Сетевые настройки сброшены. Перезагрузите компьютер, чтобы изменения вступили в силу." :
                "Кэш DNS очищен. Обновите страницу в браузере или перезапустите Discord.";
            // "netsh int ip reset" often reports one protected registry key as failed while the reset itself succeeds.
            if (failed.Count > 0) text += " Не всё выполнилось (" + string.Join(", ", failed) + "); обычно это не мешает. Подробности — в журнале.";
            ShowRepairStatus(text);
        }, full ? "Сброс сетевых настроек…" : "Очистка кэша DNS…");
    }

    private void ShowRepairStatus(string text)
    {
        RepairStatusText.Text = text; RepairStatusText.Visibility = Visibility.Visible;
    }

    private void RefreshDns_Click(object sender, RoutedEventArgs e) => RefreshDns();
    internal void RefreshDns()
    {
        try
        {
            var adapters = NetworkTools.DnsAdapters();
            DnsCurrentText.Text = adapters.Count == 0 ? "Активное подключение к интернету не найдено." :
                string.Join("\n", adapters.Select(a =>
                {
                    // IPv4 resolvers are what people recognise; IPv6 entries are shown only when nothing else is configured.
                    var servers = a.Servers.Where(s => s.AddressFamily == AddressFamily.InterNetwork).ToArray();
                    if (servers.Length == 0) servers = a.Servers.ToArray();
                    return a.Name + ": " + (servers.Length == 0 ? "не задан" : string.Join(", ", servers.Take(2).Select(s => s + " — " + DnsCatalog.Describe(s))));
                }));
        }
        catch (NetworkInformationException error) { DnsCurrentText.Text = "Не удалось получить сведения о DNS: " + error.Message; }
    }

    private async void SetDns_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || preview || sender is not Button { Tag: string id }) return;
        var preset = DnsCatalog.Presets.FirstOrDefault(p => p.Id == id);
        IReadOnlyList<DnsAdapter> adapters;
        try { adapters = NetworkTools.DnsAdapters(); }
        catch (NetworkInformationException error) { ShowError("Не удалось получить список подключений: " + error.Message); return; }
        if (adapters.Count == 0) { ShowError("Не найдено активное подключение к интернету. Подключитесь к сети и повторите."); return; }
        string target = preset == null ? "автоматический DNS (от роутера или провайдера)" : $"DNS {preset.Name} ({preset.IPv4[0]})";
        if (MessageBox.Show(this, $"Установить {target} для подключений: {string.Join(", ", adapters.Select(a => a.Name))}?",
            "DNS-сервер", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Execute(async token =>
        {
            foreach (var adapter in adapters)
            {
                foreach (var command in NetworkRepair.SetDns(adapter.IPv4Index, preset, ipv6: false))
                {
                    var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool(command.Tool), command.Arguments, TimeSpan.FromSeconds(20), token);
                    if (result.ExitCode != 0)
                    {
                        Log($"DNS: {command.Description} для «{adapter.Name}» — код {result.ExitCode}. {result.Output} {result.Error}".Trim());
                        throw new InvalidOperationException($"Windows не изменила DNS для подключения «{adapter.Name}». Подробности — в разделе «Журнал».");
                    }
                }
                // IPv6 is best effort: many connections have it disabled, and IPv4 alone already changes name resolution.
                if (adapter.IPv6Index is int ipv6)
                    foreach (var command in NetworkRepair.SetDns(ipv6, preset, ipv6: true))
                        if ((await ProcessRunner.RunAsync(ProcessRunner.SystemTool(command.Tool), command.Arguments, TimeSpan.FromSeconds(20), token)).ExitCode != 0)
                        { Log($"DNS: IPv6 для «{adapter.Name}» не изменён; используется настройка IPv4."); break; }
            }
            await ProcessRunner.RunAsync(ProcessRunner.SystemTool("ipconfig.exe"), ["/flushdns"], TimeSpan.FromSeconds(20), token);
            Log("DNS: установлен " + target + ".");
        }, "Изменение DNS…");
        RefreshDns();
    }
}
