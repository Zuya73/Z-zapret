using System.Reflection;
using System.Text;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal sealed class WindowsPcInformation : IPcInformationSource
{
    private static readonly Lazy<string> Script = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zapret.Desktop.Runtime.PcInfo.Collect.ps1")
            ?? throw new InvalidOperationException("Не найден встроенный сборщик информации о ПК.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public async Task<PcSection> ReadAsync(string category, CancellationToken token)
    {
        var entry = PcCategory.Find(category); // Only fixed identifiers can enter the command.
        string command = "& {\n" + Script.Value + "\n} -Category '" + entry.Id + "'";
        // The collector is larger than Windows' command-line limit. Send its UTF-8 bytes as
        // base64 over redirected stdin; the short, constant bootstrap contains no user input.
        const string bootstrap = "$bytes=[Convert]::FromBase64String([Console]::In.ReadToEnd()); & ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($bytes)))";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
        string input = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        try
        {
            var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], TimeSpan.FromSeconds(25), token, input);
            if (result.ExitCode != 0) throw new InvalidOperationException("Windows не смогла собрать этот раздел. Возможно, WMI или PowerShell ограничены политикой системы.");
            return PcInformation.Parse(category, result.Output.TrimStart('\uFEFF'));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidDataException)
        {
            return new(entry.Id, entry.Title, DateTimeOffset.UtcNow, [], [error.Message]);
        }
    }
}
