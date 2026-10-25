using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Zapret.Core;

public sealed record DiskBenchmarkOptions(long FileBytes, int Seconds, int Repeats)
{
    public const long MiB = 1024 * 1024;
    public static DiskBenchmarkOptions Quick => new(1024 * MiB, 3, 1);
    public void Validate()
    {
        if (FileBytes != 256 * MiB && FileBytes != 1024 * MiB && FileBytes != 4096 * MiB)
            throw new ArgumentException("Выберите файл 256 МиБ, 1 ГиБ или 4 ГиБ.");
        if (Seconds is < 1 or > 10 || Repeats is < 1 or > 3 || Seconds * Repeats > 30)
            throw new ArgumentException("Недопустимая длительность теста.");
    }
}
public sealed record DiskBenchmarkCase(string Id, string Label, int BlockBytes, int Queue, bool Random)
{
    public static IReadOnlyList<DiskBenchmarkCase> All { get; } = Array.AsReadOnly(new[] {
        new DiskBenchmarkCase("seq8", "SEQ1M Q8T1", 1024 * 1024, 8, false),
        new("seq1", "SEQ1M Q1T1", 1024 * 1024, 1, false),
        new("rnd32", "RND4K Q32T1", 4096, 32, true), new("rnd1", "RND4K Q1T1", 4096, 1, true)
    });
    public string Description => Random ? "Мелкие блоки данных" : "Крупные блоки данных";
}
public sealed record DiskMeasurement(string CaseId, bool Write, int Pass, ulong Bytes, ulong Operations,
    double Seconds, double AverageLatencyMs, double? P95LatencyMs)
{
    public double MegabytesPerSecond => Bytes / Seconds / 1_000_000d;
    public double Iops => Operations / Seconds;
}
public sealed record DiskBenchmarkReport(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Drive,
    DiskBenchmarkOptions Options, string Outcome, IReadOnlyList<DiskMeasurement> Measurements, string Error, string CleanupNote);
public sealed record DiskBenchmarkProgress(string Stage, int Completed, int Total, int Percent, DiskMeasurement? Measurement = null);
public interface IDiskBenchmarkRunner
{
    Task<DiskBenchmarkReport> RunAsync(string root, DiskBenchmarkOptions options, IProgress<DiskBenchmarkProgress> progress, CancellationToken token);
}

