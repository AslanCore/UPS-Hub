using UpsHub.Core.Local.Hid;
using UpsHub.Core.Model;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;

namespace UpsHub.Core.Local;

/// <summary>A UPS found on this PC, before it is added.</summary>
public sealed record LocalDeviceInfo
{
    public required LocalTier Tier { get; init; }
    /// <summary>Stable identity used in settings (see <see cref="LocalDeviceSettings.DeviceKey"/>).</summary>
    public required string Key { get; init; }
    public string? Path { get; init; }
    public string Vendor { get; init; } = "";
    public string Model { get; init; } = "";
    public string? Serial { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    /// <summary>One line on what was seen, e.g. "HID usage page 0x84, 71 fields".</summary>
    public string Detail { get; init; } = "";
    /// <summary>Another tier reads the same physical UPS (the HID device and its Windows battery).</summary>
    public string? SameAs { get; init; }

    public string Title => string.Join(" ", new[] { Vendor, Model }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } t ? t : "UPS";
    public string UsbId => VendorId == 0 && ProductId == 0 ? "" : $"{VendorId:x4}:{ProductId:x4}";

    public LocalDeviceSettings ToSettings() => new()
    {
        Tier = Tier, DeviceKey = Key, DevicePath = Path, Vendor = Vendor, Model = Model,
    };
}

/// <summary>Reads one local UPS. <see cref="Read"/> returns NUT-style variables (battery.charge, ups.status...).</summary>
public interface ILocalUpsSource : IDisposable
{
    /// <summary>Short source label, e.g. "USB HID", "Windows battery", "Serial COM3".</summary>
    string Kind { get; }
    void Open();
    IReadOnlyDictionary<string, string> Read();
}

/// <summary>What this OS can do with local UPSes.</summary>
public interface ILocalUpsPlatform
{
    /// <summary>Lists UPSes on this PC. Serial ports are probed only when asked (sending Q1 to unknown devices is not polite).</summary>
    IReadOnlyList<LocalDeviceInfo> Enumerate(bool probeSerial = false);
    /// <summary>A reader for a saved device, or null if this tier isn't available here.</summary>
    ILocalUpsSource? Create(LocalDeviceSettings device);
    /// <summary>Null when local UPSes work on this OS, else why not.</summary>
    string? Limitation { get; }
}

public static class LocalNames
{
    public static string TierLabel(LocalTier t) => t switch
    {
        LocalTier.Battery => "Windows battery",
        LocalTier.Hid => "USB HID",
        LocalTier.Megatec => "Megatec Q1",
        LocalTier.Replay => "USB HID (simulated)",
        _ => t.ToString(),
    };

