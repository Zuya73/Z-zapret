using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zapret.Core;

public static class SeedLists
{
    public static string ContentHash(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n"))));
    private static bool UserOwned(string name) => name.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Synchronize(string source, string destination, string receipt,
        IReadOnlyDictionary<string, string>? legacyHashes = null)
    {
        Directory.CreateDirectory(destination);
        Dictionary<string, string> previous;
        try { previous = AtomicFile.ReadJson<Dictionary<string, string>>(receipt) ?? new(); }
        catch (JsonException) { previous = new(); }
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preserved = new List<string>();
        foreach (string incoming in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(incoming);
            if (UserOwned(name)) continue;
            string current = Path.Combine(destination, name), latest = ContentHash(incoming);
            next[name] = latest;
            bool same = File.Exists(current) && string.Equals(ContentHash(current), latest, StringComparison.OrdinalIgnoreCase);
            string? old = previous.GetValueOrDefault(name) ?? legacyHashes?.GetValueOrDefault(name);
            bool unmodified = File.Exists(current) && old != null &&
                string.Equals(ContentHash(current), old, StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(current) || same || unmodified)
            {
                if (!same) AtomicFile.Write(current, File.ReadAllText(incoming));
                // This path is reserved for an earlier upstream candidate, never passed to winws.
                string candidate = current + ".upstream";
                if (File.Exists(candidate)) File.Delete(candidate);
            }
            else
            {
                preserved.Add(name);
                if (!string.Equals(previous.GetValueOrDefault(name), latest, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(current + ".upstream"))
                    AtomicFile.Write(current + ".upstream", File.ReadAllText(incoming));
            }
        }
        AtomicFile.WriteJson(receipt, next);
        return preserved;
    }
}
