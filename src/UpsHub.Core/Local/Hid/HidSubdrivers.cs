// HID-to-NUT variable mappings ("hid2nut" tables), read-only subset.
// Ported from Network UPS Tools drivers/apc-hid.c, cps-hid.c, mge-hid.c and usbhid-ups.c
// (GPL-2.0-or-later). Commands and writable defaults are left out: UPS Hub only reads.

using System.Globalization;

namespace UpsHub.Core.Local.Hid;

/// <summary>Turns a physical value into a NUT value string (or a status token for "BOOL" rows). Null = no value.</summary>
public delegate string? HidConverter(double value, IHidUpsDevice device);

public sealed record HidMapping(string Var, string Path, string? Format = null, HidConverter? Convert = null)
{
    public bool IsStatus => Var == "BOOL";
}

public sealed class HidSubdriver
{
    public required string Name { get; init; }
    /// <summary>Short manufacturer used in labels ("APC", "CyberPower", "Eaton").</summary>
    public required string Brand { get; init; }
    public required ushort[] VendorIds { get; init; }
    public required IReadOnlyDictionary<uint, string> Usages { get; init; }
    public required HidMapping[] Mappings { get; init; }
    public Func<HidDeviceIdentity, IDictionary<string, string>, string?> FormatModel { get; init; } = (id, _) => id.Product;
    public Func<HidDeviceIdentity, string> FormatMfr { get; init; } = id => id.Manufacturer ?? "";
    /// <summary>Repairs known-bad descriptors in place (NUT *_fix_report_desc). Returns true if anything changed.</summary>
    public Func<HidDeviceIdentity, IReadOnlyList<HidField>, bool> FixReportDescriptor { get; init; } = (_, _) => false;

    public bool Claims(ushort vid) => VendorIds.Contains(vid);
}

public static class HidSubdrivers
{
    // ---- lookups shared by all subdrivers (usbhid-ups.c) ---------------------------------------

    public static HidConverter Lookup(params (long Value, string Text)[] table) => (v, _) =>
    {
        var l = (long)v;
        foreach (var (k, t) in table) if (k == l) return t;
        return null;
    };

    public static readonly HidConverter Online = Lookup((1, "online"), (0, "offline"));
    public static readonly HidConverter Discharging = Lookup((1, "dischrg"), (0, "!dischrg"));
    public static readonly HidConverter Charging = Lookup((1, "chrg"), (0, "!chrg"));
    public static readonly HidConverter LowBatt = Lookup((1, "lowbatt"), (0, "!lowbatt"));
    public static readonly HidConverter Overload = Lookup((1, "overload"), (0, "!overload"));
    public static readonly HidConverter ReplaceBatt = Lookup((1, "replacebatt"), (0, "!replacebatt"));
    public static readonly HidConverter Trim = Lookup((1, "trim"), (0, "!trim"));
    public static readonly HidConverter Boost = Lookup((1, "boost"), (0, "!boost"));
    public static readonly HidConverter ShutdownImm = Lookup((1, "shutdownimm"), (0, "!shutdownimm"));
    public static readonly HidConverter TimeLimitExpired = Lookup((1, "timelimitexp"), (0, "!timelimitexp"));
    public static readonly HidConverter FullyCharged = Lookup((1, "fullycharged"), (0, "notfullycharged"));
    public static readonly HidConverter NoBattery = Lookup((1, "!nobattery"), (0, "nobattery"));
    public static readonly HidConverter Off = Lookup((0, "off"), (1, "!off"));
    public static readonly HidConverter OverHeat = Lookup((1, "overheat"), (0, "!overheat"));
    public static readonly HidConverter CommFault = Lookup((1, "commfault"), (0, "!commfault"));
    public static readonly HidConverter FanFail = Lookup((1, "fanfail"), (0, "!fanfail"));
    public static readonly HidConverter VRange = Lookup((1, "vrange"), (0, "!vrange"));
    public static readonly HidConverter FRange = Lookup((1, "frange"), (0, "!frange"));
    public static readonly HidConverter BattVoltHi = Lookup((1, "battvolthi"), (0, "!battvolthi"));
    public static readonly HidConverter BattVoltLo = Lookup((1, "battvoltlo"), (0, "!battvoltlo"));
    public static readonly HidConverter ChargerFail = Lookup((1, "chargerfail"), (0, "!chargerfail"));
    public static readonly HidConverter OnBattInverse = Lookup((1, "!online"), (0, "online"));

