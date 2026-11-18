using UpsHub.Core.Model;
using UpsHub.Core.Shutdown;

namespace UpsHub.Core.Update;

/// <summary>How this copy of UPS Hub was put on the PC.</summary>
public enum InstallScope
{
    /// <summary>Installed by UPS Hub Setup for the current Windows account (per-user, no admin needed).</summary>
    PerUser,
    /// <summary>Unzipped portable copy: never replaced automatically.</summary>
    Portable,
    /// <summary>Installed some other way or for another scope (e.g. needs an administrator): not updated automatically.</summary>
    Other,
}

public sealed record InstallConditions(
    bool IsWindows,
    InstallScope Scope,
    bool DirectoryWritable,
    ShutdownPhase ShutdownPhase,
    bool ShutdownActionRunning,
    bool ShutdownTest,
    IReadOnlyCollection<UpsStatus> LastKnownStatuses);

/// <summary>
/// When UPS Hub may close itself to install an update. Monitoring pauses for the seconds the installer
/// needs, so that only happens while every UPS is on mains power and no shutdown is pending.
/// </summary>
public static class AutoInstallPolicy
{
    public const int MaxAutoAttempts = 2;
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    /// <summary>Null when installing now is fine; otherwise why not (shown in the About card).</summary>
    public static string? BlockReason(InstallConditions c)
    {
        if (!c.IsWindows) return "Automatic install is for the Windows build. Download the new version from AslanCore Cloud instead.";
        if (c.Scope == InstallScope.Portable) return "This is a portable copy, so it isn't replaced automatically. Download the new version from AslanCore Cloud.";
        if (c.Scope == InstallScope.Other || !c.DirectoryWritable)
            return "This copy wasn't installed for your Windows account by UPS Hub Setup, so it can't be updated without an administrator. Run the new installer yourself.";
        if (c.ShutdownPhase != ShutdownPhase.Idle || c.ShutdownActionRunning || c.ShutdownTest)
            return "Waiting to install: a shutdown countdown or test is in progress.";
        if (c.LastKnownStatuses.Any(s => s.HasFlag(UpsStatus.OB) || s.HasFlag(UpsStatus.LB) || s.HasFlag(UpsStatus.FSD)))
            return "Waiting to install until the UPS is back on mains power.";
        return null;
    }

    /// <summary>Stops a broken update from being retried in a loop: twice per version, at least 6 hours apart.</summary>
    public static bool MayAutoAttempt(UpdateState s, SemVer version, DateTimeOffset now)
    {
        if (SemVer.TryParse(s.PendingVersion) is not { } p || p != version) return true;
        if (s.PendingAttempts >= MaxAutoAttempts) return false;
        return s.PendingSinceUtc is not { } t || now - t >= RetryAfter;
    }

    public static void RecordAttempt(UpdateState s, SemVer version, DateTimeOffset now)
    {
        if (SemVer.TryParse(s.PendingVersion) is { } p && p == version) s.PendingAttempts++;
        else { s.PendingVersion = version.ToString(); s.PendingAttempts = 1; }
        s.PendingSinceUtc = now;
    }

    /// <summary>The install was handed over but UPS Hub stayed up (e.g. the UPS went on battery): don't count it.</summary>
    public static void UndoAttempt(UpdateState s, SemVer version)
    {
        if (SemVer.TryParse(s.PendingVersion) is not { } p || p != version) return;
        if (--s.PendingAttempts <= 0) { s.PendingVersion = null; s.PendingAttempts = 0; s.PendingSinceUtc = null; }
    }

    /// <summary>On start-up: did the last install land? Clears the record on success.</summary>
    public static string? Reconcile(UpdateState s, SemVer current)
    {
        if (SemVer.TryParse(s.PendingVersion) is not { } p) return null;
        if (current >= p)
        {
            s.PendingVersion = null; s.PendingAttempts = 0; s.PendingSinceUtc = null;
            return $"Updated to {current}.";
        }
        return s.PendingAttempts >= MaxAutoAttempts
            ? $"Installing {p} didn't finish, so UPS Hub stays on {current}. Install it from Settings when convenient."
            : $"Installing {p} didn't finish; UPS Hub is still on {current} and will try again later.";
    }
}
