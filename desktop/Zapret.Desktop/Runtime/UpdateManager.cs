using System.Net;
using System.Net.Http;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

public sealed class UpdateManager
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Z-zapret/" + UpdateManifest.CurrentVersion.ToString(3));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Http.GetAsync(UpdateManifest.ApiUrl, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(timeout.Token);
        if (json.Length > 8_000_000) throw new InvalidDataException("Получен слишком большой ответ GitHub.");
        return UpdateManifest.Parse(json, UpdateManifest.CurrentVersion);
    }

    public Task<string> DownloadAsync(AppUpdate update, AppPaths paths, IProgress<DownloadProgress> progress, CancellationToken cancellationToken) =>
        UpdateDownloader.DownloadAsync(Http, update, Path.Combine(paths.Root, "updates"), progress, cancellationToken);
}