    public static readonly HidConverter TestResult = Lookup((1, "Done and passed"), (2, "Done and warning"),
        (3, "Done and error"), (4, "Aborted"), (5, "In progress"), (6, "No test initiated"), (7, "Test scheduled"));
    public static readonly HidConverter Beeper = Lookup((1, "disabled"), (2, "enabled"), (3, "muted"));

    public static readonly HidConverter StringId = (v, dev) =>
    {
        var s = dev.GetIndexedString((int)v);
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    };

    /// <summary>USB HID PDC packed date: (year-1980)*512 + month*32 + day.</summary>
    public static readonly HidConverter Date = (v, _) =>
    {
        long x = (long)v;
        if (x == 0) return "not set";
        return string.Create(CultureInfo.InvariantCulture, $"{1980 + (x >> 9):0000}/{(x >> 5) & 0x0f:00}/{x & 0x1f:00}");
    };

    public static readonly HidConverter KelvinCelsius = (v, _) =>
        (v >= 273 && v <= 373 ? v - 273.15 : v).ToString("0.0", CultureInfo.InvariantCulture);

    // ---- APC (apc-hid.c, "APC HID 0.100") ---------------------------------------------------------

    /// <summary>APC writes dates as hex-as-decimal, e.g. 0x102202 = 2002/10/22.</summary>
    public static readonly HidConverter ApcDate = (v, _) =>
    {
        long x = (long)v;
        if (x == 0) return "not set";
        long year = (x & 0xf) + 10 * ((x >> 4) & 0xf);
        long month = ((x >> 16) & 0xf) + 10 * ((x >> 20) & 0xf);
        long day = ((x >> 8) & 0xf) + 10 * ((x >> 12) & 0xf);
        year += year >= 70 ? 1900 : 2000;
        return string.Create(CultureInfo.InvariantCulture, $"{year:0000}/{month:00}/{day:00}");
    };

    public static readonly HidConverter ApcStatusFlag = Lookup((8, "!off"), (16, "!off"), (0, "off"));
    public static readonly HidConverter ApcLineFailVRange = Lookup((1, "vrange"), (2, "vrange"), (4, "vrange"), (8, "vrange"), (9, "vrange"), (0, "!vrange"));
    public static readonly HidConverter ApcLineFailFRange = Lookup((7, "frange"), (0, "!frange"));
    public static readonly HidConverter ApcSensitivity = Lookup((0, "low"), (1, "medium"), (2, "high"));

    private static HidMapping M(string v, string p, string? f = "%.0f", HidConverter? c = null) => new(v, p, f, c);
    private static HidMapping B(string p, HidConverter c) => new("BOOL", p, null, c);

