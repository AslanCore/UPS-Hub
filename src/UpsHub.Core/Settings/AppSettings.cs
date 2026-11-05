using System.Text.Json.Serialization;
using UpsHub.Core.Nut;

namespace UpsHub.Core.Settings;

public enum ShutdownAction { Shutdown, Hibernate, Sleep }

/// <summary>Where a UPS's readings come from.</summary>
public enum UpsSourceKind { Nut, Local }

/// <summary>How a local (USB / serial) UPS is read. Lower tiers need less, higher tiers report more.</summary>
public enum LocalTier
{
    /// <summary>Windows battery API (any HID UPS Windows shows as a battery): charge, AC/battery, runtime, critical.</summary>
    Battery,
    /// <summary>USB HID Power Device read directly (NUT usbhid-ups mappings): everything the UPS reports.</summary>
    Hid,
    /// <summary>Megatec / Q1 protocol on a serial or USB-serial port.</summary>
    Megatec,
    /// <summary>Built-in simulated APC Back-UPS (demo, screenshots, Linux preview).</summary>
    Replay,
}

public sealed class LocalDeviceSettings
{
    public LocalTier Tier { get; set; } = LocalTier.Hid;
    /// <summary>"vid:pid:serial" for HID, the device name for Battery, the port (COM3) for Megatec.</summary>
    public string DeviceKey { get; set; } = "";
    /// <summary>Last OS path seen; refreshed from <see cref="DeviceKey"/> on every start.</summary>
    public string? DevicePath { get; set; }
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public int BaudRate { get; set; } = 2400;
}

public sealed class ServerSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3493;
    public string? Username { get; set; }
    /// <summary>Password protected with DPAPI on Windows (see <see cref="Platform.ISecretProtector"/>).</summary>
    public string? ProtectedPassword { get; set; }
    public TlsMode Tls { get; set; } = TlsMode.Off;
    public bool Enabled { get; set; } = true;
    /// <summary>UPS names on this server to monitor. Empty = all UPSes from LIST UPS.</summary>
    public List<string> UpsNames { get; set; } = new();
    /// <summary>Send LOGIN (register as upsmon secondary). Off by default, see nutdotnet #106.</summary>
    public bool LoginAsSecondary { get; set; }
    /// <summary>Nut = a upsd server (Host/Port). Local = a UPS plugged into this PC (see <see cref="Local"/>).</summary>
    public UpsSourceKind Kind { get; set; } = UpsSourceKind.Nut;
    public LocalDeviceSettings? Local { get; set; }
    /// <summary>Poll interval for this source in seconds; null = the global setting.</summary>
    public int? PollIntervalSeconds { get; set; }
    /// <summary>Per-UPS preferences (display name, monitored, notifications). Missing entry = defaults.</summary>
    public List<UpsPrefs> UpsPrefs { get; set; } = new();
    [JsonIgnore] public bool IsLocal => Kind == UpsSourceKind.Local;

    public UpsPrefs? PrefsFor(string ups) => UpsPrefs.FirstOrDefault(p => string.Equals(p.Name, ups, StringComparison.OrdinalIgnoreCase));
    public bool IsMonitored(string ups) => PrefsFor(ups)?.Monitored ?? true;
}

public sealed class UpsPrefs
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Monitored { get; set; } = true;
    public bool Notifications { get; set; } = true;
}

public sealed class ShutdownRules
{
    public bool Enabled { get; set; } = true;
    /// <summary><c>ups@host:port</c> of the UPS that powers this PC. Null = first monitored UPS.</summary>
    public string? PrimaryUps { get; set; }
    public bool OnLowBatteryFlag { get; set; } = true;
    /// <summary>Shut down when on battery and charge falls below this %. 0 disables.</summary>
    public int BatteryPercentBelow { get; set; } = 30;
    /// <summary>Shut down when on battery and runtime falls below this many seconds. 0 disables.</summary>
    public int RuntimeSecondsBelow { get; set; } = 120;
    /// <summary>Shut down after being on battery this long (seconds). 0 disables.</summary>
    public int OnBatteryForSeconds { get; set; } = 0;
    public bool FollowFsd { get; set; } = true;
    /// <summary>Countdown shown before acting. 0 = act immediately.</summary>
    public int CountdownSeconds { get; set; } = 30;
    public bool AllowExtension { get; set; } = true;
    public int ExtensionSeconds { get; set; } = 120;
    public ShutdownAction Action { get; set; } = ShutdownAction.Shutdown;
    /// <summary>shutdown.exe /f: close apps without waiting.</summary>
    public bool ForceCloseApps { get; set; } = true;
    /// <summary>If the connection drops while the UPS was on battery, treat it as critical.</summary>
    public bool ActIfLostWhileOnBattery { get; set; } = false;
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool NotifyOnPowerEvents { get; set; } = true;
    public bool NotifyOnConnectionEvents { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 2;
    public double PowerFactor { get; set; } = 0.6;
    /// <summary>Look for a new UPS Hub release on GitHub once a day.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
}

public sealed class DiscoverySettings
{
    public bool UseMdns { get; set; } = true;
    public bool IncludeVirtualAdapters { get; set; }
    /// <summary>Extra ports besides 3493, e.g. "3494, 13493".</summary>
    public string ExtraPorts { get; set; } = "";
    /// <summary>Extra hosts / ranges / subnets to probe (see TargetSpec).</summary>
    public string CustomTargets { get; set; } = "";
    /// <summary>NUT servers seen by earlier scans ("host:port"), probed first next time.</summary>
    public List<string> KnownHosts { get; set; } = new();
    /// <summary>The first-run "look for servers?" card was answered (scan or not now).</summary>
    public bool FirstRunPromptDone { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<ServerSettings> Servers { get; set; } = new();
    public ShutdownRules Shutdown { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public DiscoverySettings Discovery { get; set; } = new();
    /// <summary>Set once the WinNUT registry import has run (successful or not), so it never runs again.</summary>
    public bool LegacyImportDone { get; set; }
    /// <summary>The first-time setup wizard was finished or skipped.</summary>
    public bool SetupCompleted { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
public partial class SettingsJsonContext : JsonSerializerContext { }
