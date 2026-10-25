using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Zapret.Desktop.Runtime;

internal sealed record DiskSpdManifest(string Version, string Source, string ArchiveUrl, string ArchiveSha256, int Size, string Sha256);
internal static class DiskSpdBinary
{
    internal static DiskSpdManifest Manifest
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zapret.Desktop.DiskBench.manifest.json")!;
            return JsonSerializer.Deserialize<DiskSpdManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
    }
    internal static FileStream OpenVerified()
    {
        var info = Manifest;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "x2zapret-tools", "diskspd-" + info.Version + "-" + info.Sha256[..12]);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "diskspd.exe");
        if (!File.Exists(path))
        {
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zapret.Desktop.DiskBench.diskspd.exe")!)
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { source.CopyTo(target); target.Flush(true); }
                File.Move(temporary, path, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (locked.Length != info.Size || !Convert.ToHexString(SHA256.HashData(locked)).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Встроенный движок DiskSpd не прошёл проверку SHA256. Удалите повреждённую копию: " + path);
            return locked;
        }
        catch { locked.Dispose(); throw; }
    }
}
