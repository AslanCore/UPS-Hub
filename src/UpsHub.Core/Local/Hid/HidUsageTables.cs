// HID Power Device (0x84) and Battery System (0x85) usage names, plus the vendor usages the
// APC, CyberPower and Eaton/MGE mappings need. Ported from Network UPS Tools
// drivers/libhid.c (hid_usage_lkp), apc-hid.c, cps-hid.c and mge-hid.c (GPL-2.0-or-later).

using System.Globalization;
using System.Text;

namespace UpsHub.Core.Local.Hid;

public static class HidUsageTables
{
    private static readonly (string Name, uint Code)[] Standard =
    {
        // Power Device page 0x84
        ("Undefined", 0x00840000), ("iName", 0x00840001), ("PresentStatus", 0x00840002), ("ChangedStatus", 0x00840003),
        ("UPS", 0x00840004), ("PowerSupply", 0x00840005), ("BatterySystem", 0x00840010), ("BatterySystemID", 0x00840011),
        ("Battery", 0x00840012), ("BatteryID", 0x00840013), ("Charger", 0x00840014), ("ChargerID", 0x00840015),
        ("PowerConverter", 0x00840016), ("PowerConverterID", 0x00840017), ("OutletSystem", 0x00840018), ("OutletSystemID", 0x00840019),
        ("Input", 0x0084001a), ("InputID", 0x0084001b), ("Output", 0x0084001c), ("OutputID", 0x0084001d),
        ("Flow", 0x0084001e), ("FlowID", 0x0084001f), ("Outlet", 0x00840020), ("OutletID", 0x00840021),
        ("Gang", 0x00840022), ("GangID", 0x00840023), ("PowerSummary", 0x00840024), ("PowerSummaryID", 0x00840025),
        ("Voltage", 0x00840030), ("Current", 0x00840031), ("Frequency", 0x00840032), ("ApparentPower", 0x00840033),
        ("ActivePower", 0x00840034), ("PercentLoad", 0x00840035), ("Temperature", 0x00840036), ("Humidity", 0x00840037),
        ("BadCount", 0x00840038),
        ("ConfigVoltage", 0x00840040), ("ConfigCurrent", 0x00840041), ("ConfigFrequency", 0x00840042),
        ("ConfigApparentPower", 0x00840043), ("ConfigActivePower", 0x00840044), ("ConfigPercentLoad", 0x00840045),
        ("ConfigTemperature", 0x00840046), ("ConfigHumidity", 0x00840047),
        ("SwitchOnControl", 0x00840050), ("SwitchOffControl", 0x00840051), ("ToggleControl", 0x00840052),
        ("LowVoltageTransfer", 0x00840053), ("HighVoltageTransfer", 0x00840054), ("DelayBeforeReboot", 0x00840055),
        ("DelayBeforeStartup", 0x00840056), ("DelayBeforeShutdown", 0x00840057), ("Test", 0x00840058),
        ("ModuleReset", 0x00840059), ("AudibleAlarmControl", 0x0084005a),
        ("Present", 0x00840060), ("Good", 0x00840061), ("InternalFailure", 0x00840062), ("VoltageOutOfRange", 0x00840063),
        ("FrequencyOutOfRange", 0x00840064), ("Overload", 0x00840065), ("OverCharged", 0x00840066),
        ("OverTemperature", 0x00840067), ("ShutdownRequested", 0x00840068), ("ShutdownImminent", 0x00840069),
        ("SwitchOn/Off", 0x0084006b), ("Switchable", 0x0084006c), ("Used", 0x0084006d), ("Boost", 0x0084006e),
        ("Buck", 0x0084006f), ("Initialized", 0x00840070), ("Tested", 0x00840071), ("AwaitingPower", 0x00840072),
        ("CommunicationLost", 0x00840073),
        ("iManufacturer", 0x008400fd), ("iProduct", 0x008400fe), ("iSerialNumber", 0x008400ff),
        // Battery System page 0x85
        ("Undefined", 0x00850000), ("SMBBatteryMode", 0x00850001), ("SMBBatteryStatus", 0x00850002),
        ("SMBAlarmWarning", 0x00850003), ("SMBChargerMode", 0x00850004), ("SMBChargerStatus", 0x00850005),
        ("SMBChargerSpecInfo", 0x00850006), ("SMBSelectorState", 0x00850007), ("SMBSelectorPresets", 0x00850008),
        ("SMBSelectorInfo", 0x00850009),
        ("OptionalMfgFunction1", 0x00850010), ("OptionalMfgFunction2", 0x00850011), ("OptionalMfgFunction3", 0x00850012),
        ("OptionalMfgFunction4", 0x00850013), ("OptionalMfgFunction5", 0x00850014), ("ConnectionToSMBus", 0x00850015),
        ("OutputConnection", 0x00850016), ("ChargerConnection", 0x00850017), ("BatteryInsertion", 0x00850018),
        ("Usenext", 0x00850019), ("OKToUse", 0x0085001a), ("BatterySupported", 0x0085001b),
        ("SelectorRevision", 0x0085001c), ("ChargingIndicator", 0x0085001d),
        ("ManufacturerAccess", 0x00850028), ("RemainingCapacityLimit", 0x00850029), ("RemainingTimeLimit", 0x0085002a),
        ("AtRate", 0x0085002b), ("CapacityMode", 0x0085002c), ("BroadcastToCharger", 0x0085002d),
        ("PrimaryBattery", 0x0085002e), ("ChargeController", 0x0085002f),
        ("TerminateCharge", 0x00850040), ("TerminateDischarge", 0x00850041), ("BelowRemainingCapacityLimit", 0x00850042),
        ("RemainingTimeLimitExpired", 0x00850043), ("Charging", 0x00850044), ("Discharging", 0x00850045),
        ("FullyCharged", 0x00850046), ("FullyDischarged", 0x00850047), ("ConditioningFlag", 0x00850048),
        ("AtRateOK", 0x00850049), ("SMBErrorCode", 0x0085004a), ("NeedReplacement", 0x0085004b),
        ("AtRateTimeToFull", 0x00850060), ("AtRateTimeToEmpty", 0x00850061), ("AverageCurrent", 0x00850062),
        ("Maxerror", 0x00850063), ("RelativeStateOfCharge", 0x00850064), ("AbsoluteStateOfCharge", 0x00850065),
        ("RemainingCapacity", 0x00850066), ("FullChargeCapacity", 0x00850067), ("RunTimeToEmpty", 0x00850068),
        ("AverageTimeToEmpty", 0x00850069), ("AverageTimeToFull", 0x0085006a), ("CycleCount", 0x0085006b),
        ("BattPackModelLevel", 0x00850080), ("InternalChargeController", 0x00850081), ("PrimaryBatterySupport", 0x00850082),
        ("DesignCapacity", 0x00850083), ("SpecificationInfo", 0x00850084), ("ManufacturerDate", 0x00850085),
        ("SerialNumber", 0x00850086), ("iManufacturerName", 0x00850087), ("iDevicename", 0x00850088),
        ("iDeviceChemistry", 0x00850089), ("ManufacturerData", 0x0085008a), ("Rechargeable", 0x0085008b),
        ("WarningCapacityLimit", 0x0085008c), ("CapacityGranularity1", 0x0085008d), ("CapacityGranularity2", 0x0085008e),
        ("iOEMInformation", 0x0085008f),
        ("InhibitCharge", 0x008500c0), ("EnablePolling", 0x008500c1), ("ResetToZero", 0x008500c2),
        ("ACPresent", 0x008500d0), ("BatteryPresent", 0x008500d1), ("PowerFail", 0x008500d2),
        ("AlarmInhibited", 0x008500d3), ("ThermistorUnderRange", 0x008500d4), ("ThermistorHot", 0x008500d5),
        ("ThermistorCold", 0x008500d6), ("ThermistorOverRange", 0x008500d7), ("VoltageOutOfRange", 0x008500d8),
        ("CurrentOutOfRange", 0x008500d9), ("CurrentNotRegulated", 0x008500da), ("VoltageNotRegulated", 0x008500db),
        ("MasterMode", 0x008500dc),
        ("ChargerSelectorSupport", 0x008500f0), ("ChargerSpec", 0x008500f1), ("Level2", 0x008500f2), ("Level3", 0x008500f3),
    };

