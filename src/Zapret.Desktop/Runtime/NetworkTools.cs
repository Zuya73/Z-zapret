using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal sealed class ServiceHealthProbe(HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient client = new(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3, ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
        { Timeout = TimeSpan.FromSeconds(7) };
    internal async Task<ConnectionHealth> CheckAsync(CancellationToken token)
    {
        var checks = new[] {
            ("Discord", "Discord", "https://discord.com/"),
            ("Файлы Discord", "Discord", "https://cdn.discordapp.com/"),
            ("YouTube", "YouTube", "https://www.youtube.com/"),
            ("YouTube изображения", "YouTube", "https://i.ytimg.com/"),
            ("Google", "Internet", "https://www.google.com/"),
            ("Cloudflare", "Internet", "https://www.cloudflare.com/") };
        return new(DateTimeOffset.UtcNow, await Task.WhenAll(checks.Select(async target =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target.Item3) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.Range = new(0, 1023);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                int code = (int)response.StatusCode;
                // CDN root endpoints intentionally return 403/404; a main site must return success.
                bool ok = response.RequestMessage?.RequestUri?.Scheme == "https" &&
                    (code is >= 200 and < 400 || target.Item1 is "Файлы Discord" or "YouTube изображения" && code is 403 or 404);
                return new ServiceCheck(target.Item1, target.Item2, ok, clock.Elapsed.TotalMilliseconds, "Ответ сервера: HTTP " + code);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return new ServiceCheck(target.Item1, target.Item2, false, clock.Elapsed.TotalMilliseconds, "Сервер не ответил за 7 секунд"); }
            catch (HttpRequestException error)
            {
                string reason = error.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => "Не удалось найти адрес сервера",
                    HttpRequestError.ConnectionError => "Не удалось подключиться к серверу",
                    HttpRequestError.SecureConnectionError => "Не удалось установить защищённое соединение",
                    _ => "Не удалось получить ответ сервера"
                };
                return new ServiceCheck(target.Item1, target.Item2, false, clock.Elapsed.TotalMilliseconds, reason + " (" + error.HttpRequestError + ")");
            }
        })));
    }
    public void Dispose() => client.Dispose();
}

internal sealed record NetworkGateway(string Label, IPAddress Address)
{ public override string ToString() => Label; }
internal sealed record QualityResult(string Target, IReadOnlyList<LatencySample> Internet, IReadOnlyList<LatencySample> Gateway, IReadOnlyList<string> Route);
internal sealed record SpeedResult(DateTimeOffset At, double DownloadMbps, double UploadMbps, double? IdleMs,
    double? LoadedMs, long DownloadBytes, long UploadBytes, string Outcome, string Note);

