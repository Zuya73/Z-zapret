using Microsoft.Win32;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal sealed record StartupEntry(string Name, string ValueName, string Command, string Scope, RegistryHive Hive, string ApprovalKey, bool Enabled);

// Autostart entries from the Run keys and Startup folders, with the on/off state Task Manager keeps under StartupApproved.
internal static class StartupPrograms
{
    private const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    internal static IReadOnlyList<StartupEntry> List()
    {
        var result = new List<StartupEntry>();
        foreach (var (hive, key, approval, scope) in new[] {
            (RegistryHive.CurrentUser, Run, "Run", "Для вас"), (RegistryHive.LocalMachine, Run, "Run", "Для всех пользователей"),
            (RegistryHive.LocalMachine, Run32, "Run32", "Для всех пользователей") })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var run = root.OpenSubKey(key);
            if (run == null) continue;
            foreach (string name in run.GetValueNames().Where(n => n.Length > 0))
                if (run.GetValue(name) is string command && command.Trim().Length > 0)
                    result.Add(new(name, name, command, scope, hive, Approved + approval, IsEnabled(hive, Approved + approval, name)));
        }
        foreach (var (folder, hive, scope) in new[] {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Для вас · папка автозагрузки"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Для всех · папка автозагрузки") })
        {
            if (folder.Length == 0 || !Directory.Exists(folder)) continue;
            foreach (string file in Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileName(file);
                result.Add(new(Path.GetFileNameWithoutExtension(file), name, file, scope, hive, Approved + "StartupFolder", IsEnabled(hive, Approved + "StartupFolder", name)));
            }
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsEnabled(RegistryHive hive, string key, string value)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var approved = root.OpenSubKey(key);
        return StartupApproval.Enabled(approved?.GetValue(value) as byte[]);
    }

    internal static void SetEnabled(StartupEntry entry, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
        using var approved = root.CreateSubKey(entry.ApprovalKey, writable: true);
        approved.SetValue(entry.ValueName, StartupApproval.Value(enabled, DateTimeOffset.Now), RegistryValueKind.Binary);
    }
}
