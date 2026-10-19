using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace X56LedControl;

public enum X56Kind { Stick, Throttle }

/// <summary>
/// One half of the X56 HOTAS (stick or throttle), talking to the HID collection that
/// carries feature reports. That is the same collection Logitech's RhinoDevice.AddControl
/// selects (the one with FeatureReportByteLength != 0).
///
/// Protocol recovered from Sd.Branding.Flight.Rhino.dll / Controllers\X56.dll / Sd.Devices.dll:
///   Feature report 0x09 "group command": [0x09, cmd, featureId, payload...], zero-padded to FeatureReportByteLength
///     cmd:       0 = set, 1 = set + broadcast to group, 2 = query (the reply arrives as an input report)
///     featureId: 2 = LED brightness, payload [0..100]
///                3 = LED RGB colour,  payload [R, G, B]
///   Feature report 0x01, payload [0x01] = save current settings to EEPROM
///   Reply input report: [reportId, 0x09, 1|2, featureId, value...]
/// </summary>
public sealed class X56Device : IDisposable
{
    public const ushort Vid = 0x0738;
    public const ushort StickPid = 0x2221;
    public const ushort ThrottlePid = 0xA221;

    const byte ReportGroupCommand = 0x09;
    const byte ReportSaveEeprom = 0x01;
    const byte CmdSet = 0;
    const byte CmdQuery = 2;
    const byte FeatureBrightness = 2;
    const byte FeatureColour = 3;

    readonly SafeFileHandle _handle;
    readonly FileStream _stream;
    readonly object _lock = new();

    public X56Kind Kind { get; }
    public string Path { get; }
    public int FeatureLength { get; }
    public int InputLength { get; }

    X56Device(X56Kind kind, string path, SafeFileHandle handle, Native.HIDP_CAPS caps)
    {
        Kind = kind;
        Path = path;
        _handle = handle;
        FeatureLength = caps.FeatureReportByteLength;
        InputLength = caps.InputReportByteLength;
        _stream = new FileStream(handle, FileAccess.ReadWrite, 0, true);
    }

    public string Name => Kind == X56Kind.Stick ? "Stick" : "Throttle";

    /// <summary>Finds every present X56 stick/throttle control collection.</summary>
    public static List<X56Device> Enumerate()
    {
        var found = new Dictionary<X56Kind, (string path, Native.HIDP_CAPS caps, int score)>();
        foreach (var path in EnumerateHidPaths())
        {
            var lower = path.ToLowerInvariant();
            if (!lower.Contains("vid_0738")) continue;
            X56Kind kind;
            if (lower.Contains("pid_2221")) kind = X56Kind.Stick;
            else if (lower.Contains("pid_a221")) kind = X56Kind.Throttle;
            else continue;

            using var probe = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (probe.IsInvalid) continue;
            if (!TryGetCaps(probe, out var caps) || caps.FeatureReportByteLength < 6) continue;

            // MI_02 is the interface whose INF installs the group/control filter (SaiG2221/SaiGa221).
            int score = lower.Contains("mi_02") ? 2 : 1;
            if (!found.TryGetValue(kind, out var best) || score > best.score)
                found[kind] = (path, caps, score);
        }

        var devices = new List<X56Device>();
        foreach (var (kind, info) in found.OrderBy(f => f.Key))
        {
            var handle = Native.CreateFile(info.path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            devices.Add(new X56Device(kind, info.path, handle, info.caps));
        }
        return devices;
    }

    static bool TryGetCaps(SafeFileHandle handle, out Native.HIDP_CAPS caps)
    {
        caps = default;
        var attrs = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != Vid) return false;
        if (!Native.HidD_GetPreparsedData(handle, out var pp)) return false;
        try { return Native.HidP_GetCaps(pp, out caps) == Native.HIDP_STATUS_SUCCESS; }
        finally { Native.HidD_FreePreparsedData(pp); }
    }

    static IEnumerable<string> EnumerateHidPaths()
    {
        Native.HidD_GetHidGuid(out var hidGuid);
        var set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64, 6 on x86.
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(buf + 4)!;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    void SendFeature(params byte[] packet)
    {
        // Mirrors HidDevice.CorrectBufferSize: zero-pad up to the collection's feature report length.
        var buf = new byte[Math.Max(FeatureLength, packet.Length)];
        Array.Copy(packet, buf, packet.Length);
        lock (_lock)
        {
            if (!Native.HidD_SetFeature(_handle, buf, buf.Length))
                throw new IOException($"{Name}: HidD_SetFeature(report 0x{packet[0]:X2}) failed, Win32 error {Marshal.GetLastWin32Error()}");
        }
    }

    public void SetColour(byte r, byte g, byte b) => SendFeature(ReportGroupCommand, CmdSet, FeatureColour, r, g, b);

    public void SetBrightness(int percent) =>
        SendFeature(ReportGroupCommand, CmdSet, FeatureBrightness, (byte)Math.Clamp(percent, 0, 100));

    /// <summary>Persists the current colour/brightness so it survives a power cycle.</summary>
    public void SaveToEeprom() => SendFeature(ReportSaveEeprom, 0x01);

    public async Task<(byte r, byte g, byte b)?> QueryColourAsync(int timeoutMs = 600)
    {
        var data = await QueryAsync(FeatureColour, timeoutMs);
        return data == null ? null : (data[4], data[5], data[6]);
    }

    public async Task<int?> QueryBrightnessAsync(int timeoutMs = 600)
    {
        var data = await QueryAsync(FeatureBrightness, timeoutMs);
        return data?[4];
    }

    async Task<byte[]?> QueryAsync(byte feature, int timeoutMs)
    {
        if (InputLength < 7) return null;
        using var cts = new CancellationTokenSource(timeoutMs);
        var read = ReadReplyAsync(feature, cts.Token);
        SendFeature(ReportGroupCommand, CmdQuery, feature);
        try { return await read; }
        catch (OperationCanceledException) { return null; }
    }

    async Task<byte[]?> ReadReplyAsync(byte feature, CancellationToken ct)
    {
        var buf = new byte[InputLength];
        while (true)
        {
            int n = await _stream.ReadAsync(buf, ct);
            // Same checks as RhinoDevice.Event_HidControlReader_HidDataReceived / ProcessGroupCommands.
            if (n >= 7 && buf[1] == ReportGroupCommand && (buf[2] == 1 || buf[2] == 2) && buf[3] == feature)
                return (byte[])buf.Clone();
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }
}
