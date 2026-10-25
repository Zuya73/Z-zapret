namespace Zapret.Core;

public sealed record LatencySample(int Sequence, double? Milliseconds);
public sealed record LatencySummary(int Sent, int Received, double? AverageMs, double? MinimumMs, double? MaximumMs, double? JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100d * (Sent - Received) / Sent;
}
public static class NetworkMeasurements
{
    public static LatencySummary Summarize(IReadOnlyList<LatencySample> samples)
    {
        var good = samples.Where(x => x.Milliseconds is >= 0 && double.IsFinite(x.Milliseconds.Value)).ToArray();
        var differences = samples.Zip(samples.Skip(1)).Where(x => x.First.Milliseconds is >= 0 && x.Second.Milliseconds is >= 0 &&
            double.IsFinite(x.First.Milliseconds.Value) && double.IsFinite(x.Second.Milliseconds.Value))
            .Select(x => Math.Abs(x.First.Milliseconds!.Value - x.Second.Milliseconds!.Value)).ToArray();
        return new(samples.Count, good.Length, good.Length == 0 ? null : good.Average(x => x.Milliseconds!.Value),
            good.Length == 0 ? null : good.Min(x => x.Milliseconds), good.Length == 0 ? null : good.Max(x => x.Milliseconds),
            differences.Length == 0 ? null : differences.Average());
    }
    public static double MegabitsPerSecond(long bytes, TimeSpan elapsed) => bytes > 0 && elapsed.TotalSeconds > 0 ? bytes * 8d / elapsed.TotalSeconds / 1_000_000 : 0;
}
