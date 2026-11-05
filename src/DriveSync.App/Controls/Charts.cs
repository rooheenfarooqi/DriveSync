using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DriveSync.App.Shell;

namespace DriveSync.App.Controls;

/// <summary>The "Changes, last 24 hours" bar chart: one bar per hour, the current hour in full accent.</summary>
public sealed class HourlyBars : FrameworkElement
{
    private int[] _values = new int[24];
    private readonly ToolTip _tip = new();

    public HourlyBars()
    {
        Height = 110;
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetPlacement(this, System.Windows.Controls.Primitives.PlacementMode.Mouse);
        MouseMove += (_, e) =>
        {
            int i = Math.Clamp((int)(e.GetPosition(this).X / Math.Max(1, ActualWidth) * 24), 0, 23);
            int hoursAgo = 23 - i;
            _tip.Content = $"{_values[i]} change{(_values[i] == 1 ? "" : "s")} · {(hoursAgo == 0 ? "this hour" : hoursAgo == 1 ? "1 hour ago" : $"{hoursAgo} hours ago")}";
        };
        ThemeManager.Changed += InvalidateVisual;
        System.Windows.Automation.AutomationProperties.SetName(this, "Changes per hour over the last 24 hours");
    }

    public void SetValues(int[] values)
    {
        _values = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight - 10;
        if (w <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        var grid = new Pen(ThemeManager.Res("StrokeBrush"), 1);
        foreach (double g in new[] { 0.25, 0.5, 0.75 })
            dc.DrawLine(grid, new Point(0, Math.Round(h - g * h) + 0.5), new Point(w, Math.Round(h - g * h) + 0.5));
        int max = Math.Max(4, _values.Max());
        double bw = w / 24;
        Brush full = ThemeManager.Res("AccentBrush"), faint = ThemeManager.Res("AccentFaintBrush");
        for (int i = 0; i < 24; i++)
        {
            double bh = _values[i] / (double)max * h;
            if (bh <= 0) continue;
            dc.DrawRoundedRectangle(i == 23 ? full : faint, null, new Rect(i * bw + 1.5, h - bh + 4, Math.Max(1, bw - 3), bh), 2, 2);
        }
        var label = new FormattedText(_values[23].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)Application.Current.Resources["FontBody"], FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10, ThemeManager.Res("TextBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double y = Math.Max(0, h - _values[23] / (double)max * h - 12);
        dc.DrawText(label, new Point(w - label.Width - 2, y));
    }
}

/// <summary>One line on the benchmark chart.</summary>
public sealed record Series(string Name, string BrushKey, double?[] Values, bool Dashed = false);

/// <summary>
/// The benchmark chart: median lookup time per collection size on a log scale, a line per method,
/// value labels at the end and a tooltip on every point.
/// </summary>
public sealed class LogLineChart : FrameworkElement
{
    private const double L = 58, R = 110, T = 16, B = 36;
    private int[] _sizes = Array.Empty<int>();
    private List<Series> _series = new();
    private readonly ToolTip _tip = new();
    private readonly List<(Point P, string Text)> _points = new();

    public LogLineChart()
    {
        Height = 290;
        MinWidth = 420;
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetPlacement(this, System.Windows.Controls.Primitives.PlacementMode.Mouse);
        MouseMove += OnMouseMove;
        ThemeManager.Changed += InvalidateVisual;
        System.Windows.Automation.AutomationProperties.SetName(this, "Median lookup time by collection size, log scale");
    }

    public void SetData(int[] sizes, List<Series> series)
    {
        _sizes = sizes;
        _series = series;
        InvalidateVisual();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point m = e.GetPosition(this);
        var near = _points.OrderBy(p => (p.P - m).Length).FirstOrDefault();
        _tip.Content = near.Text != null && (near.P - m).Length < 18 ? near.Text : "Hover a point to see its value";
    }

    protected override void OnRender(DrawingContext dc)
    {
        _points.Clear();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w <= 0 || _sizes.Length == 0) return;

        var all = _series.SelectMany(s => s.Values).Where(v => v is > 0).Select(v => v!.Value).ToList();
        if (all.Count == 0) return;
        double lo = Math.Pow(10, Math.Floor(Math.Log10(all.Min())));
        double hi = Math.Pow(10, Math.Ceiling(Math.Log10(all.Max())));
        if (hi <= lo) hi = lo * 10;
        double Y(double v) => T + (h - T - B) * (1 - (Math.Log10(v) - Math.Log10(lo)) / (Math.Log10(hi) - Math.Log10(lo)));
        double X(int i) => _sizes.Length == 1 ? L + (w - L - R) / 2 : L + (w - L - R) * i / (_sizes.Length - 1);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface body = new((FontFamily)Application.Current.Resources["FontBody"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Typeface bold = new((FontFamily)Application.Current.Resources["FontBody"], FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        Typeface mono = new((FontFamily)Application.Current.Resources["FontMono"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Brush text2 = ThemeManager.Res("Text2Brush");
        var gridPen = new Pen(ThemeManager.Res("StrokeBrush"), 1);

        for (int e = (int)Math.Round(Math.Log10(lo)); e <= (int)Math.Round(Math.Log10(hi)); e++)
        {
            double d = Math.Pow(10, e);
            double y = Math.Round(Y(d)) + 0.5;
            dc.DrawLine(gridPen, new Point(L, y), new Point(w - R, y));
            var label = new FormattedText(Fmt.Ms(d).Replace(".000 ms", " ms"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, mono, 10.5, text2, dip);
            dc.DrawText(label, new Point(L - 8 - label.Width, y - label.Height / 2));
        }
        for (int i = 0; i < _sizes.Length; i++)
        {
            string s = _sizes[i] >= 1000 ? $"{_sizes[i] / 1000.0:0.#}k files" : $"{_sizes[i]} files";
            var label = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, body, 11, text2, dip);
            dc.DrawText(label, new Point(X(i) - label.Width / 2, h - 12 - label.Height + 4));
        }

        Brush ground = new SolidColorBrush((Color)Application.Current.Resources["Wall1Color"]);
        var labelYs = new List<double>();
        foreach (Series series in _series)
        {
            Brush color = ThemeManager.Res(series.BrushKey);
            var pen = new Pen(color, 2.25) { LineJoin = PenLineJoin.Round };
            if (series.Dashed) pen.DashStyle = new DashStyle(new double[] { 3, 2 }, 0);
            var pts = new List<(int I, Point P, double V)>();
            for (int i = 0; i < series.Values.Length && i < _sizes.Length; i++)
                if (series.Values[i] is double v && v > 0) pts.Add((i, new Point(X(i), Y(v)), v));
            for (int k = 1; k < pts.Count; k++) dc.DrawLine(pen, pts[k - 1].P, pts[k].P);
            for (int k = 0; k < pts.Count; k++)
            {
                bool last = k == pts.Count - 1;
                dc.DrawEllipse(last ? color : ground, new Pen(color, 2), pts[k].P, last ? 5 : 3.5, last ? 5 : 3.5);
                _points.Add((pts[k].P, $"{series.Name}, {Fmt.Num(_sizes[pts[k].I])} files: {Fmt.Ms(pts[k].V)}"));
            }
            if (pts.Count > 0)
            {
                var end = pts[^1];
                var label = new FormattedText(Fmt.Ms(end.V), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 11,
                    series.BrushKey == "Text3Brush" ? text2 : color, dip);
                // Keep end labels from sitting on top of each other.
                double y = end.P.Y - label.Height / 2;
                while (labelYs.Any(other => Math.Abs(other - y) < label.Height)) y += label.Height;
                labelYs.Add(y);
                dc.DrawText(label, new Point(end.P.X + 12, y));
            }
        }
    }
}
