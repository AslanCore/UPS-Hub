using UpsHub.Core.Model;

namespace UpsHub.Core.Monitoring;

public enum PowerEvent { None, OnBattery, LowBattery, ForcedShutdown, PowerRestored }

/// <summary>
/// Decides which power notification a new reading deserves. Remembers the last <em>known</em> health per UPS:
/// stale readings (connection lost, DATA-STALE, reconnect) no longer reset it to Unknown, which used to
/// swallow the next "On battery" / "Power restored" until the state changed again (0.4 "late notifications").
/// The first reading after start notifies when the UPS is already on battery.
/// </summary>
public sealed class PowerEventDetector
{
    private readonly Dictionary<UpsAddress, UpsHealth> _known = new();

    public void Clear() => _known.Clear();

    public PowerEvent Observe(UpsAddress a, UpsSnapshot snap)
    {
        var health = snap.Health;
        if (health == UpsHealth.Unknown) return PowerEvent.None;          // stale / unreadable: keep the last known state
        var had = _known.TryGetValue(a, out var prev);
        _known[a] = health;
        if (had && prev == health) return PowerEvent.None;
        return health switch
        {
            UpsHealth.OnBattery when !had || prev is not (UpsHealth.LowBattery or UpsHealth.ForcedShutdown) => PowerEvent.OnBattery,
            UpsHealth.LowBattery => PowerEvent.LowBattery,
            UpsHealth.ForcedShutdown => PowerEvent.ForcedShutdown,
            UpsHealth.Online when had && prev is UpsHealth.OnBattery or UpsHealth.LowBattery or UpsHealth.ForcedShutdown => PowerEvent.PowerRestored,
            _ => PowerEvent.None,
        };
    }
}
