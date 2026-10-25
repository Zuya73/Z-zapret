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
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = Math.Max(1, ActualWidth - 76), h = Math.Max(1, ActualHeight - 32);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 9, 9)), null, new(0, 0, ActualWidth, ActualHeight));
        var valid = Points.Where(x => double.IsFinite(x.Value)).ToArray();
        if (valid.Length == 0) { Text("Ожидание показаний…", 15, 18); return; }
        double min = valid.Min(x => x.Value), max = valid.Max(x => x.Value);
        if (Kind is "Load" or "Level" or "Control") { min = Math.Min(0, min); max = Math.Max(100, max); }
        if (Threshold.HasValue) { min = Math.Min(min, Threshold.Value); max = Math.Max(max, Threshold.Value); }
        double padding = Math.Max(.5, (max - min) * .1); min -= padding; max += padding;
        var end = valid[^1].At; var start = valid[0].At;
        double seconds = Math.Max(10, (end - start).TotalSeconds);
        Point At(SensorPoint p) => new(8 + w * (p.At - start).TotalSeconds / seconds, 8 + h * (1 - (p.Value - min) / (max - min)));
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(40, 40, 40)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = 8 + h * i / 4; dc.DrawLine(gridPen, new(8, y), new(8 + w, y));
            Text((max - (max - min) * i / 4).ToString("0.#", CultureInfo.CurrentCulture), w + 16, y - 6);
        }
        if (Threshold is double threshold)
        {
            double y = 8 + h * (1 - (threshold - min) / (max - min));
            dc.DrawLine(new Pen(Brushes.Gray, 1) { DashStyle = DashStyles.Dash }, new(8, y), new(8 + w, y));
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int i = 0; i < valid.Length; i++)
                if (i == 0 || valid[i].At - valid[i - 1].At > TimeSpan.FromSeconds(8)) context.BeginFigure(At(valid[i]), false, false);
                else context.LineTo(At(valid[i]), true, false);
        }
        geometry.Freeze(); dc.DrawGeometry(null, new Pen(Brushes.WhiteSmoke, 1.8), geometry);
        dc.DrawEllipse(Brushes.WhiteSmoke, null, At(valid[^1]), 3, 3);
        Text(start.ToLocalTime().ToString("HH:mm:ss"), 8, h + 14);
        Text(end.ToLocalTime().ToString("HH:mm:ss"), Math.Max(8, w - 42), h + 14);
        ToolTip = $"{end.ToLocalTime():HH:mm:ss} · {SensorStatistics.Format(valid[^1].Value, Kind)}";
        void Text(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    }
}
