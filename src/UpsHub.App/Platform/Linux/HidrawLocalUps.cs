using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UpsHub.Core.Local;
using UpsHub.Core.Local.Hid;
using UpsHub.Core.Settings;

namespace UpsHub.App.Platform.Linux;

/// <summary>
/// Linux preview: HID UPSes through /dev/hidraw* (the kernel's raw HID interface), using the same
/// report-descriptor parser and NUT mappings as the tests. Needs read/write access to the hidraw node,
/// e.g. a udev rule: SUBSYSTEM=="hidraw", ATTRS{idVendor}=="051d", MODE="0660", GROUP="plugdev".
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class HidrawLocalUps : ILocalUpsPlatform
{
    public string? Limitation => null;

    private sealed record Node(string Dev, HidDeviceIdentity Id, HidReportDescriptor Desc);

    private static IEnumerable<Node> Nodes()
    {
        const string root = "/sys/class/hidraw";
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.GetDirectories(root))
        {
            Node? n = null;
            try
            {
                var rd = File.ReadAllBytes(Path.Combine(dir, "device", "report_descriptor"));
                // Top-level usage page must be Power Device (0x84) or Battery System (0x85).
                if (rd.Length < 2 || rd[0] != 0x05 || rd[1] is not (0x84 or 0x85)) continue;
                var ue = File.ReadAllLines(Path.Combine(dir, "device", "uevent"))
                    .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
                var hidId = ue.GetValueOrDefault("HID_ID", "0:0:0").Split(':');
                ushort vid = Convert.ToUInt16(hidId[1][^4..], 16), pid = Convert.ToUInt16(hidId[2][^4..], 16);
                var name = ue.GetValueOrDefault("HID_NAME");
                var serial = ue.GetValueOrDefault("HID_UNIQ");
                string? mfr = null, product = name;
                if (name is not null && vid == 0x051d && name.StartsWith("American Power Conversion ", StringComparison.Ordinal))
                { mfr = "American Power Conversion"; product = name["American Power Conversion ".Length..]; }
                var dev = "/dev/" + Path.GetFileName(dir);
                n = new Node(dev, new HidDeviceIdentity(vid, pid, mfr, product, string.IsNullOrEmpty(serial) ? null : serial, dev),
                    HidReportDescriptor.Parse(rd));
            }
            catch { }
            if (n is not null) yield return n;
        }
    }

    public IReadOnlyList<LocalDeviceInfo> Enumerate(bool probeSerial = false)
    {
        var list = Nodes().Select(n => new LocalDeviceInfo
        {
            Tier = LocalTier.Hid, Key = n.Id.Key, Path = n.Dev,
            Vendor = n.Id.Manufacturer ?? HidSubdrivers.BrandOf(n.Id.VendorId), Model = n.Id.Product ?? "HID UPS",
            Serial = n.Id.Serial, VendorId = n.Id.VendorId, ProductId = n.Id.ProductId,
            Detail = $"{n.Dev} · {n.Desc.Fields.Count} report fields · {HidSubdrivers.For(n.Id.VendorId).Name}",
        }).ToList();
        if (probeSerial) list.AddRange(SerialPortLine.Probe());
        return list;
    }

    public ILocalUpsSource? Create(LocalDeviceSettings d) => d.Tier switch
    {
        LocalTier.Hid => new HidUpsSource(() =>
        {
            var n = Nodes().FirstOrDefault(x => x.Id.Key == d.DeviceKey) ?? throw new FileNotFoundException("The UPS is not connected.");
            return new HidrawDevice(n.Dev, n.Id, n.Desc);
        }),
        LocalTier.Megatec => new MegatecSource(() => new SerialPortLine(d.DeviceKey, d.BaudRate), d.DeviceKey),
        LocalTier.Replay => new ReplayUpsSource(),
        _ => null,
    };

    private sealed class HidrawDevice : RawHidUpsDevice
    {
        private readonly int _fd;
        public HidrawDevice(string dev, HidDeviceIdentity id, HidReportDescriptor d) : this(OpenFd(dev), id, d) { }
        private HidrawDevice(int fd, HidDeviceIdentity id, HidReportDescriptor d) : base(id, d, (t, rid, len) => Get(fd, t, rid, len)) { _fd = fd; }

        private static int OpenFd(string dev)
        {
            int fd = open(dev, 2 /* O_RDWR */);
            if (fd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw err == 13 ? new UnauthorizedAccessException($"No permission for {dev} (add a udev rule).") : new FileNotFoundException(dev);
            }
            return fd;
        }

        private static byte[]? Get(int fd, HidReportType t, byte id, int len)
        {
            len = Math.Clamp(len, 2, 256);
            var buf = new byte[len];
            buf[0] = id;
            // HIDIOCGFEATURE(len) = _IOC(READ|WRITE, 'H', 0x07, len); HIDIOCGINPUT(len) = ... 0x0A
            uint nr = t == HidReportType.Feature ? 0x07u : 0x0Au;
            uint req = (3u << 30) | ((uint)len << 16) | ((uint)'H' << 8) | nr;
            int r = ioctl(fd, req, buf);
            if (r < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err is 19 or 5) throw new IOException("The UPS went away.");   // ENODEV, EIO
                return null;
            }
            return buf;
        }

        public override void Dispose() { close(_fd); }

        [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint request, byte[] data);
    }
}
