using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using UpsHub.Core.Local;

namespace UpsHub.App.Platform.Windows;

/// <summary>
/// Tier 0: the Windows battery class driver. Any USB UPS that Windows recognises (it shows a battery
/// icon) appears here through hidbatt.sys: charge, AC/battery, runtime estimate and the critical flag.
/// Works without touching the HID device at all.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsBattery
{
    private static readonly Guid GUID_DEVCLASS_BATTERY = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    private const uint BATTERY_IS_SHORT_TERM = 0x20000000, BATTERY_CAPACITY_RELATIVE = 0x40000000, BATTERY_SYSTEM_BATTERY = 0x80000000;
    private const uint UNKNOWN = 0xFFFFFFFF;

    public sealed record Info(string Path, uint Tag, uint Capabilities, uint Designed, uint FullCharge, uint Alert1, uint Alert2,
        string? Name, string? Manufacturer, string? Serial, string Chemistry)
    {
        /// <summary>Windows flags UPS batteries as "short term" (laptop batteries are long term).</summary>
        public bool IsUps => (Capabilities & BATTERY_IS_SHORT_TERM) != 0;
        public string Key => Serial is { Length: > 0 } s ? $"{Name}|{s}" : Name ?? Path;
    }

    public static List<Info> Enumerate()
    {
        var list = new List<Info>();
        foreach (var path in Native.InterfacePaths(GUID_DEVCLASS_BATTERY))
        {
            var h = Native.Open(path, out _);
            if (h == Native.INVALID_HANDLE_VALUE) continue;
            try { if (Query(h, path) is { } i) list.Add(i); }
            catch { }
            finally { Native.CloseHandle(h); }
        }
        return list;
    }

    private static uint Tag(IntPtr h)
    {
        var inb = new byte[4];
        var outb = new byte[4];
        return Native.DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, inb, 4, outb, 4, out var n, IntPtr.Zero) && n == 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(outb) : 0;
    }

    private static byte[]? QueryInfo(IntPtr h, uint tag, int level, int size)
    {
        var q = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(q, tag);
        BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(4), level);
        var o = new byte[size];
        if (!Native.DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, q, 12, o, (uint)size, out var n, IntPtr.Zero)) return null;
        return o.AsSpan(0, (int)n).ToArray();
    }

    private static string? QueryString(IntPtr h, uint tag, int level)
    {
        var b = QueryInfo(h, tag, level, 512);
        if (b is null || b.Length < 2) return null;
        var s = Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }

    private static Info? Query(IntPtr h, string path)
    {
        uint tag = Tag(h);
        if (tag == 0) return null;
        var bi = QueryInfo(h, tag, 0, 36);   // BATTERY_INFORMATION
        if (bi is null || bi.Length < 36) return null;
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(bi.AsSpan(o));
        var chem = Encoding.ASCII.GetString(bi, 8, 4).TrimEnd('\0', ' ');
        return new Info(path, tag, U(0), U(12), U(16), U(20), U(24),
            QueryString(h, tag, 4), QueryString(h, tag, 6), QueryString(h, tag, 8), chem);
    }

    /// <summary>One reading as NUT variables.</summary>
    public static Dictionary<string, string> Read(string path, Info info)
    {
        var h = Native.Open(path, out _);
        if (h == Native.INVALID_HANDLE_VALUE) throw new FileNotFoundException("The UPS battery device is gone.");
        try
        {
            uint tag = Tag(h);
            if (tag == 0) throw new IOException("The UPS battery is not present.");
            var ws = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(ws, tag);
            var st = new byte[16];
            if (!Native.DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, ws, 20, st, 16, out var n, IntPtr.Zero) || n < 16)
                throw new IOException($"Battery status query failed (Win32 error {Marshal.GetLastWin32Error()}).");
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(st), cap = BinaryPrimitives.ReadUInt32LittleEndian(st.AsSpan(4)),
                 mv = BinaryPrimitives.ReadUInt32LittleEndian(st.AsSpan(8));

            var est = QueryInfo(h, tag, 3, 4);   // BatteryEstimatedTime, at the current rate
            uint secs = est is { Length: 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(est) : UNKNOWN;
            return ToVariables(info, state, cap, mv, secs);
        }
        finally { Native.CloseHandle(h); }
    }

    public static Dictionary<string, string> ToVariables(Info info, uint powerState, uint capacity, uint millivolts, uint runtime)
    {
        static string N(double v) => v.ToString("0", CultureInfo.InvariantCulture);
        var v = new Dictionary<string, string> { ["device.type"] = "ups", ["driver.name"] = "battery-api" };
        if (info.Manufacturer is { } m) v["device.mfr"] = v["ups.mfr"] = m;
        if (info.Name is { } nm) v["device.model"] = v["ups.model"] = nm;
        if (info.Serial is { } s) v["device.serial"] = v["ups.serial"] = s;
        if (info.Chemistry.Length > 0) v["battery.type"] = info.Chemistry;

        double? pct = null;
        if (capacity != UNKNOWN && info.FullCharge is > 0 and not UNKNOWN)
            pct = Math.Clamp(100.0 * capacity / info.FullCharge, 0, 100);
        if (pct is { } p) v["battery.charge"] = N(p);
        if (info.Alert1 is > 0 and not UNKNOWN && info.FullCharge is > 0 and not UNKNOWN)
            v["battery.charge.low"] = N(100.0 * info.Alert1 / info.FullCharge);
        if (runtime != UNKNOWN && runtime > 0) v["battery.runtime"] = runtime.ToString(CultureInfo.InvariantCulture);
        if (millivolts != UNKNOWN && millivolts > 0) v["battery.voltage"] = (millivolts / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

        bool online = (powerState & 1) != 0, discharging = (powerState & 2) != 0, charging = (powerState & 4) != 0, critical = (powerState & 8) != 0;
        var tokens = new List<string> { online ? "OL" : "OB" };
        if (discharging) tokens.Add("DISCHRG");
        if (charging) tokens.Add("CHRG");
        bool low = critical || (!online && pct is { } c && v.TryGetValue("battery.charge.low", out var lowS)
                               && double.TryParse(lowS, NumberStyles.Float, CultureInfo.InvariantCulture, out var lowPct) && c <= lowPct);
        if (low) tokens.Add("LB");
        v["ups.status"] = string.Join(" ", tokens);
        return v;
    }
}

/// <summary>Tier 0 source; falls back to GetSystemPowerStatus if the battery device can't be queried.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBatterySource : ILocalUpsSource
{
    private readonly string _key;
    private WindowsBattery.Info? _info;
    public WindowsBatterySource(string key) { _key = key; }
    public string Kind => "Windows battery";

    public void Open()
    {
        var all = WindowsBattery.Enumerate();
        _info = all.FirstOrDefault(b => b.Key == _key) ?? all.FirstOrDefault(b => b.IsUps)
                ?? throw new FileNotFoundException("Windows doesn't list a UPS battery right now.");
    }

    public IReadOnlyDictionary<string, string> Read() =>
        WindowsBattery.Read(_info!.Path, _info);

    public void Dispose() { }
}
