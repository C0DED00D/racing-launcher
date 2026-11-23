using System.Runtime.InteropServices;

namespace Launcher.Core.Input;

/// <summary>One DirectInput game controller: its instance GUID (what DirectInput games bind by), names and device path.</summary>
public sealed record DirectInputDevice(Guid Instance, Guid Product, string InstanceName, string ProductName, string Path);

/// <summary>
/// Lists DirectInput game controllers with their device paths, through dinput8's COM interface. The instance GUID is
/// unique per device even for identical wheels (both vJoy devices), and the path tells them apart (vJoy device N has
/// "&amp;Col0N"; a USB wheel its USB instance), so a seat's wheel can be found by seats.json "joystick".
/// </summary>
public static unsafe class DirectInputDevices
{
    static readonly Guid IidDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    const uint DirectInputVersion = 0x0800, DevClassGameCtrl = 4, AttachedOnly = 1;

    [DllImport("dinput8.dll")]
    static extern int DirectInput8Create(IntPtr hinst, uint version, in Guid riid, out IntPtr di, IntPtr outer);

    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(IntPtr name);

    // DIDEVICEINSTANCEW: dwSize, guidInstance @4, guidProduct @20, dwDevType @36, tszInstanceName[260] @40,
    // tszProductName[260] @560, guidFFDriver @1080, wUsagePage, wUsage: 1100 bytes.
    const int InstanceSize = 1100;

    sealed class Collector { public readonly List<(Guid, Guid, string, string)> Found = []; }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    static int OnDevice(byte* inst, IntPtr refData)
    {
        var c = (Collector)GCHandle.FromIntPtr(refData).Target!;
        var instance = *(Guid*)(inst + 4);
        var product = *(Guid*)(inst + 20);
        var iname = new string((char*)(inst + 40));
        var pname = new string((char*)(inst + 560));
        c.Found.Add((instance, product, iname, pname));
        return 1; // DIENUM_CONTINUE
    }

    /// <summary>Attached game controllers (wheels, joysticks, pads), in DirectInput's order.</summary>
    public static List<DirectInputDevice> List()
    {
        var result = new List<DirectInputDevice>();
        if (DirectInput8Create(GetModuleHandleW(IntPtr.Zero), DirectInputVersion, IidDirectInput8W, out var di, IntPtr.Zero) < 0)
            return result;
        var c = new Collector();
        var h = GCHandle.Alloc(c);
        try
        {
            var vt = *(IntPtr**)di;
            // IDirectInput8W: 3 CreateDevice, 4 EnumDevices
            var enumDevices = (delegate* unmanaged[Stdcall]<IntPtr, uint, delegate* unmanaged[Stdcall]<byte*, IntPtr, int>, IntPtr, uint, int>)vt[4];
            enumDevices(di, DevClassGameCtrl, &OnDevice, GCHandle.ToIntPtr(h), AttachedOnly);
            var createDevice = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, IntPtr, int>)vt[3];
            foreach (var (instance, product, iname, pname) in c.Found)
            {
                var g = instance;
                IntPtr dev;
                string path = "";
                if (createDevice(di, &g, &dev, IntPtr.Zero) >= 0 && dev != IntPtr.Zero)
                {
                    path = DevicePath(dev);
                    var dvt = *(IntPtr**)dev;
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint>)dvt[2])(dev); // Release
                }
                result.Add(new DirectInputDevice(instance, product, iname, pname, path));
            }
        }
        finally
        {
            h.Free();
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)di)[2])(di);
        }
        return result;
    }

    /// <summary>DIPROP_GUIDANDPATH: DIPROPHEADER (16 bytes) + guidClass (16) + wszPath[260].</summary>
    static string DevicePath(IntPtr dev)
    {
        const int Size = 16 + 16 + 520;
        var buf = stackalloc byte[Size];
        new Span<byte>(buf, Size).Clear();
        ((uint*)buf)[0] = Size;   // dwSize
        ((uint*)buf)[1] = 16;     // dwHeaderSize
        ((uint*)buf)[2] = 0;      // dwObj
        ((uint*)buf)[3] = 0;      // dwHow = DIPH_DEVICE
        var vt = *(IntPtr**)dev;
        // IDirectInputDevice8W: 5 GetProperty; DIPROP_GUIDANDPATH = MAKEDIPROP(12)
        var getProperty = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)vt[5];
        return getProperty(dev, 12, buf) >= 0 ? new string((char*)(buf + 32)) : "";
    }

    /// <summary>
    /// The device whose path or name contains <paramref name="match"/> (case-insensitive). If nothing matches and only
    /// one controller is attached (one wheel, or vJoy with a single device), that one; otherwise null.
    /// </summary>
    public static DirectInputDevice? Find(string? match)
    {
        var all = List();
        if (string.IsNullOrWhiteSpace(match)) return all.FirstOrDefault();
        return Matching(all, match) ?? (all.Count == 1 ? all[0] : null);
    }

    static DirectInputDevice? Matching(List<DirectInputDevice> all, string match) =>
        all.FirstOrDefault(d => d.Path.Contains(match, StringComparison.OrdinalIgnoreCase)) ??
        all.FirstOrDefault(d => d.InstanceName.Contains(match, StringComparison.OrdinalIgnoreCase) ||
                                d.ProductName.Contains(match, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The seat's match text if an attached controller has it, else null (log says why), so programs that filter by
    /// it (Daytona's DAYTONA_JOY_MATCH, Xenia's sdl_joystick_match, VCThunder's dinput_name) fall back to the first
    /// wheel instead of seeing none.
    /// </summary>
    public static string? UsableMatch(string? match, string who)
    {
        if (string.IsNullOrWhiteSpace(match)) return null;
        try
        {
            var all = List();
            if (Matching(all, match) != null) return match;
            Log.Warn($"  {who}: no controller matches seats.json joystick \"{match}\" ({all.Count} attached: " +
                     string.Join("; ", all.Select(d => $"{d.InstanceName} {d.Path}")) + "); using the first wheel");
        }
        catch (Exception ex) { Log.Warn($"  {who}: listing DirectInput controllers failed: {ex.Message}"); }
        return null;
    }
}
