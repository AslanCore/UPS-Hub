// Polls a HID Power Device and produces NUT-style variables, so the rest of UPS Hub treats a
// USB UPS exactly like one served by upsd. Status logic ported from Network UPS Tools
// drivers/usbhid-ups.c (process_boolean_info, ups_status_set, ups_alarm_set; GPL-2.0-or-later).

using System.Globalization;

namespace UpsHub.Core.Local.Hid;

public sealed record HidDeviceIdentity(
    ushort VendorId, ushort ProductId, string? Manufacturer, string? Product, string? Serial, string Path)
{
    public string Key => $"{VendorId:x4}:{ProductId:x4}:{Serial ?? ""}";
}

/// <summary>An opened HID UPS. Implemented by the Windows HID API, Linux hidraw, and the replay device.</summary>
public interface IHidUpsDevice : IDisposable
{
    HidDeviceIdentity Identity { get; }
    IReadOnlyList<HidField> Fields { get; }
    /// <summary>Forget cached reports; called once per poll.</summary>
    void BeginPoll();
    /// <summary>Logical value of a field, or null if its report could not be read.</summary>
    long? Read(HidField field);
    string? GetIndexedString(int index);
}

/// <summary>A device known by its raw report descriptor; reads whole reports through a callback.</summary>
public class RawHidUpsDevice : IHidUpsDevice
{
    private readonly Func<HidReportType, byte, int, byte[]?> _getReport;
    private readonly Func<int, string?> _getString;
    private readonly Dictionary<(HidReportType, byte), byte[]?> _cache = new();

    public RawHidUpsDevice(HidDeviceIdentity identity, HidReportDescriptor descriptor,
        Func<HidReportType, byte, int, byte[]?> getReport, Func<int, string?>? getString = null)
    {
        Identity = identity;
        Descriptor = descriptor;
        _getReport = getReport;
        _getString = getString ?? (_ => null);
    }

    public HidDeviceIdentity Identity { get; }
    public HidReportDescriptor Descriptor { get; }
    public IReadOnlyList<HidField> Fields => Descriptor.Fields;
    public int ReportsRead { get; private set; }

    public void BeginPoll() => _cache.Clear();

    public long? Read(HidField f)
    {
        var key = (f.Type, f.ReportId);
        if (!_cache.TryGetValue(key, out var buf))
        {
            buf = _getReport(f.Type, f.ReportId, Descriptor.ReportLength(f.Type, f.ReportId) + 1);
            ReportsRead++;
            _cache[key] = buf;
        }
        if (buf is null || buf.Length * 8 < f.Offset + 8 + f.Size) return null;
        return f.Extract(buf);
    }

    public string? GetIndexedString(int index) => _getString(index);
    public virtual void Dispose() { }
}

[Flags]
public enum HidUpsFlags : uint
{
    None = 0, Online = 1 << 0, Offline = 1 << 1, Dischrg = 1 << 2, Chrg = 1 << 3, LowBatt = 1 << 4, Overload = 1 << 5,
    ReplaceBatt = 1 << 6, ShutdownImm = 1 << 7, Trim = 1 << 8, Boost = 1 << 9, BypassAuto = 1 << 10, BypassMan = 1 << 11,
    Off = 1 << 12, Calib = 1 << 13, Overheat = 1 << 14, CommFault = 1 << 15, Depleted = 1 << 16, TimeLimitExp = 1 << 17,
    FullyCharged = 1 << 18, NotFullyCharged = 1 << 19, AwaitingPower = 1 << 20, FanFail = 1 << 21, NoBattery = 1 << 22,
    BattVoltLo = 1 << 23, BattVoltHi = 1 << 24, ChargerFail = 1 << 25, VRange = 1 << 26, FRange = 1 << 27,
}

