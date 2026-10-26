using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zapret.Desktop.Runtime;

internal sealed class PageTransitions : IDisposable
{
    private readonly FrameworkElement[] pages;
    private readonly ScrollViewer scroll;
    private readonly FrameworkElement heading;
    private readonly Panel navigation;
    private readonly FrameworkElement marker;
    private readonly DispatcherTimer swap = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Dictionary<FrameworkElement, double> offsets = new();
    private FrameworkElement? current, requested;
    private Action? updateHeading;
    private Button? selected;

    internal PageTransitions(FrameworkElement[] pages, ScrollViewer scroll, FrameworkElement heading, Panel navigation, FrameworkElement marker)
    {
        this.pages = pages; this.scroll = scroll; this.heading = heading; this.navigation = navigation; this.marker = marker;
        Motion.Translation(scroll); Motion.Translation(heading); Motion.Translation(marker);
        swap.Tick += Swap;
        navigation.SizeChanged += NavigationSizeChanged;
    }

    internal void Select(FrameworkElement page, Button button, Action title)
    {
        selected = button;
        RefreshNavigation();
        MoveMarker();
        swap.Stop();
        requested = page; updateHeading = title;
        if (current == page) { Finish(); return; }
        if (current == null || !scroll.IsLoaded || !Motion.Enabled) { Swap(this, EventArgs.Empty); return; }
        Motion.To(scroll, UIElement.OpacityProperty, .55, 80);
        Motion.To(Motion.Translation(scroll), TranslateTransform.YProperty, -4, 80);
        Motion.To(heading, UIElement.OpacityProperty, .5, 80);
        swap.Start();
    }

    internal void RefreshNavigation()
    {
        foreach (var nav in navigation.Children.OfType<Button>().Where(b => b.Tag is string))
        {
            bool active = nav == selected;
            Motion.SetSelected(nav, active);
            nav.Foreground = (Brush)nav.FindResource(active ? "TextBrush" : "MutedBrush");
        }
    }

    private void Swap(object? sender, EventArgs e)
    {
        swap.Stop();
        if (requested == null) return;
        SmoothScroll.Stop(scroll);
        if (current != null)
        {
            offsets[current] = scroll.VerticalOffset;
            if (current.IsKeyboardFocusWithin) selected?.Focus();
            ResetCards(current);
        }
        current = requested;
        foreach (var panel in pages) panel.Visibility = panel == current ? Visibility.Visible : Visibility.Collapsed;
        updateHeading?.Invoke(); updateHeading = null;
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(offsets.GetValueOrDefault(current));
        bool animate = scroll.IsLoaded && Motion.Enabled;
        Motion.To(scroll, UIElement.OpacityProperty, 1, animate ? 200 : 0, animate ? .45 : 1);
        Motion.To(Motion.Translation(scroll), TranslateTransform.YProperty, 0, animate ? 240 : 0, animate ? 12 : 0);
        Motion.To(heading, UIElement.OpacityProperty, 1, animate ? 190 : 0, animate ? .5 : 1);
        Motion.To(Motion.Translation(heading), TranslateTransform.YProperty, 0, animate ? 210 : 0, animate ? 5 : 0);
        if (animate) EnterCards(current);
    }

    internal void Open()
    {
        MoveMarker(false);
        if (!Motion.Enabled || current == null) return;
        Motion.To(scroll, UIElement.OpacityProperty, 1, 260, 0);
        Motion.To(Motion.Translation(scroll), TranslateTransform.YProperty, 0, 280, 14);
        Motion.To(heading, UIElement.OpacityProperty, 1, 220, 0);
        EnterCards(current);
    }

    private void EnterCards(FrameworkElement page)
    {
        int index = 0;
        foreach (var card in Motion.Descendants(page).Where(x => Motion.GetKind(x) == MotionKind.Card))
        {
            var position = card.TransformToAncestor(scroll).Transform(new Point());
            if (position.Y > scroll.ViewportHeight || position.Y + card.ActualHeight < 0 || index >= 5) continue;
            int duration = 200 + index++ * 30;
            Motion.To(card, UIElement.OpacityProperty, 1, duration, .3);
            Motion.To(Motion.Translation(card), TranslateTransform.YProperty, 0, duration + 30, 8);
        }
    }
    private static void ResetCards(FrameworkElement page)
    {
        foreach (var card in Motion.Descendants(page).Where(x => Motion.GetKind(x) == MotionKind.Card))
        { Motion.To(card, UIElement.OpacityProperty, 1, 0); Motion.To(Motion.Translation(card), TranslateTransform.YProperty, 0, 0); }
    }
    private void NavigationSizeChanged(object sender, SizeChangedEventArgs e) => MoveMarker(false);
    private void MoveMarker(bool animate = true)
    {
        if (selected == null || selected.ActualHeight == 0) return;
        double y = selected.TranslatePoint(new Point(), navigation).Y + (selected.ActualHeight - marker.Height) / 2;
        Motion.To(Motion.Translation(marker), TranslateTransform.YProperty, y, animate ? 230 : 0);
    }
    internal void Finish()
    {
        if (swap.IsEnabled) Swap(this, EventArgs.Empty);
        Motion.To(scroll, UIElement.OpacityProperty, 1, 0);
        Motion.To(Motion.Translation(scroll), TranslateTransform.YProperty, 0, 0);
        Motion.To(heading, UIElement.OpacityProperty, 1, 0);
        Motion.To(Motion.Translation(heading), TranslateTransform.YProperty, 0, 0);
        foreach (var page in pages) ResetCards(page);
        MoveMarker(false); SmoothScroll.Stop(scroll);
    }
    public void Dispose() { swap.Stop(); navigation.SizeChanged -= NavigationSizeChanged; }
}
