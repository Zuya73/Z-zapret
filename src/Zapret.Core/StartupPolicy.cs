using System.Xml.Linq;

namespace Zapret.Core;

public static class StartupPolicy
{
    public const string TaskName = "Z-zapret Desktop";
    public const string TaskSource = "Z-zapret.Desktop";
    public const string Arguments = "--startup";
    public static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static bool StartInTray(bool startupInvocation, StartupMode mode) =>
        startupInvocation && mode is StartupMode.Tray or StartupMode.TrayAndConnect;

    public static bool ShouldConnect(bool startupInvocation, StartupMode mode, bool engineRunning) =>
        startupInvocation && mode == StartupMode.TrayAndConnect && !engineRunning;

    public static async Task ConnectAsync(bool startupInvocation, StartupMode mode, bool engineRunning,
        Func<CancellationToken, Task> connect, CancellationToken cancellationToken)
    {
        if (ShouldConnect(startupInvocation, mode, engineRunning)) await connect(cancellationToken);
    }

    public static string TaskXml(string executable, string workingDirectory, string userSid)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(workingDirectory) ||
            !System.Text.RegularExpressions.Regex.IsMatch(userSid, @"^S-1-\d+(-\d+)+$"))
            throw new ArgumentException("Не удалось прочитать параметры запуска с Windows. Откройте настройки и сохраните автозапуск заново.");
        var n = TaskNamespace;
        XElement Element(string name, object value) => new(n + name, value);
        return new XDocument(new XElement(n + "Task", new XAttribute("version", "1.2"),
            Element("RegistrationInfo", new[] { Element("Description", "Запуск x2zapret без открытия окна при входе пользователя в Windows"), Element("Source", TaskSource) }),
            Element("Triggers", new XElement(n + "LogonTrigger", Element("Enabled", "true"), Element("UserId", userSid), Element("Delay", "PT5S"))),
            Element("Principals", new XElement(n + "Principal", new XAttribute("id", "Owner"),
                Element("UserId", userSid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
            Element("Settings", new[] {
                Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"),
                Element("StopIfGoingOnBatteries", "false"), Element("AllowHardTerminate", "false"),
                Element("StartWhenAvailable", "true"), Element("RunOnlyIfNetworkAvailable", "false"),
                Element("AllowStartOnDemand", "true"), Element("Enabled", "true"),
                Element("ExecutionTimeLimit", "PT0S"), Element("Priority", "7") }),
            new XElement(n + "Actions", new XAttribute("Context", "Owner"), new XElement(n + "Exec",
                Element("Command", executable), Element("Arguments", Arguments), Element("WorkingDirectory", workingDirectory)))
        )).ToString();
    }

    public static bool OwnsTask(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        return root?.Name == TaskNamespace + "Task" &&
            root.Element(TaskNamespace + "RegistrationInfo")?.Element(TaskNamespace + "Source")?.Value == TaskSource &&
            root.Element(TaskNamespace + "Actions")?.Element(TaskNamespace + "Exec")?.Element(TaskNamespace + "Arguments")?.Value == Arguments;
    }
}
