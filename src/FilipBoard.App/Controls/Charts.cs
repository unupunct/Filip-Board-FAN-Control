using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FilipBoard.App.Controls;

public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<double> Values);

internal static class Draw
{
    public static FormattedText Text(Visual v, string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);

    public static Brush Res(FrameworkElement e, string key, Brush fallback) => e.TryFindResource(key) as Brush ?? fallback;
}

/// <summary>Lightweight multi-series line chart drawn directly (no chart library dependency).</summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series),
        typeof(IReadOnlyList<ChartSeries>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YMinProperty = DependencyProperty.Register(nameof(YMin), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YMaxProperty = DependencyProperty.Register(nameof(YMax), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("No data yet", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public double YMin { get => (double)GetValue(YMinProperty); set => SetValue(YMinProperty, value); }
    public double YMax { get => (double)GetValue(YMaxProperty); set => SetValue(YMaxProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w < 40 || h < 30) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var muted = Draw.Res(this, "TextMuted", Brushes.Gray);
        var grid = new Pen(Draw.Res(this, "Border", Brushes.DimGray), 1);

        var series = Series?.Where(s => s.Values.Count > 1).ToList() ?? new();
        if (series.Count == 0)
        {
            var t = Draw.Text(this, EmptyText, 12, muted);
            dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
            return;
        }

        var all = series.SelectMany(s => s.Values).Where(v => !double.IsNaN(v)).ToList();
        var lo = double.IsNaN(YMin) ? all.Min() : YMin;
        var hi = double.IsNaN(YMax) ? all.Max() : YMax;
        if (double.IsNaN(YMin)) lo = Math.Floor(lo - 2);
        if (double.IsNaN(YMax)) hi = Math.Ceiling(hi + 2);
        if (hi - lo < 4) hi = lo + 4;

        const double left = 44, bottom = 6, top = 8, right = 8;
        var plot = new Rect(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Draw.Text(this, $"{lo + (hi - lo) * i / 4:0}{Unit}", 10, muted);
            dc.DrawText(label, new Point(left - label.Width - 6, y - label.Height / 2));
        }

        var maxCount = series.Max(s => s.Values.Count);
        foreach (var s in series)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                var started = false;
                for (var i = 0; i < s.Values.Count; i++)
                {
                    var v = s.Values[i];
                    if (double.IsNaN(v)) { started = false; continue; }
                    var x = plot.Right - plot.Width * (s.Values.Count - 1 - i) / Math.Max(1, maxCount - 1);
                    var y = plot.Bottom - plot.Height * (Math.Clamp(v, lo, hi) - lo) / (hi - lo);
                    if (!started) { g.BeginFigure(new Point(x, y), false, false); started = true; }
                    else g.LineTo(new Point(x, y), true, true);
                }
            }
            geo.Freeze();
            var pen = new Pen(new SolidColorBrush(s.Color), 1.8) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, geo);
        }

        // legend
        var lx = plot.Left + 8;
        foreach (var s in series)
        {
            var t = Draw.Text(this, s.Name, 10.5, muted);
            dc.DrawRectangle(new SolidColorBrush(s.Color), null, new Rect(lx, plot.Top + 4, 10, 3));
            dc.DrawText(t, new Point(lx + 14, plot.Top - 3));
            lx += t.Width + 30;
        }
    }
}

/// <summary>Animated 240-degree arc gauge with normal/elevated/critical colouring.</summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double?), typeof(Gauge),
        new FrameworkPropertyMetadata(null, (d, e) => ((Gauge)d).Animate()));
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(nameof(Shown), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WarnProperty = DependencyProperty.Register(nameof(Warn), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CritProperty = DependencyProperty.Register(nameof(Crit), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(Gauge),
        new FrameworkPropertyMetadata("--", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(Gauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Shown { get => (double)GetValue(ShownProperty); set => SetValue(ShownProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Warn { get => (double)GetValue(WarnProperty); set => SetValue(WarnProperty, value); }
    public double Crit { get => (double)GetValue(CritProperty); set => SetValue(CritProperty, value); }
    public string DisplayText { get => (string)GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    private void Animate()
    {
        var target = Value ?? Minimum;
        BeginAnimation(ShownProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 40) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2 + size * 0.04);
        var r = size / 2 - 12;
        const double start = 150, sweep = 240;
        var track = new Pen(Draw.Res(this, "Border", Brushes.DimGray), 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(c, r, start, sweep));

        var frac = Maximum > Minimum ? Math.Clamp((Shown - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        Brush accent = Value is null ? Brushes.Transparent
            : Value >= Crit ? Draw.Res(this, "Red", Brushes.Red)
            : Value >= Warn ? Draw.Res(this, "Amber", Brushes.Orange)
            : Draw.Res(this, "Cyan", Brushes.Cyan);
        if (Value != null && frac > 0.001)
            dc.DrawGeometry(null, new Pen(accent, 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(c, r, start, sweep * frac));

        var main = Draw.Text(this, DisplayText, size * 0.2, Draw.Res(this, "Text", Brushes.White), true);
        dc.DrawText(main, new Point(c.X - main.Width / 2, c.Y - main.Height / 2 - 2));
        var cap = Draw.Text(this, Caption, size * 0.075 + 3, Draw.Res(this, "TextMuted", Brushes.Gray));
        dc.DrawText(cap, new Point(c.X - cap.Width / 2, c.Y + size * 0.17));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        Point P(double deg) { var a = deg * Math.PI / 180; return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)); }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false, false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
