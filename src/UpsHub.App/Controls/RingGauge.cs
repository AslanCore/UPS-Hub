using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

namespace UpsHub.App.Controls;

/// <summary>Circular gauge: a 270° track with an animated value arc and a soft glow.</summary>
public sealed class RingGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Value));
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Thickness), 10);
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(ValueBrush), new SolidColorBrush(Color.Parse("#FF5C1A")));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(TrackBrush), new SolidColorBrush(Color.Parse("#1AF3F1EE")));
    public static readonly StyledProperty<bool> HasValueProperty =
        AvaloniaProperty.Register<RingGauge, bool>(nameof(HasValue), true);

    static RingGauge()
    {
        AffectsRender<RingGauge>(ValueProperty, MaximumProperty, ThicknessProperty, ValueBrushProperty, TrackBrushProperty, HasValueProperty);
    }

    public RingGauge()
    {
        Transitions = new Transitions
        {
            new DoubleTransition { Property = ValueProperty, Duration = TimeSpan.FromMilliseconds(400), Easing = new Avalonia.Animation.Easings.SplineEasing(0.23, 1, 0.32, 1) },
        };
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public bool HasValue { get => GetValue(HasValueProperty); set => SetValue(HasValueProperty, value); }

    private const double Start = 135, Sweep = 270;

    public override void Render(DrawingContext ctx)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var t = Thickness;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = size / 2 - t / 2 - 6;
        ctx.DrawGeometry(null, new Pen(TrackBrush, t, lineCap: PenLineCap.Round), Arc(c, r, Start, Sweep));
        if (!HasValue) return;
        var frac = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);
        if (frac <= 0.001) return;
        var arc = Arc(c, r, Start, Sweep * frac);
        if (ValueBrush is ISolidColorBrush sb)
        {
            var glow = new SolidColorBrush(sb.Color, 0.18);
            ctx.DrawGeometry(null, new Pen(glow, t + 8, lineCap: PenLineCap.Round), arc);
        }
        ctx.DrawGeometry(null, new Pen(ValueBrush, t, lineCap: PenLineCap.Round), arc);
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return g;
    }
}
