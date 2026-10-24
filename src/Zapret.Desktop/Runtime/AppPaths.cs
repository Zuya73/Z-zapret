using System.Security.Cryptography;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

public sealed class AppPaths
{
    public string Bundle { get; } = AppContext.BaseDirectory;
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Z-zapret");
    public string Bin => Path.Combine(Root, "bin");
    public string Lists => Path.Combine(Root, "lists");
    public string PreparedLists => Path.Combine(Root, "runtime-lists");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Recovery => Path.Combine(Root, "test-recovery.json");
    public string Results => Path.Combine(Root, "results");
    public string Log => Path.Combine(Root, "logs", "desktop.log");
    public const string Owner = "Z-zapret.Desktop";

    public void Initialize()
    {
        foreach (string dir in new[] { Root, Bin, Lists, PreparedLists, Results, Path.GetDirectoryName(Log)! }) Directory.CreateDirectory(dir);
        if (File.Exists(Log) && new FileInfo(Log).Length > 2_000_000) File.Move(Log, Log + ".previous", true);
        string manifest = Path.Combine(Bundle, "engine-manifest.json");
        var hashes = AtomicFile.ReadJson<Dictionary<string, string>>(manifest)
            ?? throw new FileNotFoundException("Нет комплекта движка. Распакуйте весь архив приложения.", manifest);
        foreach (var (name, expected) in hashes)
        {
            if (Path.GetFileName(name) != name) throw new InvalidDataException("Некорректный манифест движка.");
            string source = Path.Combine(Bundle, "bin", name), destination = Path.Combine(Bin, name);
            if (!File.Exists(source) || !string.Equals(Hash(source), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Не пройдена проверка целостности: {name}. Загрузите полный архив приложения.");
            if (!File.Exists(destination) || !name.StartsWith("ACTIVE_", StringComparison.Ordinal) && !string.Equals(Hash(destination), expected, StringComparison.OrdinalIgnoreCase))
                File.Copy(source, destination, overwrite: true);
        }
        foreach (string source in Directory.GetFiles(Path.Combine(Bundle, "lists")))
        {
            string destination = Path.Combine(Lists, Path.GetFileName(source));
            if (!File.Exists(destination)) File.Copy(source, destination);
        }
        foreach (string file in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt", "ipset-include-user.txt" })
            if (!File.Exists(Path.Combine(Lists, file))) AtomicFile.Write(Path.Combine(Lists, file), "# Пользовательские адреса\n");
    }

    public string PrepareLists(AppSettings settings, bool dpi = false, bool test = false)
    {
        string directory = test ? Path.Combine(Root, "tests", Guid.NewGuid().ToString("N")) : PreparedLists;
        Directory.CreateDirectory(directory);
        foreach (string source in Directory.GetFiles(Lists, "*.txt")) File.Copy(source, Path.Combine(directory, Path.GetFileName(source)), true);
        string content = dpi || settings.IpSet == IpSetMode.Any ? "" : settings.IpSet == IpSetMode.None ? "203.0.113.113/32\n" :
            File.ReadAllText(Path.Combine(Lists, "ipset-all.txt.backup")) + "\n" + File.ReadAllText(Path.Combine(Lists, "ipset-include-user.txt"));
        AtomicFile.Write(Path.Combine(directory, "ipset-all.txt"), content);
        foreach (string file in new[] { "list-general-user.txt", "list-exclude-user.txt" })
        {
            string path = Path.Combine(directory, file);
            if (!File.ReadLines(path).Any(x => x.Trim().Length > 0 && !x.TrimStart().StartsWith('#')))
                AtomicFile.Write(path, "domain.example.abc\n");
        }
        string excludes = Path.Combine(directory, "ipset-exclude-user.txt");
        if (!File.ReadLines(excludes).Any(x => x.Trim().Length > 0 && !x.TrimStart().StartsWith('#')))
            AtomicFile.Write(excludes, "203.0.113.113/32\n");
        return directory;
    }

    public void ApplyFakes(AppSettings settings)
    {
        foreach (var (sourceName, target) in new[] { (settings.DiscordFake, "ACTIVE_DISCORD_UDP.bin"), (settings.GameFake, "ACTIVE_GAME_UDP.bin") })
        {
            if (Path.GetFileName(sourceName) != sourceName || !sourceName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || sourceName.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Недопустимый файл фейка.");
            File.Copy(Path.Combine(Bundle, "bin", sourceName), Path.Combine(Bin, target), true);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
