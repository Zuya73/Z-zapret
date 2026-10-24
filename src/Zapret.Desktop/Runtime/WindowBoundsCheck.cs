using System.Windows;
using System.Windows.Threading;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class WindowBoundsCheck
{
    internal static async Task RunAsync(MainWindow window, string destination)
    {
        var checks = new List<object>();
        await Check("normal");
        SystemCommands.MaximizeWindow(window);
        await Check("maximized");
        SystemCommands.RestoreWindow(window);
        await Check("restored");
        AtomicFile.WriteJson(destination, checks);

        async Task Check(string state)
        {
            await Task.Delay(150);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var work = window.WorkAreaBehavior.WorkArea;
            var bounds = window.WorkAreaBehavior.Bounds;
            if (WindowGeometry.Fit(bounds, work) != bounds)
                throw new InvalidOperationException($"{state}: window {bounds} exceeds work area {work}.");
            foreach (FrameworkElement element in new FrameworkElement[] { window.SidebarFooter, window.StatusText })
            {
                var topLeft = element.PointToScreen(new(0, 0));
                var bottomRight = element.PointToScreen(new(element.ActualWidth, element.ActualHeight));
                if (topLeft.X < work.Left - 1 || topLeft.Y < work.Top - 1 ||
                    bottomRight.X > work.Left + work.Width + 1 || bottomRight.Y > work.Top + work.Height + 1)
                    throw new InvalidOperationException($"{state}: {element.Name} is hidden outside the work area.");
            }
            checks.Add(new { state, work, bounds, authorVisible = window.AuthorText.IsVisible });
        }
    }
}
