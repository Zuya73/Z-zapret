using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

public sealed class SensorChart : FrameworkElement
{
    public IReadOnlyList<SensorPoint> Points { get; set; } = [];
    public string Kind { get; set; } = "";
    public double? Threshold { get; set; }
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
    private Brush Theme(string key, Color fallback) => TryFindResource(key) as Brush ?? Frozen(new SolidColorBrush(fallback));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        // Colours come from the active theme on every render, so a theme switch only needs InvalidateVisual.
        var surface = Theme("ChartSurfaceBrush", Color.FromRgb(9, 9, 9));
        var label = Theme("SubtleBrush", Color.FromRgb(110, 110, 110));
        var line = Theme("TextBrush", Color.FromRgb(244, 244, 244));
        var gridPen = Frozen(new Pen(Theme("ChartGridBrush", Color.FromRgb(30, 30, 30)), 1));
        var thresholdPen = Frozen(new Pen(Theme("MutedBrush", Color.FromRgb(140, 140, 140)), 1) { DashStyle = DashStyles.Dash });
        var tracePen = Frozen(new Pen(line, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var ink = (line as SolidColorBrush)?.Color ?? Colors.White;
        var areaFill = Frozen(new LinearGradientBrush(Color.FromArgb(46, ink.R, ink.G, ink.B), Color.FromArgb(0, ink.R, ink.G, ink.B), 90));
        double w = Math.Max(1, ActualWidth - 76), h = Math.Max(1, ActualHeight - 32);
        dc.DrawRoundedRectangle(surface, null, new(0, 0, ActualWidth, ActualHeight), 10, 10);
        var valid = Points.Where(x => double.IsFinite(x.Value)).ToArray();
        if (valid.Length == 0) { Text("Ожидание показаний…", 15, 18); return; }
        double min = valid.Min(x => x.Value), max = valid.Max(x => x.Value);
        if (Kind is "Load" or "Level" or "Control") { min = Math.Min(0, min); max = Math.Max(100, max); }
        if (Threshold.HasValue) { min = Math.Min(min, Threshold.Value); max = Math.Max(max, Threshold.Value); }
        double padding = Math.Max(.5, (max - min) * .1); min -= padding; max += padding;
        var end = valid[^1].At; var start = valid[0].At;
        double seconds = Math.Max(10, (end - start).TotalSeconds);
        Point At(SensorPoint p) => new(8 + w * (p.At - start).TotalSeconds / seconds, 8 + h * (1 - (p.Value - min) / (max - min)));
        for (int i = 0; i <= 4; i++)
        {
            double y = 8 + h * i / 4; dc.DrawLine(gridPen, new(8, y), new(8 + w, y));
            Text((max - (max - min) * i / 4).ToString("0.#", CultureInfo.CurrentCulture), w + 16, y - 6);
        }
        if (Threshold is double threshold)
        {
            double y = 8 + h * (1 - (threshold - min) / (max - min));
            dc.DrawLine(thresholdPen, new(8, y), new(8 + w, y));
        }
        // Gaps longer than a few polls split the trace into separate runs; each run gets its own soft area fill.
        var runs = new List<List<Point>>();
        for (int i = 0; i < valid.Length; i++)
        {
            if (i == 0 || valid[i].At - valid[i - 1].At > TimeSpan.FromSeconds(8)) runs.Add([]);
            runs[^1].Add(At(valid[i]));
        }
        var area = new StreamGeometry();
        var trace = new StreamGeometry();
        using (var areaContext = area.Open())
        using (var traceContext = trace.Open())
            foreach (var run in runs.Where(r => r.Count > 1))
            {
                areaContext.BeginFigure(new(run[0].X, 8 + h), true, true);
                foreach (var point in run) areaContext.LineTo(point, false, false);
                areaContext.LineTo(new(run[^1].X, 8 + h), false, false);
                traceContext.BeginFigure(run[0], false, false);
                traceContext.PolyLineTo(run.Skip(1).ToArray(), true, true);
            }
        area.Freeze(); trace.Freeze();
        dc.DrawGeometry(areaFill, null, area);
        dc.DrawGeometry(null, tracePen, trace);
        foreach (var run in runs.Where(r => r.Count == 1)) dc.DrawEllipse(line, null, run[0], 2, 2);
        dc.DrawEllipse(surface, new Pen(line, 2), At(valid[^1]), 4, 4);
        Text(start.ToLocalTime().ToString("HH:mm:ss"), 8, h + 14);
        Text(end.ToLocalTime().ToString("HH:mm:ss"), Math.Max(8, w - 42), h + 14);
        ToolTip = $"{end.ToLocalTime():HH:mm:ss} · {SensorStatistics.Format(valid[^1].Value, Kind)}";
        void Text(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, label, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    }
}
