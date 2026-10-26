using Zapret.Core;

internal static class SimpleToolsTests
{
    internal static void Run(Action<string, Action> check)
    {
        SensorSeries S(string id, string device, string name, string kind = "Temperature") => new(id, device, name, kind, []);
        check("Sensor tiles pick package CPU and core GPU temperatures", () =>
        {
            var catalog = new[] { S("/amdcpu/0/temperature/3", "Ryzen", "CCD1 (Tdie)"), S("/amdcpu/0/temperature/2", "Ryzen", "Core (Tctl/Tdie)"),
                S("/gpu-nvidia/0/temperature/2", "RTX", "GPU Hot Spot"), S("/gpu-nvidia/0/temperature/0", "RTX", "GPU Core"), S("/nvme/0/temperature/0", "SSD", "Temperature") };
            Assert(SensorTiles.CpuTemperature(catalog)!.Name == "Core (Tctl/Tdie)" && SensorTiles.GpuTemperature(catalog)!.Name == "GPU Core");
            Assert(SensorTiles.CpuTemperature([S("/nvme/0/temperature/0", "SSD", "Temperature")]) == null);
            Assert(SensorTiles.Temperature(60, false).Level == 0 && SensorTiles.Temperature(80, false).Level == 1 && SensorTiles.Temperature(92, true).Level == 2);
            Assert(SensorTiles.Load(95).Text == "Максимум");
        });
        check("PC brief lists processor, memory, real graphics and Windows without private rows", () =>
        {
            var section = new PcSection("summary", "Обзор", DateTimeOffset.UtcNow, [
                new("Компьютер", "Имя ПК", "SECRET-PC", true), new("Процессор", "Модель", "Intel(R) Core(TM) i5-12400F   "),
                new("Память", "Доступно системе", "31.8 ГиБ"), new("Видеокарта", "Модель", "Microsoft Basic Display Adapter"),
                new("Видеокарта", "Модель", "NVIDIA GeForce RTX 3060"), new("Windows", "Система", "Microsoft Windows 11 Pro, 64-bit")], []);
            string brief = PcBrief.From(section)!;
            Assert(brief == "Intel(R) Core(TM) i5-12400F  ·  31.8 ГиБ памяти  ·  NVIDIA GeForce RTX 3060  ·  Windows 11 Pro, 64-bit");
            Assert(PcBrief.From(section with { Rows = [] }) == null);
        });
        check("Timer tasks round-trip shutdown, restart and sleep actions", () =>
        {
            const string sid = "S-1-5-21-1-2-3-1001", exe = @"C:\Windows\System32\shutdown.exe";
            foreach (var action in Enum.GetValues<TimerAction>())
            {
                var plan = new ShutdownPlan(DateTimeOffset.Parse("2026-10-27T10:00:00Z"), action);
                var read = ShutdownTimerPolicy.ReadOwned(ShutdownTimerPolicy.TaskXml(plan, exe, sid), exe, sid);
                Assert(read == plan);
            }
            Assert(ShutdownTimerPolicy.TaskXml(new(DateTimeOffset.UtcNow, TimerAction.Sleep), exe, sid).Contains(@"WindowsPowerShell\v1.0\powershell.exe"));
            Assert(ShutdownTimerPolicy.TaskXml(new(DateTimeOffset.UtcNow, TimerAction.Restart), exe, sid).Contains("/r /t 0"));
        });
        check("Disk cleanup removes only old files below its folder and keeps links and the folder itself", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "x2-cleanup-" + Guid.NewGuid().ToString("N"));
            string outside = Path.Combine(Path.GetTempPath(), "x2-cleanup-outside-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "old"));
                Directory.CreateDirectory(outside);
                string old = Path.Combine(root, "old", "a.tmp"), fresh = Path.Combine(root, "b.tmp"), kept = Path.Combine(outside, "keep.txt");
                File.WriteAllBytes(old, new byte[300]); File.WriteAllBytes(fresh, new byte[50]); File.WriteAllText(kept, "keep");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3)); File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-3));
                bool linked = false;
                try { Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside); linked = true; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                var target = new CleanupTarget("t", "t", "", root, TimeSpan.FromDays(1));
                var estimate = DiskCleanup.Measure(target, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert(estimate.Files == 1 && estimate.Bytes == 300);
                Assert(DiskCleanup.Clean(target, DateTimeOffset.UtcNow, CancellationToken.None) == 300);
                Assert(!File.Exists(old) && File.Exists(fresh) && Directory.Exists(root) && !Directory.Exists(Path.Combine(root, "old")) && File.Exists(kept));
                if (linked) Assert(Directory.Exists(Path.Combine(root, "link")));
                Assert(DiskCleanup.Measure(target with { Root = "relative" }, DateTimeOffset.UtcNow, CancellationToken.None).Files == 0);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                if (Directory.Exists(outside)) Directory.Delete(outside, true);
            }
        });
        check("Startup approval values match Task Manager and keep the program's command intact", () =>
        {
            Assert(StartupApproval.Enabled(null) && StartupApproval.Enabled([2, 0, 0, 0]) && StartupApproval.Enabled([6]) && !StartupApproval.Enabled([3]) && !StartupApproval.Enabled([7]));
            var off = StartupApproval.Value(false, DateTimeOffset.Parse("2026-10-26T00:00:00Z"));
            Assert(off.Length == 12 && off[0] == 3 && BitConverter.ToInt64(off, 4) == DateTimeOffset.Parse("2026-10-26T00:00:00Z").ToFileTime());
            Assert(StartupApproval.Value(true, DateTimeOffset.UtcNow).SequenceEqual(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
            Assert(StartupApproval.Executable("\"C:\\Program Files\\App\\app.exe\" --tray") == "C:\\Program Files\\App\\app.exe");
            Assert(StartupApproval.Executable("C:\\Tools\\run.exe -silent") == "C:\\Tools\\run.exe");
        });
        check("Service verdicts name each service and separate a missing connection", () =>
        {
            var now = DateTimeOffset.UtcNow;
            Assert(ToolVerdicts.Services(new(now, [new("d", "Discord", true, 1, ""), new("y", "YouTube", true, 1, ""), new("c", "Internet", true, 1, "")])).Level == 0);
            var blocked = ToolVerdicts.Services(new(now, [new("d", "Discord", false, 1, ""), new("y", "YouTube", true, 1, ""), new("c", "Internet", true, 1, "")]));
            Assert(blocked.Level == 2 && blocked.Detail.Contains("Discord: не отвечает") && blocked.Detail.Contains("YouTube: отвечает"));
            Assert(ToolVerdicts.Services(new(now, [new("c", "Internet", false, 1, "")])).Title == "Интернет не подтверждён");
        });
        check("Autoselect advice distinguishes a missing connection from blocking", () =>
        {
            ProbeResult R(ProbeStatus status) => new("g", "g", "g", "HTTP/1.1", status, 10, null, 0, "");
            TestSession Session(TestMode mode, params ProbeResult[] baseline) => new(DateTimeOffset.UtcNow, mode, new("baseline", "baseline", baseline), [], [], null, false);
            Assert(AutoselectAdvice.For(Session(TestMode.Quick, R(ProbeStatus.Failed), R(ProbeStatus.Failed))).Single().Contains("подключение к интернету"));
            var blocked = AutoselectAdvice.For(Session(TestMode.Quick, R(ProbeStatus.Passed), R(ProbeStatus.Failed)));
            Assert(blocked.Count == 3 && blocked[0].Contains("DNS") && blocked[2].Contains("полную"));
            Assert(AutoselectAdvice.For(Session(TestMode.Full, R(ProbeStatus.Passed)))[2].Contains("DPI"));
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
