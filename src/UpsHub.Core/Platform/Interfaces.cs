using UpsHub.Core.Settings;

namespace UpsHub.Core.Platform;

/// <summary>Registers the app to start at logon (HKCU Run key on Windows).</summary>
public interface IStartupManager
{
    bool IsSupported { get; }
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

/// <summary>Shutdown / hibernate / sleep.</summary>
public interface IPowerActions
{
    bool IsSupported { get; }
    /// <summary>Performs the action. Returns a human description of what was (or would have been) done.</summary>
    Task<string> ExecuteAsync(ShutdownAction action, bool force, CancellationToken ct = default);
}

public enum NotificationKind { Info, Warning, Critical, Success }

public interface INotifier
{
    void Show(string title, string message, NotificationKind kind = NotificationKind.Info);
}

/// <summary>Protects secrets at rest (DPAPI CurrentUser on Windows).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    /// <summary>Returns null if the value can't be decrypted (e.g. copied from another PC or user, #224).</summary>
    string? Unprotect(string? protectedValue);
}

/// <summary>Raised when the PC resumes from sleep/hibernate, so connections can be refreshed.</summary>
public interface IPowerEvents
{
    event EventHandler? Resumed;
    event EventHandler? Suspending;
}

/// <summary>One-time import of the old WinNUT registry settings.</summary>
public interface ILegacySettingsImporter
{
    /// <summary>Returns a short summary of what was imported, or null if nothing was found.</summary>
    string? TryImport(AppSettings target);
}
