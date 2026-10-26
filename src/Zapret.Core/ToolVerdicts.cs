using System.Net;
using System.Net.Sockets;

namespace Zapret.Core;

// A short conclusion shown above raw measurements. Level orders the outcome for presentation: 0 good, 1 fair, 2 poor.
public sealed record Verdict(string Title, string Detail, int Level);

public static class ToolVerdicts
{
    public static Verdict? Disk(IReadOnlyList<DiskMeasurement> measurements)
    {
        double? Read(string id) => DiskBenchmarkPolicy.Aggregate(measurements.Where(x => x.CaseId == id && !x.Write))?.MegabytesPerSecond;
        double? sequential = Read("seq8") ?? Read("seq1");
        if (sequential is not double seq) return null;
        double? small = Read("rnd1");
        var (title, level) = seq switch
        {
            >= 2500 => ("Очень быстрый накопитель (NVMe SSD)", 0),
            >= 1200 => ("Быстрый накопитель (NVMe SSD)", 0),
            >= 380 => ("Обычный SSD", 0),
            >= 120 => ("Жёсткий диск (HDD) или медленный SSD", 1),
            _ => ("Медленный накопитель", 2)
        };
        string use = level switch
        {
            0 => "Подходит для Windows, игр и программ.",
            1 => "Подходит для хранения файлов. Для Windows и игр SSD будет заметно быстрее.",
            _ => "Похоже на флешку или старый жёсткий диск. Устанавливать на него Windows и игры не стоит."
        };
        string responsiveness = small switch
        {
            null => "",
            >= 30 => " Мелкие файлы читаются быстро: система и программы должны открываться без задержек.",
            >= 8 => " Мелкие файлы читаются со средней скоростью.",
            _ => " Мелкие файлы читаются медленно: программы и Windows могут открываться долго."
        };
        if (small is < 8 && level == 0) level = 1;
        return new(title, use + responsiveness, level);
    }

    public static Verdict Connection(LatencySummary internet, LatencySummary? gateway)
    {
        bool home = gateway is { Sent: > 0 };
        if (internet.Received == 0)
            return home && gateway!.Received > 0
                ? new("Сервер не ответил", "Роутер отвечает, а сервер в интернете — нет. Проблема может быть у провайдера, или сервер не отвечает на такие проверки. Попробуйте другой адрес, например 8.8.8.8.", 2)
                : new("Нет ответа", "Не ответили ни роутер, ни сервер. Проверьте кабель или Wi-Fi. Некоторые роутеры не отвечают на проверку, даже если интернет работает.", 2);
        if (home && gateway!.Received > 0 && (gateway.AverageMs > 20 || gateway.LossPercent > 2 || gateway.JitterMs > 10))
            return new("Проблема в домашней сети", "Роутер отвечает медленно или с потерями. Чаще всего виноват слабый Wi-Fi: подойдите ближе к роутеру или подключитесь кабелем.", 2);
        if (internet.LossPercent > 2 || internet.JitterMs > 30)
            return new("Соединение нестабильно", "Часть запросов теряется или время ответа сильно скачет, а домашняя сеть в порядке. Возможны проблемы у провайдера. В играх и звонках это заметно.", 2);
        if (internet.AverageMs > 120)
            return new("Интернет работает, но с большой задержкой", "Сайты и видео будут работать, а в играх и звонках возможны задержки. Попробуйте ближний сервер или проверьте, не идут ли загрузки.", 1);
        return new("Интернет в порядке", "Ответы приходят быстро и стабильно" + (home ? ", домашняя сеть работает нормально." : "."), 0);
    }

    public static Verdict? Speed(double downloadMbps, double uploadMbps)
    {
        if (downloadMbps <= 0) return null;
        var (title, detail, level) = downloadMbps switch
        {
            >= 100 => ("Отличная скорость", "Хватит на 4K-видео, игры и большие загрузки одновременно на нескольких устройствах.", 0),
            >= 25 => ("Хорошая скорость", "Хватит на видео в высоком качестве, игры и видеозвонки.", 0),
            >= 8 => ("Достаточная скорость", "Видео в HD и звонки будут работать; большие файлы скачиваются долго.", 1),
            _ => ("Низкая скорость", "Видео может долго загружаться или идти в низком качестве.", 2)
        };
        if (uploadMbps is > 0 and < 2) detail += " Отправка медленная: в видеозвонках собеседник может видеть вас с задержкой.";
        return new(title, detail, level);
    }
}

public sealed record DnsPreset(string Id, string Name, string[] IPv4, string[] IPv6);

public static class DnsCatalog
{
    public static IReadOnlyList<DnsPreset> Presets { get; } = Array.AsReadOnly(new[]
    {
        new DnsPreset("cloudflare", "Cloudflare", ["1.1.1.1", "1.0.0.1"], ["2606:4700:4700::1111", "2606:4700:4700::1001"]),
        new DnsPreset("google", "Google", ["8.8.8.8", "8.8.4.4"], ["2001:4860:4860::8888", "2001:4860:4860::8844"])
    });

    public static string Describe(IPAddress address)
    {
        string text = address.ToString();
        var preset = Presets.FirstOrDefault(p => p.IPv4.Contains(text) || p.IPv6.Contains(text, StringComparer.OrdinalIgnoreCase));
        if (preset != null) return preset.Name;
        return text switch
        {
            "9.9.9.9" or "149.112.112.112" => "Quad9",
            "77.88.8.8" or "77.88.8.1" => "Яндекс",
            "94.140.14.14" or "94.140.15.15" => "AdGuard",
            _ when IsLocal(address) => "роутер (обычно передаёт запросы DNS провайдера)",
            _ => "DNS провайдера или другой сервер"
        };
    }

    public static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }
}

public sealed record RepairCommand(string Tool, string[] Arguments, string Description);

public static class NetworkRepair
{
    // Flushing the resolver cache is harmless and immediate. The full reset rebuilds Winsock and TCP/IP settings,
    // removes manually configured addresses and needs a restart, so the interface asks for confirmation first.
    public static IReadOnlyList<RepairCommand> Steps(bool full)
    {
        var steps = new List<RepairCommand> { new("ipconfig.exe", ["/flushdns"], "Очистка кэша DNS") };
        if (full)
        {
            steps.Add(new("netsh.exe", ["winsock", "reset"], "Сброс Winsock"));
            steps.Add(new("netsh.exe", ["int", "ip", "reset"], "Сброс настроек TCP/IP"));
        }
        return steps;
    }

    public static IReadOnlyList<RepairCommand> SetDns(int interfaceIndex, DnsPreset? preset, bool ipv6)
    {
        if (interfaceIndex <= 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        string family = ipv6 ? "ipv6" : "ipv4";
        string name = "name=" + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (preset == null) return [new("netsh.exe", ["interface", family, "set", "dnsservers", name, "source=dhcp"], "Автоматический DNS")];
        string[] servers = ipv6 ? preset.IPv6 : preset.IPv4;
        var result = new List<RepairCommand> { new("netsh.exe", ["interface", family, "set", "dnsservers", name, "static", servers[0], "primary", "validate=no"], "Основной DNS " + preset.Name) };
        for (int i = 1; i < servers.Length; i++)
            result.Add(new("netsh.exe", ["interface", family, "add", "dnsservers", name, servers[i], "index=" + (i + 1), "validate=no"], "Резервный DNS " + preset.Name));
        return result;
    }
}
