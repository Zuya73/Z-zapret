using System.Text.Json;

namespace Zapret.Core;

public static class StrategyCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IReadOnlyList<Strategy> Load(string path)
    {
        var values = JsonSerializer.Deserialize<Strategy[]>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Не найден список профилей. Загрузите и распакуйте полный архив программы.");
        if (values.Length == 0 || values.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException("Список профилей повреждён. Загрузите полный архив программы ещё раз.");
        foreach (var strategy in values)
            if (strategy.Arguments.Length == 0 || strategy.Arguments.Any(x => !x.StartsWith("--", StringComparison.Ordinal) ||
                x.Contains('\n') || x.Contains('\r') || x.Contains('\0')))
                throw new InvalidDataException($"Некорректный профиль: {strategy.Id}");
        return values;
    }

    public static string[] Expand(Strategy strategy, string bin, string lists, AppSettings settings)
    {
        string tcp = Validation.Ports(settings.TcpPorts), udp = Validation.Ports(settings.UdpPorts);
        tcp = settings.GameFilter is GameFilterMode.Tcp or GameFilterMode.TcpAndUdp ? tcp : "12";
        udp = settings.GameFilter is GameFilterMode.Udp or GameFilterMode.TcpAndUdp ? udp : "12";
        return strategy.Arguments.Select(x => x.Replace("{BIN}", Path.TrimEndingDirectorySeparator(bin) + Path.DirectorySeparatorChar)
            .Replace("{LISTS}", Path.TrimEndingDirectorySeparator(lists) + Path.DirectorySeparatorChar)
            .Replace("{GAME_TCP}", tcp).Replace("{GAME_UDP}", udp)).ToArray();
    }

    public static string QuoteWindowsArgument(string value)
    {
        // CommandLineToArgvW quoting, including backslashes before quotes and at the end.
        var output = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            output.Append(c);
            slashes = 0;
        }
        output.Append('\\', slashes * 2).Append('"');
        return output.ToString();
    }
}
