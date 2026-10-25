using Zapret.Core;

internal static class PcInformationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("PC categories reject command injection and unknown sections", () =>
        {
            Assert(PcCategory.All.Count == 18 && PcCategory.All.Select(x => x.Id).Distinct().Count() == 18);
            try { PcCategory.Find("summary'; shutdown /s"); throw new Exception("Accepted unknown category"); }
            catch (ArgumentException) { }
        });
        check("PC snapshots handle empty arrays and preserve availability warnings", () =>
        {
            var section = PcInformation.Parse("sensors", "{\"rows\":[],\"warnings\":[\"Sensor provider absent\"]}");
            Assert(section.Rows.Count == 0 && section.Warnings.Single() == "Sensor provider absent");
            Assert(PcInformation.Parse("cpu", "{}").Warnings.Count == 1);
        });
        check("PC search and copy never reveal hidden private values", () =>
        {
            var section = Example();
            Assert(PcInformation.Visible(section, "SECRET-123", false).Count == 0);
            Assert(PcInformation.Visible(section, "SECRET-123", true).Count == 1);
            Assert(PcInformation.Visible(section, "serial", false).Single().Value == PcInformation.Hidden);
            Assert(PcInformation.Visible(section, "rYzEn", false).Count == 1);
        });
        check("PC report redacts all private values unless explicitly included", () =>
        {
            var section = Example();
            string safe = PcInformation.Report([section], false), full = PcInformation.Report([section], true);
            Assert(!safe.Contains("SECRET-123") && safe.Contains("Ryzen") && safe.Contains("Собрано разделов: 1 из " + PcCategory.All.Count));
            Assert(full.Contains("SECRET-123"));
            Assert(section.Rows[1].Value == "SECRET-123");
        });
        check("PC report cannot inject new headings through control characters", () =>
        {
            var section = PcInformation.Parse("cpu", "{\"rows\":[{\"Device\":\"CPU\\n[Fake]\",\"Property\":\"Model\",\"Value\":\"A\\u001bB\"}],\"warnings\":[]}");
            Assert(!section.Rows[0].Device.Contains('\n') && !section.Rows[0].Value.Contains('\u001b'));
        });
        check("PC session caches snapshots and refresh explicitly replaces them", () =>
        {
            var source = new FakeSource(); var session = new PcInformationSession(source);
            session.ReadAsync("cpu", false, default).GetAwaiter().GetResult();
            session.ReadAsync("cpu", false, default).GetAwaiter().GetResult(); Assert(source.Calls == 1);
            session.ReadAsync("cpu", true, default).GetAwaiter().GetResult(); Assert(source.Calls == 2);
        });
        check("Cancelled PC collection never caches a stale result", () =>
        {
            using var cts = new CancellationTokenSource();
            var source = new FakeSource { DuringRead = cts.Cancel }; var session = new PcInformationSession(source);
            try { session.ReadAsync("cpu", false, cts.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation was ignored"); }
            catch (OperationCanceledException) { }
            source.DuringRead = null; session.ReadAsync("cpu", false, default).GetAwaiter().GetResult(); Assert(source.Calls == 2);
        });
    }
    private static PcSection Example() => new("cpu", "Процессор", DateTimeOffset.UtcNow,
        [new("CPU", "Model", "Ryzen"), new("CPU", "Serial", "SECRET-123", true)], []);
    private static void Assert(bool ok) { if (!ok) throw new InvalidOperationException("PC information regression"); }
    private sealed class FakeSource : IPcInformationSource
    {
        internal int Calls;
        internal Action? DuringRead;
        public Task<PcSection> ReadAsync(string category, CancellationToken token)
        { Calls++; DuringRead?.Invoke(); return Task.FromResult(Example()); }
    }
}
