using System.Globalization;
using System.Text;

namespace Zapret.Core;

public sealed record SensorPoint(DateTimeOffset At, double Value);
public sealed record SensorAlert(string SensorId, double Above);
public sealed record SensorSeries(string Id, string Device, string Name, string Kind, IReadOnlyList<SensorPoint> Points)
{ public override string ToString() => Device + " · " + Name; }

public sealed class SensorHistory
{
    private sealed class State(HardwareReading reading)
    {
        internal HardwareReading Reading { get; } = reading;
        internal Queue<SensorPoint> Points { get; } = new();
        internal DateTimeOffset LastAt { get; set; } = DateTimeOffset.MinValue;
    }
    private readonly Dictionary<string, State> series = new();
    public const int MaxPoints = 1800;
    public IReadOnlyList<SensorSeries> Series => series.Values.Select(x => new SensorSeries(x.Reading.Id,
        x.Reading.Device, x.Reading.Name, x.Reading.Kind, x.Points.ToArray())).OrderBy(x => x.Device).ThenBy(x => x.Name).ToArray();
    public IReadOnlyList<SensorSeries> Catalog => series.Values.Select(x => new SensorSeries(x.Reading.Id,
        x.Reading.Device, x.Reading.Name, x.Reading.Kind, [])).OrderBy(x => x.Device).ThenBy(x => x.Name).ToArray();
    public SensorSeries? Find(string id) => series.TryGetValue(id, out var x) ? new(x.Reading.Id, x.Reading.Device,
        x.Reading.Name, x.Reading.Kind, x.Points.ToArray()) : null;
    public void Add(DateTimeOffset at, IEnumerable<HardwareReading> readings)
    {
        foreach (var key in series.Where(x => at - x.Value.LastAt > TimeSpan.FromHours(1)).Select(x => x.Key).ToArray()) series.Remove(key);
        foreach (var state in series.Values)
            while (state.Points.TryPeek(out var old) && at - old.At > TimeSpan.FromHours(1)) state.Points.Dequeue();
        foreach (var reading in readings)
        {
            if (reading.Value is not double value || !double.IsFinite(value)) continue;
            if (!series.TryGetValue(reading.Id, out var item))
            {
                if (series.Count >= 512) continue;
                item = new(reading); series.Add(reading.Id, item);
            }
            if (item.LastAt >= at) continue;
            item.LastAt = at;
            item.Points.Enqueue(new(at, value));
            while (item.Points.Count > MaxPoints || item.Points.TryPeek(out var first) && at - first.At > TimeSpan.FromHours(1)) item.Points.Dequeue();
        }
    }
    public void Clear() => series.Clear();
    public string Csv()
    {
        var result = new StringBuilder("Время (UTC),Устройство,Датчик,Показатель и единицы,Значение\r\n");
        static string Cell(string s) => "\"" + (s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@') ? "'" : "") + s.Replace("\"", "\"\"") + "\"";
        foreach (var sensor in Series)
            foreach (var point in sensor.Points)
                result.AppendLine(string.Join(',', Cell(point.At.UtcDateTime.ToString("O")), Cell(sensor.Device), Cell(sensor.Name), Cell(SensorStatistics.KindTitle(sensor.Kind)), point.Value.ToString("R", CultureInfo.InvariantCulture)));
        return result.ToString();
    }
}

public sealed class SensorAlertPolicy
{
    private readonly Dictionary<string, bool> latched = new();
    private readonly Dictionary<string, DateTimeOffset> last = new();
    public bool Observe(string id, double value, double threshold, DateTimeOffset now)
    {
        if (!double.IsFinite(value) || !double.IsFinite(threshold)) return false;
        if (value < threshold - Math.Max(0.1, Math.Abs(threshold) * .02)) { latched[id] = false; return false; }
        if (value < threshold || latched.GetValueOrDefault(id)) return false;
        latched[id] = true;
        if (last.TryGetValue(id, out var previous) && now - previous < TimeSpan.FromMinutes(5)) return false;
        last[id] = now; return true;
    }
}
