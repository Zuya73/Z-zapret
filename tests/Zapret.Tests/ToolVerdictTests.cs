using System.Net;
using Zapret.Core;

internal static class ToolVerdictTests
{
    internal static void Run(Action<string, Action> check)
    {
        DiskMeasurement Read(string id, double mbPerSecond) => new(id, false, 1, (ulong)(mbPerSecond * 1_000_000), 1000, 1, 0.1, null);
        LatencySummary Summary(int sent, int received, double? average, double? jitter) => new(sent, received, average, average, average, jitter);

        check("Disk verdicts classify typical NVMe, SATA SSD, HDD and flash results", () =>
        {
            Assert(ToolVerdicts.Disk([Read("seq8", 3400), Read("rnd1", 60)])!.Title.Contains("NVMe"));
            var sata = ToolVerdicts.Disk([Read("seq8", 540), Read("rnd1", 35)])!;
            Assert(sata.Title == "Обычный SSD" && sata.Level == 0);
            Assert(ToolVerdicts.Disk([Read("seq8", 180), Read("rnd1", 1)])!.Level == 1);
            Assert(ToolVerdicts.Disk([Read("seq8", 40)])!.Level == 2);
            Assert(ToolVerdicts.Disk([]) == null);
        });
        check("Slow small-file reads downgrade an otherwise fast disk", () =>
            Assert(ToolVerdicts.Disk([Read("seq8", 500), Read("rnd1", 3)])!.Level == 1));
        check("Connection verdicts separate home network, provider and silent targets", () =>
        {
            Assert(ToolVerdicts.Connection(Summary(20, 20, 18, 2), Summary(20, 20, 1, 0.5)).Level == 0);
            Assert(ToolVerdicts.Connection(Summary(20, 20, 40, 15), Summary(20, 20, 35, 20)).Title == "Проблема в домашней сети");
            Assert(ToolVerdicts.Connection(Summary(20, 15, 30, 5), Summary(20, 20, 1, 0.5)).Title == "Соединение нестабильно");
            Assert(ToolVerdicts.Connection(Summary(20, 0, null, null), Summary(20, 20, 1, 0.5)).Title == "Сервер не ответил");
            Assert(ToolVerdicts.Connection(Summary(20, 0, null, null), null).Title == "Нет ответа");
            Assert(ToolVerdicts.Connection(Summary(20, 20, 180, 4), null).Level == 1);
        });
        check("Speed verdicts never rate a failed measurement", () =>
        {
            Assert(ToolVerdicts.Speed(0, 0) == null);
            Assert(ToolVerdicts.Speed(300, 100)!.Level == 0 && ToolVerdicts.Speed(5, 1)!.Level == 2);
            Assert(ToolVerdicts.Speed(50, 1)!.Detail.Contains("Отправка медленная"));
        });
        check("DNS servers are named and private resolvers are recognised as the router", () =>
        {
            Assert(DnsCatalog.Describe(IPAddress.Parse("1.1.1.1")) == "Cloudflare" && DnsCatalog.Describe(IPAddress.Parse("8.8.4.4")) == "Google");
            Assert(DnsCatalog.Describe(IPAddress.Parse("192.168.1.1")).StartsWith("роутер") && DnsCatalog.Describe(IPAddress.Parse("fe80::1")).StartsWith("роутер"));
            Assert(!DnsCatalog.IsLocal(IPAddress.Parse("172.32.0.1")) && DnsCatalog.IsLocal(IPAddress.Parse("172.20.0.1")));
        });
        check("Network repair uses fixed system commands and DNS changes target one interface index", () =>
        {
            Assert(NetworkRepair.Steps(false).Count == 1 && NetworkRepair.Steps(false)[0].Arguments.SequenceEqual(["/flushdns"]));
            Assert(NetworkRepair.Steps(true).Select(x => x.Tool).SequenceEqual(["ipconfig.exe", "netsh.exe", "netsh.exe"]));
            var set = NetworkRepair.SetDns(12, DnsCatalog.Presets[0], false);
            Assert(set.Count == 2 && set[0].Arguments.SequenceEqual(["interface", "ipv4", "set", "dnsservers", "name=12", "static", "1.1.1.1", "primary", "validate=no"]));
            Assert(set[1].Arguments.Contains("1.0.0.1") && set[1].Arguments.Contains("index=2"));
            Assert(NetworkRepair.SetDns(7, null, true)[0].Arguments.SequenceEqual(["interface", "ipv6", "set", "dnsservers", "name=7", "source=dhcp"]));
            try { NetworkRepair.SetDns(0, null, false); throw new Exception("Invalid interface accepted"); } catch (ArgumentOutOfRangeException) { }
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
