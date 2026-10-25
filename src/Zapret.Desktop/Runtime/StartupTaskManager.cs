using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

public sealed class StartupTaskManager
{
    private string? ExistingXml()
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service!).GetFolder(@"\");
            try { task = ((dynamic)folder!).GetTask(StartupPolicy.TaskName); }
            // COM interop can translate ERROR_FILE_NOT_FOUND to FileNotFoundException.
            catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { return null; }
            return (string)((dynamic)task!).Xml;
        }
        finally
        {
            foreach (var value in new[] { task, folder, service })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    public async Task ApplyAsync(StartupMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Режим запуска с Windows не распознан. Выберите его в настройках и сохраните ещё раз.");
        string? existing = ExistingXml();
        if (existing != null && !StartupPolicy.OwnsTask(existing))
            throw new InvalidOperationException("В планировщике Windows уже есть чужая задача «Z-zapret Desktop». x2zapret не менял её. Проверьте задачу перед включением автозапуска.");
        if (mode == StartupMode.Disabled)
        {
            if (existing != null) await CommandAsync(["/Delete", "/TN", StartupPolicy.TaskName, "/F"], cancellationToken);
            return;
        }
        string executable = Path.Combine(AppContext.BaseDirectory, "x2zapret.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Не найден файл программы для запуска с Windows. Запустите x2zapret.exe из его постоянной папки и сохраните автозапуск ещё раз.", executable);
        string userSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Не удалось определить вашу учётную запись Windows. Перезапустите программу.");
        string xml = StartupPolicy.TaskXml(executable, AppContext.BaseDirectory, userSid);
        string temporary = Path.Combine(Path.GetTempPath(), "Z-zapret-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(temporary, "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + xml, Encoding.Unicode, cancellationToken);
            await CommandAsync(["/Create", "/TN", StartupPolicy.TaskName, "/XML", temporary, "/F"], cancellationToken);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task CommandAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("schtasks.exe"), arguments, TimeSpan.FromSeconds(20), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("Windows не смогла сохранить автозапуск. Подробности: " + result.Output.Trim() + " " + result.Error.Trim());
    }
}