    public static readonly HidSubdriver Apc = new()
    {
        Name = "APC HID (NUT apc-hid port)",
        Brand = "APC",
        VendorIds = new ushort[] { 0x051d },
        Usages = HidUsageTables.WithVendor(HidUsageTables.Apc),
        FormatMfr = id => string.IsNullOrWhiteSpace(id.Manufacturer) ? "APC" : id.Manufacturer!,
        FormatModel = (id, vars) =>
        {
            // "Back-UPS 500 FW: 6.5.I USB FW: c1" -> model "Back-UPS 500", ups.firmware 6.5.I, ups.firmware.aux c1
            var model = id.Product ?? "unknown";
            int fw = model.IndexOf("FW:", StringComparison.Ordinal);
            if (fw > 0)
            {
                var rest = model[(fw + 3)..];
                model = model[..fw].TrimEnd();
                int usb = rest.IndexOf("USB FW:", StringComparison.Ordinal);
                if (usb >= 0)
                {
                    vars["ups.firmware.aux"] = rest[(usb + 7)..].Trim();
                    rest = rest[..usb];
                }
                vars["ups.firmware"] = rest.Trim();
            }
            return model;
        },
        FixReportDescriptor = (id, fields) =>
        {
            // Back-UPS XS 1400U and friends declare a too-small input voltage range in 230 V regions.
            if (id.VendorId != 0x051d || id.ProductId != 0x0002) return false;
            bool changed = false;
            var hvt = fields.FirstOrDefault(f => f.ReportId == 0x33 && f.Usage == 0x00840054);
            if (hvt is null) return false;
            var volt = fields.Where(f => f.ReportId == 0x31 && f.Usage == 0x00840030).ToList();
            foreach (var v in volt)
                if (hvt.LogMax > v.LogMax) { v.LogMin = 0; v.LogMax = hvt.LogMax * 2; changed = true; }
            var cfg = fields.Where(f => f.ReportId == 0x30 && f.Usage == 0x00840040).ToList();
            foreach (var c in cfg)
                if (hvt.LogMax > c.LogMax) { c.LogMax = 255; changed = true; }
            return changed;
        },
        Mappings = new[]
        {
            M("battery.charge", "UPS.PowerSummary.RemainingCapacity"),
            M("battery.charge.low", "UPS.PowerSummary.RemainingCapacityLimit"),
            M("battery.charge.warning", "UPS.PowerSummary.WarningCapacityLimit"),
            M("battery.runtime", "UPS.Battery.RunTimeToEmpty"),
            M("battery.runtime", "UPS.PowerSummary.RunTimeToEmpty"),
            M("battery.runtime.low", "UPS.Battery.RemainingTimeLimit"),
            M("battery.runtime.low", "UPS.PowerSummary.RemainingTimeLimit"),
            M("battery.voltage", "UPS.Battery.Voltage", "%.1f"),
            M("battery.voltage", "UPS.PowerSummary.Voltage", "%.1f"),
            M("battery.voltage.nominal", "UPS.Battery.ConfigVoltage", "%.1f"),
            M("battery.voltage.nominal", "UPS.PowerSummary.ConfigVoltage", "%.1f"),
            M("battery.temperature", "UPS.Battery.Temperature", null, KelvinCelsius),
            M("battery.type", "UPS.PowerSummary.iDeviceChemistry", null, StringId),
            M("battery.mfr.date", "UPS.Battery.ManufacturerDate", null, Date),
            M("battery.mfr.date", "UPS.PowerSummary.APCBattReplaceDate", null, ApcDate),
            M("battery.date", "UPS.Battery.APCBattReplaceDate", null, ApcDate),
            M("ups.load", "UPS.Output.PercentLoad", "%.1f"),
            M("ups.load", "UPS.PowerConverter.PercentLoad"),
            M("ups.timer.start", "UPS.PowerSummary.DelayBeforeStartup"),
            M("ups.timer.shutdown", "UPS.PowerSummary.DelayBeforeShutdown"),
            M("ups.timer.reboot", "UPS.PowerSummary.DelayBeforeReboot"),
            M("ups.timer.start", "UPS.Output.DelayBeforeStartup"),
            M("ups.timer.shutdown", "UPS.Output.DelayBeforeShutdown"),
            M("ups.timer.reboot", "UPS.Output.DelayBeforeReboot"),
            M("ups.timer.start", "UPS.APCGeneralCollection.APCDelayBeforeStartup"),
            M("ups.timer.shutdown", "UPS.APCGeneralCollection.APCDelayBeforeShutdown"),
            M("ups.timer.reboot", "UPS.APCGeneralCollection.APCDelayBeforeReboot"),
            M("ups.test.result", "UPS.Battery.Test", null, TestResult),
            M("ups.beeper.status", "UPS.PowerSummary.AudibleAlarmControl", null, Beeper),
            M("ups.mfr.date", "UPS.ManufacturerDate", null, Date),
            M("ups.mfr.date", "UPS.PowerSummary.ManufacturerDate", null, Date),
            M("ups.realpower.nominal", "UPS.PowerConverter.ConfigActivePower"),
            M("ups.realpower.nominal", "UPS.Output.ConfigActivePower"),

            B("UPS.PowerSummary.PresentStatus.ACPresent", Online),
            B("UPS.PowerSummary.PresentStatus.Discharging", Discharging),
            B("UPS.PowerSummary.PresentStatus.Charging", Charging),
            B("UPS.PowerSummary.PresentStatus.ShutdownImminent", ShutdownImm),
            B("UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", LowBatt),
            B("UPS.PowerSummary.PresentStatus.Overload", Overload),
            B("UPS.PowerSummary.PresentStatus.NeedReplacement", ReplaceBatt),
            B("UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", TimeLimitExpired),
            B("UPS.PowerSummary.PresentStatus.BatteryPresent", NoBattery),
            B("UPS.PowerSummary.Charging", Charging),
            B("UPS.PowerSummary.Discharging", Discharging),
            B("UPS.PowerSummary.ACPresent", Online),
            B("UPS.PowerSummary.BelowRemainingCapacityLimit", LowBatt),
            B("UPS.PowerSummary.ShutdownImminent", ShutdownImm),
            B("UPS.PowerSummary.APCStatusFlag", ApcStatusFlag),
            B("UPS.Input.APCLineFailCause", ApcLineFailVRange),
            B("UPS.Input.APCLineFailCause", ApcLineFailFRange),

            M("input.voltage", "UPS.Input.Voltage", "%.1f"),
            M("input.voltage.nominal", "UPS.Input.ConfigVoltage"),
            M("input.transfer.low", "UPS.Output.LowVoltageTransfer"),
            M("input.transfer.high", "UPS.Output.HighVoltageTransfer"),
            M("input.transfer.low", "UPS.Input.LowVoltageTransfer"),
            M("input.transfer.high", "UPS.Input.HighVoltageTransfer"),
            M("input.sensitivity", "UPS.Input.APCSensitivity", null, ApcSensitivity),
            M("output.voltage", "UPS.Output.Voltage", "%.1f"),
            M("output.voltage.nominal", "UPS.Output.ConfigVoltage", "%.1f"),
            M("output.current", "UPS.Output.Current", "%.2f"),
            M("output.frequency", "UPS.Output.Frequency", "%.1f"),
            M("ambient.temperature", "UPS.APCEnvironment.APCProbe1.Temperature", null, KelvinCelsius),
            M("ambient.humidity", "UPS.APCEnvironment.APCProbe1.Humidity", "%.1f"),
        },
    };

