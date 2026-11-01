using UpsHub.Core.Platform;
using UpsHub.Core.Settings;

namespace UpsHub.App.Platform;

/// <summary>Demo (--wizard): pretends old WinNUT 2.x settings exist so the import step can be tried.</summary>
public sealed class DemoWinNutImporter : ILegacySettingsImporter
{
    public string? TryImport(AppSettings target)
    {
        if (!target.Servers.Any(s => s.Host == "127.0.0.1" && s.Port == 3493))
            target.Servers.Add(new ServerSettings { DisplayName = "Imported from WinNUT", Host = "127.0.0.1", Port = 3493, UpsNames = { "backups" } });
        var r = target.Shutdown;
        r.BatteryPercentBelow = 35; r.RuntimeSecondsBelow = 300; r.CountdownSeconds = 15; r.Action = ShutdownAction.Hibernate; r.FollowFsd = true;
        target.General.StartMinimized = true; target.General.CloseToTray = true; target.General.PollIntervalSeconds = 2;
        return "Imported from WinNUT: server 127.0.0.1, shutdown rules, window options. Re-enter the NUT password if your server needs one.";
    }
}
