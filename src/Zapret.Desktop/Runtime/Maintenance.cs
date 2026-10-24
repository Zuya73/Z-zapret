using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

public sealed record Diagnostic(string Name, string State, string Detail);

public sealed class Maintenance(AppPaths paths)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Upstream = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/";

    public async Task UpdateIpSetAsync(CancellationToken cancellationToken)
    {
        string text = await Http.GetStringAsync(Upstream + ".service/ipset-service.txt", cancellationToken);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 100 or > 200_000 || lines.Any(x => !Validation.IpOrSubnet(x.Trim())))
            throw new InvalidDataException("Список адресов не прошёл проверку. Предыдущий список сохранён.");
        AtomicFile.Write(Path.Combine(paths.Lists, "ipset-all.txt.backup"), string.Join('\n', lines.Distinct()) + "\n");
    }

    public async Task UpdateHostsAsync(CancellationToken cancellationToken)
    {
        string text = await Http.GetStringAsync(Upstream + ".service/hosts", cancellationToken);
        if (text.Length is < 10 or > 256_000) throw new InvalidDataException("Недопустимый размер hosts.");
        ModifyHosts(text);
    }

    public void ModifyHosts(string? replacement)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        // Latin-1 is a lossless byte mapping for BOM-less hosts, including legacy ANSI comments.
        System.Text.Encoding encoding = bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) ? System.Text.Encoding.Unicode :
            bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ? System.Text.Encoding.BigEndianUnicode :
            bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? new System.Text.UTF8Encoding(true) : System.Text.Encoding.Latin1;
        int preamble = encoding.GetPreamble().Length;
        string original = encoding.GetString(bytes, Math.Min(preamble, bytes.Length), Math.Max(0, bytes.Length - preamble));
        if (replacement == null && !original.Contains(HostsBlock.Begin, StringComparison.Ordinal)) return;
        string changed = HostsBlock.Merge(original, replacement);
        string backups = Path.Combine(paths.Root, "hosts-backups");
        Directory.CreateDirectory(backups);
        File.WriteAllBytes(Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".hosts"), bytes);
        // Preserve hosts ACL and attributes by writing through the existing file.
        File.WriteAllText(path, changed, encoding);
    }

    public async Task<IReadOnlyList<ProbeTarget>> LoadDpiTargetsAsync(CancellationToken cancellationToken)
    {
        string json;
        string cache = Path.Combine(paths.Root, "dpi-suite.json");
        try { json = await Http.GetStringAsync("https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.v2.json", cancellationToken); }
        catch (HttpRequestException) when (File.Exists(cache)) { json = File.ReadAllText(cache); }
        using var doc = JsonDocument.Parse(json);
        var result = new List<ProbeTarget>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            string? host = entry.GetProperty("host").GetString();
            string? id = entry.GetProperty("id").GetString();
            string? provider = entry.GetProperty("provider").GetString();
            if (host == null || id == null || !Validation.Domain(host) || result.Count >= 72)
                throw new InvalidDataException("Неподдерживаемый формат набора DPI-проверок.");
            result.Add(new(id, provider + " / " + id, "DPI", "https://" + host, true));
        }
        if (result.Count == 0 || result.Select(x => x.Id).Distinct().Count() != result.Count)
            throw new InvalidDataException("Нет подходящих целей DPI-проверки.");
        AtomicFile.Write(cache, json);
        return result;
    }

    public async Task<string> CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Zuya73/x2zapret/releases/latest");
        request.Headers.UserAgent.ParseAdd("Z-zapret/0.1.0");
        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "Опубликованных релизов приложения пока нет.";
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return "Последний опубликованный релиз: " + doc.RootElement.GetProperty("tag_name").GetString() + ". Установлено: 0.1.0.";
    }

    public IReadOnlyList<Diagnostic> Diagnose(EngineController engine)
    {
        var output = new List<Diagnostic>
        {
            new("Windows", "ИНФО", $"{Environment.OSVersion.VersionString}; процесс {(Environment.Is64BitProcess ? "x64" : "x86")}"),
            new("Движок", engine.IsRunning ? "РАБОТАЕТ" : "ОСТАНОВЛЕН", "Наличие процесса не подтверждает доступность сервисов."),
            new("BFE", WindowsService.State("BFE") == 4 ? "OK" : "ОШИБКА", "Базовая служба фильтрации необходима драйверу."),
            new("WinDivert", File.Exists(Path.Combine(paths.Bin, "WinDivert64.sys")) ? "OK" : "ОШИБКА", "Проверка наличия файла драйвера."),
            new("curl", File.Exists(ProcessRunner.SystemTool("curl.exe")) ? "OK" : "ОШИБКА", "Системный curl используется для сетевых проверок."),
            new("Автозапуск", engine.OwnsService ? "ВКЛЮЧЁН" : engine.ServiceInstalled ? "КОНФЛИКТ" : "ВЫКЛЮЧЕН", engine.ServiceInstalled && !engine.OwnsService ? "Найдена служба другой сборки zapret." : "Управление собственной службой Windows."),
            new("DNS", "ПРОВЕРЬТЕ", "Проверьте безопасный DNS в браузере или Windows. Приложение не меняет DNS автоматически.")
        };
        using var internet = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        bool proxy = internet?.GetValue("ProxyEnable") is int enabled && enabled == 1;
        output.Add(new("Системный прокси", proxy ? "ВНИМАНИЕ" : "OK", proxy ? "Включён системный прокси. Автотесты выполняют прямое подключение." : "Явный системный прокси выключен."));
        foreach (string name in new[] { "AdguardSvc", "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2", "TracSrvWrapper", "EPWD", "Killer Network Service", "SmartByte Network Service" })
        {
            bool found;
            try { found = WindowsService.State(name) != 0; } catch (System.ComponentModel.Win32Exception) { found = false; }
            if (found) output.Add(new(name, "ВНИМАНИЕ", "Возможен конфликт сетевых фильтров. Служба не изменялась."));
        }
        foreach (var process in Process.GetProcessesByName("AdguardSvc"))
        { process.Dispose(); output.Add(new("AdGuard", "ВНИМАНИЕ", "Обнаружен процесс фильтрации трафика.")); }
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (string serviceName in services?.GetSubKeyNames() ?? [])
        {
            using var service = services!.OpenSubKey(serviceName);
            string description = $"{serviceName} {service?.GetValue("DisplayName")}";
            if (new[] { "Killer", "SmartByte", "Intel Connectivity", "VPN", "HappService" }.Any(x => description.Contains(x, StringComparison.OrdinalIgnoreCase)))
                output.Add(new(serviceName, "ВНИМАНИЕ", "Возможный сетевой фильтр или VPN: " + service?.GetValue("DisplayName")));
        }
        return output;
    }

    public void ClearDiscordCache()
    {
        foreach (string name in new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" })
            foreach (var process in Process.GetProcessesByName(name))
            { process.Dispose(); throw new InvalidOperationException("Сначала закройте Discord, включая значок в трее."); }
        foreach (string edition in new[] { "discord", "discordptb", "discordcanary", "discorddevelopment" })
            foreach (string folder in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), edition, folder);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
    }
}
