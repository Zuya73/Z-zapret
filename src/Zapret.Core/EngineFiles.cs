using System.Security.Cryptography;

namespace Zapret.Core;

public static class EngineFiles
{
    public static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool Mutable(string name) => name.StartsWith("ACTIVE_", StringComparison.Ordinal);
    private static List<string> Changes(string source, string destination, IReadOnlyDictionary<string, string> hashes)
    {
        var changed = new List<string>();
        foreach (var (name, expected) in hashes)
        {
            if (Path.GetFileName(name) != name || name.Contains('\\') || name.Contains('/') || name is "." or "..")
                throw new InvalidDataException("Не удалось прочитать список файлов обхода. Скачайте и распакуйте полный архив x2zapret.");
            string incoming = Path.Combine(source, name), current = Path.Combine(destination, name);
            if (!File.Exists(incoming) || !string.Equals(Hash(incoming), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Не пройдена проверка целостности: {name}. Загрузите полный архив приложения.");
            if (!File.Exists(current) || !Mutable(name) && !string.Equals(Hash(current), expected, StringComparison.OrdinalIgnoreCase))
                changed.Add(name);
        }
        return changed;
    }
    public static bool NeedsUpdate(string source, string destination, IReadOnlyDictionary<string, string> hashes) =>
        Changes(source, destination, hashes).Count != 0;

    public static void Synchronize(string source, string destination, IReadOnlyDictionary<string, string> hashes)
    {
        var changed = Changes(source, destination, hashes); // Validate the entire source before changing a single file.
        if (changed.Count == 0) return;
        Directory.CreateDirectory(destination);
        string stage = Path.Combine(destination, ".update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var installed = new List<(string Name, bool Existed)>();
        bool retainBackup = false;
        try
        {
            foreach (string name in changed)
            {
                File.Copy(Path.Combine(source, name), Path.Combine(stage, name));
                string current = Path.Combine(destination, name);
                if (File.Exists(current)) File.Copy(current, Path.Combine(stage, name + ".previous"));
            }
            foreach (string name in changed)
            {
                string current = Path.Combine(destination, name);
                bool existed = File.Exists(current);
                File.Move(Path.Combine(stage, name), current, true);
                installed.Add((name, existed));
            }
        }
        catch (Exception updateError)
        {
            var failures = new List<Exception> { updateError };
            foreach (var (name, existed) in installed.AsEnumerable().Reverse())
                try
                {
                    string current = Path.Combine(destination, name);
                    if (existed) File.Move(Path.Combine(stage, name + ".previous"), current, true);
                    else File.Delete(current);
                }
                catch (Exception rollbackError) { failures.Add(rollbackError); retainBackup = true; }
            if (failures.Count > 1) throw new AggregateException("Не удалось вернуть все прежние файлы обхода. Резервная копия находится здесь: " + stage, failures);
            throw;
        }
        finally
        {
            if (!retainBackup && Directory.Exists(stage))
                try { Directory.Delete(stage, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
