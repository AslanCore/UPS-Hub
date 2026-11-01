using Avalonia.Data.Converters;
using Avalonia.Media;

namespace UpsHub.App.ViewModels;

/// <summary>Small value converters for the setup wizard.</summary>
public static class WizardText
{
    public static readonly IValueConverter WinNutHint =
        new FuncValueConverter<bool, string>(has => has ? "Use your old settings" : "No WinNUT settings found");
    public static readonly IValueConverter OkBrush =
        new FuncValueConverter<bool, IBrush>(ok => Palette.Brush(ok ? Palette.Up : Palette.Degraded));
}