    /// <summary>APC vendor page 0xFF86 and the Back-UPS (BUP) quirks (apc-hid.c).</summary>
    public static readonly (string Name, uint Code)[] Apc =
    {
        ("APCGeneralCollection", 0xff860005), ("APCEnvironment", 0xff860006), ("APCProbe1", 0xff860007),
        ("APCProbe2", 0xff860008), ("APCBattReplaceDate", 0xff860016), ("APCBattCapBeforeStartup", 0xff860019),
        ("APC_UPS_FirmwareRevision", 0xff860042), ("APCLineFailCause", 0xff860052), ("APCStatusFlag", 0xff860060),
        ("APCSensitivity", 0xff860061), ("APCPanelTest", 0xff860072), ("APCShutdownAfterDelay", 0xff860076),
        ("APC_USB_FirmwareRevision", 0xff860079), ("APCDelayBeforeReboot", 0xff86007c),
        ("APCDelayBeforeShutdown", 0xff86007d), ("APCDelayBeforeStartup", 0xff86007e),
        ("BUPHibernate", 0x00850058), ("BUPBattCapBeforeStartup", 0x00860012),
        ("BUPDelayBeforeStartup", 0x00860076), ("BUPSelfTest", 0x00860010),
    };

    /// <summary>CyberPower vendor usages (cps-hid.c).</summary>
    public static readonly (string Name, uint Code)[] CyberPower =
    {
        ("CPSFirmwareVersion", 0xff0100d0), ("CPSInputSensitivity", 0xff010043),
    };

