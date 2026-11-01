using Avalonia;

namespace UpsHub.App;

public static class Program
{
    public static AppOptions Options { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        Options = AppOptions.Parse(args);
        using var instance = new SingleInstance(Options.Demo || Options.ScreenshotDir is not null ? "demo-" + Environment.ProcessId : "main");
        if (!instance.IsFirst)
        {
            instance.SignalFirst();
            return 0;
        }
        Program.Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    internal static SingleInstance? Instance { get; private set; }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { CompositionMode = new[] { Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface } })
            .LogToTrace();
}

public sealed record AppOptions
{
    public bool Minimized { get; init; }
    public bool Demo { get; init; }
    public string? ScreenshotDir { get; init; }
    /// <summary>With --screenshots: capture the discovery screens instead of the main tour.</summary>
    public bool DiscoveryShots { get; init; }
    /// <summary>With --screenshots: capture the local USB UPS screens.</summary>
    public bool LocalShots { get; init; }
    /// <summary>With --screenshots: capture the 0.4 screens (updates, About, setup wizard, shutdown test).</summary>
    public bool Phase4Shots { get; init; }
    /// <summary>Demo: open the setup wizard (with a stand-in WinNUT import).</summary>
    public bool Wizard { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--minimized": o = o with { Minimized = true }; break;
                case "--demo": o = o with { Demo = true }; break;
                case "--screenshots" when i + 1 < args.Length: o = o with { ScreenshotDir = args[++i], Demo = true }; break;
                case "--screenshots-local" when i + 1 < args.Length: o = o with { ScreenshotDir = args[++i], Demo = true, LocalShots = true }; break;
                case "--wizard": o = o with { Wizard = true, Demo = true }; break;
                case "--screenshots-phase4" when i + 1 < args.Length: o = o with { ScreenshotDir = args[++i], Demo = true, Phase4Shots = true }; break;
                case "--screenshots-discovery" when i + 1 < args.Length: o = o with { ScreenshotDir = args[++i], Demo = true, DiscoveryShots = true }; break;
            }
        }
        return o;
    }
}
