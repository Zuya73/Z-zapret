namespace Zapret.Desktop.Runtime;

internal readonly record struct PixelBounds(int Left, int Top, int Width, int Height);

internal static class WindowGeometry
{
    internal static PixelBounds Fit(PixelBounds window, PixelBounds work)
    {
        int width = Math.Min(window.Width, work.Width), height = Math.Min(window.Height, work.Height);
        return new(Math.Clamp(window.Left, work.Left, work.Left + work.Width - width),
            Math.Clamp(window.Top, work.Top, work.Top + work.Height - height), width, height);
    }

    internal static (double Width, double Height) Minimum(PixelBounds work, uint dpi) =>
        (Math.Min(1040, work.Width * 96.0 / dpi), Math.Min(760, work.Height * 96.0 / dpi));

    // WM_GETMINMAXINFO uses monitor-relative physical pixels, including on monitors left of the primary.
    internal static PixelBounds Maximized(PixelBounds monitor, PixelBounds work) =>
        new(work.Left - monitor.Left, work.Top - monitor.Top, work.Width, work.Height);
}
