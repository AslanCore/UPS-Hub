using UpsHub.Core.Platform;

namespace UpsHub.App.Platform;

/// <summary>Bundle of OS integrations. Windows gets the real ones; elsewhere safe stand-ins.</summary>
public sealed class PlatformServices
{
    public required IStartupManager Startup { get; init; }
    public required IPowerActions Power { get; init; }
    public required ISecretProtector Secrets { get; init; }
    public required IPowerEvents PowerEvents { get; init; }
    public required ILegacySettingsImporter LegacyImporter { get; init; }
    /// <summary>OS-level notifier (Windows toasts). Null where unavailable; the app then uses in-app toasts only.</summary>
    public INotifier? SystemNotifier { get; init; }
    public bool IsWindows { get; init; }
    /// <summary>UPSes plugged into this PC (USB HID, Windows battery, Megatec serial).</summary>
    public required UpsHub.Core.Local.ILocalUpsPlatform LocalUps { get; set; }

    /// <summary>
    /// Demo and screenshot runs pass dryRun = true: they then get <see cref="DryRunPowerActions"/>, which
    /// never calls a power API, so a demo can't shut the PC down whatever is clicked.
    /// </summary>
    public static IPowerActions PowerFor(bool dryRun, Func<IPowerActions> real) => dryRun ? new DryRunPowerActions() : real();

    public static PlatformServices Create(bool dryRunPower, string? iconPath)
    {
#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            var toast = new Windows.WindowsToastNotifier(iconPath);
            return new PlatformServices
            {
                Startup = new Windows.WindowsStartupManager(),
                Power = PowerFor(dryRunPower, () => new Windows.WindowsPowerActions()),
                Secrets = new Windows.DpapiSecretProtector(),
                PowerEvents = new Windows.WindowsPowerEvents(),
                LegacyImporter = new Windows.WinNutRegistryImporter(),
                SystemNotifier = toast.IsAvailable ? toast : null,
                IsWindows = true,
                LocalUps = new Windows.WindowsLocalUps(),
            };
        }
#endif
        return new PlatformServices
        {
            Startup = new NullStartupManager(),
            Power = new DryRunPowerActions(),
            Secrets = new PlainSecretProtector(),
            PowerEvents = new NullPowerEvents(),
            LegacyImporter = new NullLegacyImporter(),
            SystemNotifier = null,
            IsWindows = false,
            LocalUps = OperatingSystem.IsLinux() ? new Linux.HidrawLocalUps()
                : new NullLocalUps("Reading a USB UPS directly is supported on Windows (and on Linux through hidraw)."),
        };
    }
}
