using System.Globalization;
using Zapret.Core;

internal static class DiskBenchmarkTests
{
    private const string File = @"D:\x2zapret-disk-test-0123456789abcdef0123456789abcdef\data.bin";
    internal static void Run(Action<string, Action> check)
    {
        check("Disk benchmarks accept only supported bounded sizes and durations", () =>
        {
            DiskBenchmarkOptions.Quick.Validate();
            Throws(() => new DiskBenchmarkOptions(10, 3, 1).Validate());
            Throws(() => new DiskBenchmarkOptions(DiskBenchmarkOptions.Quick.FileBytes, 100, 10).Validate());
            Equal(@"D:\", DiskBenchmarkPolicy.Root(@"d:\"));
            Throws(() => DiskBenchmarkPolicy.Root(@"\\server\share"));
            Throws(() => DiskBenchmarkPolicy.Root(@"C:\Windows\"));
        });
        check("DiskSpd cannot receive a raw device, existing user file or creation switch", () =>
        {
            foreach (string bad in new[] { "#0", "D:", @"\\.\PhysicalDrive0", @"D:\photos\data.bin", @"D:\x2zapret-disk-test-0123456789abcdef0123456789abcdef\..\important.bin" })
                Throws(() => DiskBenchmarkPolicy.Arguments(bad, DiskBenchmarkCase.All[0], true, 3));
            foreach (var profile in DiskBenchmarkCase.All)
                foreach (bool write in new[] { false, true })
                {
                    var args = DiskBenchmarkPolicy.Arguments(File, profile, write, 3);
                    Equal(File, args[^1]); Assert(!args.Any(a => a.StartsWith("-c", StringComparison.OrdinalIgnoreCase)));
                    Assert(args.Contains("-Su") && args.Contains("-W0") && args.Contains("-t1") && args.Contains("-Rxml"));
                    Assert(args.Contains(write ? "-w100" : "-w0"));
                    Equal(profile.Random, args.Contains("-r4K"));
                }
        });
        check("DiskSpd XML parser sums only measured thread targets using invariant culture", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
                var value = DiskBenchmarkPolicy.Parse(Xml(2, 1, 4), DiskBenchmarkCase.All[0], false, 1, File);
                Equal(3UL * 1048576, value.Bytes); Equal(3UL, value.Operations);
                Equal(.75, value.Iops); Equal(.25, value.AverageLatencyMs); Equal(.8, value.P95LatencyMs!.Value);
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        check("DiskSpd parser rejects wrong targets, zero work and changed workload", () =>
        {
            Throws(() => DiskBenchmarkPolicy.Parse(Xml(0, 0, 1), DiskBenchmarkCase.All[0], false, 1, File));
            Throws(() => DiskBenchmarkPolicy.Parse(Xml(1, 0, 1).Replace(File, @"C:\important.bin"), DiskBenchmarkCase.All[0], false, 1, File));
            Throws(() => DiskBenchmarkPolicy.Parse(Xml(1, 0, 1).Replace("<WriteCount>0", "<WriteCount>1"), DiskBenchmarkCase.All[0], false, 1, File));
            Throws(() => DiskBenchmarkPolicy.Parse(Xml(1, 0, 1).Replace("<ReadBytes>1048576", "<ReadBytes>1"), DiskBenchmarkCase.All[0], false, 1, File));
        });
        check("DiskSpd XML parser rejects external entity documents", () =>
        {
            Throws(() => DiskBenchmarkPolicy.Parse("<!DOCTYPE Results [<!ENTITY e SYSTEM 'file:///etc/passwd'>]>" + Xml(1, 0, 1), DiskBenchmarkCase.All[0], false, 1, File));
        });
        check("Benchmark rollups weight throughput by duration and latency by operations", () =>
        {
            var value = DiskBenchmarkPolicy.Aggregate(new[] {
                new DiskMeasurement("seq8", false, 1, 100_000_000, 100, 1, 1, 2),
                new DiskMeasurement("seq8", false, 2, 300_000_000, 300, 3, 3, 4) })!;
            Equal(100d, value.MegabytesPerSecond); Equal(100d, value.Iops); Equal(2.5, value.AverageLatencyMs); Equal(4d, value.P95LatencyMs!.Value);
            Throws(() => DiskBenchmarkPolicy.Aggregate(new[] { value, value with { Write = true } }));
        });
        check("Cancelled disk reports retain completed measurements and cleanup notes", () =>
        {
            var item = new DiskMeasurement("seq8", false, 1, 1048576, 1, 1, .1, .2);
            var report = new DiskBenchmarkReport(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, @"D:\", DiskBenchmarkOptions.Quick, "Cancelled", [item], "", "Temporary directory contains a foreign file");
            string text = DiskBenchmarkPolicy.Export(report);
            Assert(text.Contains("Cancelled") && text.Contains("SEQ1M Q8T1") && text.Contains("Temporary directory") && text.Contains("1/1"));
        });
    }
    private static string Xml(int first, int second, double seconds) => $"<Results><Profile><TimeSpan><Target><ReadBytes>99999999</ReadBytes></Target></TimeSpan></Profile><TimeSpan><TestTimeSeconds>{seconds.ToString(CultureInfo.InvariantCulture)}</TestTimeSeconds><Latency><AverageReadMilliseconds>0.25</AverageReadMilliseconds><Bucket><Percentile>95</Percentile><ReadMilliseconds>0.8</ReadMilliseconds></Bucket></Latency>{Thread(first)}{Thread(second)}</TimeSpan></Results>";
    private static string Thread(int operations) => $"<Thread><Target><Path>{File}</Path><ReadBytes>{operations * 1048576L}</ReadBytes><ReadCount>{operations}</ReadCount><WriteBytes>0</WriteBytes><WriteCount>0</WriteCount></Target></Thread>";
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Disk benchmark regression"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}"); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or System.Xml.XmlException or OverflowException) { return; }
        throw new InvalidOperationException("Unsafe input was accepted");
    }
}
