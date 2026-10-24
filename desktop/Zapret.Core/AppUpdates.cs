using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zapret.Core;

public sealed record AppUpdate(Version Version, string Tag, Uri DownloadUrl, long Size, string Sha256);
public sealed record DownloadProgress(long Received, long Total)
{ public int Percent => Total <= 0 ? 0 : (int)Math.Min(100, Received * 100 / Total); }

public static class UpdateManifest
{
    public const string ApiUrl = "https://api.github.com/repos/Zuya73/Z-zapret/releases?per_page=30";
    public const string DownloadPrefix = "/Zuya73/Z-zapret/releases/download/";
    public const long MaximumInstallerSize = 300L * 1024 * 1024;

    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(AppSettings).Assembly.GetName().Version ?? new Version(0, 1, 0);
            return new(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    public static bool TrustedDownload(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && (uri.AbsolutePath.StartsWith(DownloadPrefix, StringComparison.Ordinal) ||
            uri.AbsolutePath.StartsWith("/Zuya73/x2zapret/releases/download/", StringComparison.Ordinal));

    public static AppUpdate? Parse(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Неподдерживаемый ответ GitHub.");
        var candidates = new List<(Version Version, string Tag, JsonElement Release)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            var match = Regex.Match(tag, @"^desktop-v(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);
            if (match.Success && Version.TryParse(match.Groups[1].Value, out var version) && version > current)
                candidates.Add((version, tag, release));
        }
        if (candidates.Count == 0) return null;
        var latest = candidates.OrderByDescending(x => x.Version).First();
        string filename = $"x2zapret-Setup-{latest.Version.ToString(3)}-x64.exe";
        foreach (var asset in latest.Release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != filename) continue;
            if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var uri) ||
                !TrustedDownload(uri) || Uri.UnescapeDataString(uri.Segments[^1]) != filename)
                throw new InvalidDataException("Адрес обновления не принадлежит репозиторию x2zapret.");
            long size = asset.GetProperty("size").GetInt64();
            string digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
            if (size is < 4096 or > MaximumInstallerSize || !Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("В релизе нет корректной контрольной суммы или размера установщика.");
            return new(latest.Version, latest.Tag, uri, size, digest[7..].ToLowerInvariant());
        }
        throw new InvalidDataException($"В релизе {latest.Tag} отсутствует установщик {filename}.");
    }
}

public static class UpdateDownloader
{
    public static async Task<string> DownloadAsync(HttpClient client, AppUpdate update, string cacheDirectory,
        IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        if (!UpdateManifest.TrustedDownload(update.DownloadUrl) || update.Size is < 4096 or > UpdateManifest.MaximumInstallerSize ||
            !Regex.IsMatch(update.Sha256, @"^[0-9a-fA-F]{64}$")) throw new InvalidDataException("Недопустимые данные обновления.");
        string folder = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string partial = Path.Combine(folder, "installer.download");
        string destination = Path.Combine(folder, $"x2zapret-Setup-{update.Version.ToString(3)}-x64.exe");
        bool complete = false;
        try
        {
            using var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != update.Size)
                throw new InvalidDataException("Размер скачиваемого установщика изменился. Повторите проверку обновлений.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0; var buffer = new byte[65536];
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                while (true)
                {
                    int count = await input.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    received += count;
                    if (received > update.Size) throw new InvalidDataException("Установщик превышает заявленный размер.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress.Report(new(received, update.Size));
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            if (received != update.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException("Контрольная сумма обновления не совпала. Установка отменена.");
            using (var header = File.OpenRead(partial))
                if (header.ReadByte() != 'M' || header.ReadByte() != 'Z') throw new InvalidDataException("Полученный файл не является установщиком Windows.");
            File.Move(partial, destination);
            complete = true;
            return destination;
        }
        finally
        {
            if (!complete && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
