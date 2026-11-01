using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using UpsHub.Core.Local.Hid;

namespace UpsHub.App.Platform.Windows;

/// <summary>Win32 SetupAPI / HID / file interop, kept to blittable signatures (byte buffers, IntPtr handles).</summary>
[SupportedOSPlatform("windows")]
internal static class Native
{
    public const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, byte[] data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, byte[] data, byte[]? detail, uint size, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, byte[]? inBuf, uint inSize, byte[]? outBuf, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(IntPtr h, byte[] attributes);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetManufacturerString(IntPtr h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetProductString(IntPtr h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetSerialNumberString(IntPtr h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetIndexedString(IntPtr h, uint index, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(IntPtr h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetInputReport(IntPtr h, byte[] buf, uint len);

    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")] public static extern int HidP_GetLinkCollectionNodes(byte[] nodes, ref uint length, IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort length, IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort length, IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr pp, byte[] report, uint reportLength);
    [DllImport("hid.dll")] public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        ushort[] usageList, ref uint usageLength, IntPtr pp, byte[] report, uint reportLength);

    /// <summary>Device interface paths for a SetupAPI interface class.</summary>
    public static List<string> InterfacePaths(Guid cls)
    {
        var list = new List<string>();
        var set = SetupDiGetClassDevsW(ref cls, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return list;
        try
        {
            int cb = IntPtr.Size == 8 ? 32 : 28;           // SP_DEVICE_INTERFACE_DATA
            for (uint i = 0; i < 512; i++)
            {
                var data = new byte[cb];
                BinaryPrimitives.WriteInt32LittleEndian(data, cb);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cls, i, data)) break;
                SetupDiGetDeviceInterfaceDetailW(set, data, null, 0, out var need, IntPtr.Zero);
                if (need == 0) continue;
                var detail = new byte[need];
                BinaryPrimitives.WriteInt32LittleEndian(detail, IntPtr.Size == 8 ? 8 : 6);   // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                if (!SetupDiGetDeviceInterfaceDetailW(set, data, detail, need, out _, IntPtr.Zero)) continue;
                var path = Encoding.Unicode.GetString(detail, 4, (int)need - 4).TrimEnd('\0');
                int nul = path.IndexOf('\0');
                list.Add(nul >= 0 ? path[..nul] : path);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    /// <summary>Opens read/write, then read-only, then with no access (enough for attributes and feature reports on many stacks).</summary>
    public static IntPtr Open(string path, out uint accessUsed)
    {
        foreach (var access in new[] { GENERIC_READ | GENERIC_WRITE, GENERIC_READ, 0u })
        {
            var h = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h != INVALID_HANDLE_VALUE) { accessUsed = access; return h; }
        }
        accessUsed = 0;
        return INVALID_HANDLE_VALUE;
    }

    public static string? WString(byte[] buf)
    {
        var s = Encoding.Unicode.GetString(buf);
        int nul = s.IndexOf('\0');
        s = (nul >= 0 ? s[..nul] : s).Trim();
        return s.Length == 0 ? null : s;
    }
}

/// <summary>
/// Tier 1 on Windows: a HID Power Device read through hid.dll. Windows doesn't hand out the raw report
/// descriptor, so fields are rebuilt from the HidP value/button caps and link-collection tree (giving the
/// same "UPS.PowerSummary.RemainingCapacity" paths NUT uses) and values are pulled with HidP_GetUsageValue.
/// Feature reports are fetched with HidD_GetFeature, Input reports with HidD_GetInputReport. Read-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHidUpsDevice : IHidUpsDevice
{
    private sealed record Tag(int ReportType, ushort UsagePage, ushort Usage, ushort LinkCollection, bool Button);

    private IntPtr _h;
    private IntPtr _pp;
    private readonly int _inputLen, _featureLen;
    private readonly Dictionary<(HidReportType, byte), byte[]?> _cache = new();

    public HidDeviceIdentity Identity { get; }
    public IReadOnlyList<HidField> Fields { get; }
    public ushort TopUsagePage { get; }
    public ushort TopUsage { get; }

    private WindowsHidUpsDevice(IntPtr h, IntPtr pp, HidDeviceIdentity id, List<HidField> fields, int inLen, int featLen, ushort page, ushort usage)
    {
        _h = h; _pp = pp; Identity = id; Fields = fields; _inputLen = inLen; _featureLen = featLen; TopUsagePage = page; TopUsage = usage;
    }

    /// <summary>Opens a HID interface path. With <paramref name="probeOnly"/> the field table is still built (cheap) but no report is read.</summary>
    public static WindowsHidUpsDevice Open(string path)
    {
        var h = Native.Open(path, out _);
        if (h == Native.INVALID_HANDLE_VALUE)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 5) throw new UnauthorizedAccessException("Windows denied access to the UPS HID device.");
            throw new FileNotFoundException($"Can't open the UPS (Win32 error {err}).");
        }
        try
        {
            var attr = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(attr, 12);
            if (!Native.HidD_GetAttributes(h, attr)) throw new IOException("HidD_GetAttributes failed.");
            ushort vid = BinaryPrimitives.ReadUInt16LittleEndian(attr.AsSpan(4)), pid = BinaryPrimitives.ReadUInt16LittleEndian(attr.AsSpan(6));
            string? Str(Func<IntPtr, byte[], uint, bool> f) { var b = new byte[512]; return f(h, b, (uint)b.Length) ? Native.WString(b) : null; }
            var id = new HidDeviceIdentity(vid, pid, Str(Native.HidD_GetManufacturerString), Str(Native.HidD_GetProductString),
                Str(Native.HidD_GetSerialNumberString), path);

            if (!Native.HidD_GetPreparsedData(h, out var pp)) throw new IOException("HidD_GetPreparsedData failed.");
            var caps = new byte[64];
            if (Native.HidP_GetCaps(pp, caps) != Native.HIDP_STATUS_SUCCESS) { Native.HidD_FreePreparsedData(pp); throw new IOException("HidP_GetCaps failed."); }
            ushort U(int i) => BinaryPrimitives.ReadUInt16LittleEndian(caps.AsSpan(i * 2));
            ushort usage = U(0), page = U(1), inLen = U(2), featLen = U(4), nodes = U(22);
            var fields = BuildFields(pp, nodes);
            return new WindowsHidUpsDevice(h, pp, id, fields, inLen, featLen, page, usage);
        }
        catch
        {
            Native.CloseHandle(h);
            throw;
        }
    }

    private static List<HidField> BuildFields(IntPtr pp, ushort nodeCount)
    {
        // Link collections: HIDP_LINK_COLLECTION_NODE (x64: 24 bytes).
        int nodeSize = IntPtr.Size == 8 ? 24 : 20;
        uint n = nodeCount;
        var nodes = new byte[Math.Max(1, (int)n) * nodeSize];
        if (n > 0 && Native.HidP_GetLinkCollectionNodes(nodes, ref n, pp) != Native.HIDP_STATUS_SUCCESS) n = 0;
        var paths = new List<uint>[n];
        for (int i = 0; i < n; i++)
        {
            var path = new List<uint>();
            int cur = i, guard = 0;
            var chain = new List<int>();
            while (guard++ < 64)
            {
                chain.Add(cur);
                if (cur == 0) break;
                cur = BinaryPrimitives.ReadUInt16LittleEndian(nodes.AsSpan(cur * nodeSize + 4));   // Parent
            }
            chain.Reverse();
            foreach (var c in chain)
            {
                var o = c * nodeSize;
                ushort lu = BinaryPrimitives.ReadUInt16LittleEndian(nodes.AsSpan(o)), lp = BinaryPrimitives.ReadUInt16LittleEndian(nodes.AsSpan(o + 2));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(nodes.AsSpan(o + 12)) & 0xff;
                path.Add(((uint)lp << 16) | lu);
                if (type >= 0x80) path.Add(0x00ff0000 | (type & 0x7f));   // NUT's "[n]" collection index
            }
            paths[i] = path;
        }

        var fields = new List<HidField>();
        foreach (var (rt, type) in new[] { (0, HidReportType.Input), (2, HidReportType.Feature) })
        {
            ushort count = 0;
            Native.HidP_GetValueCaps(rt, Array.Empty<byte>(), ref count, pp);
            count = Math.Min(count, (ushort)4096);
            if (count > 0)
            {
                var vc = new byte[count * 72];
                if (Native.HidP_GetValueCaps(rt, vc, ref count, pp) == Native.HIDP_STATUS_SUCCESS)
                    for (int i = 0; i < count; i++) AddValue(fields, paths, vc.AsSpan(i * 72, 72), rt, type);
            }
            count = 0;
            Native.HidP_GetButtonCaps(rt, Array.Empty<byte>(), ref count, pp);
            count = Math.Min(count, (ushort)4096);
            if (count > 0)
            {
                var bc = new byte[count * 72];
                if (Native.HidP_GetButtonCaps(rt, bc, ref count, pp) == Native.HIDP_STATUS_SUCCESS)
                    for (int i = 0; i < count; i++) AddButton(fields, paths, bc.AsSpan(i * 72, 72), rt, type);
            }
        }
        return fields;
    }

    private static uint[] PathFor(List<uint>[] paths, ushort link, uint usage) =>
        (link < paths.Length ? paths[link] : new List<uint>()).Append(usage).ToArray();

    private static void AddValue(List<HidField> fields, List<uint>[] paths, ReadOnlySpan<byte> c, int rt, HidReportType type)
    {
        ushort page = BinaryPrimitives.ReadUInt16LittleEndian(c);
        byte reportId = c[2];
        bool alias = c[3] != 0;
        ushort link = BinaryPrimitives.ReadUInt16LittleEndian(c[6..]);
        bool isRange = c[12] != 0;
        ushort bitSize = BinaryPrimitives.ReadUInt16LittleEndian(c[18..]);
        uint unitExp = BinaryPrimitives.ReadUInt32LittleEndian(c[32..]);
        uint units = BinaryPrimitives.ReadUInt32LittleEndian(c[36..]);
        long logMin = BinaryPrimitives.ReadInt32LittleEndian(c[40..]), logMax = BinaryPrimitives.ReadInt32LittleEndian(c[44..]);
        long phyMin = BinaryPrimitives.ReadInt32LittleEndian(c[48..]), phyMax = BinaryPrimitives.ReadInt32LittleEndian(c[52..]);
        ushort uMin = BinaryPrimitives.ReadUInt16LittleEndian(c[56..]), uMax = isRange ? BinaryPrimitives.ReadUInt16LittleEndian(c[58..]) : uMin;
        if (alias || bitSize == 0 || bitSize > 32) return;
        bool assumed = false;
        if (logMax < logMin)
        {
            // Same repair as NUT: a max that only looks negative because of a short signed encoding.
            logMax = bitSize >= 32 ? uint.MaxValue : (long)((1UL << bitSize) - 1) & (uint)logMax;
            assumed = true;
        }
        for (uint u = uMin; u <= uMax && u - uMin < 256; u++)
        {
            fields.Add(new HidField
            {
                Path = PathFor(paths, link, ((uint)page << 16) | u),
                Type = type, ReportId = reportId, Offset = -1, Size = bitSize,
                LogMin = logMin, LogMax = logMax, AssumedLogMax = assumed,
                PhyMin = phyMin, PhyMax = phyMax, HavePhyMin = phyMin != 0 || phyMax != 0, HavePhyMax = phyMin != 0 || phyMax != 0,
                Unit = units, UnitExp = (sbyte)(unitExp > 7 ? (int)(unitExp | 0xF0) - 256 : (int)unitExp),
                Tag = new Tag(rt, page, (ushort)u, link, false),
            });
        }
    }

    private static void AddButton(List<HidField> fields, List<uint>[] paths, ReadOnlySpan<byte> c, int rt, HidReportType type)
    {
        ushort page = BinaryPrimitives.ReadUInt16LittleEndian(c);
        byte reportId = c[2];
        bool alias = c[3] != 0;
        ushort link = BinaryPrimitives.ReadUInt16LittleEndian(c[6..]);
        bool isRange = c[12] != 0;
        ushort uMin = BinaryPrimitives.ReadUInt16LittleEndian(c[56..]), uMax = isRange ? BinaryPrimitives.ReadUInt16LittleEndian(c[58..]) : uMin;
        if (alias) return;
        for (uint u = uMin; u <= uMax && u - uMin < 256; u++)
        {
            fields.Add(new HidField
            {
                Path = PathFor(paths, link, ((uint)page << 16) | u),
                Type = type, ReportId = reportId, Offset = -1, Size = 1, LogMin = 0, LogMax = 1,
                Tag = new Tag(rt, page, (ushort)u, link, true),
            });
        }
    }

    public void BeginPoll() => _cache.Clear();

    private byte[]? Report(HidReportType type, byte id)
    {
        if (_cache.TryGetValue((type, id), out var b)) return b;
        int len = type == HidReportType.Feature ? _featureLen : _inputLen;
        if (len <= 0) return _cache[(type, id)] = null;
        var buf = new byte[len];
        buf[0] = id;
        bool ok = type == HidReportType.Feature
            ? Native.HidD_GetFeature(_h, buf, (uint)len)
            : Native.HidD_GetInputReport(_h, buf, (uint)len);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            // 1167 = device not connected, 6 = invalid handle: the UPS went away.
            if (err is 1167 or 6 or 31) throw new IOException($"The UPS stopped responding (Win32 error {err}).");
            buf = null;
        }
        return _cache[(type, id)] = buf;
    }

    public long? Read(HidField f)
    {
        if (f.Tag is not Tag t) return null;
        var buf = Report(f.Type, f.ReportId);
        if (buf is null) return null;
        if (t.Button)
        {
            var list = new ushort[256];
            uint n = (uint)list.Length;
            if (Native.HidP_GetUsages(t.ReportType, t.UsagePage, t.LinkCollection, list, ref n, _pp, buf, (uint)buf.Length) != Native.HIDP_STATUS_SUCCESS)
                return null;
            for (int i = 0; i < n; i++) if (list[i] == t.Usage) return 1;
            return 0;
        }
        if (Native.HidP_GetUsageValue(t.ReportType, t.UsagePage, t.LinkCollection, t.Usage, out var v, _pp, buf, (uint)buf.Length) != Native.HIDP_STATUS_SUCCESS)
            return null;
        return f.Normalize(v);
    }

    public string? GetIndexedString(int index)
    {
        if (index <= 0 || index > 255) return null;
        var b = new byte[512];
        return Native.HidD_GetIndexedString(_h, (uint)index, b, (uint)b.Length) ? Native.WString(b) : null;
    }

    public void Dispose()
    {
        if (_pp != IntPtr.Zero) { Native.HidD_FreePreparsedData(_pp); _pp = IntPtr.Zero; }
        if (_h != IntPtr.Zero && _h != Native.INVALID_HANDLE_VALUE) { Native.CloseHandle(_h); _h = IntPtr.Zero; }
    }
}
