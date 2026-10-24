using System.Runtime.InteropServices;
using Zapret.Core;

int passed = 0;
var failures = new List<string>();
var strategiesPath = Path.Combine(args.FirstOrDefault() ?? ".", "desktop", "Zapret.Desktop", "Assets", "strategies.json");
var catalog = StrategyCatalog.Load(strategiesPath);
var required = new[] { new ProbeTarget("discord", "Discord", "Discord", "https://discord.com", true), new ProbeTarget("youtube", "YouTube", "YouTube", "https://youtube.com", true) };
ProbeResult R(string id, ProbeStatus status = ProbeStatus.Passed, string protocol = "HTTP/1.1", double ms = 30) =>
    new(id, id, id, protocol, status, ms, 200, 2000, "", true);
StrategyReport Report(string id, params ProbeResult[] results) => new(id, id, results);

Check("All 22 original strategies are imported", () => Equal(22, catalog.Count));
Check("Repeated options and literal exclamation survive import", () =>
{
    var fake = catalog.Single(x => x.Id == "general-fake-tls-auto");
    Assert(fake.Arguments.Contains("--dpi-desync-fake-tls=!"));
    Assert(fake.Arguments.Count(x => x.StartsWith("--dpi-desync-fake-tls=")) > 2);
});
Check("Disabled game filtering retains original disabled port", () =>
{
    var args = StrategyCatalog.Expand(catalog[0], "C:/Program Files/Z/bin", "C:/Data/lists", new());
    Assert(args.All(x => !x.Contains('{')));
    Assert(args[0].EndsWith(",12"));
    Assert(args.Any(x => x.Contains("C:/Program Files/Z/bin")));
});
Check("TCP and UDP filter ranges expand independently", () =>
{
    var args = StrategyCatalog.Expand(catalog[0], "bin", "lists", new() { GameFilter = GameFilterMode.Udp, TcpPorts = "2000-3000", UdpPorts = "4000,5000-6000" });
    Assert(args[0].EndsWith(",12")); Assert(args[1].EndsWith(",4000,5000-6000"));
});
Check("Ports reject injection, malformed input and invalid boundaries", () =>
{
    foreach (string value in new[] { "", "0", "65536", "1-0", "1-65536", "1,,2", "1-2-3", "1&calc", "-1", "+80", "1\n2" })
        Throws<FormatException>(() => Validation.Ports(value));
    Equal("1,65535,80-443", Validation.Ports("1,65535,80 - 443"));
});
Check("IPv4 and IPv6 CIDR boundaries are validated", () =>
{
    foreach (string value in new[] { "1.2.3.4/32", "::1", "fc00::/7", "0.0.0.0/0", "2001:db8::/128" }) Assert(Validation.IpOrSubnet(value));
    foreach (string value in new[] { "1.2.3.4/33", "::/129", "::/-1", "256.1.1.1", "example.com", "1.2.3.4/24/2" }) Assert(!Validation.IpOrSubnet(value));
});
Check("User domains cannot become process arguments", () =>
{
    Assert(Validation.Domain("^discord.com")); Assert(!Validation.Domain("--new"));
    Throws<FormatException>(() => Validation.UserList("discord.com\n& calc.exe", false));
});
Check("All-failed strategies never become recommendations", () =>
{
    var report = Report("bad", R("discord", ProbeStatus.Failed), R("youtube", ProbeStatus.Failed));
    Equal<string?>(null, Ranking.Recommend([report], [report], required));
});
Check("Ping success cannot nominate a strategy", () =>
{
    var report = Report("ping", R("discord", protocol: "PING"), R("youtube", protocol: "PING"));
    Assert(!Ranking.Eligible(report, required));
});
Check("Missing required resource disqualifies an otherwise successful strategy", () =>
    Assert(!Ranking.Eligible(Report("partial", R("discord")), required)));
