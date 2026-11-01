using UpsHub.Core.Model;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;

namespace UpsHub.Core.Monitoring;

public enum ConnectionState { Stopped, Connecting, Connected, Reconnecting }

public sealed record ServerStatus(
    string ServerId,
    ConnectionState State,
    string? Message = null,
    string? ServerVersion = null,
    bool Tls = false,
    int Attempt = 0,
    DateTimeOffset? NextRetry = null,
    NutError? LastError = null);

/// <summary>
/// Keeps one connection to one upsd and polls every configured UPS on it.
/// Reconnects forever with backoff; publishes stale snapshots when data can't be trusted.
/// </summary>
public sealed class ServerMonitor : IUpsMonitor
{
    private readonly ServerSettings _cfg;
    private readonly string? _password;
    private readonly Func<NutConnectOptions, NutClient> _clientFactory;
    private readonly TimeProvider _time;
    private readonly Backoff _backoff;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Dictionary<string, UpsSnapshot> _last = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _forceReconnect;

    public TimeSpan PollInterval { get; set; }
    public double PowerFactor { get; set; } = 0.6;
    public ServerStatus Status { get; private set; }
    public IReadOnlyList<NutUpsInfo> KnownUps { get; private set; } = Array.Empty<NutUpsInfo>();
    public ServerSettings Config => _cfg;

    public event Action<ServerStatus>? StatusChanged;
    public event Action<UpsAddress, UpsSnapshot>? SnapshotUpdated;
    public event Action<IReadOnlyList<NutUpsInfo>>? UpsListChanged;

    public ServerMonitor(ServerSettings cfg, string? password, TimeSpan pollInterval,
        Func<NutConnectOptions, NutClient>? clientFactory = null, TimeProvider? time = null, Backoff? backoff = null)
    {
        _cfg = cfg;
        _password = password;
        PollInterval = pollInterval;
        _clientFactory = clientFactory ?? (o => new NutClient(o));
        _time = time ?? TimeProvider.System;
        _backoff = backoff ?? new Backoff();
        Status = new ServerStatus(cfg.Id, ConnectionState.Stopped);
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Poll now. With <paramref name="reconnect"/>, drop the socket first (use after resume from sleep).</summary>
    public void Kick(bool reconnect = false)
    {
        if (reconnect) { _forceReconnect = true; _backoff.Reset(); }
        _wake.Release();
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose(); _cts = null; _loop = null;
        SetStatus(Status with { State = ConnectionState.Stopped, Message = null, NextRetry = null });
    }

    private void SetStatus(ServerStatus s) { Status = s; StatusChanged?.Invoke(s); }

    private NutConnectOptions Options() => new()
    {
        Host = _cfg.Host,
        Port = _cfg.Port,
        Tls = _cfg.Tls,
        CommandTimeout = TimeSpan.FromSeconds(Math.Clamp(PollInterval.TotalSeconds * 2, 3, 10)),
    };

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = _clientFactory(Options());
            try
            {
                SetStatus(Status with
                {
                    State = _backoff.Attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting,
                    NextRetry = null,
                });
                await client.ConnectAsync(ct).ConfigureAwait(false);
                string? version = null;
                try { version = await client.GetServerVersionAsync(ct).ConfigureAwait(false); }
                catch (NutException) { /* very old upsd */ }

                await client.AuthenticateAsync(_cfg.Username, _password, ct).ConfigureAwait(false);

                var all = await client.ListUpsAsync(ct).ConfigureAwait(false);
                KnownUps = all;
                UpsListChanged?.Invoke(all);
                var names = _cfg.UpsNames.Count > 0 ? _cfg.UpsNames.ToList() : all.Select(u => u.Name).ToList();
                if (_cfg.LoginAsSecondary)
                    foreach (var n in names) await client.LoginAsync(n, ct).ConfigureAwait(false);

                _backoff.Reset();
                _forceReconnect = false;
                SetStatus(new ServerStatus(_cfg.Id, ConnectionState.Connected,
                    names.Count == 0 ? "Connected, but the server lists no UPS" : null, version, client.IsTls));

                await PollLoopAsync(client, names, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is NutConnectionException or NutException or IOException or System.Security.Authentication.AuthenticationException)
            {
                MarkAllStale();
                var delay = _backoff.Next();
                var nutErr = (ex as NutException)?.Error;
                var msg = ex is NutException ne ? NutErrorParser.Describe(ne.Error) : ex.Message;
                SetStatus(new ServerStatus(_cfg.Id, ConnectionState.Reconnecting, msg, Status.ServerVersion, false,
                    _backoff.Attempt, _time.GetUtcNow() + delay, nutErr));
                await WaitAsync(delay, ct).ConfigureAwait(false);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task PollLoopAsync(NutClient client, List<string> names, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_forceReconnect)
        {
            string? problem = null;
            foreach (var ups in names)
            {
                var addr = new UpsAddress(ups, _cfg.Host, _cfg.Port);
                try
                {
                    var vars = await client.ListVarsAsync(ups, ct).ConfigureAwait(false);
                    var snap = UpsSnapshot.FromVariables(ups, vars, _time.GetUtcNow(), PowerFactor);
                    _last[ups] = snap;
                    SnapshotUpdated?.Invoke(addr, snap);
                }
                catch (NutException ex) when (ex.Error is NutError.DataStale or NutError.DriverNotConnected or NutError.UnknownUps or NutError.AccessDenied)
                {
                    var stale = UpsSnapshot.StaleFor(ups, _time.GetUtcNow(), _last.GetValueOrDefault(ups));
                    _last[ups] = stale;
                    SnapshotUpdated?.Invoke(addr, stale);
                    problem = $"{ups}: {NutErrorParser.Describe(ex.Error)}";
                }
            }
            if (problem != Status.Message)
                SetStatus(Status with { Message = problem });
            await WaitAsync(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await _wake.WaitAsync(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    private void MarkAllStale()
    {
        foreach (var (ups, prev) in _last.ToList())
        {
            var stale = UpsSnapshot.StaleFor(ups, _time.GetUtcNow(), prev);
            _last[ups] = stale;
            SnapshotUpdated?.Invoke(new UpsAddress(ups, _cfg.Host, _cfg.Port), stale);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wake.Dispose();
    }
}
