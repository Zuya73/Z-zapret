using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Zapret.Desktop.Runtime;

internal sealed record PawnState(string Version, bool Ready);
internal sealed record HardwareComponents(string Library, string LibraryVersion, string LibrarySource,
    string DriverVersion, string DriverUrl, string DriverSha256, int DriverSize);

internal static class PawnDriver
{
    internal static HardwareComponents Components
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zapret.Desktop.Hardware.components.json")
                ?? throw new InvalidOperationException("Не найдены сведения о драйвере датчиков. Переустановите x2zapret из полного пакета.");
            return JsonSerializer.Deserialize<HardwareComponents>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
    }
    internal static PawnState State()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        string text = key?.GetValue("DisplayVersion") as string ?? "Не установлен";
        bool ready = Version.TryParse(text, out var version) && version >= new Version(2, 2, 0);
        if (ready)
        {
            try { using var device = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); ready = !device.IsInvalid; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ready = false; }
        }
        return new(text, ready);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    internal static void Verify(ReadOnlySpan<byte> bytes)
    {
        var info = Components;
        if (bytes.Length != info.DriverSize || bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z' ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(info.DriverSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл драйвера PawnIO повреждён или отличается от опубликованного. Установка остановлена; повторите загрузку.");
    }
    internal static bool CanInstallOver(string existing)
    {
        static Version Normal(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        return !Version.TryParse(existing, out var version) || Normal(version) <= Normal(Version.Parse(Components.DriverVersion));
    }
    internal static async Task<bool> InstallAsync(CancellationToken token)
    {
        var info = Components;
        if (!CanInstallOver(State().Version)) throw new InvalidOperationException("Установлена более новая версия драйвера PawnIO. Заменять её старой не нужно. Если датчики недоступны, перезагрузите Windows и обновите показания.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var response = await http.GetAsync(info.DriverUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != info.DriverSize) throw new InvalidDataException("Размер файла драйвера PawnIO отличается от ожидаемого. Установка остановлена; повторите загрузку.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream(); byte[] buffer = new byte[32768];
        while (await input.ReadAsync(buffer, token) is int count && count > 0)
        {
            if (memory.Length + count > info.DriverSize) throw new InvalidDataException("Файл драйвера оказался больше ожидаемого. Установка остановлена; повторите загрузку позже.");
            memory.Write(buffer, 0, count);
        }
        byte[] bytes = memory.ToArray(); Verify(bytes); token.ThrowIfCancellationRequested();
        // Program Files inherits administrator-only write access. Do not execute an elevated setup from a user-writable temp folder.
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "x2zapret-hardware-setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string setup = Path.Combine(directory, "PawnIO_setup.exe");
        try
        {
            await File.WriteAllBytesAsync(setup, bytes, token);
            await using var locked = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read);
            string script = "(Get-AuthenticodeSignature -LiteralPath '" + setup.Replace("'", "''") + "').Status.ToString()";
            var signature = await ProcessRunner.RunAsync(ProcessRunner.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(40), token);
            if (signature.ExitCode != 0 || signature.Output.Trim() != "Valid") throw new InvalidDataException("Не удалось подтвердить цифровую подпись драйвера PawnIO. Установка остановлена; повторите загрузку позже.");
            token.ThrowIfCancellationRequested();
            // Complete an already-started driver installation even if the user closes the information page.
            var result = await ProcessRunner.RunAsync(setup, ["-install", "-silent"], TimeSpan.FromMinutes(2), CancellationToken.None);
            if (result.ExitCode is not 0 and not 3010) throw new InvalidOperationException("Не удалось установить драйвер PawnIO. Код ошибки: " + result.ExitCode + ".");
            return result.ExitCode == 3010;
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
