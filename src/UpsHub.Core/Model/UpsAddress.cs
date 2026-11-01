namespace UpsHub.Core.Model;

/// <summary>A UPS on a NUT server, in the standard <c>ups@host:port</c> form.</summary>
public readonly record struct UpsAddress(string Ups, string Host, int Port = 3493)
{
    public override string ToString() => Port == 3493 ? $"{Ups}@{Host}" : $"{Ups}@{Host}:{Port}";

    public static bool TryParse(string? text, out UpsAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var at = text.IndexOf('@');
        if (at <= 0 || at == text.Length - 1) return false;
        var ups = text[..at].Trim();
        var hostPort = text[(at + 1)..].Trim();
        int port = 3493;
        string host = hostPort;
        if (hostPort.StartsWith('['))                    // [ipv6]:port
        {
            var end = hostPort.IndexOf(']');
            if (end < 0) return false;
            host = hostPort[1..end];
            if (end + 1 < hostPort.Length && hostPort[end + 1] == ':' && !int.TryParse(hostPort[(end + 2)..], out port)) return false;
        }
        else if (hostPort.Count(c => c == ':') == 1)
        {
            var parts = hostPort.Split(':');
            host = parts[0];
            if (!int.TryParse(parts[1], out port)) return false;
        }
        if (port is <= 0 or > 65535 || host.Length == 0) return false;
        address = new UpsAddress(ups, host, port);
        return true;
    }
}