Check("TLS unsupported does not count as passed or penalize a supported Windows protocol", () =>
{
    var report = Report("ok", R("discord"), R("youtube"), R("discord", ProbeStatus.Unsupported, "TLS 1.3"), R("youtube", ProbeStatus.Unsupported, "TLS 1.3"));
    Equal(2, report.Passed); Assert(Ranking.Eligible(report, required));
    Assert(!Ranking.Eligible(Report("none", R("discord", ProbeStatus.Unsupported), R("youtube", ProbeStatus.Unsupported)), required));
});
Check("An inconclusive required probe cannot pass", () =>
    Assert(!Ranking.Eligible(Report("inconclusive", R("discord", ProbeStatus.Inconclusive), R("youtube")), required)));
Check("A recommendation requires an independent confirmation", () =>
{
    var report = Report("ok", R("discord"), R("youtube"));
    Equal<string?>(null, Ranking.Recommend([report], [], required));
    Equal("ok", Ranking.Recommend([report], [report], required));
});
Check("DNS, handshake and certificate failures are failures, not unsupported", () =>
{
    foreach (int exit in new[] { 6, 35, 60 }) Equal(ProbeStatus.Failed, ProbeClassifier.Classify(exit, 0, 0, 0, false, null, "").Status);
    Equal(ProbeStatus.Unsupported, ProbeClassifier.Classify(4, 0, 0, 0, false, null, "").Status);
});
Check("Unexpected HTTP errors and missing response are not successful", () =>
{
    Equal(ProbeStatus.Inconclusive, ProbeClassifier.Classify(0, 503, 100, 0, false, null, "").Status);
    Equal(ProbeStatus.Inconclusive, ProbeClassifier.Classify(0, 0, 100, 0, false, null, "").Status);
    Equal(ProbeStatus.Passed, ProbeClassifier.Classify(0, 404, 100, 0, false, [404], "").Status);
});
Check("DPI classification requires actual transfer in both directions", () =>
{
    Equal(ProbeStatus.Inconclusive, ProbeClassifier.Classify(0, 200, 0, 65536, true, null, "").Status);
    Equal(ProbeStatus.Passed, ProbeClassifier.Classify(0, 200, 65536, 65536, true, null, "").Status);
    Equal(ProbeStatus.Failed, ProbeClassifier.Classify(28, 0, 100, 18000, true, null, "").Status);
});
Check("Hosts update is idempotent and preserves unrelated entries", () =>
{
    string original = "127.0.0.1 localhost\r\n10.0.0.1 private.example\r\n";
    string first = HostsBlock.Merge(original, "1.2.3.4 discord.com");
    Equal(first, HostsBlock.Merge(first, "1.2.3.4 discord.com"));
    Equal(original, HostsBlock.Merge(first, null));
    Throws<InvalidDataException>(() => HostsBlock.Merge(original, "bad discord.com"));
    Throws<InvalidDataException>(() => HostsBlock.Merge(HostsBlock.Begin, null));
});
Check("Atomic writes replace the file without leaving partial temporary data", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try { string path = Path.Combine(dir, "settings.json"); AtomicFile.WriteJson(path, new AppSettings()); AtomicFile.WriteJson(path, new AppSettings { StrategyId = "second" }); Equal("second", AtomicFile.ReadJson<AppSettings>(path)!.StrategyId); Equal(1, Directory.GetFiles(dir).Length); }
    finally { Directory.Delete(dir, true); }
});

