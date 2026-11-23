using System.Text;
using System.Text.RegularExpressions;
using static AtGamesMouse.Native;

namespace AtGamesMouse;

/// <summary>
/// Hidden window that receives Raw Input from every HID joystick/gamepad (also while other apps are in front,
/// RIDEV_INPUTSINK) plus arrival/removal notices (RIDEV_DEVNOTIFY). Only devices whose VID/PID (and interface,
/// if set) match the settings are decoded; reports from every other device are dropped after one lookup.
/// Buttons are read generically with HidP_GetUsages on usage page 0x09, so the report descriptor needn't be known.
/// Assumption: HID button usage N = DirectInput/JoyToKey button N (DirectInput numbers buttons by usage).
/// </summary>
internal sealed partial class DeviceWatcher : NativeWindow, IDisposable
{
    private sealed class Device
    {
        public required string Name;
        public ushort Vid, Pid, UsagePage, Usage;
        public int? Interface;
        public bool Matches;
        public byte[]? Preparsed;
        public ushort[] Usages = [];
        public HashSet<int> Down = [];
    }

    private readonly Settings _settings;
    private readonly Dictionary<IntPtr, Device> _devices = new();
    private byte[] _buffer = new byte[256];

    /// <summary>Button number, pressed (true) or released (false), on a matching device.</summary>
    public event Action<int, bool>? ButtonChanged;

    /// <summary>The number of matching devices changed.</summary>
    public event Action? ConnectionChanged;

    public int MatchingCount => _devices.Values.Count(d => d.Matches);

