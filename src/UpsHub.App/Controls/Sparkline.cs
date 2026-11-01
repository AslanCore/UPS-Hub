using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UpsHub.App.Controls;

/// <summary>Minimal line chart with a soft area fill. Points are 0..Maximum.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> PointsProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Points));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<Sparkline, Color>(nameof(LineColor), Color.Parse("#FF5C1A"));

    static Sparkline() => AffectsRender<Sparkline>(PointsProperty, MaximumProperty, LineColorProperty);

    public IReadOnlyList<double>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Color LineColor { get => GetValue(LineColorProperty); set => SetValue(LineColorProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        var grid = new Pen(new SolidColorBrush(Color.Parse("#0FF3F1EE")), 1);
        for (int i = 1; i < 4; i++) ctx.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
        var pts = Points;
        if (pts is null || pts.Count < 2 || Maximum <= 0) return;
        var step = w / (pts.Count - 1);
        Point P(int i) => new(i * step, h - 2 - Math.Clamp(pts[i] / Maximum, 0, 1) * (h - 4));
        var line = new StreamGeometry();
        using (var g = line.Open()) { g.BeginFigure(P(0), false); for (int i = 1; i < pts.Count; i++) g.LineTo(P(i)); g.EndFigure(false); }
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(0, h), true); for (int i = 0; i < pts.Count; i++) g.LineTo(P(i));
            g.LineTo(new Point(w, h)); g.EndFigure(true);
        }
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x40, LineColor.R, LineColor.G, LineColor.B), 0), new GradientStop(Color.FromArgb(0, LineColor.R, LineColor.G, LineColor.B), 1) },
        };
        ctx.DrawGeometry(fill, null, area);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(LineColor), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }
}