public static class DiskBenchmarkPolicy
{
    public const string FolderPrefix = "x2zapret-disk-test-";
    public const string DataName = "data.bin";
    public const long ReserveBytes = 1024L * 1024 * 1024;
    public static string Root(string root)
    {
        if (!Regex.IsMatch(root, @"\A[A-Za-z]:\\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Выберите диск с буквой, например C: или D:. Сетевые папки для этого теста не подходят.");
        return char.ToUpperInvariant(root[0]) + @":\";
    }
    public static bool OwnedFileShape(string path) => Regex.IsMatch(path,
        @"\A[A-Za-z]:\\x2zapret-disk-test-[0-9a-f]{32}\\data\.bin\z", RegexOptions.CultureInvariant);
    public static IReadOnlyList<string> Arguments(string file, DiskBenchmarkCase profile, bool write, int seconds)
    {
        if (!OwnedFileShape(file) || !DiskBenchmarkCase.All.Contains(profile) || seconds is < 1 or > 10)
            throw new ArgumentException("Недопустимая цель или профиль теста.");
        var args = new List<string> { "-b" + profile.BlockBytes.ToString(CultureInfo.InvariantCulture), "-o" + profile.Queue.ToString(CultureInfo.InvariantCulture),
            "-t1", "-d" + seconds.ToString(CultureInfo.InvariantCulture), "-W0", "-C0", "-Su", "-L", "-Rxml", "-n", write ? "-w100" : "-w0" };
        if (profile.Random) args.Add("-r4K");
        if (write) args.Add("-Z16M");
        args.Add(file);
        return args;
    }
    public static DiskMeasurement Parse(string xml, DiskBenchmarkCase profile, bool write, int pass, string expectedFile)
    {
        if (xml.Length > 4 * 1024 * 1024 || !OwnedFileShape(expectedFile)) throw new InvalidDataException("Не удалось прочитать результат проверки диска. Попробуйте выполнить её ещё раз.");
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var doc = XDocument.Load(reader);
        var span = doc.Root?.Elements("TimeSpan").SingleOrDefault() ?? throw new InvalidDataException("Инструмент проверки диска не сообщил длительность замера. Результат не используется.");
        double seconds = Number(span.Element("TestTimeSeconds"));
        if (seconds <= 0) throw new InvalidDataException("Не получено время измерения. Попробуйте проверить диск ещё раз.");
        var targets = span.Elements("Thread").Elements("Target").ToArray();
        if (targets.Length == 0 || targets.Any(t => !string.Equals(t.Element("Path")?.Value, expectedFile, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Результат получен для другого тестового файла. Он не используется; повторите проверку.");
        ulong bytes = 0, count = 0;
        foreach (var target in targets)
        {
            bytes = checked(bytes + Integer(target.Element(write ? "WriteBytes" : "ReadBytes")));
            count = checked(count + Integer(target.Element(write ? "WriteCount" : "ReadCount")));
            if (Integer(target.Element(write ? "ReadCount" : "WriteCount")) != 0) throw new InvalidDataException("Инструмент выполнил не тот вид проверки. Результат не используется; повторите тест.");
        }
        if (count == 0 || bytes != checked(count * (ulong)profile.BlockBytes)) throw new InvalidDataException("Не получено корректных завершённых чтений или записей. Результат не используется; повторите проверку.");
        var latency = span.Element("Latency") ?? throw new InvalidDataException("Инструмент проверки диска не сообщил время ответа. Повторите проверку.");
        double avg = Number(latency.Element(write ? "AverageWriteMilliseconds" : "AverageReadMilliseconds"));
        var p95 = latency.Elements("Bucket").FirstOrDefault(b => (string?)b.Element("Percentile") == "95")?.Element(write ? "WriteMilliseconds" : "ReadMilliseconds");
        return new(profile.Id, write, pass, bytes, count, seconds, avg, p95 == null ? null : Number(p95));
    }
    private static double Number(XElement? element)
    {
        if (!double.TryParse(element?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0)
            throw new InvalidDataException("В результате проверки диска получено неверное значение. Выполните проверку ещё раз.");
        return value;
    }
    private static ulong Integer(XElement? element)
    {
        if (!ulong.TryParse(element?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
            throw new InvalidDataException("Не удалось определить число чтений или записей. Повторите проверку диска.");
        return value;
    }
    public static DiskMeasurement? Aggregate(IEnumerable<DiskMeasurement> source)
    {
        var data = source.ToArray(); if (data.Length == 0) return null;
        if (data.Any(x => x.CaseId != data[0].CaseId || x.Write != data[0].Write)) throw new ArgumentException("Результаты разных видов проверки диска нельзя объединить. Выполните проверку заново.");
        ulong bytes = 0, count = 0;
        foreach (var row in data) { bytes = checked(bytes + row.Bytes); count = checked(count + row.Operations); }
        return new(data[0].CaseId, data[0].Write, data.Length, bytes, count, data.Sum(x => x.Seconds),
            data.Sum(x => x.AverageLatencyMs * x.Operations) / count,
            data.Any(x => x.P95LatencyMs.HasValue) ? data.Max(x => x.P95LatencyMs) : null);
    }
    public static string Export(DiskBenchmarkReport report)
    {
        var text = new StringBuilder("x2zapret / X2 Tools - Проверка скорости диска\n");
        text.AppendLine($"Диск: {report.Drive}");
        text.AppendLine($"Начало: {report.StartedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}; завершение: {report.FinishedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        text.AppendLine($"Размер временного файла: {report.Options.FileBytes / DiskBenchmarkOptions.MiB} МиБ. Каждый замер: {report.Options.Seconds} с. Проходов: {report.Options.Repeats}.");
        text.AppendLine("Результат: " + OutcomeText(report.Outcome) + $" ({report.Outcome}).");
        text.AppendLine("Скорость в МБ/с: больше — быстрее. Задержка в мс: меньше — лучше. Операций/с (IOPS) — число отдельных чтений или записей за секунду.");
        text.AppendLine("Инструмент: Microsoft DiskSpd 2.3. Кэш Windows не используется (-Su); встроенный кэш накопителя работает как обычно.");
        text.AppendLine("P95 — задержка, в которую уложились 95% операций в самом медленном проходе. Это не общая оценка всех проходов.");
        foreach (var profile in DiskBenchmarkCase.All)
            foreach (bool write in new[] { false, true })
            {
                var item = Aggregate(report.Measurements.Where(x => x.CaseId == profile.Id && x.Write == write));
                if (item == null) continue;
                text.AppendLine(FormattableString.Invariant($"{profile.Description} ({profile.Label}) | {(write ? "Запись" : "Чтение")}: {item.MegabytesPerSecond:0.##} МБ/с; {item.Iops:0.##} операций/с (IOPS); среднее время операции: {item.AverageLatencyMs:0.###} мс; P95: {item.P95LatencyMs:0.###} мс; проходов выполнено {item.Pass}/{report.Options.Repeats}"));
            }
        if (report.Error.Length > 0) text.AppendLine("Ошибка: " + report.Error);
        if (report.CleanupNote.Length > 0) text.AppendLine("Очистка: " + report.CleanupNote);
        text.AppendLine("Результат зависит от размера файла, заполненности, кэша накопителя и фоновой нагрузки; это не прямое сравнение с другими настройками CrystalDiskMark.");
        return text.ToString();
    }
    public static string OutcomeText(string outcome) => outcome switch
    { "Completed" => "Проверка завершена", "Cancelled" => "Проверка отменена", "Failed" => "Проверка завершилась с ошибкой", _ => "Результат неизвестен" };
}
