using System.Runtime.InteropServices;
using Zapret.Core;

int passed = 0;
var failures = new List<string>();
var strategiesPath = Path.Combine(args.FirstOrDefault() ?? ".", "resources", "catalogs", "strategies.json");
var catalog = StrategyCatalog.Load(strategiesPath);
var required = new[] { new ProbeTarget("discord", "Discord", "Discord", "https://discord.com", true), new ProbeTarget("youtube", "YouTube", "YouTube", "https://youtube.com", true) };
ProbeResult R(string id, ProbeStatus status = ProbeStatus.Passed, string protocol = "HTTP/1.1", double ms = 30) =>
    new(id, id, id, protocol, status, ms, 200, 2000, "", true);
StrategyReport Report(string id, params ProbeResult[] results) => new(id, id, results);

Check("A 900px window fits above the taskbar on a 1366x768 desktop", () =>
{
    var work = new Zapret.Desktop.Runtime.PixelBounds(0, 0, 1366, 728);
    var result = Zapret.Desktop.Runtime.WindowGeometry.Fit(new(43, -66, 1280, 900), work);
    Equal(new Zapret.Desktop.Runtime.PixelBounds(43, 0, 1280, 728), result);
});
Check("Window minimums fit the work area at 100, 125, 150 and 200 percent DPI", () =>
{
    foreach (uint dpi in new uint[] { 96, 120, 144, 192 })
    {
        var minimum = Zapret.Desktop.Runtime.WindowGeometry.Minimum(new(0, 0, 1366, 728), dpi);
        Assert(minimum.Width * dpi / 96 <= 1366.001);
        Assert(minimum.Height * dpi / 96 <= 728.001);
    }
});
Check("Maximizing respects taskbars on all four edges and negative monitor coordinates", () =>
{
    var monitor = new Zapret.Desktop.Runtime.PixelBounds(-1920, -1080, 1920, 1080);
    var areas = new Zapret.Desktop.Runtime.PixelBounds[]
    {
        new(-1920, -1080, 1920, 1040), new(-1920, -1040, 1920, 1040),
        new(-1880, -1080, 1880, 1080), new(-1920, -1080, 1880, 1080)
    };
    foreach (var area in areas)
    {
        var result = Zapret.Desktop.Runtime.WindowGeometry.Maximized(monitor, area);
        Equal(area.Left, monitor.Left + result.Left); Equal(area.Top, monitor.Top + result.Top);
        Equal(area.Width, result.Width); Equal(area.Height, result.Height);
    }
});
Check("Restoring a window after a monitor change keeps every edge in its work area", () =>
{
    var result = Zapret.Desktop.Runtime.WindowGeometry.Fit(new(1600, 850, 1600, 1000), new(-1280, 40, 1280, 680));
    Equal(new Zapret.Desktop.Runtime.PixelBounds(-1280, 40, 1280, 680), result);
});
Check("Every pinned upstream profile is imported exactly once", () =>
{
    var sources = Directory.GetFiles(Path.Combine(args.FirstOrDefault() ?? ".", "legacy", "flowseal"), "general*.bat").Select(Path.GetFileName).Order().ToArray();
    Equal(sources.Length, catalog.Count);
    Assert(sources.SequenceEqual(catalog.Select(x => x.Source).Order()));
});
Check("Portable bundles resolve resources beside the executable", () =>
{
    string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    var layout = BundleLayout.Resolve(root);
    Equal(root, layout.Catalogs); Equal(Path.Combine(root, "bin"), layout.Binaries);
    Equal(Path.Combine(root, "lists"), layout.SeedLists);
});
Check("Repository root executable resolves vendored engine and resource catalogs", () =>
{
    string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "vendor", "zapret"));
        var layout = BundleLayout.Resolve(root);
        Equal(Path.Combine(root, "resources", "catalogs"), layout.Catalogs);
        Equal(Path.Combine(root, "vendor", "zapret", "bin"), layout.Binaries);
        Equal(Path.Combine(root, "resources", "lists"), layout.SeedLists);
        Equal(Path.Combine(root, "vendor", "zapret", "engine-manifest.json"), layout.Manifest);
    }
    finally { Directory.Delete(root, true); }
});
Check("A complete portable layout wins when source folders are also present", () =>
{
    string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "resources", "catalogs"));
        File.WriteAllText(Path.Combine(root, "engine-manifest.json"), "{}");
        Equal(root, BundleLayout.Resolve(root).Catalogs);
    }
    finally { Directory.Delete(root, true); }
});
Check("Repeated options and literal exclamation survive import", () =>
{
    var fake = new Strategy("fixture", "Fixture", "fixture.bat", "", ["--dpi-desync-fake-tls=!", "--dpi-desync-fake-tls={BIN}a.bin", "--dpi-desync-fake-tls={BIN}b.bin"]);
    Assert(fake.Arguments.Contains("--dpi-desync-fake-tls=!"));
    Assert(fake.Arguments.Count(x => x.StartsWith("--dpi-desync-fake-tls=")) > 2);
});
Check("Disabled game filtering retains original disabled port", () =>
{
    var args = StrategyCatalog.Expand(FilterFixture(), "C:/Program Files/Z/bin", "C:/Data/lists", new());
    Assert(args.All(x => !x.Contains('{')));
    Assert(args[0].EndsWith(",12"));
    Assert(args.Any(x => x.Contains("C:/Program Files/Z/bin")));
});
Check("TCP and UDP filter ranges expand independently", () =>
{
    var args = StrategyCatalog.Expand(FilterFixture(), "bin", "lists", new() { GameFilter = GameFilterMode.Udp, TcpPorts = "2000-3000", UdpPorts = "4000,5000-6000" });
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

Check("Existing settings keep application autostart disabled", () =>
{
    var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"strategyId\":\"general-alt\",\"minimizeToTray\":true}", StrategyCatalog.JsonOptions)!;
    Equal(StartupMode.Disabled, old.Startup); Equal("general-alt", old.StrategyId);
});
Check("Only explicit Windows startup opens directly in the tray", () =>
{
    foreach (var mode in Enum.GetValues<StartupMode>()) Assert(!StartupPolicy.StartInTray(false, mode));
    Assert(!StartupPolicy.StartInTray(true, StartupMode.Disabled));
    Assert(StartupPolicy.StartInTray(true, StartupMode.Tray));
    Assert(StartupPolicy.StartInTray(true, StartupMode.TrayAndConnect));
});
Check("Startup task uses an interactive user and no stored password", () =>
{
    const string exe = @"C:\Program Files\Z & Friends\Z-zapret.exe";
    const string sid = "S-1-5-21-1-2-3-1001";
    string xml = StartupPolicy.TaskXml(exe, @"C:\Program Files\Z & Friends", sid);
    var document = System.Xml.Linq.XDocument.Parse(xml); var n = StartupPolicy.TaskNamespace;
    Equal(exe, document.Root!.Element(n + "Actions")!.Element(n + "Exec")!.Element(n + "Command")!.Value);
    Equal("--startup", document.Root.Element(n + "Actions")!.Element(n + "Exec")!.Element(n + "Arguments")!.Value);
    var principal = document.Root.Element(n + "Principals")!.Element(n + "Principal")!;
    Equal(sid, principal.Element(n + "UserId")!.Value);
    Equal("InteractiveToken", principal.Element(n + "LogonType")!.Value);
    Equal("HighestAvailable", principal.Element(n + "RunLevel")!.Value);
    Assert(!xml.Contains("<Password")); Assert(StartupPolicy.OwnsTask(xml));
    Assert(!StartupPolicy.OwnsTask(xml.Replace(StartupPolicy.TaskSource, "OtherApplication")));
    Throws<ArgumentException>(() => StartupPolicy.TaskXml(exe, "dir", "somebody & another-user"));
});
await AsyncCheck("Tray-only startup never starts a connection", async () =>
{
    int calls = 0;
    foreach (var mode in new[] { StartupMode.Disabled, StartupMode.Tray })
        await StartupPolicy.ConnectAsync(true, mode, false, _ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
    await StartupPolicy.ConnectAsync(false, StartupMode.TrayAndConnect, false, _ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
    Equal(0, calls);
});
await AsyncCheck("Automatic connection runs once and respects an already running service", async () =>
{
    int calls = 0; bool running = false;
    Task Connect(CancellationToken token) { token.ThrowIfCancellationRequested(); calls++; running = true; return Task.CompletedTask; }
    await StartupPolicy.ConnectAsync(true, StartupMode.TrayAndConnect, running, Connect, CancellationToken.None);
    await StartupPolicy.ConnectAsync(true, StartupMode.TrayAndConnect, running, Connect, CancellationToken.None);
    Equal(1, calls);
});

string Releases(string tag = "desktop-v2.0.0", bool prerelease = false, string? url = null, string? digest = null) =>
    System.Text.Json.JsonSerializer.Serialize(new[] { new { tag_name = tag, draft = false, prerelease,
        assets = new[] { new { name = "x2zapret-Setup-2.0.0-x64.exe",
            browser_download_url = url ?? "https://github.com/Zuya73/Z-zapret/releases/download/desktop-v2.0.0/x2zapret-Setup-2.0.0-x64.exe",
            size = 8192, digest = digest ?? "sha256:" + new string('a', 64) } } } });
Check("Only newer stable desktop releases are offered", () =>
{
    Equal(new Version(2, 0, 0), UpdateManifest.Parse(Releases(), new Version(1, 0, 0))!.Version);
    Equal<AppUpdate?>(null, UpdateManifest.Parse(Releases(), new Version(2, 0, 0)));
    Equal<AppUpdate?>(null, UpdateManifest.Parse(Releases(prerelease: true), new Version(1, 0, 0)));
    Equal<AppUpdate?>(null, UpdateManifest.Parse(Releases(tag: "1.10.5"), new Version(0, 1, 0)));
});
Check("Updates cannot download from another repository or without a digest", () =>
{
    Throws<InvalidDataException>(() => UpdateManifest.Parse(Releases(url: "https://github.com/other/repo/releases/download/v2.0.0/x2zapret-Setup-2.0.0-x64.exe"), new Version(1, 0, 0)));
    Throws<InvalidDataException>(() => UpdateManifest.Parse(Releases(digest: ""), new Version(1, 0, 0)));
    Throws<InvalidDataException>(() => UpdateManifest.Parse(Releases(url: "http://github.com/Zuya73/Z-zapret/releases/download/v2/x2zapret-Setup-2.0.0-x64.exe"), new Version(1, 0, 0)));
});
Check("Repository rename accepts only the two owned release paths", () =>
{
    const string filename = "x2zapret-Setup-2.0.0-x64.exe";
    Equal(new Version(2, 0, 0), UpdateManifest.Parse(Releases(url: "https://github.com/Zuya73/x2zapret/releases/download/desktop-v2.0.0/" + filename), new Version(0, 2, 0))!.Version);
    Assert(!UpdateManifest.TrustedDownload(new Uri("https://github.com/Zuya73/x2zapret-other/releases/download/v2/" + filename)));
    Assert(!UpdateManifest.TrustedDownload(new Uri("https://github.com/other/x2zapret/releases/download/v2/" + filename)));
});
await AsyncCheck("Verified installers survive fragmented HTTP reads", async () =>
{
    byte[] bytes = new byte[8192]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
    string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try
    {
        using var http = new System.Net.Http.HttpClient(new DownloadHandler(bytes, fragmented: true));
        var info = new AppUpdate(new Version(2, 0, 0), "desktop-v2.0.0", new Uri("https://github.com/Zuya73/Z-zapret/releases/download/v2/installer.exe"), bytes.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        string file = await UpdateDownloader.DownloadAsync(http, info, dir, new QuietDownloadProgress(), CancellationToken.None);
        Assert(File.ReadAllBytes(file).SequenceEqual(bytes));
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});
await AsyncCheck("Checksum failures remove downloads instead of leaving an executable", async () =>
{
    byte[] bytes = new byte[8192]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
    string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try
    {
        using var http = new System.Net.Http.HttpClient(new DownloadHandler(bytes));
        var info = new AppUpdate(new Version(2, 0, 0), "desktop-v2.0.0", new Uri("https://github.com/Zuya73/Z-zapret/releases/download/v2/installer.exe"), bytes.Length, new string('0', 64));
        try { await UpdateDownloader.DownloadAsync(http, info, dir, new QuietDownloadProgress(), CancellationToken.None); throw new Exception("Expected checksum failure"); }
        catch (InvalidDataException) { Equal(0, Directory.GetFileSystemEntries(dir).Length); }
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});
await AsyncCheck("Cancelling an update leaves no installer behind", async () =>
{
    string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try
    {
        using var http = new System.Net.Http.HttpClient(new DownloadHandler(new byte[8192]));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var info = new AppUpdate(new Version(2, 0, 0), "desktop-v2.0.0", new Uri("https://github.com/Zuya73/Z-zapret/releases/download/v2/installer.exe"), 8192, new string('0', 64));
        try { await UpdateDownloader.DownloadAsync(http, info, dir, new QuietDownloadProgress(), stop.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { Equal(0, Directory.GetFileSystemEntries(dir).Length); }
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
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


Check("Shutdown input accepts hours and minutes and rejects unsafe or empty durations", () =>
{
    Equal(TimeSpan.FromMinutes(90), ShutdownTimerPolicy.Delay("1", "30"));
    Equal(TimeSpan.FromDays(7), ShutdownTimerPolicy.Delay("168", "0"));
    Throws<FormatException>(() => ShutdownTimerPolicy.Delay("1 & shutdown", "0"));
    Throws<FormatException>(() => ShutdownTimerPolicy.Delay("0", "60"));
    Throws<ArgumentOutOfRangeException>(() => ShutdownTimerPolicy.Delay("0", "0"));
    Throws<ArgumentOutOfRangeException>(() => ShutdownTimerPolicy.Delay("168", "1"));
});
Check("Shutdown task is one-shot, non-forced and bound to an absolute UTC deadline", () =>
{
    var deadline = new DateTimeOffset(2026, 10, 25, 22, 15, 0, TimeSpan.FromHours(4));
    string xml = ShutdownTimerPolicy.TaskXml(new(deadline), @"C:\Windows\System32\shutdown.exe", "S-1-5-21-1-2-3-1001");
    var n = StartupPolicy.TaskNamespace;
    var doc = System.Xml.Linq.XDocument.Parse(xml);
    Equal("2026-10-25T18:15:00Z", doc.Descendants(n + "StartBoundary").Single().Value);
    Assert(!ShutdownTimerPolicy.Arguments.Contains("/f"));
    Assert(ShutdownTimerPolicy.Arguments.Contains("/t 0"));
    Equal("false", doc.Descendants(n + "StartWhenAvailable").Single().Value);
    Equal("false", doc.Descendants(n + "WakeToRun").Single().Value);
    Equal(deadline.ToUniversalTime(), ShutdownTimerPolicy.ReadOwned(xml, @"C:\Windows\System32\shutdown.exe", "S-1-5-21-1-2-3-1001").Deadline);
    Equal("1 д 01:00:00", ShutdownTimerPolicy.Remaining(deadline.AddHours(25), deadline));
    Equal("00:00:00", ShutdownTimerPolicy.Remaining(deadline.AddSeconds(-1), deadline));
});
await AsyncCheck("Timer is restored by another controller and cancellation is idempotent", async () =>
{
    var backend = new TimerBackend();
    const string exe = @"C:\Windows\System32\shutdown.exe", sid = "S-1-5-21-1-2-3-1001";
    var first = new ShutdownTimerController(backend, exe, sid);
    var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    var scheduled = await first.ScheduleAsync(TimeSpan.FromMinutes(75), now, CancellationToken.None);
    var restored = new ShutdownTimerController(backend, exe, sid);
    Equal(scheduled, await restored.ReadAsync(CancellationToken.None));
    await restored.CancelAsync(CancellationToken.None);
    await restored.CancelAsync(CancellationToken.None);
    Equal(1, backend.Deletes);
    Equal<ShutdownPlan?>(null, await restored.ReadAsync(CancellationToken.None));
});
await AsyncCheck("Foreign and modified shutdown tasks are never replaced or deleted", async () =>
{
    const string exe = @"C:\Windows\System32\shutdown.exe", sid = "S-1-5-21-1-2-3-1001";
    string own = ShutdownTimerPolicy.TaskXml(new(DateTimeOffset.UtcNow.AddHours(1)), exe, sid);
    foreach (string foreign in new[] { own.Replace(ShutdownTimerPolicy.Source, "foreign"),
        own.Replace(ShutdownTimerPolicy.Arguments.Replace("\"", "&quot;"), "/s /f /t 0"),
        own.Replace(sid, "S-1-5-21-9-9-9-1001") })
    {
        // Attribute/text escaping may differ; use XML to change the action explicitly.
        string xml = foreign;
        if (xml == own)
        {
            var document = System.Xml.Linq.XDocument.Parse(own);
            document.Descendants(StartupPolicy.TaskNamespace + "Arguments").Single().Value = "/s /f /t 0";
            xml = document.ToString();
        }
        var backend = new TimerBackend { Xml = xml };
        var timer = new ShutdownTimerController(backend, exe, sid);
        bool rejectedSchedule = false, rejectedCancel = false;
        try { await timer.ScheduleAsync(TimeSpan.FromHours(1), DateTimeOffset.UtcNow, CancellationToken.None); }
        catch (InvalidOperationException) { rejectedSchedule = true; }
        try { await timer.CancelAsync(CancellationToken.None); }
        catch (InvalidOperationException) { rejectedCancel = true; }
        Assert(rejectedSchedule && rejectedCancel);
        Equal(0, backend.Registers); Equal(0, backend.Deletes);
    }
});
Check("Upstream list updates replace defaults while retaining user edits and lists", () => Temporary(directory =>
{
    string source = Path.Combine(directory, "source"), data = Path.Combine(directory, "data");
    Directory.CreateDirectory(source); Directory.CreateDirectory(data);
    File.WriteAllText(Path.Combine(source, "list-general.txt"), "old.example\r\n");
    File.WriteAllText(Path.Combine(data, "list-general.txt"), "old.example\n");
    File.WriteAllText(Path.Combine(data, "list-general-user.txt"), "my.example\n");
    string receipt = Path.Combine(directory, "receipt.json");
    var baseline = new Dictionary<string, string> { ["list-general.txt"] = SeedLists.ContentHash(Path.Combine(data, "list-general.txt")) };
    File.WriteAllText(Path.Combine(source, "list-general.txt"), "new.example\n");
    SeedLists.Synchronize(source, data, receipt, baseline);
    Equal("new.example\n", File.ReadAllText(Path.Combine(data, "list-general.txt")));
    File.WriteAllText(Path.Combine(data, "list-general.txt"), "new.example\ncustom.example\n");
    File.WriteAllText(Path.Combine(source, "list-general.txt"), "third.example\n");
    File.WriteAllText(Path.Combine(source, "list-general-user.txt"), "must-not-overwrite.example\n");
    var preserved = SeedLists.Synchronize(source, data, receipt, baseline);
    Assert(preserved.Contains("list-general.txt"));
    Equal("new.example\ncustom.example\n", File.ReadAllText(Path.Combine(data, "list-general.txt")));
    Equal("third.example\n", File.ReadAllText(Path.Combine(data, "list-general.txt.upstream")));
    Equal("my.example\n", File.ReadAllText(Path.Combine(data, "list-general-user.txt")));
}));
Check("Bundle migration retains available preferences and replaces only removed components", () =>
{
    var saved = new AppSettings { StrategyId = catalog[0].Id, DiscordFake = "known.bin", GameFake = "removed.bin",
        AutoUpdate = true, Startup = StartupMode.TrayAndConnect, TcpPorts = "12345" };
    var migrated = SettingsMigration.ForBundle(saved, catalog, ["known.bin", "quic_new.bin"]);
    Equal(saved.StrategyId, migrated.StrategyId); Equal("known.bin", migrated.DiscordFake); Equal("quic_new.bin", migrated.GameFake);
    Equal(saved.Startup, migrated.Startup); Equal(saved.TcpPorts, migrated.TcpPorts); Assert(migrated.AutoUpdate);
});
Check("Engine integrity is checked before any installed file is changed", () => Temporary(directory =>
{
    string source = Path.Combine(directory, "source"), data = Path.Combine(directory, "data");
    Directory.CreateDirectory(source); Directory.CreateDirectory(data);
    File.WriteAllText(Path.Combine(source, "a.bin"), "new");
    File.WriteAllText(Path.Combine(data, "a.bin"), "old");
    var hashes = new Dictionary<string, string> { ["a.bin"] = new string('0', 64) };
    Throws<InvalidDataException>(() => EngineFiles.Synchronize(source, data, hashes));
    Equal("old", File.ReadAllText(Path.Combine(data, "a.bin")));
}));
Check("Engine updates preserve active fakes and roll back replacements on a locked file", () => Temporary(directory =>
{
    string source = Path.Combine(directory, "source"), data = Path.Combine(directory, "data");
    Directory.CreateDirectory(source); Directory.CreateDirectory(data);
    foreach (string name in new[] { "a.bin", "b.bin", "ACTIVE_GAME_UDP.bin" })
    {
        File.WriteAllText(Path.Combine(source, name), "new-" + name);
        File.WriteAllText(Path.Combine(data, name), "old-" + name);
    }
    var hashes = Directory.GetFiles(source).ToDictionary(x => Path.GetFileName(x)!, EngineFiles.Hash);
    if (OperatingSystem.IsWindows())
    {
        bool rejected = false;
        using (var locked = new FileStream(Path.Combine(data, "b.bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
            try { EngineFiles.Synchronize(source, data, hashes); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
        Assert(rejected);
        Equal("old-a.bin", File.ReadAllText(Path.Combine(data, "a.bin")));
        Equal(0, Directory.GetDirectories(data, ".update-*").Length);
    }
    EngineFiles.Synchronize(source, data, hashes);
    Equal("new-a.bin", File.ReadAllText(Path.Combine(data, "a.bin")));
    Equal("old-ACTIVE_GAME_UDP.bin", File.ReadAllText(Path.Combine(data, "ACTIVE_GAME_UDP.bin")));
}));

if (Array.IndexOf(args, "--published-update") is int publishedIndex && publishedIndex >= 0)
{
    Check("A client on 0.2.2 discovers the published desktop installer", () =>
    {
        string json = File.ReadAllText(args[publishedIndex + 1]);
        var update = UpdateManifest.Parse(json, new Version(0, 2, 2));
        Assert(update != null);
        Assert(update!.Version >= UpdateManifest.CurrentVersion);
        Assert(UpdateManifest.TrustedDownload(update.DownloadUrl));
        Console.WriteLine($"Published update visible to 0.2.2: {update.Version}, {update.Size} bytes, SHA256 {update.Sha256}");
    });
}
Console.WriteLine($"{passed} passed; {failures.Count} failed.");
foreach (string failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

Strategy FilterFixture() => new("fixture", "Fixture", "fixture.bat", "",
    ["--wf-tcp=80,443,{GAME_TCP}", "--wf-udp=443,{GAME_UDP}", "--dpi-desync-fake-tls={BIN}fake.bin", "--hostlist={LISTS}domains.txt"]);
void Temporary(Action<string> action)
{
    string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { action(directory); } finally { Directory.Delete(directory, true); }
}
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

sealed class QuietDownloadProgress : IProgress<DownloadProgress> { public void Report(DownloadProgress value) { } }
sealed class DownloadHandler(byte[] bytes, bool fragmented = false) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = fragmented ? new System.Net.Http.StreamContent(new FragmentedStream(bytes)) : new System.Net.Http.ByteArrayContent(bytes) });
    }
}
sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}

sealed class TimerBackend : IShutdownTimerBackend
{
    public string? Xml;
    public int Registers, Deletes;
    public Task<string?> ReadXmlAsync(CancellationToken token) => Task.FromResult(Xml);
    public Task RegisterAsync(string xml, CancellationToken token) { Registers++; Xml = xml; return Task.CompletedTask; }
    public Task DeleteAsync(CancellationToken token) { Deletes++; Xml = null; return Task.CompletedTask; }
}
