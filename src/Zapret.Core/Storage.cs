using System.Text;
using System.Text.Json;

namespace Zapret.Core;

public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static T? ReadJson<T>(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), StrategyCatalog.JsonOptions) : default;

    public static void WriteJson<T>(string path, T value) =>
        Write(path, JsonSerializer.Serialize(value, StrategyCatalog.JsonOptions));
}

public static class HostsBlock
{
    public const string Begin = "# BEGIN Z-ZAPRET";
    public const string End = "# END Z-ZAPRET";
    public static string Merge(string original, string? replacement)
    {
        var lines = original.Replace("\r", "").Split('\n');
        var output = new List<string>();
        bool inside = false;
        foreach (var line in lines)
        {
            if (line.Trim() == Begin)
            {
                if (inside) throw new InvalidDataException("Записи x2zapret в файле hosts повреждены. Изменение остановлено, чтобы сохранить остальные записи.");
                inside = true; continue;
            }
            if (line.Trim() == End)
            {
                if (!inside) throw new InvalidDataException("Записи x2zapret в файле hosts повреждены. Изменение остановлено, чтобы сохранить остальные записи.");
                inside = false; continue;
            }
            if (!inside) output.Add(line);
        }
        if (inside) throw new InvalidDataException("Не найдена граница записей x2zapret в файле hosts. Изменение остановлено, чтобы сохранить остальные записи.");
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1])) output.RemoveAt(output.Count - 1);
        if (!string.IsNullOrWhiteSpace(replacement))
        {
            foreach (var line in replacement.Replace("\r", "").Split('\n').Where(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith('#')))
            {
                var pieces = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length < 2 || !System.Net.IPAddress.TryParse(pieces[0], out _) ||
                    pieces.Skip(1).Any(x => !Validation.Domain(x)))
                    throw new InvalidDataException("Список адресов для hosts не прошёл проверку. Прежние записи сохранены.");
            }
            output.Add(Begin); output.Add(replacement.Trim()); output.Add(End);
        }
        return string.Join("\r\n", output).TrimEnd() + "\r\n";
    }
}
