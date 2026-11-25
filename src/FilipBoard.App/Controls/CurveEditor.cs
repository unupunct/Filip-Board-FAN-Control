using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FilipBoard.Core;

namespace FilipBoard.App.Controls;

/// <summary>
/// Interactive fan-curve editor. Drag points; double-click empty space to add; right-click a point to remove.
/// Speeds are kept monotonic and never below <see cref="FloorPercent"/>.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(nameof(Curve), typeof(FanCurve), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FloorPercentProperty = DependencyProperty.Register(nameof(FloorPercent), typeof(double), typeof(CurveEditor),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CurrentTempProperty = DependencyProperty.Register(nameof(CurrentTemp), typeof(double?), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FanCurve? Curve { get => (FanCurve?)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
    public double FloorPercent { get => (double)GetValue(FloorPercentProperty); set => SetValue(FloorPercentProperty, value); }
    /// <summary>Live temperature (the preview marker shows what speed the curve gives right now).</summary>
    public double? CurrentTemp { get => (double?)GetValue(CurrentTempProperty); set => SetValue(CurrentTempProperty, value); }

    public static readonly DependencyProperty ChangedCommandProperty = DependencyProperty.Register(nameof(ChangedCommand), typeof(ICommand), typeof(CurveEditor));
    public ICommand? ChangedCommand { get => (ICommand?)GetValue(ChangedCommandProperty); set => SetValue(ChangedCommandProperty, value); }

    public event EventHandler? CurveChanged;

    private const double MinT = 20, MaxT = 100, L = 46, T = 12, R = 14, B = 30;
    private int _drag = -1;

    public CurveEditor() { Focusable = true; ClipToBounds = true; }

    private Rect Plot => new(L, T, Math.Max(10, ActualWidth - L - R), Math.Max(10, ActualHeight - T - B));
    private Point ToPx(CurvePoint p) => new(Plot.Left + Plot.Width * (p.TempC - MinT) / (MaxT - MinT), Plot.Bottom - Plot.Height * p.Percent / 100);
    private (double t, double p) FromPx(Point px) => (
        MinT + (MaxT - MinT) * (px.X - Plot.Left) / Plot.Width,
        100 * (Plot.Bottom - px.Y) / Plot.Height);

    private List<CurvePoint> Sorted() => Curve!.Points;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var plot = Plot;
        var muted = Draw.Res(this, "TextMuted", Brushes.Gray);
        var grid = new Pen(Draw.Res(this, "Border", Brushes.DimGray), 1);
        for (var p = 0; p <= 100; p += 20)
        {
            var y = plot.Bottom - plot.Height * p / 100;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var l = Draw.Text(this, $"{p}%", 10, muted); dc.DrawText(l, new Point(L - l.Width - 6, y - l.Height / 2));
        }
        for (var t = (int)MinT; t <= MaxT; t += 10)
        {
            var x = plot.Left + plot.Width * (t - MinT) / (MaxT - MinT);
            dc.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var l = Draw.Text(this, $"{t}°", 10, muted); dc.DrawText(l, new Point(x - l.Width / 2, plot.Bottom + 6));
        }
        // minimum-safe floor
        var fy = plot.Bottom - plot.Height * FloorPercent / 100;
        dc.DrawLine(new Pen(Draw.Res(this, "Amber", Brushes.Orange), 1) { DashStyle = DashStyles.Dash }, new Point(plot.Left, fy), new Point(plot.Right, fy));
        var fl = Draw.Text(this, "min safe", 9.5, Draw.Res(this, "Amber", Brushes.Orange)); dc.DrawText(fl, new Point(plot.Right - fl.Width - 4, fy - fl.Height));

        if (Curve is null || Curve.Points.Count == 0) return;
        var pts = Curve.Points.OrderBy(p => p.TempC).ToList();
        var line = new Pen(Draw.Res(this, "Red", Brushes.Red), 2.2);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            var first = ToPx(new CurvePoint(MinT, pts[0].Percent));
            g.BeginFigure(first, false, false);
            foreach (var p in pts) g.LineTo(ToPx(p), true, true);
            g.LineTo(ToPx(new CurvePoint(MaxT, pts[^1].Percent)), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, line, geo);

        if (CurrentTemp is { } ct && ct >= MinT && ct <= MaxT)
        {
            var pct = Curve.Evaluate(ct);
            var x = ToPx(new CurvePoint(ct, pct));
            dc.DrawLine(new Pen(Draw.Res(this, "Cyan", Brushes.Cyan), 1) { DashStyle = DashStyles.Dot }, new Point(x.X, plot.Top), new Point(x.X, plot.Bottom));
            dc.DrawEllipse(Draw.Res(this, "Cyan", Brushes.Cyan), null, x, 5, 5);
            var lab = Draw.Text(this, $"now {ct:0.#}° → {pct:0}%", 10.5, Draw.Res(this, "Cyan", Brushes.Cyan), true);
            dc.DrawText(lab, new Point(Math.Min(x.X + 8, plot.Right - lab.Width), Math.Max(plot.Top, x.Y - 20)));
        }

        for (var i = 0; i < pts.Count; i++)
        {
            var px = ToPx(pts[i]);
            dc.DrawEllipse(Draw.Res(this, "Surface", Brushes.Black), new Pen(Draw.Res(this, "Red", Brushes.Red), 2), px, 6.5, 6.5);
            if (i == _drag)
            {
                var tip = Draw.Text(this, $"{pts[i].TempC:0}°C  {pts[i].Percent:0}%", 10.5, Draw.Res(this, "Text", Brushes.White), true);
                dc.DrawText(tip, new Point(Math.Min(px.X + 10, plot.Right - tip.Width), Math.Max(plot.Top, px.Y - 24)));
            }
        }
    }

    private int HitPoint(Point px)
    {
        if (Curve is null) return -1;
        var pts = Curve.Points;
        for (var i = 0; i < pts.Count; i++)
        {
            var p = ToPx(pts[i]);
            if ((p - px).Length <= 10) return i;
        }
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Curve is null) return;
        Curve.Points.Sort((a, b) => a.TempC.CompareTo(b.TempC));
        var pos = e.GetPosition(this);
        var hit = HitPoint(pos);
        if (e.ClickCount == 2 && hit < 0 && Plot.Contains(pos))
        {
            var (t, p) = FromPx(pos);
            t = Math.Round(Math.Clamp(t, MinT, MaxT));
            if (Curve.Points.All(x => Math.Abs(x.TempC - t) >= 1))
            {
                p = Math.Round(Math.Clamp(p, FloorPercent, 100));
                Curve.Points.Add(new CurvePoint(t, p));
                Curve.Points.Sort((a, b) => a.TempC.CompareTo(b.TempC));
                var norm = Curve.Normalized(FloorPercent);
                Curve.Points.Clear(); Curve.Points.AddRange(norm.Points); // mutate in place so the owning config sees it
                Raise(); InvalidateVisual();
            }
            return;
        }
        if (hit >= 0) { _drag = hit; CaptureMouse(); InvalidateVisual(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag < 0 || Curve is null) return;
        var pts = Curve.Points;
        if (_drag >= pts.Count) { _drag = -1; return; }
        var (t, p) = FromPx(e.GetPosition(this));
        var loT = _drag > 0 ? pts[_drag - 1].TempC + 1 : MinT;
        var hiT = _drag < pts.Count - 1 ? pts[_drag + 1].TempC - 1 : MaxT;
        var loP = _drag > 0 ? Math.Max(FloorPercent, pts[_drag - 1].Percent) : FloorPercent;
        var hiP = _drag < pts.Count - 1 ? pts[_drag + 1].Percent : 100;
        pts[_drag] = new CurvePoint(Math.Round(Math.Clamp(t, loT, hiT)), Math.Round(Math.Clamp(p, loP, hiP)));
        Raise(); InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag >= 0) { _drag = -1; ReleaseMouseCapture(); InvalidateVisual(); Raise(); }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (Curve is null || Curve.Points.Count <= 2) return;
        Curve.Points.Sort((a, b) => a.TempC.CompareTo(b.TempC));
        var hit = HitPoint(e.GetPosition(this));
        if (hit >= 0) { Curve.Points.RemoveAt(hit); Raise(); InvalidateVisual(); }
    }

    private void Raise() { CurveChanged?.Invoke(this, EventArgs.Empty); if (ChangedCommand?.CanExecute(null) == true) ChangedCommand.Execute(null); }
}
