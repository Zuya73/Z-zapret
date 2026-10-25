using System.Net;
using System.Text.Json.Serialization;

namespace Zapret.Core;

public sealed record Strategy(string Id, string Name, string Source, string Description, string[] Arguments)
{
    public override string ToString() => Name;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpSetMode { None, Loaded, Any }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameFilterMode { Disabled, TcpAndUdp, Tcp, Udp }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeStatus { Passed, Failed, Unsupported, Inconclusive }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestMode { Quick, Full, Dpi }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StartupMode { Disabled, Tray, TrayAndConnect }

public sealed record AppSettings
{
    public string StrategyId { get; init; } = "general";
    public IpSetMode IpSet { get; init; } = IpSetMode.None;
    public GameFilterMode GameFilter { get; init; } = GameFilterMode.Disabled;
    public string TcpPorts { get; init; } = "1024-65535";
    public string UdpPorts { get; init; } = "1024-65535";
    public string DiscordFake { get; init; } = "quic_initial_steamcommunity_com.bin";
    public string GameFake { get; init; } = "quic_initial_4pda_to.bin";
    public bool MinimizeToTray { get; init; } = true;
    public StartupMode Startup { get; init; } = StartupMode.Disabled;
    public bool AutoUpdate { get; init; }
    public bool MonitorConnection { get; init; } = true;
    public bool AutoReconnect { get; init; }
    public SensorAlert[] SensorAlerts { get; init; } = [];
}

public sealed record ProbeTarget(string Id, string Name, string Group, string Url, bool Required = false,
    int[]? AcceptedCodes = null, bool PingOnly = false);

public sealed record ProbeResult(string TargetId, string TargetName, string Group, string Protocol,
    ProbeStatus Status, double Milliseconds, int? HttpCode, long DownloadBytes, string Detail,
    bool Required = false);

public sealed record StrategyReport(string StrategyId, string Name, IReadOnlyList<ProbeResult> Results,
    string? StartError = null)
{
    [JsonIgnore] public int Passed => Results.Count(x => x.Status == ProbeStatus.Passed && x.Protocol != "PING");
    [JsonIgnore] public int Failed => Results.Count(x => x.Status == ProbeStatus.Failed && x.Protocol != "PING");
    [JsonIgnore] public int Unsupported => Results.Count(x => x.Status == ProbeStatus.Unsupported);
    [JsonIgnore] public int Measured => Results.Count(x => x.Protocol != "PING" && x.Status != ProbeStatus.Unsupported);
    [JsonIgnore] public double SuccessRate => Measured == 0 ? 0 : (double)Passed / Measured;
    [JsonIgnore] public double AverageMs => Passed == 0 ? double.MaxValue :
        Results.Where(x => x.Status == ProbeStatus.Passed && x.Protocol != "PING").Average(x => x.Milliseconds);
}

public sealed record TestProgress(int Completed, int Total, string Stage, ProbeResult? Result = null);
public sealed record EngineSnapshot(string? StrategyId, bool WasRunning, bool ServiceMode);
public sealed record TestSession(DateTimeOffset Started, TestMode Mode, StrategyReport Baseline,
    IReadOnlyList<StrategyReport> Reports, IReadOnlyList<StrategyReport> Confirmation,
    string? RecommendedStrategyId, bool Cancelled);

public static class Validation
{
    public static string Ports(string value)
    {
        var parts = value.Replace(" ", "").Split(',');
        if (parts.Length == 0 || parts.Any(x => x.Length == 0)) throw new FormatException("Укажите порты, например 1024-1934,1936-65535.");
        foreach (var item in parts)
        {
            var pair = item.Split('-');
            if (pair.Length is < 1 or > 2 || !int.TryParse(pair[0], out int first) || first is < 1 or > 65535 ||
                pair[0].Any(c => !char.IsAsciiDigit(c))) throw new FormatException("Порт должен быть от 1 до 65535.");
            if (pair.Length == 2 && (!int.TryParse(pair[1], out int last) || last < first || last > 65535 ||
                pair[1].Any(c => !char.IsAsciiDigit(c)))) throw new FormatException("Некорректный диапазон портов.");
        }
        return string.Join(',', parts);
    }

    public static bool IpOrSubnet(string value)
    {
        var parts = value.Split('/');
        return parts.Length is 1 or 2 && IPAddress.TryParse(parts[0], out var ip) &&
            (parts.Length == 1 || int.TryParse(parts[1], out int prefix) && prefix >= 0 &&
                prefix <= (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
    }

    public static bool Domain(string value)
    {
        var domain = value.TrimStart('^');
        return domain.Length <= 253 && domain.Contains('.') &&
            Uri.CheckHostName(domain) == UriHostNameType.Dns &&
            domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    public static string UserList(string text, bool addresses)
    {
        var lines = text.Replace("\r", "").Split('\n');
        foreach (string line in lines.Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')))
            if (!(addresses ? IpOrSubnet(line) : Domain(line))) throw new FormatException($"Некорректная строка: {line}");
        return text.Trim() + Environment.NewLine;
    }
}
