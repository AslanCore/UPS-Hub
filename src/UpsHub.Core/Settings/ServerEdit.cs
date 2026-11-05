using System.Net;
using UpsHub.Core.Model;
using UpsHub.Core.Nut;

namespace UpsHub.Core.Settings;

/// <summary>One UPS row in the edit dialog.</summary>
public sealed record UpsEdit(string Name, string DisplayName, bool Monitored, bool Notifications, bool Primary);

/// <summary>
/// A pending edit of one source. <see cref="NewPassword"/> null = keep the stored password ("unchanged"
/// placeholder), "" = remove it. Validation, change detection and applying live are kept here so they
/// are testable without the UI.
/// </summary>
public sealed record ServerEdit
{
    public required string Id { get; init; }
    public bool IsLocal { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 3493;
    public string DisplayName { get; init; } = "";
    public string? Username { get; init; }
    public string? NewPassword { get; init; }
    public TlsMode Tls { get; init; }
    public int? PollIntervalSeconds { get; init; }
    public bool LoginAsSecondary { get; init; }
    public IReadOnlyList<UpsEdit> Ups { get; init; } = Array.Empty<UpsEdit>();

    public const string UnchangedPlaceholder = "(unchanged)";

    public static ServerEdit From(ServerSettings s, IEnumerable<string> knownUps, string? primaryUps) => new()
    {
        Id = s.Id, IsLocal = s.IsLocal, Host = s.Host, Port = s.Port, DisplayName = s.DisplayName, Username = s.Username,
        Tls = s.Tls, PollIntervalSeconds = s.PollIntervalSeconds, LoginAsSecondary = s.LoginAsSecondary,
        Ups = knownUps.Concat(s.UpsNames).Concat(s.UpsPrefs.Select(p => p.Name)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new UpsEdit(n, s.PrefsFor(n)?.DisplayName ?? "", s.IsMonitored(n) && (s.UpsNames.Count == 0 || s.UpsNames.Contains(n, StringComparer.OrdinalIgnoreCase)),
                s.PrefsFor(n)?.Notifications ?? true,
                primaryUps is not null && UpsAddressMatches(primaryUps, n, s)))
            .ToList(),
    };

    private static bool UpsAddressMatches(string primary, string ups, ServerSettings s) => IsAt(primary, ups, s.Host, s.Port);

    private static bool IsAt(string? primary, string ups, string host, int port) =>
        UpsAddress.TryParse(primary, out var a) && a.Equals(new UpsAddress(ups, host, port));

    /// <summary>Field errors keyed by field name; empty = valid.</summary>
    public IReadOnlyDictionary<string, string> Validate()
    {
        var e = new Dictionary<string, string>();
        if (!IsLocal)
        {
            var h = Host.Trim();
            if (h.Length == 0) e["Host"] = "Enter a host name or IP address.";
            else if (h.Length > 253 || h.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '?' or '#'))
                e["Host"] = "That doesn't look like a host name or IP address.";
            else if (!IPAddress.TryParse(h.Trim('[', ']'), out _) && Uri.CheckHostName(h) == UriHostNameType.Unknown)
                e["Host"] = "That doesn't look like a host name or IP address.";
            if (Port is < 1 or > 65535) e["Port"] = "Port must be between 1 and 65535.";
            if (Username is { } u && u.Any(char.IsWhiteSpace)) e["Username"] = "NUT user names can't contain spaces.";
            if (NewPassword is { Length: > 0 } p && (p.Contains('\n') || p.Contains('\r'))) e["Password"] = "The password can't contain line breaks.";
            if (NewPassword is { Length: > 0 } && string.IsNullOrWhiteSpace(Username)) e["Username"] = "A password needs a user name (upsd.users).";
        }
        if (PollIntervalSeconds is { } pi && pi is < 1 or > 300) e["PollInterval"] = "Poll every 1 to 300 seconds.";
        if (DisplayName.Length > 64) e["DisplayName"] = "Keep the name under 64 characters.";
        if (Ups.Count(u => u.Primary) > 1) e["Primary"] = "Only one UPS can power this PC.";
        if (Ups.Count > 0 && Ups.All(u => !u.Monitored)) e["Monitored"] = "Keep at least one UPS monitored, or remove the server.";
        return e;
    }

    /// <summary>True when the edit changes how UPS Hub connects (host, port, login, TLS, UPS set), so the monitor must reconnect.</summary>
    public bool NeedsReconnect(ServerSettings old) =>
        !string.Equals(Host.Trim(), old.Host, StringComparison.OrdinalIgnoreCase) || Port != old.Port
        || (string.IsNullOrWhiteSpace(Username) ? null : Username.Trim()) != old.Username
        || NewPassword is not null || Tls != old.Tls || LoginAsSecondary != old.LoginAsSecondary
        || !MonitoredNames(old).SequenceEqual(old.UpsNames, StringComparer.OrdinalIgnoreCase);

    private List<string> MonitoredNames(ServerSettings old)
    {
        if (IsLocal || Ups.Count == 0) return old.UpsNames.ToList();
        var mon = Ups.Where(u => u.Monitored).Select(u => u.Name).ToList();
        return mon.Count == Ups.Count && old.UpsNames.Count == 0 ? new() : mon;   // all ticked = "every UPS" (also new ones)
    }

    /// <summary>Writes the edit into <paramref name="target"/>; <paramref name="protect"/> encrypts a new password (DPAPI).</summary>
    public void ApplyTo(ServerSettings target, AppSettings all, Func<string, string?> protect)
    {
        var oldHost = target.Host; var oldPort = target.Port;
        var names = MonitoredNames(target);
        target.DisplayName = DisplayName.Trim();
        target.PollIntervalSeconds = PollIntervalSeconds;
        if (!IsLocal)
        {
            target.Host = Host.Trim();
            target.Port = Port;
            target.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
            if (NewPassword is not null) target.ProtectedPassword = NewPassword.Length == 0 ? null : protect(NewPassword);
            target.Tls = Tls;
            target.LoginAsSecondary = LoginAsSecondary;
            target.UpsNames = names;
        }
        target.UpsPrefs = Ups.Where(u => u.DisplayName.Length > 0 || !u.Monitored || !u.Notifications)
            .Select(u => new UpsPrefs { Name = u.Name, DisplayName = u.DisplayName.Trim(), Monitored = u.Monitored, Notifications = u.Notifications })
            .ToList();
        // Primary UPS: set when ticked; re-pointed when the address moved; cleared when unticked.
        var primary = Ups.FirstOrDefault(u => u.Primary);
        var wasHere = Ups.Any(u => IsAt(all.Shutdown.PrimaryUps, u.Name, oldHost, oldPort));
        if (primary is not null) all.Shutdown.PrimaryUps = new UpsAddress(primary.Name, target.Host, target.Port).ToString();
        else if (wasHere) all.Shutdown.PrimaryUps = null;
    }
}
