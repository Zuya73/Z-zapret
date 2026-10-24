using System.Windows;
using System.Windows.Threading;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class WindowBoundsCheck
{
    internal static async Task RunAsync(MainWindow window, string destination)
    {
        var checks = new List<object>();
        var original = window.WorkAreaBehavior.WorkArea;
        try
        {
            await Cycle("desktop");
            var compact = original with { Width = Math.Min(1280, original.Width), Height = Math.Min(680, original.Height) };
            window.WorkAreaBehavior.WorkAreaOverride = compact;
            window.WorkAreaBehavior.Refresh();
            await Cycle("compact");
            window.WorkAreaBehavior.WorkAreaOverride = new(compact.Left + 48, compact.Top + 32, compact.Width - 48, compact.Height - 32);
            window.WorkAreaBehavior.Refresh();
            await Cycle("offset");
        }
        finally { window.WorkAreaBehavior.WorkAreaOverride = null; window.WorkAreaBehavior.Refresh(); }
        AtomicFile.WriteJson(destination, checks);

        async Task Cycle(string scenario)
        {
            await Check(scenario, WindowState.Normal);
            SystemCommands.MaximizeWindow(window);
            await Check(scenario, WindowState.Maximized);
            SystemCommands.RestoreWindow(window);
            await Check(scenario, WindowState.Normal);
        }

        async Task Check(string scenario, WindowState state)
        {
            await Task.Delay(150);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var work = window.WorkAreaBehavior.WorkArea;
            var bounds = window.WorkAreaBehavior.Bounds;
            if (window.WindowState != state)
                throw new InvalidOperationException($"{scenario}: expected {state}, got {window.WindowState}.");
            if (WindowGeometry.Fit(bounds, work) != bounds)
                throw new InvalidOperationException($"{state}: window {bounds} exceeds work area {work}.");
            if (state == WindowState.Maximized && bounds != work)
                throw new InvalidOperationException($"{scenario}: maximized window must fill the work area.");
            foreach (FrameworkElement element in new FrameworkElement[] { window.SidebarFooter, window.StatusText })
            {
                var topLeft = element.PointToScreen(new(0, 0));
                var bottomRight = element.PointToScreen(new(element.ActualWidth, element.ActualHeight));
                if (topLeft.X < work.Left - 1 || topLeft.Y < work.Top - 1 ||
                    bottomRight.X > work.Left + work.Width + 1 || bottomRight.Y > work.Top + work.Height + 1)
                    throw new InvalidOperationException($"{state}: {element.Name} is hidden outside the work area.");
            }
            checks.Add(new { scenario, state = state.ToString(), work, bounds, authorVisible = window.AuthorText.IsVisible });
        }
    }
}
