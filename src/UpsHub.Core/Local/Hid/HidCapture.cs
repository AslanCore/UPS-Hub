using System.Globalization;
using System.Reflection;

namespace UpsHub.Core.Local.Hid;

/// <summary>
/// A HID UPS fixture: its report descriptor plus the reports it returns (see <see cref="SyntheticHid"/>).
/// Used by the tests and by the replay device.
/// </summary>
public sealed class HidCapture
{
    public sealed record Item(byte[] Report, string Path, HidReportType Type, byte ReportId, int Offset, int Size, double Value);

    public required string Source { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public string? Vendor { get; init; }
    public string? Product { get; init; }
    public int Objects { get; init; }
    public required byte[] Descriptor { get; init; }
    public List<Item> Items { get; } = new();
    /// <summary>String descriptors by index (iProduct, iDeviceChemistry...).</summary>
    public Dictionary<int, string> Strings { get; } = new();

    /// <summary>The last report seen for each (type, id), as the replay device starts from.</summary>
    public Dictionary<(HidReportType, byte), byte[]> LastReports()
    {
        var d = new Dictionary<(HidReportType, byte), byte[]>();
        foreach (var i in Items) d[(i.Type, i.ReportId)] = i.Report;
        return d;
    }
}

/// <summary>
/// A simulated USB UPS built from a fixture: its descriptor and report bytes, then values are
/// written back into the reports (charge, runtime, AC present...) so the whole HID decode path runs.
/// </summary>
public sealed class ReplayHidDevice : RawHidUpsDevice
{
    private readonly Dictionary<(HidReportType, byte), byte[]> _reports;

    private ReplayHidDevice(HidCapture cap, HidReportDescriptor d, Dictionary<(HidReportType, byte), byte[]> reports,
        Func<int, string?> strings, string? serial)
        : base(new HidDeviceIdentity(cap.VendorId, cap.ProductId, cap.Vendor, cap.Product, serial, "replay:" + cap.Product),
            d, (t, id, _) => reports.TryGetValue((t, id), out var b) ? (byte[])b.Clone() : null, strings)
    {
        Capture = cap;
        _reports = reports;
    }

    public HidCapture Capture { get; }

    public static ReplayHidDevice Create(HidCapture cap, string? serial = SyntheticHid.ApcSerial)
    {
        var d = HidReportDescriptor.Parse(cap.Descriptor);
        var reports = new Dictionary<(HidReportType, byte), byte[]>();
        foreach (var (k, v) in cap.LastReports())
        {
            // Pad to the declared length (some firmwares return short reports).
            var len = d.ReportLength(k.Item1, k.Item2) + 1;
            var buf = new byte[Math.Max(len, v.Length)];
            v.CopyTo(buf, 0);
            reports[k] = buf;
        }
        return new ReplayHidDevice(cap, d, reports, i => cap.Strings.GetValueOrDefault(i), serial);
    }

    /// <summary>Writes a physical value into every report that carries <paramref name="path"/> (Feature and Input).</summary>
    public int Set(string path, double physical)
    {
        int n = 0;
        foreach (var f in Descriptor.Fields)
        {
            if (HidUsageTables.PathToString(f.Path, HidUsageTables.WithVendor(HidUsageTables.Apc)) != path
                && HidUsageTables.PathToString(f.Path) != path) continue;
            if (!_reports.TryGetValue((f.Type, f.ReportId), out var buf)) continue;
            int e = f.EffectiveExponent;
            long logical = (long)Math.Round(e == 0 ? physical : physical / Math.Pow(10, e));
            f.Write(buf, Math.Clamp(logical, f.LogMin, f.LogMax));
            n++;
        }
        return n;
    }

    /// <summary>Sets the main readings in one go; null leaves a value as it is.</summary>
    public void Apply(bool? acPresent = null, double? charge = null, double? runtime = null, double? load = null,
        double? inputVoltage = null, bool? lowBattery = null, bool? charging = null)
    {
        if (acPresent is { } ac)
        {
            foreach (var p in new[] { "UPS.PowerSummary.PresentStatus.ACPresent", "UPS.PowerSummary.ACPresent", "UPS.PresentStatus.ACPresent" })
                Set(p, ac ? 1 : 0);
            foreach (var p in new[] { "UPS.PowerSummary.PresentStatus.Discharging", "UPS.PowerSummary.Discharging", "UPS.PresentStatus.Discharging" })
                Set(p, ac ? 0 : 1);
        }
        if (charging is { } ch)
            foreach (var p in new[] { "UPS.PowerSummary.PresentStatus.Charging", "UPS.PowerSummary.Charging", "UPS.PresentStatus.Charging" })
                Set(p, ch ? 1 : 0);
        if (charge is { } c) { Set("UPS.PowerSummary.RemainingCapacity", c); Set("UPS.Battery.RemainingCapacity", c); }
        if (runtime is { } r) { Set("UPS.PowerSummary.RunTimeToEmpty", r); Set("UPS.Battery.RunTimeToEmpty", r); }
        if (load is { } l) Set("UPS.PowerConverter.PercentLoad", l);
        if (inputVoltage is { } v) Set("UPS.Input.Voltage", v);
        if (lowBattery is { } lb)
            foreach (var p in new[] { "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", "UPS.PowerSummary.BelowRemainingCapacityLimit" })
                Set(p, lb ? 1 : 0);
    }
}
