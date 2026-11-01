using UpsHub.Core.Model;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;

namespace UpsHub.Core.Monitoring;

/// <summary>
/// A source of UPS snapshots: a NUT server (<see cref="ServerMonitor"/>) or a UPS plugged into this PC
/// (<see cref="Local.LocalUpsMonitor"/>). Both publish the same snapshots, so the dashboard, tray and
/// shutdown rules don't care where the data came from.
/// </summary>
public interface IUpsMonitor : IAsyncDisposable
{
    ServerSettings Config { get; }
    ServerStatus Status { get; }
    IReadOnlyList<NutUpsInfo> KnownUps { get; }
    TimeSpan PollInterval { get; set; }
    double PowerFactor { get; set; }
    event Action<ServerStatus>? StatusChanged;
    event Action<UpsAddress, UpsSnapshot>? SnapshotUpdated;
    void Start();
    void Kick(bool reconnect = false);
    Task StopAsync();
}