internal static class NetworkTools
{
    internal static IReadOnlyList<NetworkGateway> Gateways() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().GatewayAddresses.Where(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any))
            .Select(g => new NetworkGateway(n.Name + " · " + g.Address, g.Address))).ToArray();
    internal static async Task<IPAddress> ResolveAsync(string host, CancellationToken token)
    {
        host = host.Trim();
        if (host.Length is 0 or > 253 || host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Укажите IP-адрес или домен без https:// и номера порта.");
        if (IPAddress.TryParse(host, out var address)) return address;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new IOException("Не удалось найти адрес сервера. Проверьте название и подключение к интернету.");
    }
    private static async Task<double?> PingAsync(IPAddress address, int ttl, CancellationToken token)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1), new byte[32], new PingOptions(ttl, false), token);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception e) when (e is PingException or SocketException) { return null; }
    }
    internal static async Task<QualityResult> QualityAsync(string host, IPAddress? gateway, bool route,
        IProgress<string> progress, CancellationToken token, int packetCount = 30)
    {
        if (packetCount is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(packetCount));
        var address = await ResolveAsync(host, token);
        var internet = new List<LatencySample>(); var local = new List<LatencySample>(); var hops = new List<string>();
        for (int i = 1; i <= packetCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var result = await Task.WhenAll(PingAsync(address, 128, token), gateway == null ? Task.FromResult<double?>(null) : PingAsync(gateway, 128, token));
            internet.Add(new(i, result[0])); if (gateway != null) local.Add(new(i, result[1]));
            progress.Report($"Пакет {i}/{packetCount} · {address}: {(result[0].HasValue ? result[0] + " мс" : "нет ответа на тестовый запрос")}");
            if (i < packetCount) await Task.Delay(500, token);
        }
        if (route)
            for (int ttl = 1; ttl <= 20; ttl++)
            {
                token.ThrowIfCancellationRequested(); progress.Report("Маршрут · переход " + ttl);
                using var ping = new Ping();
                try
                {
                    var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(800), new byte[32], new PingOptions(ttl, false), token);
                    string replyText = reply.Status == IPStatus.Success ? "Сервер ответил" : reply.Status == IPStatus.TtlExpired ? "Промежуточное устройство" : "Нет ответа";
                    hops.Add($"{ttl,2}  {(reply.Status is IPStatus.Success or IPStatus.TtlExpired ? reply.Address.ToString() : "*")}  {replyText}");
                    if (reply.Status == IPStatus.Success) break;
                }
                catch (Exception e) when (e is PingException or SocketException) { hops.Add(ttl + "  *"); }
            }
        return new(host + " (" + address + ")", internet, local, hops);
    }

    internal static async Task<SpeedResult> SpeedAsync(bool detailed, IProgress<string> progress, CancellationToken token,
        HttpMessageHandler? testHandler = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        using var client = new HttpClient(testHandler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = 4 })
            { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("x2zapret/" + UpdateManifest.CurrentVersion.ToString(3));
        var idle = new List<double>(); var loaded = new List<double>();
        long downloaded = 0, uploaded = 0; double down = 0, up = 0;
        string outcome = "Completed", note = "";
        async Task<double> Latency(CancellationToken cancellation)
        {
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync("https://speed.cloudflare.com/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode(); return watch.Elapsed.TotalMilliseconds;
        }
        try
        {
            progress.Report("Cloudflare · подготовка соединения…");
            _ = await Latency(ct); // Warm-up is excluded from latency statistics.
            for (int i = 0; i < 5; i++) idle.Add(await Latency(ct));
            foreach (bool write in new[] { false, true })
            {
                long bytes = 0; var elapsed = TimeSpan.Zero;
                foreach (int size in detailed ? new[] { 1_000_000, 5_000_000, 25_000_000, 100_000_000 } : new[] { 1_000_000, 5_000_000, 25_000_000 })
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report($"Cloudflare · {(write ? "отдача" : "загрузка")} · блок {size / 1_000_000} МБ…");
                    using var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    async Task SampleLoad()
                    {
                        try { while (true) { await Task.Delay(500, loadCancellation.Token); loaded.Add(await Latency(loadCancellation.Token)); } }
                        catch (OperationCanceledException) { }
                        catch (HttpRequestException) { }
                    }
                    var monitor = SampleLoad(); var watch = Stopwatch.StartNew();
                    try
                    {
                        if (write)
                        {
                            using var content = new RandomUploadContent(size);
                            using var request = new HttpRequestMessage(HttpMethod.Post, "https://speed.cloudflare.com/__up?bytes=" + size) { Content = content };
                            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                            response.EnsureSuccessStatusCode(); uploaded += size; bytes += size;
                        }
                        else
                        {
                            using var response = await client.GetAsync("https://speed.cloudflare.com/__down?bytes=" + size, HttpCompletionOption.ResponseHeadersRead, ct);
                            response.EnsureSuccessStatusCode();
                            using var body = await response.Content.ReadAsStreamAsync(ct);
                            var buffer = new byte[65536]; long received = 0; int count;
                            while ((count = await body.ReadAsync(buffer, ct)) > 0)
                            {
                                received += count; downloaded += count;
                                if (received > size) throw new InvalidDataException("Сервер прислал неожиданный объём данных. Проверка остановлена; попробуйте ещё раз.");
                            }
                            if (received != size) throw new InvalidDataException("Сервер прислал не все тестовые данные. Результат этого замера не учитывается; попробуйте ещё раз.");
                            bytes += received;
                        }
                    }
                    finally { watch.Stop(); loadCancellation.Cancel(); await monitor; }
                    elapsed += watch.Elapsed;
                    if (write) up = NetworkMeasurements.MegabitsPerSecond(bytes, elapsed); else down = NetworkMeasurements.MegabitsPerSecond(bytes, elapsed);
                    if (watch.Elapsed >= TimeSpan.FromSeconds(3)) break;
                }
            }
        }
        catch (OperationCanceledException) { outcome = token.IsCancellationRequested ? "Cancelled" : "Failed"; note = token.IsCancellationRequested ? "Замер отменён" : "Превышено время ожидания"; }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException) { outcome = "Failed"; note = e.Message; }
        return new(DateTimeOffset.UtcNow, down, up, idle.Count == 0 ? null : idle.Average(), loaded.Count == 0 ? null : loaded.Average(), downloaded, uploaded, outcome, note);
    }
    private sealed class RandomUploadContent(int size) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);
        private async Task WriteAsync(Stream stream, CancellationToken token)
        {
            var buffer = RandomNumberGenerator.GetBytes(65536);
            for (int sent = 0; sent < size; sent += buffer.Length) await stream.WriteAsync(buffer.AsMemory(0, Math.Min(buffer.Length, size - sent)), token);
        }
    }
}
