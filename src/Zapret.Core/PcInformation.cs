using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zapret.Core;

public sealed record PcCategory(string Id, string Title)
{
    public override string ToString() => Title;
    public static IReadOnlyList<PcCategory> All { get; } = Array.AsReadOnly(new[]
    {
        new PcCategory("summary", "Обзор компьютера"), new("cpu", "Процессор"),
        new("memory", "Оперативная память"), new("board", "Плата и BIOS"),
        new("gpu", "Видеокарты"), new("disks", "Накопители и разделы"),
        new("display", "Мониторы"), new("audio", "Звуковые устройства"),
        new("network", "Сеть"), new("devices", "Устройства и драйверы"),
        new("windows", "Windows"), new("software", "Установленные программы"),
        new("processes", "Процессы"), new("services", "Службы"),
        new("startup", "Автозагрузка"), new("sensors", "Датчики и питание")
    });
    public static PcCategory Find(string id) => All.FirstOrDefault(c => c.Id == id)
        ?? throw new ArgumentException("Неизвестный раздел информации о ПК.", nameof(id));
}

public sealed record PcRow(string Device, string Property, string Value, bool Sensitive = false);
public sealed record PcSection(string Id, string Title, DateTimeOffset CapturedAt, IReadOnlyList<PcRow> Rows, IReadOnlyList<string> Warnings);

public static class PcInformation
{
    public const string Hidden = "Скрыто";
    public static PcSection Parse(string id, string json)
    {
        var category = PcCategory.Find(id);
        if (json.Length > 8 * 1024 * 1024) throw new InvalidDataException("Ответ системы слишком большой.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rows = new List<PcRow>();
        if (root.TryGetProperty("rows", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var row in data.EnumerateArray().Take(10000))
                rows.Add(new(Clean(row.GetProperty("Device").GetString()), Clean(row.GetProperty("Property").GetString()),
                    Clean(row.GetProperty("Value").GetString()), row.TryGetProperty("Sensitive", out var flag) && flag.ValueKind == JsonValueKind.True));
        var warnings = new List<string>();
        if (root.TryGetProperty("rows", out var allRows) && allRows.ValueKind == JsonValueKind.Array && allRows.GetArrayLength() > 10000)
            warnings.Add("Показаны первые 10 000 параметров раздела.");
        if (root.TryGetProperty("warnings", out var notes) && notes.ValueKind == JsonValueKind.Array)
            warnings.AddRange(notes.EnumerateArray().Select(x => Clean(x.GetString())).Where(x => x.Length > 0));
        if (rows.Count == 0 && warnings.Count == 0) warnings.Add("Windows не предоставила данные для этого раздела.");
        return new(id, category.Title, DateTimeOffset.UtcNow, rows, warnings);
    }
    public static IReadOnlyList<PcRow> Visible(PcSection section, string query, bool includePrivate)
    {
        query = query.Trim();
        return section.Rows.Select(r => r.Sensitive && !includePrivate ? r with { Value = Hidden } : r)
            .Where(r => query.Length == 0 || r.Device.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Property.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public static string Report(IEnumerable<PcSection> sections, bool includePrivate)
    {
        var items = sections.ToArray();
        var text = new StringBuilder("x2zapret / X2 Tools - Информация о ПК\n");
        text.AppendLine("Сформировано: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        text.AppendLine(includePrivate ? "Личные идентификаторы включены." : "Серийные номера, адреса и личные идентификаторы скрыты.");
        text.AppendLine($"Разделов: {items.Length}/{PcCategory.All.Count}. Это снимки данных Windows; недоступные показания не вычисляются.");
        foreach (var section in items)
        {
            text.AppendLine().AppendLine("[" + section.Title + "] " + section.CapturedAt.ToLocalTime().ToString("HH:mm:ss"));
            foreach (var row in Visible(section, "", includePrivate)) text.AppendLine($"{row.Device} | {row.Property}: {row.Value}");
            foreach (var warning in section.Warnings) text.AppendLine("Примечание: " + Clean(warning));
        }
        return text.ToString();
    }
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "—" :
        new string(value.Trim().Take(4096).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}

public interface IPcInformationSource
{
    Task<PcSection> ReadAsync(string category, CancellationToken token);
}

public sealed class PcInformationSession(IPcInformationSource source)
{
    private readonly Dictionary<string, PcSection> cache = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<PcSection> ReadAsync(string id, bool refresh, CancellationToken token)
    {
        PcCategory.Find(id);
        await gate.WaitAsync(token);
        try
        {
            if (!refresh && cache.TryGetValue(id, out var known)) return known;
            var section = await source.ReadAsync(id, token);
            token.ThrowIfCancellationRequested();
            cache[id] = section;
            return section;
        }
        finally { gate.Release(); }
    }
}