    // ---- CyberPower (cps-hid.c, "CyberPower HID 0.87") --------------------------------------------

    public static readonly HidConverter CpsSensitivity = Lookup((0, "low"), (1, "normal"), (2, "high"));
    private static readonly HidConverter CpsCharge = (v, _) => Math.Min(v, 100).ToString("0", CultureInfo.InvariantCulture);
    private static HidConverter CpsFrequency() => (v, _) =>
        // Some firmwares report 0.1 Hz units without saying so (499 = 49.9 Hz).
        (v > 450 && v <= 650 ? v / 10 : v).ToString("0.0", CultureInfo.InvariantCulture);

    public static readonly HidSubdriver CyberPower = new()
    {
        Name = "CyberPower HID (NUT cps-hid port)",
        Brand = "CyberPower",
        VendorIds = new ushort[] { 0x0764, 0x0483 },
        Usages = HidUsageTables.WithVendor(HidUsageTables.CyberPower),
        FormatMfr = id => string.IsNullOrWhiteSpace(id.Manufacturer) ? "CPS" : id.Manufacturer!,
        FormatModel = (id, _) => id.Product?.Trim(),
        FixReportDescriptor = (id, fields) =>
        {
            // Output voltage declared with the high-transfer range (reads e.g. 136 V instead of 120 V).
            if (id.VendorId != 0x0764 || (id.ProductId != 0x0501 && id.ProductId != 0x0601)) return false;
            bool changed = false;
            var hvt = fields.FirstOrDefault(f => f.ReportId == 0x10 && f.Usage == 0x00840054);
            if (hvt is null) return false;
            static bool Scaled(HidField f) => f.HavePhyMin && f.HavePhyMax && !(f.PhyMin == 0 && f.PhyMax == 0);
            foreach (var o in fields.Where(f => f.ReportId == 0x12 && f.Usage == 0x00840030))
                if (hvt.LogMin == o.LogMin && hvt.LogMax == o.LogMax) { o.LogMin = 0; o.LogMax = 511; changed = true; }
            foreach (var i in fields.Where(f => f.ReportId == 0x0f && f.Usage == 0x00840030))
                if (((hvt.LogMin == i.LogMin && hvt.LogMax == i.LogMax) || hvt.LogMax > i.LogMax) && !Scaled(i))
                { i.LogMin = 0; i.LogMax = 511; changed = true; }
            foreach (var c in fields.Where(f => f.ReportId == 0x0e && f.Usage == 0x00840040))
                if (hvt.LogMax > c.LogMax && !Scaled(c)) { c.LogMax = 255; changed = true; }
            return changed;
        },
        Mappings = new[]
        {
            M("battery.type", "UPS.PowerSummary.iDeviceChemistry", null, StringId),
            M("battery.mfr.date", "UPS.Battery.ManufacturerDate", null, Date),
            M("battery.mfr.date", "UPS.PowerSummary.iOEMInformation", null, StringId),
            M("battery.charge.warning", "UPS.PowerSummary.WarningCapacityLimit"),
            M("battery.charge.low", "UPS.PowerSummary.RemainingCapacityLimit"),
            M("battery.charge", "UPS.PowerSummary.RemainingCapacity", null, CpsCharge),
            M("battery.runtime", "UPS.PowerSummary.RunTimeToEmpty"),
            M("battery.runtime.low", "UPS.PowerSummary.RemainingTimeLimit"),
            M("battery.voltage.nominal", "UPS.PowerSummary.ConfigVoltage"),
            M("battery.voltage", "UPS.PowerSummary.Voltage", "%.1f"),
            M("ups.load", "UPS.Output.PercentLoad"),
            M("ups.beeper.status", "UPS.PowerSummary.AudibleAlarmControl", null, Beeper),
            M("ups.test.result", "UPS.Output.Test", null, TestResult),
            M("ups.power", "UPS.Output.ApparentPower"),
            M("ups.power.nominal", "UPS.Output.ConfigApparentPower"),
            M("ups.realpower", "UPS.Output.ActivePower"),
            M("ups.realpower.nominal", "UPS.Output.ConfigActivePower"),
            M("ups.timer.start", "UPS.Output.DelayBeforeStartup"),
            M("ups.timer.shutdown", "UPS.Output.DelayBeforeShutdown"),
            M("ups.timer.reboot", "UPS.Output.DelayBeforeReboot"),
            M("ups.firmware", "UPS.PowerSummary.CPSFirmwareVersion", null, StringId),
            M("ups.temperature", "UPS.PowerSummary.Temperature", null, KelvinCelsius),

            B("UPS.PowerSummary.PresentStatus.ACPresent", Online),
            B("UPS.PowerSummary.PresentStatus.Charging", Charging),
            B("UPS.PowerSummary.PresentStatus.Discharging", Discharging),
            B("UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", LowBatt),
            B("UPS.PowerSummary.PresentStatus.FullyCharged", FullyCharged),
            B("UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", TimeLimitExpired),
            B("UPS.PowerSummary.PresentStatus.NeedReplacement", ReplaceBatt),
            B("UPS.Output.Boost", Boost),
            B("UPS.Output.Overload", Overload),

            M("input.frequency", "UPS.Input.Frequency", null, CpsFrequency()),
            M("input.voltage.nominal", "UPS.Input.ConfigVoltage"),
            M("input.voltage", "UPS.Input.Voltage", "%.1f"),
            M("input.transfer.low", "UPS.Input.LowVoltageTransfer"),
            M("input.transfer.high", "UPS.Input.HighVoltageTransfer"),
            M("input.transfer.low", "UPS.Output.LowVoltageTransfer"),
            M("input.transfer.high", "UPS.Output.HighVoltageTransfer"),
            M("input.sensitivity", "UPS.Output.CPSInputSensitivity", null, CpsSensitivity),
            M("output.frequency", "UPS.Output.Frequency", null, CpsFrequency()),
            M("output.voltage", "UPS.Output.Voltage", "%.1f"),
            M("output.voltage.nominal", "UPS.Output.ConfigVoltage"),
        },
    };

