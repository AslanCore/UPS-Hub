using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

namespace UpsHub.App.Controls;

/// <summary>Thin rounded meter (like the Studio level meters). Value is placed between Minimum and Maximum.</summary>
public sealed class MeterBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<MeterBar, double>(nameof(Value));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<MeterBar, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<MeterBar, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(BarBrush), new SolidColorBrush(Color.Parse("#FF5C1A")));
    public static readonly StyledProperty<bool> HasValueProperty = AvaloniaProperty.Register<MeterBar, bool>(nameof(HasValue), true);

    static MeterBar() => AffectsRender<MeterBar>(ValueProperty, MinimumProperty, MaximumProperty, BarBrushProperty, HasValueProperty);

    public MeterBar()
    {
        Height = 6;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = ValueProperty, Duration = TimeSpan.FromMilliseconds(400), Easing = new Avalonia.Animation.Easings.SplineEasing(0.23, 1, 0.32, 1) },
        };
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public bool HasValue { get => GetValue(HasValueProperty); set => SetValue(HasValueProperty, value); }

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#14F3F1EE"));

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        var r = b.Height / 2;
        ctx.DrawRectangle(Track, null, b, r, r);
        if (!HasValue || Maximum <= Minimum) return;
        var frac = Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1);
        if (frac <= 0) return;
        ctx.DrawRectangle(BarBrush, null, new Rect(0, 0, Math.Max(b.Height, b.Width * frac), b.Height), r, r);
    }
}
