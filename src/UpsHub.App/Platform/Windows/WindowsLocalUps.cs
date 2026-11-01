using System.Runtime.Versioning;
using UpsHub.Core.Local;
using UpsHub.Core.Local.Hid;
using UpsHub.Core.Settings;

namespace UpsHub.App.Platform.Windows;

/// <summary>Finds and opens UPSes plugged into this PC: HID Power Devices (Tier 1), battery-class UPSes (Tier 0), Megatec serial (Tier 2).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLocalUps : ILocalUpsPlatform
{
    public string? Limitation => null;

    public static List<(string Path, HidDeviceIdentity Id, int Fields, ushort Page)> HidUpsDevices()
    {
        var list = new List<(string, HidDeviceIdentity, int, ushort)>();
        Native.HidD_GetHidGuid(out var hid);
        foreach (var path in Native.InterfacePaths(hid))
        {
            try
            {
                using var dev = WindowsHidUpsDevice.Open(path);
                if (dev.TopUsagePage is 0x84 or 0x85)
                    list.Add((path, dev.Identity, dev.Fields.Count, dev.TopUsagePage));
            }
            catch { /* keyboards, mice and other devices Windows keeps to itself */ }
        }
        return list;
    }

    public IReadOnlyList<LocalDeviceInfo> Enumerate(bool probeSerial = false)
    {
        var found = new List<LocalDeviceInfo>();
        foreach (var (path, id, n, page) in HidUpsDevices())
        {
            found.Add(new LocalDeviceInfo
            {
                Tier = LocalTier.Hid, Key = id.Key, Path = path,
                Vendor = id.Manufacturer ?? HidSubdrivers.BrandOf(id.VendorId), Model = id.Product ?? "HID UPS",
                Serial = id.Serial, VendorId = id.VendorId, ProductId = id.ProductId,
                Detail = $"HID usage page 0x{page:x2} · {n} report fields · {HidSubdrivers.For(id.VendorId).Name}",
            });
        }
        try
        {
            foreach (var b in WindowsBattery.Enumerate().Where(b => b.IsUps))
            {
                var same = found.FirstOrDefault(h => h.Tier == LocalTier.Hid && b.Serial is { Length: > 0 } && h.Serial == b.Serial);
                found.Add(new LocalDeviceInfo
                {
                    Tier = LocalTier.Battery, Key = b.Key, Path = b.Path,
                    Vendor = b.Manufacturer ?? "", Model = b.Name ?? "UPS battery", Serial = b.Serial,
                    Detail = "Windows battery API · charge, AC, runtime, critical", SameAs = same?.Key,
                });
            }
        }
        catch { }
        if (probeSerial) found.AddRange(SerialPortLine.Probe());
        return found;
    }

    public ILocalUpsSource? Create(LocalDeviceSettings d) => d.Tier switch
    {
        LocalTier.Hid => new HidUpsSource(() =>
        {
            // The interface path changes between USB ports; find the device again by VID:PID:serial.
            var match = HidUpsDevices().FirstOrDefault(x => x.Id.Key == d.DeviceKey);
            var path = match.Path ?? d.DevicePath ?? throw new FileNotFoundException();
            if (match.Path is null && !HidUpsDevices().Any(x => x.Path == path)) throw new FileNotFoundException("The UPS is not connected.");
            d.DevicePath = path;
            return WindowsHidUpsDevice.Open(path);
        }),
        LocalTier.Battery => new WindowsBatterySource(d.DeviceKey),
        LocalTier.Megatec => new MegatecSource(() => new SerialPortLine(d.DeviceKey, d.BaudRate), d.DeviceKey),
        LocalTier.Replay => new ReplayUpsSource(),
        _ => null,
    };
}