    // ---- Eaton / MGE (mge-hid.c), the read-only core of the table ---------------------------------

    public static readonly HidSubdriver EatonMge = new()
    {
        Name = "Eaton/MGE HID (NUT mge-hid port, core subset)",
        Brand = "Eaton",
        VendorIds = new ushort[] { 0x0463, 0x047c, 0x0592, 0x03f0, 0x2b2d, 0x06da, 0x04b3, 0x09d6 },
        Usages = HidUsageTables.WithVendor(HidUsageTables.Mge),
        FormatMfr = id => string.IsNullOrWhiteSpace(id.Manufacturer) ? "Eaton" : id.Manufacturer!,
        FormatModel = (id, _) => id.Product?.Trim(),
        Mappings = new[]
        {
            M("battery.charge", "UPS.PowerSummary.RemainingCapacity"),
            M("battery.charge.low", "UPS.PowerSummary.RemainingCapacityLimitSetting"),
            M("battery.charge.low", "UPS.PowerSummary.RemainingCapacityLimit"),
            M("battery.runtime", "UPS.PowerSummary.RunTimeToEmpty"),
            M("battery.runtime.low", "UPS.PowerSummary.RemainingTimeLimit"),
            M("battery.temperature", "UPS.BatterySystem.Battery.Temperature", null, KelvinCelsius),
            M("battery.type", "UPS.PowerSummary.iDeviceChemistry", null, StringId),
            M("battery.voltage", "UPS.BatterySystem.Voltage", "%.1f"),
            M("battery.voltage", "UPS.PowerSummary.Voltage", "%.1f"),
            M("ups.firmware", "UPS.PowerSummary.iVersion", null, StringId),
            M("ups.load", "UPS.PowerSummary.PercentLoad"),
            M("ups.temperature", "UPS.PowerSummary.Temperature", null, KelvinCelsius),
            M("ups.power", "UPS.PowerConverter.Output.ApparentPower"),
            M("ups.power.nominal", "UPS.Flow.[4].ConfigApparentPower"),
            M("ups.realpower", "UPS.PowerConverter.Output.ActivePower"),
            M("ups.realpower.nominal", "UPS.Flow.[4].ConfigActivePower"),

            B("UPS.PowerSummary.PresentStatus.ACPresent", Online),
            B("UPS.PowerConverter.Input.[3].PresentStatus.Used", OnBattInverse),
            B("UPS.PowerConverter.Input.[1].PresentStatus.Used", Online),
            B("UPS.PowerSummary.PresentStatus.Discharging", Discharging),
            B("UPS.PowerSummary.PresentStatus.Charging", Charging),
            B("UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", LowBatt),
            B("UPS.PowerConverter.Output.Overload.[1].PresentStatus.OverThreshold", Overload),
            B("UPS.PowerConverter.Output.Overload.[2].PresentStatus.OverThreshold", Overload),
            B("UPS.PowerSummary.PresentStatus.Overload", Overload),
            B("UPS.PowerSummary.PresentStatus.NeedReplacement", ReplaceBatt),
            B("UPS.LCMSystem.LCMAlarm.[2].PresentStatus.TimerExpired", ReplaceBatt),
            B("UPS.PowerConverter.Input.[1].PresentStatus.Buck", Trim),
            B("UPS.PowerConverter.Input.[1].PresentStatus.Boost", Boost),
            B("UPS.PowerConverter.Input.[1].PresentStatus.VoltageOutOfRange", VRange),
            B("UPS.PowerConverter.Input.[1].PresentStatus.FrequencyOutOfRange", FRange),
            B("UPS.PowerSummary.PresentStatus.Good", Off),
            B("UPS.PowerSummary.PresentStatus.FanFailure", FanFail),
            B("UPS.BatterySystem.Battery.PresentStatus.Present", NoBattery),
            B("UPS.BatterySystem.Charger.PresentStatus.InternalFailure", ChargerFail),
            B("UPS.BatterySystem.Charger.PresentStatus.VoltageTooHigh", BattVoltHi),
            B("UPS.BatterySystem.Charger.PresentStatus.VoltageTooLow", BattVoltLo),
            B("UPS.PowerSummary.PresentStatus.InternalFailure", CommFault),
            B("UPS.PowerSummary.PresentStatus.OverTemperature", OverHeat),
            B("UPS.PowerSummary.PresentStatus.ShutdownImminent", ShutdownImm),

            M("input.voltage", "UPS.PowerConverter.Input.[1].Voltage", "%.1f"),
            M("input.voltage.nominal", "UPS.Flow.[1].ConfigVoltage"),
            M("input.frequency", "UPS.PowerConverter.Input.[1].Frequency", "%.1f"),
            M("output.voltage", "UPS.PowerConverter.Output.Voltage", "%.1f"),
            M("output.voltage.nominal", "UPS.Flow.[4].ConfigVoltage"),
            M("output.current", "UPS.PowerConverter.Output.Current", "%.2f"),
            M("output.frequency", "UPS.PowerConverter.Output.Frequency", "%.1f"),
        },
    };

