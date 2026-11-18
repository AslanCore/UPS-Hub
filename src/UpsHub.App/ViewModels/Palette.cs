using Avalonia.Media.Immutable;
using Avalonia.Media;
using UpsHub.Core.Model;

namespace UpsHub.App.ViewModels;

/// <summary>Token colours for code-behind use (same values as Styles/Tokens.axaml).</summary>
public static class Palette
{
    public static readonly Color Primary = Color.Parse("#FF5C1A");
    public static readonly Color Up = Color.Parse("#6CCB5F");
    public static readonly Color Degraded = Color.Parse("#F5A524");
    public static readonly Color Down = Color.Parse("#FF6B6B");
    public static readonly Color Subtle = Color.Parse("#9D9D9D");
    public static readonly Color Muted = Color.Parse("#C5C5C5");
    public static readonly Color Meter = Color.Parse("#FF5C1A");
    public static readonly Color MeterWarn = Color.Parse("#F5A524");

    // Brushes are cached and immutable: the same colour always gives the same instance, so setting a
    // bound brush property to an unchanged colour raises no PropertyChanged and causes no redraw.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, IBrush> Cache = new();
    public static IBrush Brush(Color c) => Cache.GetOrAdd(c.ToUInt32(), static k => new ImmutableSolidColorBrush(Color.FromUInt32(k)));
    public static IBrush Soft(Color c, byte alpha = 0x26) => Brush(Color.FromArgb(alpha, c.R, c.G, c.B));

    public static Color For(UpsHealth h) => h switch
    {
        UpsHealth.Online => Up,
        UpsHealth.OnBattery => Degraded,
        UpsHealth.LowBattery or UpsHealth.ForcedShutdown => Down,
        UpsHealth.Alarm => Degraded,
        _ => Subtle,
    };

    public static Color ForCharge(double c) => c >= 50 ? Meter : c >= 25 ? MeterWarn : Down;
    public static Color ForLoad(double l) => l < 70 ? Primary : l < 90 ? MeterWarn : Down;
}