    public DeviceWatcher(Settings settings)
    {
        _settings = settings;
        CreateHandle(new CreateParams { Caption = "AtGamesMouse" }); // hidden top-level window (not message-only)

        foreach (var handle in ListHidDevices()) Add(handle, "present");
        if (MatchingCount == 0) Log.Write("AtGames device not found (waiting for it to be plugged in)");

        var rid = new[]
        {
            new RAWINPUTDEVICE { UsagePage = UsagePageGeneric, Usage = UsageJoystick, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Target = Handle },
            new RAWINPUTDEVICE { UsagePage = UsagePageGeneric, Usage = UsageGamepad, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Target = Handle },
        };
        if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)System.Runtime.InteropServices.Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Write($"RegisterRawInputDevices failed: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    protected override void WndProc(ref Message m)
    {
        try
        {
            if (m.Msg == WM_INPUT) OnInput(m.LParam);
            else if (m.Msg == WM_INPUT_DEVICE_CHANGE)
            {
                if ((int)m.WParam == GIDC_ARRIVAL) { if (!_devices.ContainsKey(m.LParam)) Add(m.LParam, "arrived"); }
                else if ((int)m.WParam == GIDC_REMOVAL) Remove(m.LParam);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Input error: " + ex);
        }
        base.WndProc(ref m); // DefWindowProc also frees the WM_INPUT data
    }

    private void OnInput(IntPtr hRawInput)
    {
        uint size = 0;
        int header = RawInputHeaderSize;
        GetRawInputData(hRawInput, RID_INPUT, null, ref size, (uint)header);
        if (size > _buffer.Length) _buffer = new byte[size];
        if (GetRawInputData(hRawInput, RID_INPUT, _buffer, ref size, (uint)header) == unchecked((uint)-1)) return;

        if (BitConverter.ToUInt32(_buffer, 0) != RIM_TYPEHID) return;
        var hDevice = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(_buffer, 8) : BitConverter.ToInt32(_buffer, 8);
        if (!_devices.TryGetValue(hDevice, out var dev))
        {
            if (hDevice == IntPtr.Zero) return;
            dev = Add(hDevice, "seen");
            if (dev == null) return;
        }
        if (!dev.Matches) return; // every other controller stops here

        // RAWHID: dwSizeHid, dwCount, then dwCount reports of dwSizeHid bytes each.
        int sizeHid = (int)BitConverter.ToUInt32(_buffer, header);
        int count = (int)BitConverter.ToUInt32(_buffer, header + 4);
        if (sizeHid <= 0 || dev.Preparsed == null) return;
        var report = new byte[sizeHid];
        for (int r = 0; r < count; r++)
        {
            int offset = header + 8 + r * sizeHid;
            if (offset + sizeHid > size) break;
            Buffer.BlockCopy(_buffer, offset, report, 0, sizeHid);
            Decode(dev, report);
        }
    }

    private void Decode(Device dev, byte[] report)
    {
        uint length = (uint)dev.Usages.Length;
        if (length == 0) return;
        int status = HidP_GetUsages(HidP_Input, UsagePageButton, 0, dev.Usages, ref length, dev.Preparsed!, report, (uint)report.Length);
        if (status != HIDP_STATUS_SUCCESS) return; // e.g. a report id without buttons: keep the last state

        var now = new HashSet<int>();
        for (int i = 0; i < length; i++) now.Add(dev.Usages[i]);
        if (now.SetEquals(dev.Down)) return;

        var pressed = now.Except(dev.Down).ToList();
        var released = dev.Down.Except(now).ToList();
        dev.Down = now;
        Log.Button($"Buttons down: [{string.Join(",", now.Order())}]" +
                   (pressed.Count > 0 ? $" pressed {string.Join(",", pressed)}" : "") +
                   (released.Count > 0 ? $" released {string.Join(",", released)}" : ""));
        foreach (var b in released) ButtonChanged?.Invoke(b, false);
        foreach (var b in pressed) ButtonChanged?.Invoke(b, true);
    }

    private Device? Add(IntPtr hDevice, string how)
    {
        var info = DeviceInfo(hDevice);
        if (info == null) return null;
        var (vid, pid, page, usage) = info.Value;
        if (page != UsagePageGeneric || (usage != UsageJoystick && usage != UsageGamepad)) return null;

        var name = DeviceName(hDevice) ?? "?";
        var mi = InterfaceRegex().Match(name);
        var dev = new Device
        {
            Name = name, Vid = vid, Pid = pid, UsagePage = page, Usage = usage,
            Interface = mi.Success ? Convert.ToInt32(mi.Groups[1].Value, 16) : null,
        };
        dev.Matches = vid == _settings.Vid && pid == _settings.Pid &&
                      (_settings.Interface == null || _settings.Interface == dev.Interface);

        if (dev.Matches)
        {
            dev.Preparsed = Preparsed(hDevice);
            if (dev.Preparsed != null)
                dev.Usages = new ushort[Math.Max(HidP_MaxUsageListLength(HidP_Input, UsagePageButton, dev.Preparsed), 1)];
            else
                Log.Write("Couldn't read the HID preparsed data; buttons can't be decoded");
        }

        _devices[hDevice] = dev;
        Log.Write($"HID {(usage == UsageJoystick ? "joystick" : "gamepad")} {how}: VID {vid:X4} PID {pid:X4} " +
                  $"interface {(dev.Interface is { } i ? $"MI_{i:X2}" : "-")} " +
                  (dev.Matches ? $"MATCHES ({dev.Usages.Length} button usages)" : "ignored") + $" {name}");
        if (dev.Matches) ConnectionChanged?.Invoke();
        return dev;
    }

    private void Remove(IntPtr hDevice)
    {
        if (!_devices.Remove(hDevice, out var dev)) return;
        Log.Write($"HID device removed: VID {dev.Vid:X4} PID {dev.Pid:X4}{(dev.Matches ? " (the AtGames device)" : "")}");
        if (!dev.Matches) return;
        foreach (var b in dev.Down) ButtonChanged?.Invoke(b, false); // release anything held
        ConnectionChanged?.Invoke();
    }

    private static IEnumerable<IntPtr> ListHidDevices()
    {
        uint count = 0, size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, size);
        if (count == 0) return [];
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return [];
        return list.Take((int)count).Where(d => d.Type == RIM_TYPEHID).Select(d => d.Device).ToList();
    }

    /// <summary>RID_DEVICE_INFO: cbSize, dwType, then for HID dwVendorId, dwProductId, dwVersionNumber, usUsagePage, usUsage.</summary>
    private static (ushort Vid, ushort Pid, ushort Page, ushort Usage)? DeviceInfo(IntPtr hDevice)
    {
        var buf = new byte[32];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), (uint)buf.Length);
        uint size = (uint)buf.Length;
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, buf, ref size) == unchecked((uint)-1)) return null;
        if (BitConverter.ToUInt32(buf, 4) != RIM_TYPEHID) return null;
        return ((ushort)BitConverter.ToUInt32(buf, 8), (ushort)BitConverter.ToUInt32(buf, 12),
            BitConverter.ToUInt16(buf, 20), BitConverter.ToUInt16(buf, 22));
    }

    private static string? DeviceName(IntPtr hDevice)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, null, ref chars);
        if (chars == 0) return null;
        var buf = new byte[chars * 2];
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref chars) == unchecked((uint)-1)) return null;
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    private static byte[]? Preparsed(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, null, ref size);
        if (size == 0) return null;
        var buf = new byte[size];
        return GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, buf, ref size) == unchecked((uint)-1) ? null : buf;
    }

    [GeneratedRegex(@"[&#\\]MI_([0-9A-Fa-f]{2})")]
    private static partial Regex InterfaceRegex();

    public void Dispose() => DestroyHandle();
}
