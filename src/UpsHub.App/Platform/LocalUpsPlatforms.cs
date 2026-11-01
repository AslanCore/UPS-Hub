using System.IO.Ports;
using UpsHub.Core.Local;
using UpsHub.Core.Local.Hid;
using UpsHub.Core.Settings;

namespace UpsHub.App.Platform;

/// <summary>Megatec line over System.IO.Ports (2400 8N1, CR-terminated).</summary>
public sealed class SerialPortLine : ISerialLine
{
    private readonly SerialPort _port;
    public SerialPortLine(string name, int baud)
    {
        _port = new SerialPort(name, baud, Parity.None, 8, StopBits.One) { NewLine = "\r", ReadTimeout = 1500, WriteTimeout = 1500 };
        _port.Open();
        _port.DtrEnable = true;   // some USB-serial cables power the UPS interface from DTR/RTS
        _port.RtsEnable = false;
    }

    public string? Query(string command, TimeSpan timeout)
    {
        _port.ReadTimeout = (int)timeout.TotalMilliseconds;
        try
        {
            _port.DiscardInBuffer();
            _port.Write(command + "\r");
            return _port.ReadLine().Trim('\n', ' ');
        }
        catch (TimeoutException) { return null; }
    }

    public void Dispose() { try { _port.Close(); } catch { } _port.Dispose(); }

    /// <summary>Sends Q1 to each COM port once; returns ports that answered like a Megatec UPS.</summary>
    public static List<LocalDeviceInfo> Probe(int baud = 2400)
    {
        var found = new List<LocalDeviceInfo>();
        string[] ports;
        try { ports = SerialPort.GetPortNames(); } catch { return found; }
        foreach (var p in ports.Distinct().OrderBy(x => x))
        {
            try
            {
                using var line = new SerialPortLine(p, baud);
                var q = MegatecProtocol.ParseQ1(line.Query("Q1", TimeSpan.FromMilliseconds(900)));
                if (q is null) continue;
                var info = MegatecProtocol.ParseI(line.Query("I", TimeSpan.FromMilliseconds(900)));
                found.Add(new LocalDeviceInfo
                {
                    Tier = LocalTier.Megatec, Key = p, Path = p,
                    Vendor = info?.Mfr is { Length: > 0 } m ? m : "Megatec",
                    Model = info?.Model is { Length: > 0 } mo ? mo : "UPS",
                    Detail = $"{p} · {baud} baud · Q1 reply {q.InputV:0} V in, {q.LoadPct:0}% load",
                });
            }
            catch { }
        }
        return found;
    }
}

/// <summary>Used where no OS integration exists: lists nothing, explains why.</summary>
public sealed class NullLocalUps(string why) : ILocalUpsPlatform
{
    public string? Limitation => why;
    public IReadOnlyList<LocalDeviceInfo> Enumerate(bool probeSerial = false) =>
        probeSerial ? SerialPortLine.Probe() : Array.Empty<LocalDeviceInfo>();
    public ILocalUpsSource? Create(LocalDeviceSettings d) => d.Tier switch
    {
        LocalTier.Replay => new ReplayUpsSource(),
        LocalTier.Megatec => new MegatecSource(() => new SerialPortLine(d.DeviceKey, d.BaudRate), d.DeviceKey),
        _ => null,
    };
}

/// <summary>Demo mode: the real platform plus a simulated APC Back-UPS on USB.</summary>
public sealed class DemoLocalUps(ILocalUpsPlatform inner) : ILocalUpsPlatform
{
    public static LocalDeviceInfo Simulated { get; } = new()
    {
        Tier = LocalTier.Replay, Key = "051d:0002:" + SyntheticHid.ApcSerial, Path = "replay:synthetic",
        Vendor = SyntheticHid.ApcVendor, Model = SyntheticHid.ApcProduct, Serial = SyntheticHid.ApcSerial,
        VendorId = 0x051d, ProductId = 0x0002,
        Detail = $"Simulated · HID usage page 0x84 · {FieldCount} report fields (synthetic, built from the HID Power Device spec)",
    };

    private static int FieldCount => SyntheticHid.ApcBackUps().Objects;

    public static LocalDeviceInfo SimulatedBattery { get; } = new()
    {
        Tier = LocalTier.Battery, Key = SyntheticHid.ApcProduct + "|" + SyntheticHid.ApcSerial, Path = "replay:battery",
        Vendor = SyntheticHid.ApcVendor, Model = SyntheticHid.ApcProduct, Serial = SyntheticHid.ApcSerial,
        Detail = "Simulated · Windows battery API · charge, AC, runtime, critical", SameAs = Simulated.Key,
    };

    public string? Limitation => null;

    public IReadOnlyList<LocalDeviceInfo> Enumerate(bool probeSerial = false)
    {
        var list = new List<LocalDeviceInfo> { Simulated, SimulatedBattery };
        try { list.AddRange(inner.Enumerate(probeSerial)); } catch { }
        return list;
    }

    /// <summary>Screenshot hook: drives the simulated UPS each poll (outage, low battery...).</summary>
    public Action<ReplayHidDevice, int>? Script { get; set; }
    /// <summary>Screenshot hook: pretend the USB cable was pulled.</summary>
    public bool Unplugged { get; set; }

    public ILocalUpsSource? Create(LocalDeviceSettings d) =>
        d.DevicePath?.StartsWith("replay:", StringComparison.Ordinal) == true || d.Tier == LocalTier.Replay
            ? new SimulatedSource(this) : inner.Create(d);

    private sealed class SimulatedSource(DemoLocalUps owner) : ILocalUpsSource
    {
        private readonly ReplayUpsSource _src = new(script: (dev, t) => (owner.Script ?? Idle)(dev, t));
        public string Kind => _src.Kind;
        public void Open() { if (owner.Unplugged) throw new FileNotFoundException("The UPS is not connected."); _src.Open(); }
        public IReadOnlyDictionary<string, string> Read() =>
            owner.Unplugged ? throw new IOException("The device is no longer connected.") : _src.Read();
        public void Dispose() => _src.Dispose();

        private static void Idle(ReplayHidDevice d, int t)
        {
            double load = 21 + 3 * Math.Sin(t / 4.0) + (t % 7 == 0 ? 2 : 0);
            d.Apply(load: Math.Round(load), inputVoltage: 231 + (t % 5 == 0 ? 1 : 0), runtime: Math.Round(2580 * 21 / load));
        }
    }
}