    // ---- Generic: any HID Power Device that follows the USB PDC spec ------------------------------

    public static readonly HidSubdriver Generic = new()
    {
        Name = "Generic HID Power Device",
        Brand = "",
        VendorIds = Array.Empty<ushort>(),
        Usages = HidUsageTables.Empty,
        Mappings = new[]
        {
            M("battery.charge", "UPS.PowerSummary.RemainingCapacity"),
            M("battery.charge", "UPS.Battery.RemainingCapacity"),
            M("battery.charge.low", "UPS.PowerSummary.RemainingCapacityLimit"),
            M("battery.charge.warning", "UPS.PowerSummary.WarningCapacityLimit"),
            M("battery.runtime", "UPS.Battery.RunTimeToEmpty"),
            M("battery.runtime", "UPS.PowerSummary.RunTimeToEmpty"),
            M("battery.runtime.low", "UPS.PowerSummary.RemainingTimeLimit"),
            M("battery.voltage", "UPS.Battery.Voltage", "%.1f"),
            M("battery.voltage", "UPS.PowerSummary.Voltage", "%.1f"),
            M("battery.voltage.nominal", "UPS.Battery.ConfigVoltage", "%.1f"),
            M("battery.voltage.nominal", "UPS.PowerSummary.ConfigVoltage", "%.1f"),
            M("battery.temperature", "UPS.Battery.Temperature", null, KelvinCelsius),
            M("battery.type", "UPS.PowerSummary.iDeviceChemistry", null, StringId),
            M("battery.mfr.date", "UPS.Battery.ManufacturerDate", null, Date),
            M("ups.load", "UPS.Output.PercentLoad"),
            M("ups.load", "UPS.PowerConverter.PercentLoad"),
            M("ups.load", "UPS.PowerSummary.PercentLoad"),
            M("ups.power", "UPS.Output.ApparentPower"),
            M("ups.power.nominal", "UPS.Output.ConfigApparentPower"),
            M("ups.realpower", "UPS.Output.ActivePower"),
            M("ups.realpower.nominal", "UPS.Output.ConfigActivePower"),
            M("ups.realpower.nominal", "UPS.PowerConverter.ConfigActivePower"),
            M("ups.test.result", "UPS.Battery.Test", null, TestResult),
            M("ups.beeper.status", "UPS.PowerSummary.AudibleAlarmControl", null, Beeper),
            M("ups.temperature", "UPS.PowerSummary.Temperature", null, KelvinCelsius),

            B("UPS.PowerSummary.PresentStatus.ACPresent", Online),
            B("UPS.PowerSummary.PresentStatus.Discharging", Discharging),
            B("UPS.PowerSummary.PresentStatus.Charging", Charging),
            B("UPS.PowerSummary.PresentStatus.ShutdownImminent", ShutdownImm),
            B("UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", LowBatt),
            B("UPS.PowerSummary.PresentStatus.Overload", Overload),
            B("UPS.PowerSummary.PresentStatus.NeedReplacement", ReplaceBatt),
            B("UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", TimeLimitExpired),
            B("UPS.PowerSummary.PresentStatus.FullyCharged", FullyCharged),
            B("UPS.PowerSummary.PresentStatus.BatteryPresent", NoBattery),
            B("UPS.PowerSummary.PresentStatus.Boost", Boost),
            B("UPS.PowerSummary.PresentStatus.Buck", Trim),
            B("UPS.PowerSummary.ACPresent", Online),
            B("UPS.PowerSummary.Charging", Charging),
            B("UPS.PowerSummary.Discharging", Discharging),
            B("UPS.PowerSummary.BelowRemainingCapacityLimit", LowBatt),
            B("UPS.PowerSummary.ShutdownImminent", ShutdownImm),

            M("input.voltage", "UPS.Input.Voltage", "%.1f"),
            M("input.voltage.nominal", "UPS.Input.ConfigVoltage"),
            M("input.frequency", "UPS.Input.Frequency", "%.1f"),
            M("input.transfer.low", "UPS.Input.LowVoltageTransfer"),
            M("input.transfer.high", "UPS.Input.HighVoltageTransfer"),
            M("output.voltage", "UPS.Output.Voltage", "%.1f"),
            M("output.voltage.nominal", "UPS.Output.ConfigVoltage"),
            M("output.frequency", "UPS.Output.Frequency", "%.1f"),
            M("output.current", "UPS.Output.Current", "%.2f"),
        },
    };

    public static IReadOnlyList<HidSubdriver> All { get; } = new[] { Apc, CyberPower, EatonMge };

    public static HidSubdriver For(ushort vendorId) => All.FirstOrDefault(s => s.Claims(vendorId)) ?? Generic;

    /// <summary>Known vendor names for the device list, before a device is opened.</summary>
    public static string BrandOf(ushort vendorId) => vendorId switch
    {
        0x051d => "APC",
        0x0764 or 0x0483 => "CyberPower",
        0x0463 => "Eaton",
        0x047c => "Dell",
        0x0592 => "Powerware",
        0x03f0 => "HP",
        0x09ae => "Tripp Lite",
        0x050d => "Belkin",
        0x0d9f => "Powercom",
        0x06da => "Phoenixtec",
        0x04b3 => "IBM",
        0x2b2d => "AEG",
        0x09d6 => "Kstar",
        0x0665 => "Cypress (generic UPS)",
        0x10af => "Liebert",
        0x2e51 => "Ablerex",
        _ => "",
    };
}
