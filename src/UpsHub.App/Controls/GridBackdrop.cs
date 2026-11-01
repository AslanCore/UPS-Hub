using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace UpsHub.App.Controls;

/// <summary>
/// The Cloud ".hex-grid" backdrop: 1px lines every 48px at 4% foreground, fading out radially from the
/// top centre (full 20% -> none at 75%). The fade is baked into per-segment line alpha instead of an
/// opacity mask, so the backdrop is plain static geometry: no offscreen layer each time anything above it
/// redraws.
/// </summary>
public sealed class GridBackdrop : Control
{
    public static readonly StyledProperty<double> CellProperty = AvaloniaProperty.Register<GridBackdrop, double>(nameof(Cell), 48);
    public double Cell { get => GetValue(CellProperty); set => SetValue(CellProperty, value); }

    private const int Levels = 16;
    private static readonly IPen[] Pens = Enumerable.Range(0, Levels + 1)
        .Select(i => (IPen)new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(0x0C * i / (double)Levels), 0xF3, 0xF1, 0xEE)), 1))
        .ToArray();

    static GridBackdrop() => AffectsRender<GridBackdrop>(CellProperty);

    public GridBackdrop() => IsHitTestVisible = false;

    private static double Fade(double x, double y, double w, double h)
    {
        double dx = (x - w / 2) / (0.75 * w), dy = y / (1.1 * h);
        double d = Math.Sqrt(dx * dx + dy * dy);
        return d <= 0.2 ? 1 : d >= 0.75 ? 0 : 1 - (d - 0.2) / 0.55;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height; var cell = Math.Max(8, Cell);
        if (w <= 0 || h <= 0) return;
        void Segment(Point a, Point b)
        {
            int level = (int)Math.Round(Fade((a.X + b.X) / 2, (a.Y + b.Y) / 2, w, h) * Levels);
            if (level > 0) ctx.DrawLine(Pens[level], a, b);
        }
        for (double x = 0.5; x < w; x += cell)
            for (double y = 0; y < h; y += cell) Segment(new Point(x, y), new Point(x, Math.Min(h, y + cell)));
        for (double y = 0.5; y < h; y += cell)
            for (double x = 0; x < w; x += cell) Segment(new Point(x, y), new Point(Math.Min(w, x + cell), y));
    }
}