    /// <summary>"USB · APC Back-UPS BK650M2-CH", "Serial · COM3 · Megatec UPS".</summary>
    public static string SourceLabel(LocalDeviceSettings d, UpsSnapshot? snap = null)
    {
        var vendor = Friendly(snap?.Manufacturer ?? d.Vendor);
        var model = FriendlyModel(vendor, snap?.Model ?? d.Model);
        var name = string.Join(" ", new[] { vendor, model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(name)) name = "UPS";
        return d.Tier switch
        {
            LocalTier.Megatec => $"Serial · {d.DeviceKey} · {name}",
            LocalTier.Battery => $"USB · {name} · battery API",
            _ => $"USB · {name}",
        };
    }

    /// <summary>"APC Back-UPS BK650M2-CH".</summary>
    public static string DisplayName(string? vendor, string? model)
    {
        var v = Friendly(vendor);
        var n = string.Join(" ", new[] { v, FriendlyModel(v, model) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return n.Length > 0 ? n : "UPS";
    }

    public static string Friendly(string? mfr) => mfr?.Trim() switch
    {
        null or "" => "",
        "American Power Conversion" or "APC" => "APC",
        "CPS" or "CyberPower Systems" => "CyberPower",
        "EATON" or "MGE UPS SYSTEMS" or "MGE OPS SYSTEMS" => "Eaton",
        var s => s,
    };

    /// <summary>APC Back-UPS units often report just the part number (BK650M2-CH); show the family too.</summary>
    public static string FriendlyModel(string vendor, string? model)
    {
        model = model?.Trim() ?? "";
        if (vendor == "APC" && model.Length > 2 && !model.Contains("UPS", StringComparison.OrdinalIgnoreCase)
            && model[0] == 'B' && char.IsLetter(model[1]))
            return "Back-UPS " + model;
        return model;
    }

    /// <summary>NUT-style UPS name from a model: "Back-UPS BK650M2-CH" -> "backups".</summary>
    public static string UpsName(string? vendor, string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("back-ups") || m.StartsWith("b") && Friendly(vendor) == "APC") return "backups";
        if (m.Contains("smart-ups")) return "smartups";
        var v = Friendly(vendor).ToLowerInvariant();
        var s = new string(v.Where(char.IsLetterOrDigit).ToArray());
        return s.Length > 0 ? s : "ups";
    }
}

/// <summary>
/// Polls a local UPS and publishes the same events as <see cref="ServerMonitor"/>. If the device goes
/// away (unplugged, driver reset) it marks data stale and keeps reopening it with backoff.
/// </summary>
public sealed class LocalUpsMonitor : IUpsMonitor
{
    private readonly Func<ILocalUpsSource?> _factory;
    private readonly TimeProvider _time;
    private readonly Backoff _backoff;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private UpsSnapshot? _last;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _reopen;

    public LocalUpsMonitor(ServerSettings cfg, Func<ILocalUpsSource?> factory, TimeSpan pollInterval,
        TimeProvider? time = null, Backoff? backoff = null)
    {
        Config = cfg;
        _factory = factory;
        PollInterval = pollInterval;
        _time = time ?? TimeProvider.System;
        _backoff = backoff ?? new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        Status = new ServerStatus(cfg.Id, ConnectionState.Stopped);
        var name = cfg.UpsNames.FirstOrDefault() ?? LocalNames.UpsName(cfg.Local?.Vendor, cfg.Local?.Model);
        Address = new UpsAddress(name, SettingsStore.LocalHost, cfg.Port);
        KnownUps = new[] { new NutUpsInfo(name, LocalNames.SourceLabel(cfg.Local ?? new())) };
    }

    public ServerSettings Config { get; }
    public UpsAddress Address { get; }
    public ServerStatus Status { get; private set; }
    public IReadOnlyList<NutUpsInfo> KnownUps { get; }
    public TimeSpan PollInterval { get; set; }
    public double PowerFactor { get; set; } = 0.6;
    public string? SourceKind { get; private set; }

    public event Action<ServerStatus>? StatusChanged;
    public event Action<UpsAddress, UpsSnapshot>? SnapshotUpdated;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Kick(bool reconnect = false)
    {
        if (reconnect) { _reopen = true; _backoff.Reset(); }
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

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ILocalUpsSource? src = null;
            try
            {
                SetStatus(Status with
                {
                    State = _backoff.Attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting,
                    NextRetry = null,
                });
                src = _factory() ?? throw new IOException("This kind of local UPS isn't supported on this system.");
                src.Open();
                SourceKind = src.Kind;
                _reopen = false;
                bool first = true;
                while (!ct.IsCancellationRequested && !_reopen)
                {
                    var vars = src.Read();
                    var snap = UpsSnapshot.FromVariables(Address.Ups, vars, _time.GetUtcNow(), PowerFactor);
                    _last = snap;
                    if (first)
                    {
                        first = false;
                        _backoff.Reset();
                        SetStatus(new ServerStatus(Config.Id, ConnectionState.Connected, null, src.Kind));
                    }
                    SnapshotUpdated?.Invoke(Address, snap);
                    await WaitAsync(PollInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delay = _backoff.Next();
                SetStatus(new ServerStatus(Config.Id, ConnectionState.Reconnecting, Describe(ex), Status.ServerVersion, false,
                    _backoff.Attempt, _time.GetUtcNow() + delay));
                if (_last is not null)
                {
                    _last = UpsSnapshot.StaleFor(Address.Ups, _time.GetUtcNow(), _last);
                    SnapshotUpdated?.Invoke(Address, _last);
                }
                await WaitAsync(delay, ct).ConfigureAwait(false);
            }
            finally
            {
                try { src?.Dispose(); } catch { }
            }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access to the UPS was denied. Another program may have it open exclusively.",
        FileNotFoundException or DirectoryNotFoundException => "The UPS is not connected (unplugged, or it went to sleep).",
        _ => ex.Message,
    };

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await _wake.WaitAsync(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wake.Dispose();
    }
}

/// <summary>Reads a HID UPS through the NUT-style mappings.</summary>
public sealed class HidUpsSource : ILocalUpsSource
{
    private readonly Func<IHidUpsDevice> _open;
    private IHidUpsDevice? _dev;
    private HidUpsDriver? _driver;

    public HidUpsSource(Func<IHidUpsDevice> open, string kind = "USB HID") { _open = open; Kind = kind; }
    public string Kind { get; }
    public HidUpsDriver? Driver => _driver;

    public void Open()
    {
        _dev = _open();
        _driver = new HidUpsDriver(_dev);
        if (_driver.MappedFields == 0)
            throw new IOException("This HID device doesn't expose any UPS readings UPS Hub understands.");
    }

    public IReadOnlyDictionary<string, string> Read() =>
        (_driver ?? throw new InvalidOperationException("Not open")).Poll();

    public void Dispose() { _dev?.Dispose(); _dev = null; _driver = null; }
}
