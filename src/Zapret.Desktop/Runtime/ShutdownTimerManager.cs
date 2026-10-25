using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class ShutdownTimerManager
{
    internal static ShutdownTimerController Create()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Не удалось определить вашу учётную запись Windows. Перезапустите программу.");
        return new(new ScheduledShutdownBackend(ShutdownTimerPolicy.TaskName(sid)), ProcessRunner.SystemTool("shutdown.exe"), sid);
    }
}

internal sealed class ScheduledShutdownBackend(string taskName) : IShutdownTimerBackend
{
    public Task<string?> ReadXmlAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service!).GetFolder(@"\");
            try { task = ((dynamic)folder!).GetTask(taskName); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { return null; }
            return (string?)((dynamic)task!).Xml;
        }
        finally
        {
            foreach (var value in new[] { task, folder, service })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }, token);

    public async Task RegisterAsync(string xml, CancellationToken token)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "x2zapret-shutdown-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(temporary, "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + xml, Encoding.Unicode, token);
            await Command(["/Create", "/TN", taskName, "/XML", temporary, "/F"], token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task DeleteAsync(CancellationToken token) => Command(["/Delete", "/TN", taskName, "/F"], token);
    private static async Task Command(string[] arguments, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("schtasks.exe"), arguments, TimeSpan.FromSeconds(20), token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Windows не смогла сохранить или отменить таймер. Подробности: " + result.Output.Trim() + " " + result.Error.Trim());
    }
}
