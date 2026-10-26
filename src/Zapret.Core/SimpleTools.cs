using System.Text.RegularExpressions;

namespace Zapret.Core;

public static class SensorTiles
{
    public const double OverheatCelsius = 90;

    public static SensorSeries? CpuTemperature(IReadOnlyList<SensorSeries> catalog) =>
        Best(catalog.Where(s => s.Kind == "Temperature" && s.Id.Contains("cpu/", StringComparison.OrdinalIgnoreCase)), "Package", "Tctl", "Tdie", "Core Max", "Core Average", "CPU");
    public static SensorSeries? GpuTemperature(IReadOnlyList<SensorSeries> catalog) =>
        Best(catalog.Where(s => s.Kind == "Temperature" && s.Id.Contains("/gpu", StringComparison.OrdinalIgnoreCase)), "GPU Core", "Core", "Hot Spot");

    // Prefer the reading that best represents the whole chip; fall back to any temperature of that device.
    private static SensorSeries? Best(IEnumerable<SensorSeries> candidates, params string[] names)
    {
        var list = candidates.ToArray();
        foreach (string name in names)
            if (list.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) is { } match) return match;
        return list.FirstOrDefault();
    }

    public static (string Text, int Level) Temperature(double celsius, bool gpu) =>
        celsius < (gpu ? 75 : 70) ? ("Норма", 0) : celsius < (gpu ? 88 : 85) ? ("Тепло", 1) : ("Перегрев", 2);
    public static (string Text, int Level) Load(double percent) =>
        percent < 70 ? ("Спокойно", 0) : percent < 90 ? ("Высокая", 1) : ("Максимум", 2);
}

public static class PcBrief
{
    // One line for people who only want to know "what is inside": processor, memory, graphics and Windows.
    public static string? From(PcSection summary)
    {
        string? Value(string device, string property, Func<string, bool>? accept = null) => summary.Rows
            .Where(r => r.Device == device && r.Property == property && !r.Sensitive && r.Value.Length > 0 && r.Value != "—" && (accept?.Invoke(r.Value) ?? true))
            .Select(r => Regex.Replace(r.Value, @"\s+", " ").Trim()).FirstOrDefault();
        var parts = new[]
        {
            Value("Процессор", "Модель"),
            Value("Память", "Доступно системе") is { } memory ? memory + " памяти" : null,
            Value("Видеокарта", "Модель", v => !v.Contains("Basic", StringComparison.OrdinalIgnoreCase) && !v.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                ?? Value("Видеокарта", "Модель"),
            Value("Windows", "Система")?.Replace("Microsoft ", "", StringComparison.Ordinal)
        }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return parts.Length == 0 ? null : string.Join("  ·  ", parts);
    }
}

public sealed record CleanupTarget(string Id, string Title, string Description, string Root, TimeSpan MinimumAge);
public sealed record CleanupEstimate(string Id, long Bytes, int Files);

public static class DiskCleanup
{
    public static IReadOnlyList<CleanupTarget> Targets(string userTemp, string windows, string localAppData) =>
    [
        new("user-temp", "Временные файлы программ", "Остатки установщиков и программ. Файлы младше суток не трогаем.", userTemp, TimeSpan.FromDays(1)),
        new("windows-temp", "Временные файлы Windows", "Служебные временные файлы системы старше суток.", Path.Combine(windows, "Temp"), TimeSpan.FromDays(1)),
        new("update-cache", "Загруженные обновления Windows", "Уже скачанные пакеты обновлений. Если понадобятся, Windows скачает их снова.", Path.Combine(windows, "SoftwareDistribution", "Download"), TimeSpan.FromDays(3)),
        new("shader-cache", "Кэш графики DirectX", "Игры и программы создадут его заново при запуске.", Path.Combine(localAppData, "D3DSCache"), TimeSpan.FromDays(1))
    ];

    public static CleanupEstimate Measure(CleanupTarget target, DateTimeOffset now, CancellationToken token)
    {
        long bytes = 0; int files = 0;
        foreach (var file in Eligible(target, now, token)) { bytes += file.Length; files++; }
        return new(target.Id, bytes, files);
    }

    // Deletes only old files below the target folder; locked files and links are skipped, the folder itself stays.
    public static long Clean(CleanupTarget target, DateTimeOffset now, CancellationToken token)
    {
        long freed = 0;
        foreach (var file in Eligible(target, now, token).ToArray())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                long length = file.Length;
                if (file.IsReadOnly) file.IsReadOnly = false;
                file.Delete(); freed += length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        RemoveEmptyFolders(new DirectoryInfo(target.Root), top: true);
        return freed;
    }

    private static IEnumerable<FileInfo> Eligible(CleanupTarget target, DateTimeOffset now, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(target.Root) || !Directory.Exists(target.Root)) yield break;
        var cutoff = now.UtcDateTime - target.MinimumAge;
        var pending = new Stack<DirectoryInfo>(); pending.Push(new DirectoryInfo(target.Root));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = folder.GetFileSystemInfos(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (entry is FileInfo file && file.LastWriteTimeUtc < cutoff) yield return file;
            }
        }
    }

    private static void RemoveEmptyFolders(DirectoryInfo folder, bool top)
    {
        try
        {
            foreach (var child in folder.GetDirectories())
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint)) RemoveEmptyFolders(child, top: false);
            if (!top && !folder.EnumerateFileSystemInfos().Any()) folder.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

public static class AutoselectAdvice
{
    public static IReadOnlyList<string> For(TestSession session)
    {
        var baseline = session.Baseline.Results.Where(r => r.Protocol != "PING" && r.Status != ProbeStatus.Unsupported).ToArray();
        if (baseline.Length > 0 && baseline.All(r => r.Status == ProbeStatus.Failed))
            return ["Без обхода не ответил ни один сервер. Проверьте подключение к интернету и запустите проверку снова."];
        var advice = new List<string>
        {
            "Очистите кэш DNS или поставьте DNS Cloudflare: X2 Tools → Интернет.",
            "Отключите VPN, прокси и другие программы обхода (AdGuard, GoodbyeDPI). Возможные конфликты показывает «Диагностика»."
        };
        advice.Add(session.Mode switch
        {
            TestMode.Quick => "Запустите полную проверку — она переберёт все профили.",
            TestMode.Full => "Попробуйте режим «Передача данных · DPI» — он проверяет ограничения по-другому.",
            _ => "Повторите проверку позже: иногда провайдер меняет ограничения в течение дня."
        });
        return advice;
    }
}
