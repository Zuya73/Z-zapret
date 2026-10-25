using System.Globalization;

namespace Zapret.Core;

public sealed record HardwareReading(string Id, string Device, string Name, string Kind, double? Value);
public sealed record HardwareFrame(DateTimeOffset CapturedAt, string Driver, bool DriverReady,
    IReadOnlyList<HardwareReading> Readings, IReadOnlyList<PcRow> Smart, IReadOnlyList<PcRow> Spd, IReadOnlyList<string> Warnings);

public sealed class SensorStatistics
{
    private readonly Dictionary<string, (double Min, double Max)> ranges = new();
    public IReadOnlyList<PcRow> Rows(IEnumerable<HardwareReading> readings)
    {
        var rows = new List<PcRow>();
        foreach (var reading in readings)
        {
            if (!reading.Value.HasValue || !double.IsFinite(reading.Value.Value)) continue;
            double value = reading.Value.Value;
            var range = ranges.TryGetValue(reading.Id, out var known) ?
                (Math.Min(known.Min, value), Math.Max(known.Max, value)) : (value, value);
            ranges[reading.Id] = range;
            rows.Add(new(reading.Device, reading.Name, Format(value, reading.Kind), false,
                Format(range.Item1, reading.Kind), Format(range.Item2, reading.Kind)));
        }
        return rows;
    }
    public void Clear() => ranges.Clear();
    public static string Format(double value, string kind)
    {
        string unit = kind switch
        {
            "Temperature" => "°C", "Voltage" => "В", "Current" => "А", "Power" => "Вт",
            "Clock" => "МГц", "Frequency" => "Гц", "Load" or "Control" or "Level" or "Humidity" => "%",
            "Fan" => "об/мин", "Flow" => "л/ч", "Data" => "ГиБ", "SmallData" => "МиБ",
            "Throughput" => "Б/с", "TimeSpan" => "с", "Timing" => "нс", "Energy" => "мВт·ч", "Noise" => "дБА", _ => ""
        };
        return (value.ToString("0.###", CultureInfo.CurrentCulture) + " " + unit).TrimEnd();
    }
}