    /// <summary>Eaton / MGE vendor page 0xFFFF, the subset our mapping uses (mge-hid.c).</summary>
    public static readonly (string Name, uint Code)[] Mge =
    {
        ("LCMSystem", 0xffff0018), ("LCMAlarm", 0xffff001a), ("RemainingCapacityLimitSetting", 0xffff004d),
        ("VoltageTooHigh", 0xffff0074), ("VoltageTooLow", 0xffff0075), ("FanFailure", 0xffff0077),
        ("OverThreshold", 0xffff007e), ("TimerExpired", 0xffff0093), ("Mode", 0xffff0094),
        ("Status", 0xffff00e9), ("iVersion", 0xffff00f1),
    };

    private static readonly Dictionary<uint, string> StandardNames = Build(Standard);
    private static Dictionary<uint, string> Build(IEnumerable<(string Name, uint Code)> t)
    {
        var d = new Dictionary<uint, string>();
        foreach (var (n, c) in t) d.TryAdd(c, n);
        return d;
    }

    public static readonly IReadOnlyDictionary<uint, string> Empty = new Dictionary<uint, string>();

    public static IReadOnlyDictionary<uint, string> WithVendor(params (string Name, uint Code)[][] vendor)
    {
        var d = new Dictionary<uint, string>();
        foreach (var t in vendor) foreach (var (n, c) in t) d.TryAdd(c, n);
        return d;
    }

    /// <summary>NUT path_to_string: vendor table first, then the standard table, "[n]" for indexes, else 8-digit hex.</summary>
    public static string PathToString(IReadOnlyList<uint> path, IReadOnlyDictionary<uint, string>? vendor = null)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) sb.Append('.');
            var n = path[i];
            if (vendor is not null && vendor.TryGetValue(n, out var vn)) sb.Append(vn);
            else if (StandardNames.TryGetValue(n, out var sn)) sb.Append(sn);
            else if ((n & 0xffff0000) == 0x00ff0000) sb.Append('[').Append((n & 0xffff).ToString(CultureInfo.InvariantCulture)).Append(']');
            else sb.Append(n.ToString("x8", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
