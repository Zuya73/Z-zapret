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
                    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден исполняемый файл приложения.");
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
                    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, (Assembly.GetEntryAssembly()?.GetName().Name ?? "x2zapret") + ".dll"));
                    start.ArgumentList.Add(workerArgument);
                    process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить сборщик датчиков.");
                    try { job = new(process); }
                    catch { StopLocked(); throw; }
                    process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
                }
                current = process ?? throw new InvalidOperationException("Сборщик не запущен.");
            }
            using var registration = deadline.Token.Register(() => StopIfCurrent(current));
            await current.StandardInput.WriteLineAsync("sample".AsMemory(), deadline.Token);
            await current.StandardInput.FlushAsync(deadline.Token);
            for (int lines = 0; lines < 30; lines++)
            {
                string? line = await current.StandardOutput.ReadLineAsync(deadline.Token);
                if (line == null) throw new IOException("Сборщик датчиков завершился. Нажмите «Обновить», чтобы запустить его заново.");
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("Ответ сборщика слишком большой.");
                if (!line.StartsWith(HardwareWorker.Prefix, StringComparison.Ordinal)) continue;
                return JsonSerializer.Deserialize<HardwareFrame>(line[HardwareWorker.Prefix.Length..]) ?? throw new InvalidDataException("Пустой ответ сборщика.");
            }
            throw new InvalidDataException("Не удалось прочитать ответ сборщика.");
        }
        catch (OperationCanceledException) { if (current != null) StopIfCurrent(current); token.ThrowIfCancellationRequested(); throw new TimeoutException("Сборщик датчиков не ответил вовремя."); }
        catch { if (current != null) StopIfCurrent(current); token.ThrowIfCancellationRequested(); if (deadline.IsCancellationRequested) throw new TimeoutException("Сборщик датчиков не ответил вовремя."); throw; }
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
                warnings.Add("Встроенный LibreHardwareMonitor 0.9.6. Минимум/максимум - с начала сеанса. SMART опрашивается не чаще раза в 30 секунд.");
            }
            if (category == "smart") warnings.Add("RAW и нормализованные значения имеют разный смысл. Нулевой RAW не является общей оценкой здоровья. Интерпретация зависит от модели диска.");
            if (category == "spd") warnings.Add("Показаны доступные SPD-тайминги DDR4/DDR5. Это паспортные данные, а не измерение текущих таймингов контроллера; XMP/EXPO и запись SPD не выполняются.");
            if (rows.Count == 0) warnings.Add("Данные недоступны на этом оборудовании или контроллере. Проверьте состояние PawnIO для CPU, платы и SPD.");
            return new(category, entry.Title, frame.CapturedAt, rows, warnings.Distinct().ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(category, entry.Title, DateTimeOffset.UtcNow, [], [error.Message]); }
    }
    internal void Stop() { Client.Stop(); statistics.Clear(); }
    public void Dispose() => Client.Dispose();
}
