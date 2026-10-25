using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class HardwareWorker
{
    internal const string Prefix = "X2HW1:";
    internal static void Run()
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        Computer? computer = null;
        try
        {
            while (Console.ReadLine() is { } command)
            {
                if (command == "close") return;
                if (command != "sample") continue;
                var warnings = new List<string>();
                var driver = PawnDriver.State();
                try
                {
                    if (computer == null)
                    {
                        // No controller/PSU control APIs or clock/voltage setters are exposed.
                        computer = new Computer { IsCpuEnabled = driver.Ready, IsGpuEnabled = true,
                            IsMemoryEnabled = driver.Ready, IsMotherboardEnabled = driver.Ready,
                            IsStorageEnabled = true, IsBatteryEnabled = true };
                        StorageDevice.ThrottleInterval = TimeSpan.FromSeconds(30);
                        computer.Open();
                    }
                    if (!driver.Ready) warnings.Add("Для датчиков процессора, платы и SPD включите подписанный драйвер PawnIO. Доступные GPU, диски и батарея работают независимо.");
                    var readings = new List<HardwareReading>(); var smart = new List<PcRow>(); var spd = new List<PcRow>();
                    foreach (var hardware in computer.Hardware) Visit(hardware);
                    if (readings.Count == 0) warnings.Add("Оборудование или драйверы не предоставили показания. Значения не подменяются нулями.");
                    Write(new(DateTimeOffset.UtcNow, driver.Version, driver.Ready, readings, smart, spd, warnings));

                    void Visit(IHardware hardware)
                    {
                        try
                        {
                            hardware.Update();
                            bool dimm = hardware.GetType().Name == "DimmMemory";
                            foreach (var sensor in hardware.Sensors)
                            {
                                if (sensor.Value is not { } value || !float.IsFinite(value)) continue;
                                if (hardware is StorageDevice disk && sensor.SensorType == SensorType.Temperature)
                                {
                                    if ((sensor.Name is "Temperature" or "Composite Temperature") && !disk.Storage.Smart.Temperature.HasValue) continue;
                                    if (sensor.Name == "Warning Temperature" && !disk.Storage.Smart.TemperatureWarning.HasValue) continue;
                                    if (sensor.Name == "Critical Temperature" && !disk.Storage.Smart.TemperatureCritical.HasValue) continue;
                                }
                                if (dimm && sensor.Name == "Capacity") continue; // Capacity is already read from SMBIOS, with explicit bytes.
                                readings.Add(new(sensor.Identifier.ToString(), hardware.Name, sensor.Name, sensor.SensorType.ToString(), value));
                                if (dimm && sensor.SensorType is SensorType.Timing or SensorType.Temperature)
                                    spd.Add(new(hardware.Name, sensor.Name, SensorStatistics.Format(value, sensor.SensorType.ToString())));
                            }
                            if (hardware is StorageDevice storage)
                            {
                                string device = "Диск " + storage.Storage.DriveNumber + " · " + storage.Name;
                                smart.Add(new(device, "Интерфейс NVMe", storage.Storage.IsNVMe ? "Да" : "Нет"));
                                smart.Add(new(device, "Прошивка", storage.Storage.Firmware ?? "—"));
                                foreach (var name in new[] { "SerialNumber", "Serial" })
                                    if (ReadProperty(storage.Storage, name) is { } serial) { smart.Add(new(device, "Серийный номер", serial, true)); break; }
                                foreach (var attribute in storage.Attributes)
                                {
                                    // Use the full raw integer, not SmartAttribute.Value (float loses precision).
                                    string raw = attribute.Attribute.Attribute.RawValueULong.ToString(CultureInfo.InvariantCulture);
                                    var data = attribute.Attribute.Attribute;
                                    string normalized = ReadProperty(data, "Current") ?? ReadProperty(data, "Value") ?? "—";
                                    string worst = ReadProperty(data, "Worst") ?? "—";
                                    smart.Add(new(device, $"{attribute.Id:X2} · {attribute.Name}", $"RAW: {raw}; текущее: {normalized}; худшее: {worst}; порог: {attribute.Threshold}"));
                                }
                                foreach (var sensor in storage.Sensors.Where(s => s.Value.HasValue && s.SensorType is not SensorType.Throughput and not SensorType.Load))
                                    smart.Add(new(device, sensor.Name, SensorStatistics.Format(sensor.Value!.Value, sensor.SensorType.ToString())));
                                if (storage.Attributes.Count == 0) warnings.Add("Не все устройства предоставляют таблицу SMART через текущий контроллер. Для NVMe могут быть доступны только отдельные показатели здоровья.");
                            }
                        }
                        catch (Exception error) { warnings.Add(hardware.HardwareType + ": " + error.GetType().Name); }
                        foreach (var sub in hardware.SubHardware) Visit(sub);
                    }
                }
                catch (Exception error)
                { Write(new(DateTimeOffset.UtcNow, driver.Version, driver.Ready, [], [], [], ["Не удалось открыть мониторинг: " + error.GetType().Name])); }
            }
        }
        finally { try { computer?.Close(); } catch (Exception) { } }
    }
    private static string? ReadProperty(object instance, string name)
    {
        var type = instance.GetType();
        object? value = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance)
            ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance);
        return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
    private static void Write(HardwareFrame frame)
    { Console.WriteLine(Prefix + JsonSerializer.Serialize(frame)); Console.Out.Flush(); }
}
