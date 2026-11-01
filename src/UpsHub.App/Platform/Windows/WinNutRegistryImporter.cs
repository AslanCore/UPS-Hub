using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UpsHub.Core.Platform;
using UpsHub.Core.Settings;

namespace UpsHub.App.Platform.Windows;

/// <summary>
/// One-time import of WinNUT 2.x settings from HKCU\SOFTWARE\WinNUT\{Connexion,Power,Appareance}.
/// The WinNUT login/password are TripleDES-encrypted with a machine key, so they are not imported;
/// the user re-enters them once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinNutRegistryImporter : ILegacySettingsImporter
{
    private const string Root = @"SOFTWARE\WinNUT";

    public string? TryImport(AppSettings target)
    {
        using var root = Registry.CurrentUser.OpenSubKey(Root);
        if (root is null) return null;
        using var conn = root.OpenSubKey("Connexion");
        using var power = root.OpenSubKey("Power");
        using var look = root.OpenSubKey("Appareance");
        var notes = new List<string>();

        var host = Str(conn, "ServerAddress");
        if (!string.IsNullOrWhiteSpace(host) && host != "nutserver host" &&
            !target.Servers.Any(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase)))
        {
            var ups = Str(conn, "UPSName");
            var srv = new ServerSettings
            {
                DisplayName = "Imported from WinNUT",
                Host = host!,
                Port = Int(conn, "Port") is int p and > 0 and < 65536 ? p : 3493,
            };
            if (!string.IsNullOrWhiteSpace(ups) && ups != "UPSName") srv.UpsNames.Add(ups!);
            target.Servers.Add(srv);
            notes.Add($"server {host}");
            if (Int(conn, "Delay") is int d and > 0) target.General.PollIntervalSeconds = Math.Clamp(d, 1, 300);
        }

        if (power is not null)
        {
            var r = target.Shutdown;
            if (Int(power, "ShutdownLimitBatteryCharge") is int b) r.BatteryPercentBelow = Math.Clamp(b, 0, 100);
            if (Int(power, "ShutdownLimitUPSRemainTime") is int t) r.RuntimeSecondsBelow = Math.Max(0, t);
            if (Bool(power, "Follow_FSD") is bool f) r.FollowFsd = f;
            if (Int(power, "DelayToShutdown") is int c) r.CountdownSeconds = Math.Clamp(c, 0, 3600);
            if (Bool(power, "AllowExtendedShutdownDelay") is bool ae) r.AllowExtension = ae;
            if (Int(power, "ExtendedShutdownDelay") is int ed) r.ExtensionSeconds = Math.Clamp(ed, 0, 3600);
            if (Bool(power, "ImmediateStopAction") == true) r.CountdownSeconds = 0;
            // WinNUT: 0 = shutdown, 1 = SetSystemPowerState(suspend), 2 = hibernate
            r.Action = Int(power, "TypeOfStop") switch { 1 => ShutdownAction.Sleep, 2 => ShutdownAction.Hibernate, _ => ShutdownAction.Shutdown };
            notes.Add("shutdown rules");
        }
        if (look is not null)
        {
            if (Bool(look, "CloseToTray") is bool ct) target.General.CloseToTray = ct;
            if (Bool(look, "MinimizeOnStart") is bool ms) target.General.StartMinimized = ms;
            if (Bool(look, "StartWithWindows") is bool sw) target.General.StartWithWindows = sw;
            notes.Add("window options");
        }
        return notes.Count == 0 ? null : "Imported from WinNUT: " + string.Join(", ", notes) + ". Re-enter the NUT password if your server needs one.";
    }

    private static string? Str(RegistryKey? k, string name) => k?.GetValue(name)?.ToString();

    private static int? Int(RegistryKey? k, string name)
    {
        var v = k?.GetValue(name);
        return v switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => null,
        };
    }

    private static bool? Bool(RegistryKey? k, string name)
    {
        var v = k?.GetValue(name);
        return v switch
        {
            int i => i != 0,
            string s when bool.TryParse(s, out var b) => b,
            string s when int.TryParse(s, out var i) => i != 0,
            _ => null,
        };
    }
}
