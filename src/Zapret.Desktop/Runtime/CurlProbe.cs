using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

public sealed class CurlProbe(string workingDirectory) : IConnectivityProbe
{
    public async Task<IReadOnlyList<ProbeResult>> RunAsync(IReadOnlyList<ProbeTarget> targets, TestMode mode,
        Action<ProbeResult> onResult, CancellationToken cancellationToken)
    {
        string? payload = null;
        if (mode == TestMode.Dpi)
        {
            payload = Path.Combine(workingDirectory, "zzapret-probe-" + Guid.NewGuid().ToString("N") + ".bin");
            await File.WriteAllBytesAsync(payload, RandomNumberGenerator.GetBytes(65536), cancellationToken);
        }
        try
        {
            using var slots = new SemaphoreSlim(8);
            string[] protocols = mode == TestMode.Quick ? ["HTTP/1.1"] : ["HTTP/1.1", "TLS 1.2", "TLS 1.3"];
            var work = targets.SelectMany(target => target.PingOnly
                ? new[] { (target, protocol: "PING") }
                : protocols.Select(protocol => (target, protocol)));
            var tasks = work.Select(async item =>
            {
                await slots.WaitAsync(cancellationToken);
                try
                {
                    var result = item.protocol == "PING" ? await PingAsync(item.target, cancellationToken) :
                        await CheckAsync(item.target, item.protocol, mode, payload, cancellationToken);
                    onResult(result);
                    return result;
                }
                finally { slots.Release(); }
            }).ToArray();
            return await Task.WhenAll(tasks);
        }
        finally { if (payload != null && File.Exists(payload)) File.Delete(payload); }
    }

    private static async Task<ProbeResult> PingAsync(ProbeTarget target, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target.Url, TimeSpan.FromSeconds(1), cancellationToken: cancellationToken);
            return new(target.Id, target.Name, target.Group, "PING", reply.Status == IPStatus.Success ? ProbeStatus.Passed : ProbeStatus.Inconclusive,
                reply.RoundtripTime, null, 0, "Обычный пинг не определяет, подходит ли профиль, и не влияет на рекомендацию.");
        }
        catch (Exception ex) when (ex is PingException or System.Net.Sockets.SocketException)
        { return new(target.Id, target.Name, target.Group, "PING", ProbeStatus.Inconclusive, 0, null, 0, "Не получен ответ на тестовый запрос пинга. Узел может не поддерживать такие ответы."); }
    }

    private static async Task<ProbeResult> CheckAsync(ProbeTarget target, string protocol, TestMode mode,
        string? payload, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        ProbeResult Make(ProbeStatus status, string detail, int? code = null, long bytes = 0) =>
            new(target.Id, target.Name, target.Group, protocol, status, watch.Elapsed.TotalMilliseconds, code, bytes, detail, target.Required);
        if (protocol == "TLS 1.3" && Environment.OSVersion.Version.Build < 20348)
            return Make(ProbeStatus.Unsupported, "TLS 1.3 в системном Schannel этой версии Windows не поддерживается.");
        var args = new List<string>
        {
            "--silent", "--show-error", "--noproxy", "*", "--connect-timeout", "3", "--max-time", "6",
            "--max-filesize", "8388608", "--output", "NUL", "--range", "0-65535",
            "--write-out", "%{http_code}\t%{time_total}\t%{size_download}\t%{size_upload}",
            "--proto", "=https", "--proto-redir", "=https"
        };
        if (protocol == "HTTP/1.1") args.Add("--http1.1");
        else args.AddRange(["--tlsv1." + (protocol == "TLS 1.2" ? "2" : "3"), "--tls-max", protocol == "TLS 1.2" ? "1.2" : "1.3"]);
        if (mode == TestMode.Dpi) args.AddRange(["--request", "POST", "--data-binary", "@" + payload]);
        else args.AddRange(["--location", "--max-redirs", "3"]);
        args.Add(target.Url);
        try
        {
            var command = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("curl.exe"), args, TimeSpan.FromSeconds(9), cancellationToken);
            var pieces = command.Output.Trim().Split('\t');
            int.TryParse(pieces.ElementAtOrDefault(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code);
            long.TryParse(pieces.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out long downloaded);
            long.TryParse(pieces.ElementAtOrDefault(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out long uploaded);
            var evaluation = ProbeClassifier.Classify(command.ExitCode, code, downloaded, uploaded, mode == TestMode.Dpi, target.AcceptedCodes, command.Error);
            return Make(evaluation.Status, evaluation.Detail, code, downloaded);
        }
        catch (TimeoutException) { return Make(ProbeStatus.Failed, "Превышено время ожидания ответа."); }
        catch (System.ComponentModel.Win32Exception) { return Make(ProbeStatus.Failed, "Не найден инструмент сетевых проверок curl.exe. Проверьте установку Windows и повторите запуск."); }
    }
}
