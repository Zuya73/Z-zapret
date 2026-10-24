using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Zapret.Desktop.Runtime;

internal sealed class WindowWorkArea
{
    private readonly Window window;
    private HwndSource? source;
    private IntPtr handle, monitor;
    private bool queued;

    internal WindowWorkArea(Window window)
    {
        this.window = window;
        window.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle);
            source?.AddHook(WindowProc);
            UpdateMinimum();
        };
        window.Loaded += (_, _) => QueueFit();
        window.DpiChanged += (_, _) => QueueFit();
        window.StateChanged += (_, _) => QueueFit();
        window.LocationChanged += (_, _) =>
        {
            if (handle != IntPtr.Zero && MonitorFromWindow(handle, 2) != monitor) QueueFit();
        };
        window.Closed += (_, _) => { source?.RemoveHook(WindowProc); source = null; };
    }

    private void QueueFit()
    {
        if (queued || window.Dispatcher.HasShutdownStarted) return;
        queued = true;
        window.Dispatcher.BeginInvoke(() =>
        {
            queued = false;
            if (source is null || source.IsDisposed) return;
            UpdateMinimum();
            if (window.WindowState != WindowState.Normal || !GetWindowRect(handle, out var rect)) return;
            var current = rect.Bounds;
            var fitted = WindowGeometry.Fit(current, WorkArea);
            if (current != fitted)
                SetWindowPos(handle, IntPtr.Zero, fitted.Left, fitted.Top, fitted.Width, fitted.Height, 0x0014);
        }, DispatcherPriority.Loaded);
    }

    internal PixelBounds WorkArea => GetMonitor().Work.Bounds;
    internal PixelBounds Bounds => GetWindowRect(handle, out var rect) ? rect.Bounds : default;

    private MonitorInfo GetMonitor()
    {
        monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }

    private void UpdateMinimum()
    {
        uint dpi = GetDpiForWindow(handle);
        var minimum = WindowGeometry.Minimum(WorkArea, dpi == 0 ? 96 : dpi);
        window.MinWidth = minimum.Width;
        window.MinHeight = minimum.Height;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO: custom chrome must respect this monitor's taskbar.
        {
            var info = GetMonitor();
            var bounds = WindowGeometry.Maximized(info.Monitor.Bounds, info.Work.Bounds);
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MaxPosition = new() { X = bounds.Left, Y = bounds.Top };
            limits.MaxSize = new() { X = bounds.Width, Y = bounds.Height };
            // WPF's nominal minimum must never force a larger window than the work area.
            limits.MinTrackSize.X = Math.Min(limits.MinTrackSize.X, bounds.Width);
            limits.MinTrackSize.Y = Math.Min(limits.MinTrackSize.Y, bounds.Height);
            Marshal.StructureToPtr(limits, lParam, false);
            handled = true;
        }
        else if (message is 0x001A or 0x007E) QueueFit(); // Work area, taskbar or display change.
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelBounds Bounds => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
