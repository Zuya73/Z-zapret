using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal static class MotionChecks
{
    internal static async Task RunAsync(MainWindow window, string destination)
    {
        bool? preference = Motion.EnabledOverride;
        var checks = new List<string>();
        var pages = new FrameworkElement[] { window.HomePanel, window.TestsPanel, window.StrategiesPanel, window.DiagnosticsPanel, window.SettingsPanel, window.LogsPanel, window.ToolsPanel };
        try
        {
            Motion.EnabledOverride = true; window.ApplyMotionPreferences();
            string originalPorts = window.TcpPortsBox.Text;
            window.TcpPortsBox.Text = "12345";
            foreach (string page in new[] { "Tests", "Strategies", "Logs", "Settings", "Home", "Settings" })
            { window.SelectPage(page); await Task.Delay(15); }
            await Task.Delay(500);
            Require(window.SettingsPanel.Visibility == Visibility.Visible && pages.Count(p => p.Visibility == Visibility.Visible) == 1,
                "Rapid navigation keeps only the last selected page visible");
            Require(window.PageTitle.Text == "Настройки" && window.TcpPortsBox.Text == "12345", "Navigation retains form data and the correct title");
            Require(window.PageScroll.Opacity == 1 && !DependencyPropertyHelper.GetValueSource(window.PageScroll, UIElement.OpacityProperty).IsAnimated,
                "Finished page transitions release their animation clocks");
            window.TcpPortsBox.Text = originalPorts;

            window.PageScroll.ScrollToVerticalOffset(180);
            await Task.Delay(80);
            double remembered = window.PageScroll.VerticalOffset;
            window.SelectPage("Home"); await Task.Delay(450);
            window.SelectPage("Settings"); await Task.Delay(450);
            Require(Math.Abs(window.PageScroll.VerticalOffset - remembered) < 1, "Each page retains its own scroll position");

            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            window.PageScroll.RaiseEvent(wheel);
            await Task.Delay(320);
            Require(wheel.Handled && window.PageScroll.VerticalOffset > remembered, "Mouse wheel scrolls the outer page smoothly");
            var nestedWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            double beforeNested = window.PageScroll.VerticalOffset;
            window.UserListBox.RaiseEvent(nestedWheel);
            await Task.Delay(80);
            Require(Math.Abs(window.PageScroll.VerticalOffset - beforeNested) < 1, "Text editors retain their native wheel input");

            window.StartupSwitch.IsChecked = false; await Task.Delay(240);
            window.StartupSwitch.IsChecked = true;
            var move = (TranslateTransform)((System.Windows.Shapes.Ellipse)window.StartupSwitch.Template.FindName("Thumb", window.StartupSwitch)).RenderTransform;
            Require(move.HasAnimatedProperties, "Switch interaction starts an animation");
            await Task.Delay(260);
            Require(move.X == 24 && !move.HasAnimatedProperties, "Switch settles at the correct position and releases clocks");

            Motion.To(window.OperationProgress, RangeBase.ValueProperty, 40);
            await Task.Delay(260);
            Require(window.OperationProgress.Value == 40 && !DependencyPropertyHelper.GetValueSource(window.OperationProgress, RangeBase.ValueProperty).IsAnimated,
                "Progress animations finish without retaining clocks");

            window.SelectPage("Home"); await Task.Delay(450);
            window.SelectPage("Settings");
            string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
            for (int frame = 0; frame < 4; frame++)
            {
                InterfacePreview.Save(window, Path.Combine(folder, $"motion-transition-{frame}.png"));
                await Task.Delay(frame == 2 ? 240 : 90);
            }

            // Disable motion while a navigation is pending: settle immediately on its latest target.
            window.SelectPage("Home");
            Motion.EnabledOverride = false; window.ApplyMotionPreferences();
            window.SelectPage("Logs");
            Require(window.LogsPanel.Visibility == Visibility.Visible && pages.Count(p => p.Visibility == Visibility.Visible) == 1 && window.PageScroll.Opacity == 1,
                "Reduced motion switches pages immediately, including during an interrupted transition");
            window.StartupSwitch.IsChecked = false; window.StartupSwitch.IsChecked = true;
            Require(move.X == 24 && !move.HasAnimatedProperties, "Reduced motion switches controls immediately");
            window.SelectPage("Home");
            AtomicFile.WriteJson(destination, new { passed = checks.Count, checks });
        }
        finally { Motion.EnabledOverride = preference; window.ApplyMotionPreferences(); }

        void Require(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
    }
}
