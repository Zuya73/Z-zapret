using Zapret.Core;

internal static class HardwareTelemetryTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Sensors ignore missing and non-finite readings rather than inventing zero", () =>
        {
            var stats = new SensorStatistics();
            var rows = stats.Rows([new("a", "GPU", "Temperature", "Temperature", null), new("b", "CPU", "Clock", "Clock", double.NaN), new("c", "CPU", "Load", "Load", 0)]);
            Assert(rows.Count == 1 && rows[0].Value.StartsWith("0"));
        });
        check("Sensor extrema follow stable identifiers even when devices share names", () =>
        {
            var stats = new SensorStatistics();
            stats.Rows([new("a", "CPU", "Temperature", "Temperature", 45), new("b", "CPU", "Temperature", "Temperature", 65)]);
            var rows = stats.Rows([new("b", "CPU", "Temperature", "Temperature", 60), new("a", "CPU", "Temperature", "Temperature", 50)]);
            Assert(rows[0].Minimum.StartsWith("60") && rows[0].Maximum.StartsWith("65"));
            Assert(rows[1].Minimum.StartsWith("45") && rows[1].Maximum.StartsWith("50"));
            stats.Clear(); Assert(stats.Rows([new("a", "CPU", "Temperature", "Temperature", 55)])[0].Minimum.StartsWith("55"));
        });
        check("Private values stay redacted in min and max and report output", () =>
        {
            var section = new PcSection("sensors", "Sensors", DateTimeOffset.UtcNow, [new("Device", "Private", "SECRET", true, "MIN-SECRET", "MAX-SECRET")], []);
            Assert(!PcInformation.Report([section], false).Contains("SECRET"));
            Assert(PcInformation.Report([section], true).Contains("MIN-SECRET"));
        });
        check("Native hardware names cannot inject report headings or control characters", () =>
        {
            var section = new PcSection("sensors", "Sensors", DateTimeOffset.UtcNow, [new("Device\n[Injected]", "Temperature", "42\u001b")], []);
            var row = PcInformation.Visible(section, "", false).Single();
            Assert(!row.Device.Contains('\n') && !row.Value.Contains('\u001b'));
        });
        check("Sensor units preserve timing and distinguish clocks from throughput", () =>
        {
            Assert(SensorStatistics.Format(12, "Timing").EndsWith("нс"));
            Assert(SensorStatistics.Format(12, "Clock").EndsWith("МГц"));
            Assert(SensorStatistics.Format(12, "Throughput").EndsWith("Б/с"));
        });
    }
    private static void Assert(bool ok) { if (!ok) throw new InvalidOperationException("Hardware telemetry regression"); }
}
