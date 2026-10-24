using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Zapret.Desktop.Runtime;

public enum MotionKind { None, Button, Card, Switch }

public static class Motion
{
    internal static bool? EnabledOverride { get; set; }
    internal static bool Enabled => EnabledOverride ?? SystemParameters.ClientAreaAnimation;
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, AnimationClock>> Clocks = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Feedback> Feedbacks = new();
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(MotionKind), typeof(Motion), new PropertyMetadata(MotionKind.None, KindChanged));
    public static MotionKind GetKind(DependencyObject target) => (MotionKind)target.GetValue(KindProperty);
    public static void SetKind(DependencyObject target, MotionKind value) => target.SetValue(KindProperty, value);
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.RegisterAttached("Selected", typeof(bool), typeof(Motion), new PropertyMetadata(false, SelectedChanged));
    public static bool GetSelected(DependencyObject target) => (bool)target.GetValue(SelectedProperty);
    public static void SetSelected(DependencyObject target, bool value) => target.SetValue(SelectedProperty, value);

    internal static void To(DependencyObject target, DependencyProperty property, double value, int milliseconds = 180, double? from = null) =>
        Run(target, property, value, new DoubleAnimation(from ?? (double)target.GetValue(property), value,
            TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, milliseconds);

    private static void ColorTo(SolidColorBrush brush, Color value, int milliseconds) =>
        Run(brush, SolidColorBrush.ColorProperty, value, new ColorAnimation(brush.Color, value, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, milliseconds);

    private static void Run(DependencyObject target, DependencyProperty property, object value, AnimationTimeline animation, int milliseconds)
    {
        Stop(target, property);
        if (!Enabled || milliseconds == 0) { target.SetValue(property, value); return; }
        var clock = animation.CreateClock();
        var clocks = Clocks.GetOrCreateValue(target);
        clocks[property] = clock;
        clock.Completed += (_, _) =>
        {
            if (!clocks.TryGetValue(property, out var active) || active != clock) return;
            target.SetValue(property, value);
            Stop(target, property);
        };
        ((IAnimatable)target).ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Stop(DependencyObject target, DependencyProperty property)
    {
        // Preserve the current visual value when a newer interaction replaces a transition.
        object value = target.GetValue(property);
        if (Clocks.TryGetValue(target, out var clocks)) clocks.Remove(property);
        ((IAnimatable)target).ApplyAnimationClock(property, null);
        target.SetValue(property, value);
    }

    internal static TranslateTransform Translation(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform translate)
        { if (translate.IsFrozen) { translate = translate.Clone(); element.RenderTransform = translate; } return translate; }
        var result = new TranslateTransform(); element.RenderTransform = result; return result;
    }

    internal static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static void Refresh(DependencyObject root)
    {
        foreach (var element in Descendants(root))
            if (Feedbacks.TryGetValue(element, out var feedback)) feedback.Update(true);
    }

    private static void SelectedChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    { if (target is FrameworkElement element && Feedbacks.TryGetValue(element, out var feedback)) feedback.Update(); }
    private static void KindChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        element.Loaded -= Loaded; element.Unloaded -= Unloaded;
        Unloaded(element, new RoutedEventArgs());
        if ((MotionKind)e.NewValue == MotionKind.None) return;
        element.Loaded += Loaded; element.Unloaded += Unloaded;
        if (element.IsLoaded) Loaded(element, new RoutedEventArgs());
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (!Feedbacks.TryGetValue(element, out _)) Feedbacks.Add(element, new Feedback(element));
    }
    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (Feedbacks.TryGetValue(element, out var feedback)) { feedback.Dispose(); Feedbacks.Remove(element); }
    }

    private sealed class Feedback : IDisposable
    {
        private readonly FrameworkElement element;
        private readonly DependencyPropertyDescriptor? pressed;
        private readonly SolidColorBrush? cardBrush;
        internal Feedback(FrameworkElement element)
        {
            this.element = element;
            if (element is Control control) control.ApplyTemplate();
            if (element is Border card)
            { cardBrush = new SolidColorBrush(Color.FromRgb(41, 41, 41)); card.BorderBrush = cardBrush; }
            if (element is Button button)
            {
                pressed = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button));
                pressed?.AddValueChanged(button, Changed);
            }
            element.MouseEnter += MouseChanged; element.MouseLeave += MouseChanged;
            element.GotKeyboardFocus += FocusChanged; element.LostKeyboardFocus += FocusChanged;
            element.IsEnabledChanged += EnabledChanged;
            if (element is CheckBox toggle) { toggle.Checked += CheckedChanged; toggle.Unchecked += CheckedChanged; }
            Update(true);
        }
        private void Changed(object? sender, EventArgs e) => Update();
        private void MouseChanged(object sender, MouseEventArgs e) => Update();
        private void FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => Update();
        private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();
        private void CheckedChanged(object sender, RoutedEventArgs e) => Update();

        internal void Update(bool immediate = false)
        {
            int duration = immediate ? 0 : 170;
            if (cardBrush != null)
            { ColorTo(cardBrush, element.IsMouseOver ? Color.FromRgb(63, 63, 63) : Color.FromRgb(41, 41, 41), duration); return; }
            if (element is not Control control) return;
            object? Part(string name) => control.Template.FindName(name, control);
            if (element is Button button)
            {
                foreach (var (name, value) in new[] { ("HoverSurface", button.IsEnabled && button.IsMouseOver ? 1.0 : 0),
                    ("PressSurface", button.IsPressed ? 1.0 : 0), ("FocusSurface", button.IsKeyboardFocused ? 1.0 : 0),
                    ("SelectionSurface", GetSelected(button) ? 1.0 : 0) })
                    if (Part(name) is UIElement part) To(part, UIElement.OpacityProperty, value, duration);
                if (Part("Chrome") is FrameworkElement chrome)
                {
                    To(chrome, UIElement.OpacityProperty, button.IsEnabled ? 1 : .4, duration);
                    if (chrome.RenderTransform is ScaleTransform scale)
                    {
                        if (scale.IsFrozen) { scale = scale.Clone(); chrome.RenderTransform = scale; }
                        To(scale, ScaleTransform.ScaleXProperty, button.IsPressed ? .98 : 1, duration);
                        To(scale, ScaleTransform.ScaleYProperty, button.IsPressed ? .98 : 1, duration);
                    }
                }
            }
            else if (element is CheckBox toggle)
            {
                bool on = toggle.IsChecked == true;
                if (Part("CheckedTrack") is UIElement track) To(track, UIElement.OpacityProperty, on ? 1 : 0, duration);
                if (Part("Thumb") is System.Windows.Shapes.Ellipse thumb && thumb.Fill is SolidColorBrush fill)
                {
                    To(Translation(thumb), TranslateTransform.XProperty, on ? 24 : 0, duration);
                    if (fill.IsFrozen) { fill = fill.Clone(); thumb.Fill = fill; }
                    ColorTo(fill, on ? Color.FromRgb(17, 17, 17) : Color.FromRgb(150, 150, 150), duration);
                }
            }
        }
        public void Dispose()
        {
            pressed?.RemoveValueChanged(element, Changed);
            element.MouseEnter -= MouseChanged; element.MouseLeave -= MouseChanged;
            element.GotKeyboardFocus -= FocusChanged; element.LostKeyboardFocus -= FocusChanged;
            element.IsEnabledChanged -= EnabledChanged;
            if (element is CheckBox toggle) { toggle.Checked -= CheckedChanged; toggle.Unchecked -= CheckedChanged; }
            Update(true);
        }
    }
}