public sealed class HidUpsDriver
{
    private static readonly Dictionary<string, HidUpsFlags> Tokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["online"] = HidUpsFlags.Online, ["offline"] = HidUpsFlags.Offline, ["dischrg"] = HidUpsFlags.Dischrg,
        ["chrg"] = HidUpsFlags.Chrg, ["lowbatt"] = HidUpsFlags.LowBatt, ["overload"] = HidUpsFlags.Overload,
        ["replacebatt"] = HidUpsFlags.ReplaceBatt, ["shutdownimm"] = HidUpsFlags.ShutdownImm, ["trim"] = HidUpsFlags.Trim,
        ["boost"] = HidUpsFlags.Boost, ["bypassauto"] = HidUpsFlags.BypassAuto, ["bypassman"] = HidUpsFlags.BypassMan,
        ["off"] = HidUpsFlags.Off, ["cal"] = HidUpsFlags.Calib, ["overheat"] = HidUpsFlags.Overheat,
        ["commfault"] = HidUpsFlags.CommFault, ["depleted"] = HidUpsFlags.Depleted, ["timelimitexp"] = HidUpsFlags.TimeLimitExp,
        ["fullycharged"] = HidUpsFlags.FullyCharged, ["notfullycharged"] = HidUpsFlags.NotFullyCharged,
        ["awaitingpower"] = HidUpsFlags.AwaitingPower, ["fanfail"] = HidUpsFlags.FanFail, ["nobattery"] = HidUpsFlags.NoBattery,
        ["battvoltlo"] = HidUpsFlags.BattVoltLo, ["battvolthi"] = HidUpsFlags.BattVoltHi, ["chargerfail"] = HidUpsFlags.ChargerFail,
        ["vrange"] = HidUpsFlags.VRange, ["frange"] = HidUpsFlags.FRange,
    };

    private readonly IHidUpsDevice _dev;
    private readonly List<(HidMapping Map, HidField Field)> _bound = new();
    private readonly Dictionary<string, string> _static = new();

    public HidUpsDriver(IHidUpsDevice device, HidSubdriver? subdriver = null)
    {
        _dev = device;
        Subdriver = subdriver ?? HidSubdrivers.For(device.Identity.VendorId);
        DescriptorFixed = Subdriver.FixReportDescriptor(device.Identity, device.Fields);

        // Index fields by their path string once (vendor names first, like NUT's usage tables).
        var byPath = new Dictionary<(string, HidReportType), HidField>();
        foreach (var f in device.Fields)
            byPath.TryAdd((HidUsageTables.PathToString(f.Path, Subdriver.Usages), f.Type), f);
        foreach (var m in Subdriver.Mappings)
        {
            // NUT reads Feature reports; Input reports arrive on the interrupt pipe. We poll, so fall back to Input.
            if (byPath.TryGetValue((m.Path, HidReportType.Feature), out var f) || byPath.TryGetValue((m.Path, HidReportType.Input), out f))
                _bound.Add((m, f));
        }

        var id = device.Identity;
        _static["device.type"] = "ups";
        _static["driver.name"] = "usbhid";
        _static["driver.version.data"] = Subdriver.Name;
        _static["ups.vendorid"] = id.VendorId.ToString("x4");
        _static["ups.productid"] = id.ProductId.ToString("x4");
        var mfr = Subdriver.FormatMfr(id);
        if (!string.IsNullOrWhiteSpace(mfr)) _static["device.mfr"] = _static["ups.mfr"] = mfr.Trim();
        var model = Subdriver.FormatModel(id, _static);
        if (!string.IsNullOrWhiteSpace(model)) _static["device.model"] = _static["ups.model"] = model.Trim();
        if (!string.IsNullOrWhiteSpace(id.Serial)) _static["device.serial"] = _static["ups.serial"] = id.Serial.Trim();
    }

    public HidSubdriver Subdriver { get; }
    public bool DescriptorFixed { get; }
    public int MappedFields => _bound.Count;
    public HidUpsFlags Flags { get; private set; }
    public IReadOnlyList<(HidMapping Map, HidField Field)> Bound => _bound;

    /// <summary>One poll: reads every mapped field and returns the NUT variables.</summary>
    public Dictionary<string, string> Poll()
    {
        _dev.BeginPoll();
        var vars = new Dictionary<string, string>(_static);
        int ok = 0;
        foreach (var (m, f) in _bound)
        {
            var raw = _dev.Read(f);
            if (raw is null) continue;
            ok++;
            double value = f.ToPhysical(raw.Value);
            if (m.IsStatus)
            {
                if (m.Convert?.Invoke(value, _dev) is { } token) ApplyToken(token);
                continue;
            }
            string? s = m.Convert is not null ? m.Convert(value, _dev) : Format(value, m.Format);
            if (s is not null) vars[m.Var] = s;   // later rows win, as in NUT
        }
        if (ok == 0) throw new IOException("The UPS did not answer any report request.");
        StatusTo(vars);
        return vars;
    }

    public static string Format(double v, string? fmt) => fmt switch
    {
        "%.1f" => v.ToString("0.0", CultureInfo.InvariantCulture),
        "%.2f" => v.ToString("0.00", CultureInfo.InvariantCulture),
        _ => Math.Round(v, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>NUT process_boolean_info: set or clear one flag ("online", "!dischrg", ...).</summary>
    public void ApplyToken(string token)
    {
        if (token == "online") ApplyToken("!offline");
        else if (token == "offline") ApplyToken("!online");
        if (token == "fullycharged") ApplyToken("!notfullycharged");
        else if (token == "notfullycharged") ApplyToken("!fullycharged");
        bool clear = token.StartsWith('!');
        if (!Tokens.TryGetValue(clear ? token[1..] : token, out var flag)) return;
        Flags = clear ? Flags & ~flag : Flags | flag;
    }

    /// <summary>NUT ups_status_set + ups_alarm_set, without the user-tunable OL+DISCHRG options.</summary>
    public void StatusTo(IDictionary<string, string> vars)
    {
        var f = Flags;
        bool Has(HidUpsFlags x) => (f & x) != 0;
        var st = new List<string>();
        void Set(string s) { if (!st.Contains(s)) st.Add(s); }

        if (Has(HidUpsFlags.VRange)) vars["input.transfer.reason"] = "input voltage out of range";
        else if (Has(HidUpsFlags.FRange)) vars["input.transfer.reason"] = "input frequency out of range";

        if (Has(HidUpsFlags.Calib)) Set("CAL");
        if (Has(HidUpsFlags.Offline)) Set("OB");
        if (!Has(HidUpsFlags.Online)) { if (Has(HidUpsFlags.Offline) || Has(HidUpsFlags.Dischrg)) Set("OB"); }
        else Set("OL");   // online, or online and discharging (calibration / hovering battery)
        if (Has(HidUpsFlags.Dischrg) && !Has(HidUpsFlags.Depleted)) Set("DISCHRG");
        if (Has(HidUpsFlags.Chrg))
        {
            if (Has(HidUpsFlags.NotFullyCharged)) Set("CHRG");
            else if (!Has(HidUpsFlags.FullyCharged)
                     && vars.TryGetValue("battery.charge", out var c)
                     && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch) && ch is > 0 and < 100)
                Set("CHRG");
        }
        if ((f & (HidUpsFlags.LowBatt | HidUpsFlags.TimeLimitExp | HidUpsFlags.ShutdownImm)) != 0) Set("LB");
        if (Has(HidUpsFlags.Overload)) Set("OVER");
        if (Has(HidUpsFlags.ReplaceBatt) || Has(HidUpsFlags.NoBattery)) Set("RB");
        if (Has(HidUpsFlags.Trim)) Set("TRIM");
        if (Has(HidUpsFlags.Boost)) Set("BOOST");
        if (Has(HidUpsFlags.BypassAuto) || Has(HidUpsFlags.BypassMan)) Set("BYPASS");
        if (Has(HidUpsFlags.Off)) Set("OFF");

        var alarms = new List<string>();
        if (Has(HidUpsFlags.ReplaceBatt)) alarms.Add("Replace battery!");
        if (Has(HidUpsFlags.ShutdownImm)) alarms.Add("Shutdown imminent!");
        if (Has(HidUpsFlags.FanFail)) alarms.Add("Fan failure!");
        if (Has(HidUpsFlags.NoBattery)) alarms.Add("No battery installed!");
        if (Has(HidUpsFlags.BattVoltLo)) alarms.Add("Battery voltage too low!");
        if (Has(HidUpsFlags.BattVoltHi)) alarms.Add("Battery voltage too high!");
        if (Has(HidUpsFlags.ChargerFail)) alarms.Add("Battery charger fail!");
        if (Has(HidUpsFlags.Overheat)) alarms.Add("Temperature too high!");
        if (Has(HidUpsFlags.CommFault)) alarms.Add("Internal UPS fault!");
        if (Has(HidUpsFlags.AwaitingPower)) alarms.Add("Awaiting power!");
        if (Has(HidUpsFlags.BypassAuto)) alarms.Add("Automatic bypass mode!");
        if (Has(HidUpsFlags.BypassMan)) alarms.Add("Manual bypass mode!");
        if (alarms.Count > 0) { vars["ups.alarm"] = string.Join(" ", alarms); Set("ALARM"); }

        vars["ups.status"] = string.Join(" ", st);
    }
}
