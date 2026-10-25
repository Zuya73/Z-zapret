using System.Security.Principal;
using System.Windows;
using System.Xml.Linq;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class ToolsChecks
{
    internal static ShutdownTimerController CreateController() =>
        new(new MemoryTimerBackend(), @"C:\Windows\System32\shutdown.exe", "S-1-5-21-100-200-300-1001");

    internal static async Task RunAsync(MainWindow window, string destination)
    {
        var checks = new List<string>();
        window.SelectPage("Tools");
        await Task.Delay(450);
        Require(window.ToolsPanel.Visibility == Visibility.Visible && window.PageTitle.Text == "X2 Tools", "Tools tab is reachable with the correct title");
        var bottom = window.StartShutdownTimerButton.PointToScreen(new(0, window.StartShutdownTimerButton.ActualHeight));
        var viewport = window.PageScroll.PointToScreen(new(0, window.PageScroll.ActualHeight));
        Require(bottom.Y <= viewport.Y + 1, "Timer inputs and start button fit the initial viewport");
        window.ShutdownHoursBox.Text = "0"; window.ShutdownMinutesBox.Text = "0";
        await window.StartShutdownTimerAsync();
        Require((await window.ShutdownTimer!.ReadAsync(CancellationToken.None)) == null && window.ShutdownTimerError.Text.Length > 0,
            "Invalid durations never register a task");
        window.ShutdownHoursBox.Text = "1"; window.ShutdownMinutesBox.Text = "15";
        await window.StartShutdownTimerAsync();
        var plan = await window.ShutdownTimer!.ReadAsync(CancellationToken.None);
        Require(plan != null && (plan.Deadline - DateTimeOffset.UtcNow).TotalMinutes > 74,
            "Hours and minutes create the expected deadline");
        Require(!window.StartShutdownTimerButton.IsEnabled && window.CancelShutdownTimerButton.IsEnabled &&
            !window.ShutdownHoursBox.IsEnabled && window.ShutdownCountdown.Text != "— : — : —", "Active timer shows a countdown and cancellation");
        window.SelectPage("Home"); await Task.Delay(450);
        window.SelectPage("Tools"); await window.RefreshShutdownTimerAsync(); await Task.Delay(450);
        Require(window.ShutdownStatus.Text.Contains("Выключение:"), "Timer state survives leaving and returning to Tools");
        await window.CancelShutdownTimerAsync();
        Require(await window.ShutdownTimer.ReadAsync(CancellationToken.None) == null &&
            window.StartShutdownTimerButton.IsEnabled && !window.CancelShutdownTimerButton.IsEnabled, "Cancellation removes only the scheduled timer");
        InterfacePreview.Save(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "tools-preview.png"));

        // Validate the real Windows scheduler schema with a DISABLED task whose action is cmd.exe.
        // No test ever registers shutdown.exe as an executable on the CI machine.
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string harmless = ProcessRunner.SystemTool("cmd.exe");
        var backend = new ScheduledShutdownBackend("x2zapret Tools Smoke " + Guid.NewGuid().ToString("N"));
        var xml = XDocument.Parse(ShutdownTimerPolicy.TaskXml(new(DateTimeOffset.UtcNow.AddDays(1)), harmless, sid));
        foreach (var enabled in xml.Descendants(StartupPolicy.TaskNamespace + "Enabled")) enabled.Value = "false";
        try
        {
            await backend.RegisterAsync(xml.ToString(), CancellationToken.None);
            string installed = await backend.ReadXmlAsync(CancellationToken.None) ?? throw new InvalidOperationException("Native task was not created.");
            ShutdownTimerPolicy.ReadOwned(installed, harmless, sid);
            Require(installed.Contains("cmd.exe") && !installed.Contains("shutdown.exe"), "Windows accepts the disabled one-shot task without a shutdown action");
        }
        finally { if (await backend.ReadXmlAsync(CancellationToken.None) != null) await backend.DeleteAsync(CancellationToken.None); }
        AtomicFile.WriteJson(destination, new { passed = checks.Count, checks });

        void Require(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
    }

    private sealed class MemoryTimerBackend : IShutdownTimerBackend
    {
        private string? xml;
        public Task<string?> ReadXmlAsync(CancellationToken token) => Task.FromResult(xml);
        public Task RegisterAsync(string value, CancellationToken token) { xml = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken token) { xml = null; return Task.CompletedTask; }
    }
}
