using System.Globalization;
using System.Xml.Linq;

namespace Zapret.Core;

public sealed record ShutdownPlan(DateTimeOffset Deadline);
public interface IShutdownTimerBackend
{
    Task<string?> ReadXmlAsync(CancellationToken token);
    Task RegisterAsync(string xml, CancellationToken token);
    Task DeleteAsync(CancellationToken token);
}

public static class ShutdownTimerPolicy
{
    public const string Source = "x2zapret.Tools.Shutdown";
    public const string Arguments = "/s /t 0 /d p:0:0 /c \"x2zapret X2 Tools\"";
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromDays(7);
    private static readonly XNamespace N = StartupPolicy.TaskNamespace;

    public static TimeSpan Delay(string hours, string minutes)
    {
        bool Number(string s, out int n) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n) && s.All(char.IsAsciiDigit);
        if (!Number(hours.Trim(), out int h) || !Number(minutes.Trim(), out int m) || h is < 0 or > 168 || m is < 0 or > 59)
            throw new FormatException("Укажите часы от 0 до 168 и минуты от 0 до 59.");
        var delay = TimeSpan.FromMinutes(h * 60 + m);
        Validate(delay);
        return delay;
    }
    public static void Validate(TimeSpan delay)
    {
        if (delay < TimeSpan.FromMinutes(1) || delay > MaximumDelay)
            throw new ArgumentOutOfRangeException(nameof(delay), "Таймер можно установить от 1 минуты до 7 дней.");
    }
    public static string Remaining(DateTimeOffset deadline, DateTimeOffset now)
    {
        var left = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((deadline - now).TotalSeconds)));
        return left.Days > 0 ? $"{left.Days} д {left:hh\\:mm\\:ss}" : left.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }
    public static string TaskName(string sid)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid, @"^S-1-\d+(-\d+)+$")) throw new ArgumentException("Некорректный пользователь Windows.");
        return "x2zapret Shutdown " + sid;
    }
    public static string TaskXml(ShutdownPlan plan, string executable, string sid)
    {
        TaskName(sid);
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("Не задан shutdown.exe.");
        XElement E(string name, object value) => new(N + name, value);
        string Boundary(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return new XDocument(new XElement(N + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new[] { E("Description", "Однократное выключение ПК по таймеру X2 Tools"), E("Source", Source) }),
            E("Triggers", new XElement(N + "TimeTrigger",
                E("StartBoundary", Boundary(plan.Deadline)), E("EndBoundary", Boundary(plan.Deadline.AddMinutes(5))), E("Enabled", "true"))),
            E("Principals", new XElement(N + "Principal", new XAttribute("id", "Owner"),
                E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("AllowHardTerminate", "false"), E("StartWhenAvailable", "false"),
                E("AllowStartOnDemand", "false"), E("Enabled", "true"), E("WakeToRun", "false"),
                E("ExecutionTimeLimit", "PT1M"), E("DeleteExpiredTaskAfter", "PT1H"), E("Priority", "7") }),
            new XElement(N + "Actions", new XAttribute("Context", "Owner"),
                new XElement(N + "Exec", E("Command", executable), E("Arguments", Arguments))))).ToString();
    }
    public static ShutdownPlan ReadOwned(string xml, string executable, string sid)
    {
        if (xml.Length > 128_000) throw new InvalidDataException("Неожиданная задача планировщика.");
        var root = XDocument.Parse(xml).Root;
        var actions = root?.Element(N + "Actions")?.Elements().ToArray();
        var triggers = root?.Element(N + "Triggers")?.Elements().ToArray();
        if (root?.Name != N + "Task" || root.Element(N + "RegistrationInfo")?.Element(N + "Source")?.Value != Source ||
            root.Element(N + "Principals")?.Element(N + "Principal")?.Element(N + "UserId")?.Value != sid ||
            actions is not { Length: 1 } || actions[0].Name != N + "Exec" ||
            !string.Equals(actions[0].Element(N + "Command")?.Value, executable, StringComparison.OrdinalIgnoreCase) ||
            actions[0].Element(N + "Arguments")?.Value != Arguments ||
            triggers is not { Length: 1 } || triggers[0].Name != N + "TimeTrigger" ||
            !DateTimeOffset.TryParse(triggers[0].Element(N + "StartBoundary")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var deadline))
            throw new InvalidOperationException("Задача таймера принадлежит другой программе или была изменена. Она не изменялась.");
        return new(deadline);
    }
}

public sealed class ShutdownTimerController(IShutdownTimerBackend backend, string executable, string sid)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<ShutdownPlan?> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await ReadCore(token); }
        finally { gate.Release(); }
    }
    private async Task<ShutdownPlan?> ReadCore(CancellationToken token)
    {
        string? xml = await backend.ReadXmlAsync(token);
        return xml == null ? null : ShutdownTimerPolicy.ReadOwned(xml, executable, sid);
    }
    public async Task<ShutdownPlan> ScheduleAsync(TimeSpan delay, DateTimeOffset now, CancellationToken token)
    {
        ShutdownTimerPolicy.Validate(delay);
        await gate.WaitAsync(token);
        try
        {
            await ReadCore(token); // Only replace a task whose ownership and action were verified.
            var deadline = now.Add(delay);
            var plan = new ShutdownPlan(deadline.AddTicks(-(deadline.Ticks % TimeSpan.TicksPerSecond)));
            await backend.RegisterAsync(ShutdownTimerPolicy.TaskXml(plan, executable, sid), token);
            return plan;
        }
        finally { gate.Release(); }
    }
    public async Task CancelAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { if (await ReadCore(token) != null) await backend.DeleteAsync(token); }
        finally { gate.Release(); }
    }
}