await AsyncCheck("Cancellation restores the previous connection with a fresh token", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var engine = new FakeEngine(); bool journal = false;
    var runner = new TestCoordinator(engine, new FakeProbe(() => cancellation.Cancel()));
    var session = await runner.RunAsync(catalog.Take(1).ToArray(), required, TestMode.Quick, "test", new(), new SilentProgress(), _ => journal = true, () => journal = false, cancellation.Token);
    Assert(session.Cancelled); Assert(engine.Restored); Assert(!journal);
});
await AsyncCheck("A failed restoration leaves the recovery journal intact", async () =>
{
    var engine = new FakeEngine { FailRestore = true }; bool journal = false;
    try
    {
        await new TestCoordinator(engine, new FakeProbe()).RunAsync(catalog.Take(1).ToArray(), required, TestMode.Quick, "test", new(), new SilentProgress(), _ => journal = true, () => journal = false, CancellationToken.None);
        throw new Exception("Expected restoration failure");
    }
    catch (IOException) { Assert(journal); }
});
await AsyncCheck("The original connection is restored after successful selection", async () =>
{
    var engine = new FakeEngine();
    var report = await new TestCoordinator(engine, new FakeProbe()).RunAsync(catalog.Take(1).ToArray(), required, TestMode.Quick, "test", new(), new SilentProgress(), _ => { }, () => { }, CancellationToken.None);
    Assert(engine.Restored); Equal(catalog[0].Id, report.RecommendedStrategyId);
    Equal(2, engine.Starts); // Initial run and independent confirmation.
});
await AsyncCheck("A strategy is disqualified if its engine stops while probes are running", async () =>
{
    var engine = new FakeEngine { CrashDuringTest = true };
    var report = await new TestCoordinator(engine, new FakeProbe()).RunAsync(catalog.Take(1).ToArray(), required, TestMode.Quick, "test", new(), new SilentProgress(), _ => { }, () => { }, CancellationToken.None);
    Equal<string?>(null, report.RecommendedStrategyId); Assert(report.Reports[0].StartError != null); Assert(engine.Restored);
});

if (OperatingSystem.IsWindows())
    Check("Service command quoting round-trips through the Windows parser", () =>
    {
        string[] values = ["plain", "C:\\Program Files\\Z\\", "--path=C:\\with spaces\\file.bin", "", "a\"b", "literal!", "trailing\\"];
        IntPtr data = Native.CommandLineToArgvW("winws.exe " + string.Join(' ', values.Select(StrategyCatalog.QuoteWindowsArgument)), out int count);
        try
        {
            Equal(values.Length + 1, count);
            for (int i = 0; i < values.Length; i++) Equal(values[i], Marshal.PtrToStringUni(Marshal.ReadIntPtr(data, (i + 1) * IntPtr.Size)));
        }
        finally { Native.LocalFree(data); }
    });

Console.WriteLine($"{passed} passed; {failures.Count} failed.");
foreach (string failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

void Check(string name, Action action)
{ try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failures.Add(name + ": " + error); } }
async Task AsyncCheck(string name, Func<Task> action)
{ try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failures.Add(name + ": " + error); } }
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

sealed class SilentProgress : IProgress<TestProgress> { public void Report(TestProgress value) { } }
sealed class FakeEngine : IEngineController
{
    public bool Restored, FailRestore, CrashDuringTest;
    public bool TestProcessRunning => !CrashDuringTest;
    public int Starts;
    public Task<EngineSnapshot> CaptureAsync(CancellationToken token) => Task.FromResult(new EngineSnapshot("original", true, true));
    public Task StopAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StartTestAsync(Strategy strategy, string lists, AppSettings settings, CancellationToken token) { token.ThrowIfCancellationRequested(); Starts++; return Task.CompletedTask; }
    public Task RestoreAsync(EngineSnapshot snapshot, AppSettings settings, CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (FailRestore) throw new IOException("Restore failed"); Restored = true; return Task.CompletedTask; }
}
sealed class FakeProbe(Action? before = null) : IConnectivityProbe
{
    public Task<IReadOnlyList<ProbeResult>> RunAsync(IReadOnlyList<ProbeTarget> targets, TestMode mode, Action<ProbeResult> result, CancellationToken token)
    {
        before?.Invoke(); token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProbeResult>>(targets.Select(x => new ProbeResult(x.Id, x.Name, x.Group, "HTTP/1.1", ProbeStatus.Passed, 20, 200, 2000, "", x.Required)).ToArray());
    }
}
static partial class Native
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr data);
}
