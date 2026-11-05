namespace UpsHub.Core.Monitoring;

/// <summary>User-facing connection wording, shared by the dashboard, the servers page and the tray.</summary>
public static class ConnectionText
{
    /// <summary>"Reconnecting (attempt 3, next try in 8s)". Never a terminal failure: UPS Hub retries forever.</summary>
    public static string Describe(ServerStatus? st, DateTimeOffset now) => st?.State switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Reconnecting when st.NextRetry is { } nr =>
            $"Reconnecting (attempt {Math.Max(1, st.Attempt)}, next try in {Math.Max(0, Math.Ceiling((nr - now).TotalSeconds)):0}s)",
        ConnectionState.Reconnecting => st.Attempt > 0 ? $"Reconnecting (attempt {st.Attempt + 1}, trying now)" : "Reconnecting (trying now)",
        _ => "Stopped",
    };
}
