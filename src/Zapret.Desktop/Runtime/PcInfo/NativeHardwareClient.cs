using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal sealed class NativeHardwareClient(string workerArgument = "--hardware-worker", TimeSpan? timeout = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private Process? process;
    private ChildProcessJob? job;
    internal int? ProcessId { get { lock (sync) return process?.Id; } }
    internal bool Running { get { lock (sync) return process is { HasExited: false }; } }
    internal async Task<HardwareFrame> SampleAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        Process? current = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(40));
        try
        {
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (process == null || process.HasExited)
                {
                    StopLocked();
                    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден файл x2zapret.exe. Запустите программу из полностью распакованной папки.");
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
                    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, (Assembly.GetEntryAssembly()?.GetName().Name ?? "x2zapret") + ".dll"));
                    start.ArgumentList.Add(workerArgument);
                    process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось включить чтение датчиков. Нажмите «Обновить» или перезапустите программу.");
                    try { job = new(process); }
                    catch { StopLocked(); throw; }
                    process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
                }
                current = process ?? throw new InvalidOperationException("Чтение датчиков не запущено. Нажмите «Обновить».");
            }
            using var registration = deadline.Token.Register(() => StopIfCurrent(current));
            await current.StandardInput.WriteLineAsync("sample".AsMemory(), deadline.Token);
            await current.StandardInput.FlushAsync(deadline.Token);
            for (int lines = 0; lines < 30; lines++)
            {
                string? line = await current.StandardOutput.ReadLineAsync(deadline.Token);
                if (line == null) throw new IOException("Чтение датчиков остановилось. Нажмите «Обновить», чтобы попробовать снова.");
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("Получено слишком много данных от датчиков. Сбор остановлен; попробуйте обновить показания.");
                if (!line.StartsWith(HardwareWorker.Prefix, StringComparison.Ordinal)) continue;
                return JsonSerializer.Deserialize<HardwareFrame>(line[HardwareWorker.Prefix.Length..]) ?? throw new InvalidDataException("Сборщик не вернул показания. Нажмите «Обновить».");
            }
            throw new InvalidDataException("Не удалось прочитать показания датчиков. Нажмите «Обновить».");
        }
        catch (OperationCanceledException) { if (current != null) StopIfCurrent(current); token.ThrowIfCancellationRequested(); throw new TimeoutException("Датчики не ответили за отведённое время. Нажмите «Обновить»; если ошибка повторяется, перезапустите программу."); }
        catch { if (current != null) StopIfCurrent(current); token.ThrowIfCancellationRequested(); if (deadline.IsCancellationRequested) throw new TimeoutException("Датчики не ответили за отведённое время. Нажмите «Обновить»; если ошибка повторяется, перезапустите программу."); throw; }
        finally { gate.Release(); }
    }
    private void StopIfCurrent(Process candidate) { lock (sync) if (ReferenceEquals(process, candidate)) StopLocked(); }
    internal void Stop() { lock (sync) StopLocked(); }
    private void StopLocked()
    {
        job?.Dispose(); job = null;
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
    }
    public void Dispose() => Stop();
}

internal sealed class HardwarePcInformation : IPcInformationSource, IDisposable
{
    internal readonly NativeHardwareClient Client = new();
    private readonly WindowsPcInformation windows = new();
    private readonly SensorStatistics statistics = new();
    private PcSection? legacy;
    internal static bool IsHardware(string id) => id is "sensors" or "smart" or "spd";
    public async Task<PcSection> ReadAsync(string category, CancellationToken token)
    {
        if (!IsHardware(category)) return await windows.ReadAsync(category, token);
        var entry = PcCategory.Find(category);
        try
        {
            var frame = await Client.SampleAsync(token);
            var warnings = new List<string>(frame.Warnings);
            IReadOnlyList<PcRow> rows = category switch { "smart" => frame.Smart, "spd" => frame.Spd, _ => statistics.Rows(frame.Readings) };
            if (category == "sensors")
            {
                if (legacy == null || DateTimeOffset.UtcNow - legacy.CapturedAt > TimeSpan.FromMinutes(1))
                    legacy = await windows.ReadAsync("sensors", token);
                rows = rows.Concat(legacy.Rows.Select(r => r with { Device = "WMI · " + r.Device })).ToArray();
                if (rows.Count == 0) warnings.AddRange(legacy.Warnings);
                warnings.Add("Минимум и максимум считаются с открытия раздела. Сведения о состоянии дисков обновляются не чаще раза в 30 секунд. Используется встроенный LibreHardwareMonitor 0.9.6.");
            }
            if (category == "smart") warnings.Add("Это показатели самодиагностики диска. RAW — исходное значение счётчика; его смысл зависит от модели. Ноль в одном поле не означает, что весь диск исправен.");
            if (category == "spd") warnings.Add("SPD — сведения, записанные производителем в модуль памяти. Здесь показаны доступные характеристики DDR4/DDR5, а не текущие настройки разгона. Программа не меняет память и профили XMP/EXPO.");
            if (rows.Count == 0) warnings.Add("Оборудование не передало эти данные. Для процессора, платы и памяти может потребоваться драйвер PawnIO — кнопка «Включить датчики процессора и памяти».");
            return new(category, entry.Title, frame.CapturedAt, rows, warnings.Distinct().ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(category, entry.Title, DateTimeOffset.UtcNow, [], [error.Message]); }
    }
    internal void Stop() { Client.Stop(); statistics.Clear(); }
    public void Dispose() => Client.Dispose();
}
