using System.Runtime.InteropServices;

namespace Launcher.Core.Input;

/// <summary>
/// DirectInput game controllers (wheels, twin sticks) read through dinput8's COM interface, non-exclusive and with no
/// cooperative level set (= background), so it reads while another program has focus, also while a game holds the
/// same wheel exclusively for force feedback.
/// Replaced winmm's joyGetPosEx (2026-11-21): winmm keeps its own cached per-device configuration, and for the G923 it
/// held one with no buttons and no axis ranges, so the menu saw X = 0 (full left) and the hat "up" held forever.
/// Not thread-safe: use one instance from one thread (GamepadPoller's).
/// </summary>
public sealed unsafe class Joystick : IDisposable
{
    public const int MaxJoysticks = 16;

    /// <summary>DirectInput's DIJOYSTATE: axes in 0..65535 (set by <see cref="Open"/>), POV in hundredths of a degree.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public int X, Y, Z, Rx, Ry, Rz, Slider0, Slider1;
        public int Pov0, Pov1, Pov2, Pov3;
        public fixed byte Buttons[32];

        public readonly bool Button(int n) => n is >= 1 and <= 32 && (Buttons[n - 1] & 0x80) != 0;

        /// <summary>An axis by DirectInput name (X Y Z Rx Ry Rz Slider0 Slider1), see JoystickLayout.NormAxis.</summary>
        public readonly int Axis(string normName) => normName switch
        {
            "X" => X, "Y" => Y, "Z" => Z, "Rx" => Rx, "Ry" => Ry, "Rz" => Rz, "Slider0" => Slider0, _ => Slider1,
        };
    }

    /// <summary>One attached controller, from <see cref="Attached"/>.</summary>
    public sealed record Info(Guid Instance, string Name, ushort VendorId, ushort ProductId, int Axes, int Buttons, int Povs);

    public const int AxisMax = 65535;
    const uint DirectInputVersion = 0x0800, DevClassGameCtrl = 4, AttachedOnly = 1;
    static readonly Guid IidDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");

    [DllImport("dinput8.dll")]
    static extern int DirectInput8Create(IntPtr hinst, uint version, in Guid riid, out IntPtr di, IntPtr outer);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(IntPtr name);

    // DIOBJECTDATAFORMAT { const GUID* pguid; DWORD dwOfs, dwType, dwFlags; } and DIDATAFORMAT, laid out natively.
    [StructLayout(LayoutKind.Sequential)] struct ObjFormat { public IntPtr Guid; public uint Ofs, Type, Flags; }
    [StructLayout(LayoutKind.Sequential)] struct DataFormat { public uint Size, ObjSize, Flags, DataSize, NumObjs; public IntPtr Objs; }

    IntPtr _di;
    readonly IntPtr _guids;        // native GUIDs the data format points at
    readonly IntPtr _objs;         // native DIOBJECTDATAFORMAT[44]
    readonly DataFormat* _format;  // native DIDATAFORMAT (c_dfDIJoystick, built by hand: it lives in dinput8.lib)
    readonly Dictionary<Guid, IntPtr> _open = [];

    public Joystick()
    {
        if (DirectInput8Create(GetModuleHandleW(IntPtr.Zero), DirectInputVersion, IidDirectInput8W, out _di, IntPtr.Zero) < 0)
            _di = IntPtr.Zero;

        // dinput.h: GUID_XAxis..GUID_POV = A36D02xx-C9F3-11CF-BFC7-444553540000.
        uint[] ids = [0xE0, 0xE1, 0xE2, 0xF4, 0xF5, 0xE3, 0xE4, 0xF2]; // X Y Z Rx Ry Rz Slider POV
        _guids = Marshal.AllocHGlobal(16 * ids.Length);
        for (int i = 0; i < ids.Length; i++)
            *(Guid*)(_guids + 16 * i) = new Guid(0xA36D0200 | ids[i], 0xC9F3, 0x11CF, 0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00);
        IntPtr G(int i) => _guids + 16 * i;

        const uint AnyOptional = 0x80FFFF00; // DIDFT_OPTIONAL | DIDFT_ANYINSTANCE
        const uint Axis = 3, Pov = 0x10, Button = 0x0C, AspectPosition = 0x100;
        var list = new List<ObjFormat>();
        for (int i = 0; i < 6; i++) list.Add(new ObjFormat { Guid = G(i), Ofs = (uint)(4 * i), Type = AnyOptional | Axis, Flags = AspectPosition });
        list.Add(new ObjFormat { Guid = G(6), Ofs = 24, Type = AnyOptional | Axis, Flags = AspectPosition });
        list.Add(new ObjFormat { Guid = G(6), Ofs = 28, Type = AnyOptional | Axis, Flags = AspectPosition });
        for (int i = 0; i < 4; i++) list.Add(new ObjFormat { Guid = G(7), Ofs = (uint)(32 + 4 * i), Type = AnyOptional | Pov });
        for (int i = 0; i < 32; i++) list.Add(new ObjFormat { Guid = IntPtr.Zero, Ofs = (uint)(48 + i), Type = AnyOptional | Button });

        _objs = Marshal.AllocHGlobal(sizeof(ObjFormat) * list.Count);
        for (int i = 0; i < list.Count; i++) ((ObjFormat*)_objs)[i] = list[i];
        _format = (DataFormat*)Marshal.AllocHGlobal(sizeof(DataFormat));
        *_format = new DataFormat
        {
            Size = (uint)sizeof(DataFormat), ObjSize = (uint)sizeof(ObjFormat), Flags = 1 /* DIDF_ABSAXIS */,
            DataSize = (uint)sizeof(State), NumObjs = (uint)list.Count, Objs = _objs,
        };
    }

    public bool Available => _di != IntPtr.Zero;

    static IntPtr* Vt(IntPtr com) => *(IntPtr**)com;
    static void Release(IntPtr com) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vt(com)[2])(com);

    sealed class Collector { public readonly List<(Guid Instance, string Name)> Found = []; }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    static int OnDevice(byte* inst, IntPtr refData)
    {
        // DIDEVICEINSTANCEW: guidInstance @4, tszInstanceName[260] @40.
        ((Collector)GCHandle.FromIntPtr(refData).Target!).Found.Add((*(Guid*)(inst + 4), new string((char*)(inst + 40))));
        return 1; // DIENUM_CONTINUE
    }

    /// <summary>
    /// Attached game controllers in DirectInput's order, opened for reading (new ones are opened here), except those
    /// in <paramref name="skip"/>.
    /// </summary>
    public List<Info> Attached(IReadOnlySet<Guid>? skip = null)
    {
        var result = new List<Info>();
        if (_di == IntPtr.Zero) return result;
        var c = new Collector();
        var h = GCHandle.Alloc(c);
        try
        {
            var enumDevices = (delegate* unmanaged[Stdcall]<IntPtr, uint, delegate* unmanaged[Stdcall]<byte*, IntPtr, int>, IntPtr, uint, int>)Vt(_di)[4];
            enumDevices(_di, DevClassGameCtrl, &OnDevice, GCHandle.ToIntPtr(h), AttachedOnly);
        }
        finally { h.Free(); }

        var caps = stackalloc uint[11]; // DIDEVCAPS: dwSize, dwFlags, dwDevType, dwAxes, dwButtons, dwPOVs, ... (44 bytes)
        foreach (var (instance, name) in c.Found)
        {
            if (skip?.Contains(instance) == true) continue;
            if (!_open.TryGetValue(instance, out var dev))
            {
                dev = Open(instance);
                if (dev == IntPtr.Zero) continue;
                _open[instance] = dev;
            }
            new Span<uint>(caps, 11).Clear();
            caps[0] = 44;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vt(dev)[3])(dev, caps);
            uint vidPid = GetDword(dev, 24); // DIPROP_VIDPID: LOWORD vendor, HIWORD product
            result.Add(new Info(instance, name, (ushort)vidPid, (ushort)(vidPid >> 16), (int)caps[3], (int)caps[4], (int)caps[5]));
        }
        return result;
    }

    /// <summary>Creates the device, sets the joystick data format and 0..65535 axis ranges, and acquires it.</summary>
    IntPtr Open(Guid instance)
    {
        IntPtr dev;
        var createDevice = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, IntPtr, int>)Vt(_di)[3];
        if (createDevice(_di, &instance, &dev, IntPtr.Zero) < 0 || dev == IntPtr.Zero) return IntPtr.Zero;
        var vt = Vt(dev);
        // IDirectInputDevice8W: 6 SetProperty, 7 Acquire, 11 SetDataFormat. No SetCooperativeLevel: the default is
        // non-exclusive + background, which reads without a window and alongside a game's exclusive access.
        if (((delegate* unmanaged[Stdcall]<IntPtr, DataFormat*, int>)vt[11])(dev, _format) < 0) { Release(dev); return IntPtr.Zero; }
        // DIPROPRANGE: DIPROPHEADER { dwSize 24, dwHeaderSize 16, dwObj 0, dwHow DIPH_DEVICE } + lMin, lMax.
        var range = stackalloc int[6] { 24, 16, 0, 0, 0, AxisMax };
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)vt[6])(dev, 4 /* DIPROP_RANGE */, range);
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[7])(dev);
        return dev;
    }

    static uint GetDword(IntPtr dev, int prop)
    {
        var buf = stackalloc uint[5] { 20, 16, 0, 0, 0 }; // DIPROPDWORD
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Vt(dev)[5])(dev, prop, buf) >= 0 ? buf[4] : 0;
    }

    /// <summary>Reads a device listed by <see cref="Attached"/>; false if it is gone (it is then closed).</summary>
    public bool TryGetState(Guid instance, out State state)
    {
        state = default;
        if (!_open.TryGetValue(instance, out var dev)) return false;
        var vt = Vt(dev);
        var poll = (delegate* unmanaged[Stdcall]<IntPtr, int>)vt[25];
        var getState = (delegate* unmanaged[Stdcall]<IntPtr, uint, State*, int>)vt[9];
        var acquire = (delegate* unmanaged[Stdcall]<IntPtr, int>)vt[7];
        fixed (State* s = &state)
        {
            poll(dev);
            if (getState(dev, (uint)sizeof(State), s) >= 0) return true;
            // Lost (another program took it, or a device reset): acquire again once.
            if (acquire(dev) >= 0) { poll(dev); if (getState(dev, (uint)sizeof(State), s) >= 0) return true; }
        }
        Close(instance);
        return false;
    }

    public void Close(Guid instance)
    {
        if (!_open.Remove(instance, out var dev)) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(dev)[8])(dev); // Unacquire
        Release(dev);
    }

    public void Dispose()
    {
        foreach (var g in _open.Keys.ToList()) Close(g);
        if (_di != IntPtr.Zero) { Release(_di); _di = IntPtr.Zero; }
        Marshal.FreeHGlobal((IntPtr)_format);
        Marshal.FreeHGlobal(_objs);
        Marshal.FreeHGlobal(_guids);
    }

    /// <summary>-1..1 for an axis value within 0..AxisMax.</summary>
    public static double Centered(int v) => (2.0 * v / AxisMax) - 1;

    /// <summary>0..1 for a pedal; released = 0 unless reversed.</summary>
    public static double Pedal(int v, bool reversed)
    {
        double f = (double)v / AxisMax;
        return Math.Clamp(reversed ? 1 - f : f, 0, 1);
    }

    /// <summary>The hat as d-pad bits (POV in hundredths of a degree, 0 = up, clockwise; centred = -1 / 0xFFFF).</summary>
    public static PadInput Hat(int pov)
    {
        if ((pov & 0xFFFF) == 0xFFFF || pov < 0) return PadInput.None;
        var r = PadInput.None;
        if (pov is > 27000 or < 9000) r |= PadInput.DPadUp;
        if (pov is > 0 and < 18000) r |= PadInput.DPadRight;
        if (pov is > 9000 and < 27000) r |= PadInput.DPadDown;
        if (pov > 18000) r |= PadInput.DPadLeft;
        return r;
    }
}
