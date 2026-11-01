using System.Text;
using UpsHub.Core.Settings;

namespace UpsHub.Core.Platform;

/// <summary>Non-Windows stand-ins so the app runs (and can be tested) on Linux.</summary>
public sealed class NullStartupManager : IStartupManager
{
    private bool _enabled;
    public bool IsSupported => false;
    public bool IsEnabled() => _enabled;
    public void SetEnabled(bool enabled) => _enabled = enabled;
}

public sealed class DryRunPowerActions : IPowerActions
{
    public List<string> Executed { get; } = new();
    public bool IsSupported => false;
    public Task<string> ExecuteAsync(ShutdownAction action, bool force, CancellationToken ct = default)
    {
        var msg = $"[dry run] would {action.ToString().ToLowerInvariant()} this PC{(force ? " (force)" : "")}";
        Executed.Add(msg);
        return Task.FromResult(msg);
    }
}

public sealed class LogNotifier : INotifier
{
    public List<(string Title, string Message, NotificationKind Kind)> Shown { get; } = new();
    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        lock (Shown) Shown.Add((title, message, kind));
        Console.WriteLine($"[notify:{kind}] {title}: {message}");
    }
}

/// <summary>NOT secure: base64 only. Used where DPAPI doesn't exist (Linux dev/test).</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    private const string Prefix = "plain:";
    public string Protect(string plaintext) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value[Prefix.Length..])); }
        catch (FormatException) { return null; }
    }
}

public sealed class NullPowerEvents : IPowerEvents
{
    public event EventHandler? Resumed;
    public event EventHandler? Suspending;
    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
    public void RaiseSuspending() => Suspending?.Invoke(this, EventArgs.Empty);
}

public sealed class NullLegacyImporter : ILegacySettingsImporter
{
    public string? TryImport(AppSettings target) => null;
}
