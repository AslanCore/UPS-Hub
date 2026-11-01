using System.Text.Json;

namespace UpsHub.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON (atomic write, keeps a .bak of the last good file).</summary>
public sealed class SettingsStore
{
    /// <summary>Host part of the address of a local (USB) UPS, e.g. <c>backups@usb</c>.</summary>
    public const string LocalHost = "usb";
    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "settings.json");

    public SettingsStore(string directory) => Directory = directory;

    /// <summary>%APPDATA%\UPS Hub on Windows, ~/.config/UPS Hub elsewhere.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create), AppInfo.SettingsFolderName);

    public bool Exists => File.Exists(FilePath);

    public AppSettings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings);
                if (s is not null) return Normalize(s);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // fall through to backup / defaults
            }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(Normalize(settings), SettingsJsonContext.Default.AppSettings);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public string Export(AppSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

    private static AppSettings Normalize(AppSettings s)
    {
        s.Servers ??= new();
        s.Shutdown ??= new();
        s.General ??= new();
        s.Discovery ??= new();
        s.Discovery.KnownHosts ??= new();
        s.Discovery.ExtraPorts ??= "";
        s.Discovery.CustomTargets ??= "";
        if (s.Discovery.KnownHosts.Count > 64) s.Discovery.KnownHosts = s.Discovery.KnownHosts.TakeLast(64).ToList();
        s.General.PollIntervalSeconds = Math.Clamp(s.General.PollIntervalSeconds, 1, 300);
        s.General.PowerFactor = s.General.PowerFactor is > 0 and <= 1 ? s.General.PowerFactor : 0.6;
        var r = s.Shutdown;
        r.BatteryPercentBelow = Math.Clamp(r.BatteryPercentBelow, 0, 100);
        r.RuntimeSecondsBelow = Math.Max(0, r.RuntimeSecondsBelow);
        r.OnBatteryForSeconds = Math.Max(0, r.OnBatteryForSeconds);
        r.CountdownSeconds = Math.Clamp(r.CountdownSeconds, 0, 3600);
        r.ExtensionSeconds = Math.Clamp(r.ExtensionSeconds, 0, 3600);
        foreach (var srv in s.Servers)
        {
            srv.UpsNames ??= new();
            if (srv.Kind == UpsSourceKind.Local) { srv.Local ??= new(); srv.Local.DeviceKey ??= ""; if (string.IsNullOrWhiteSpace(srv.Host)) srv.Host = LocalHost; }
            if (srv.Port is <= 0 or > 65535) srv.Port = 3493;
            if (string.IsNullOrWhiteSpace(srv.Id)) srv.Id = Guid.NewGuid().ToString("N");
        }
        return s;
    }
}
