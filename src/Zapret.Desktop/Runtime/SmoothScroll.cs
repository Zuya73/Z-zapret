using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Zapret.Desktop.Runtime;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (target, e) => ((ScrollViewer)target).ScrollToVerticalOffset((double)e.NewValue)));
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached("Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= Wheel; viewer.PreviewMouseDown -= Pointer; viewer.PreviewKeyDown -= Key; viewer.Unloaded -= Unloaded;
        if (!(bool)e.NewValue) { Stop(viewer); return; }
        viewer.PreviewMouseWheel += Wheel; viewer.PreviewMouseDown += Pointer; viewer.PreviewKeyDown += Key; viewer.Unloaded += Unloaded;
    }
    private static void Wheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (!Motion.Enabled || Keyboard.Modifiers != ModifierKeys.None) { Stop(viewer); return; }
        // Text editors, grids, lists and their own scrollbars retain their native wheel behavior.
        for (var node = e.OriginalSource as DependencyObject; node != null && node != viewer; node = Parent(node))
            if (node is ScrollViewer or TextBoxBase or ItemsControl or RangeBase or Thumb) return;
        double lines = SystemParameters.WheelScrollLines;
        double distance = lines < 0 ? viewer.ViewportHeight * .85 : lines * 24;
        double previous = (double)viewer.GetValue(TargetProperty);
        double target = Math.Clamp((double.IsNaN(previous) ? viewer.VerticalOffset : previous) - e.Delta / 120.0 * distance, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - viewer.VerticalOffset) < .1) return;
        viewer.SetValue(TargetProperty, target);
        Motion.To(viewer, OffsetProperty, target, 210, viewer.VerticalOffset);
        e.Handled = true;
    }
    private static DependencyObject? Parent(DependencyObject node) => node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    private static void Pointer(object sender, MouseButtonEventArgs e) => Stop((ScrollViewer)sender);
    private static void Key(object sender, KeyEventArgs e) => Stop((ScrollViewer)sender);
    private static void Unloaded(object sender, RoutedEventArgs e) => Stop((ScrollViewer)sender);
    internal static void Stop(ScrollViewer viewer)
    { Motion.Stop(viewer, OffsetProperty); viewer.SetValue(TargetProperty, double.NaN); }
}
